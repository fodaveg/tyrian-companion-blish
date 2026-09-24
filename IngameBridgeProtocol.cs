using System;
using System.Collections.Generic;
using System.Text;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// The wire contract of the in-game bridge, version 2, as this module's own client speaks it.
    /// Normative text: `docs/SPEC-puente-ingame.md` in the `tyrian-companion` repo; the plugin's
    /// executable copy of the same contract is `src/alerts/alert-ingame-protocol.ts`. Pure: no
    /// socket, no clock, no <c>SettingEntry</c>. The bridge used to be one-directional (v1: a
    /// single `hello`, then this module only ever read); David decided on 2026-09-24 that Nexus
    /// and Blish HUD, with one and the same protocol, report the game context (map, character,
    /// whether the player is in gameplay) so the plugin can mark sessions without a click. That
    /// makes the channel bidirectional, and therefore authenticated with a per-installation token
    /// the player pastes into this module's settings — never logged, never echoed.
    /// </summary>
    internal static class IngameBridgeProtocol {

        public const int Version = 2;

        /// <summary>Hard cap on one frame, excluding its terminator, in either direction.</summary>
        public const int MaxLineBytes = 512;

        /// <summary>
        /// The heartbeat interval this module assumes until a `welcome` tells it otherwise. Never
        /// actually used to decide when to send anything before authentication, since there is
        /// nothing to send yet; kept only as a well-defined default for <see cref="IngameConnectionState"/>.
        /// </summary>
        public const int DefaultHeartbeatIntervalMs = 5_000;

        /// <summary>
        /// How often the connection loop re-samples <c>GameContextSampler</c> for a changed map,
        /// character or game state once authenticated. Independent of the server's heartbeat
        /// interval: this is how quickly a player sees their own session start, not a wire limit.
        /// </summary>
        public const int GameContextPollIntervalMs = 250;

        /// <summary>H8's reconnect table, reused verbatim per the spec's transport section.</summary>
        public static readonly int[] ReconnectBackoffDelaysMs = { 250, 500, 1_000, 2_000, 5_000 };

        public const string ClientName = "blish";

        private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        /// <summary>
        /// `client`, `clientVersion`, `instance` and `token` exactly, in that order after `v`/`type`
        /// — order does not matter to the plugin's parser, but a fixed order makes this line
        /// reproducible for the console tests that diff it against the spec's own example.
        /// </summary>
        public static byte[] EncodeHello(string clientVersion, string instance, string token) {
            return EncodeLine(new[] {
                new KeyValuePair<string, object>("v", (long)Version),
                new KeyValuePair<string, object>("type", "hello"),
                new KeyValuePair<string, object>("client", ClientName),
                new KeyValuePair<string, object>("clientVersion", clientVersion),
                new KeyValuePair<string, object>("instance", instance),
                new KeyValuePair<string, object>("token", token),
            });
        }

        public static byte[] EncodeContext(string nonce, long seq, IngameGameContext context) {
            return EncodeLine(new[] {
                new KeyValuePair<string, object>("v", (long)Version),
                new KeyValuePair<string, object>("type", "context"),
                new KeyValuePair<string, object>("nonce", nonce),
                new KeyValuePair<string, object>("seq", seq),
                new KeyValuePair<string, object>("state", context.State),
                new KeyValuePair<string, object>("mapId", context.MapId.HasValue ? (object)(long)context.MapId.Value : null),
                new KeyValuePair<string, object>("character", context.Character),
            });
        }

        public static byte[] EncodeHeartbeat(string nonce, long seq) {
            return EncodeLine(new[] {
                new KeyValuePair<string, object>("v", (long)Version),
                new KeyValuePair<string, object>("type", "heartbeat"),
                new KeyValuePair<string, object>("nonce", nonce),
                new KeyValuePair<string, object>("seq", seq),
            });
        }

        public static byte[] EncodeBye(string nonce, long seq, string reason) {
            return EncodeLine(new[] {
                new KeyValuePair<string, object>("v", (long)Version),
                new KeyValuePair<string, object>("type", "bye"),
                new KeyValuePair<string, object>("nonce", nonce),
                new KeyValuePair<string, object>("seq", seq),
                new KeyValuePair<string, object>("reason", reason),
            });
        }

        private static byte[] EncodeLine(IReadOnlyList<KeyValuePair<string, object>> members) {
            var json = FlatJsonLine.Encode(members);
            var body = Encoding.UTF8.GetBytes(json);
            if (body.Length > MaxLineBytes) {
                // Every field above is bounded by the settings/sampler that build it (token up to
                // 128 characters, character name up to 32, map id an int); this should not happen.
                // Failing loudly beats silently sending a line the plugin's framer is built to reject.
                throw new InvalidOperationException($"An outgoing {json.Length}-byte line is over the {MaxLineBytes}-byte cap the spec fixes.");
            }
            var line = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, line, 0, body.Length);
            line[body.Length] = (byte)'\n';
            return line;
        }

        /// <summary>
        /// What one incoming line from the plugin turned out to be. <see cref="Unknown"/> covers
        /// every "ignore and keep reading" case the spec's addon contract describes: a `type` this
        /// module does not know, or a known `type` with keys missing or extra. <see cref="ProtocolNewer"/>
        /// is the one case that is not silent: <c>"v"</c> greater than <see cref="Version"/> means
        /// "update this module".
        /// </summary>
        public enum IncomingKind { Unknown, ProtocolNewer, Welcome, Alert, Error }

        public sealed class IncomingLine {
            public IncomingKind Kind;
            public string Server;
            public string Nonce;
            public int HeartbeatIntervalMs;
            public long AlertSeq;
            public string AlertKind;
            public string AlertContent;
            public string ErrorCode;
        }

        private static readonly string[] WelcomeKeys = { "v", "type", "server", "nonce", "heartbeatIntervalMs" };
        private static readonly string[] AlertKeys = { "v", "type", "seq", "kind", "name", "quantity", "totalCopper", "content" };
        private static readonly string[] ErrorKeys = { "v", "type", "code" };

        /// <summary>
        /// Turns one frame (the bytes between two <c>\n</c>, terminator excluded) into an
        /// <see cref="IncomingLine"/>. Tolerant by design, per the spec's addon contract: malformed
        /// JSON, an unrecognized <c>type</c>, or a known <c>type</c> with the wrong key set all come
        /// back as <see cref="IncomingKind.Unknown"/> rather than a thrown exception or a closed
        /// connection — only the plugin's side of this bridge is the strict one.
        /// </summary>
        public static IncomingLine ParseIncomingLine(byte[] frame) {
            var length = frame.Length;
            if (length > 0 && frame[length - 1] == (byte)'\r') length -= 1; // a C# StreamWriter on the plugin's own end would never do this, but tolerate it anyway
            if (length == 0 || length > MaxLineBytes) return Unknown();

            string text;
            try {
                text = new UTF8Encoding(false, true).GetString(frame, 0, length);
            } catch (DecoderFallbackException) {
                return Unknown();
            }

            if (!FlatJsonLine.TryParse(text, out var record)) return Unknown();
            if (!TryGetInteger(record, "v", out var version)) return Unknown();
            if (version > Version) return new IncomingLine { Kind = IncomingKind.ProtocolNewer };
            if (version != Version) return Unknown();
            if (!record.TryGetValue("type", out var typeValue) || !(typeValue is string type)) return Unknown();

            switch (type) {
                case "welcome":
                    if (!HasExactKeys(record, WelcomeKeys)) return Unknown();
                    if (!(record["server"] is string server) || !(record["nonce"] is string nonce) || !TryGetInteger(record, "heartbeatIntervalMs", out var heartbeatIntervalMs)) return Unknown();
                    return new IncomingLine { Kind = IncomingKind.Welcome, Server = server, Nonce = nonce, HeartbeatIntervalMs = (int)heartbeatIntervalMs };
                case "alert":
                    if (!HasExactKeys(record, AlertKeys)) return Unknown();
                    if (!TryGetInteger(record, "seq", out var seq) || !(record["kind"] is string kind) || !(record["content"] is string content)) return Unknown();
                    return new IncomingLine { Kind = IncomingKind.Alert, AlertSeq = seq, AlertKind = kind, AlertContent = content };
                case "error":
                    if (!HasExactKeys(record, ErrorKeys)) return Unknown();
                    if (!(record["code"] is string code)) return Unknown();
                    return new IncomingLine { Kind = IncomingKind.Error, ErrorCode = code };
                default:
                    return Unknown();
            }
        }

        private static IncomingLine Unknown() => new IncomingLine { Kind = IncomingKind.Unknown };

        private static bool TryGetInteger(Dictionary<string, object> record, string key, out long value) {
            value = 0;
            if (!record.TryGetValue(key, out var raw) || !(raw is long integer)) return false;
            value = integer;
            return true;
        }

        private static bool HasExactKeys(Dictionary<string, object> record, string[] keys) {
            if (record.Count != keys.Length) return false;
            foreach (var key in keys) if (!record.ContainsKey(key)) return false;
            return true;
        }

        /// <summary>16 CSPRNG bytes as 22 base64url characters: this module's per-process `instance`.</summary>
        public static string CreateInstanceId(Action<byte[]> fillRandom) {
            var bytes = new byte[16];
            fillRandom(bytes);
            return EncodeBase64Url(bytes);
        }

        private static string EncodeBase64Url(byte[] bytes) {
            var builder = new StringBuilder();
            for (var index = 0; index < bytes.Length; index += 3) {
                int first = bytes[index];
                int? second = index + 1 < bytes.Length ? bytes[index + 1] : (int?)null;
                int? third = index + 2 < bytes.Length ? bytes[index + 2] : (int?)null;
                var value = (first << 16) | ((second ?? 0) << 8) | (third ?? 0);
                builder.Append(Base64UrlAlphabet[(value >> 18) & 63]);
                builder.Append(Base64UrlAlphabet[(value >> 12) & 63]);
                if (second.HasValue) builder.Append(Base64UrlAlphabet[(value >> 6) & 63]);
                if (third.HasValue) builder.Append(Base64UrlAlphabet[value & 63]);
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// One snapshot of what this module reports as `context`: the three fields the spec's
    /// `IngameGameContext` fixes. Equality is by value so the connection loop can tell "nothing
    /// changed" from "send it again" without comparing the encoded JSON.
    /// </summary>
    internal readonly struct IngameGameContext : IEquatable<IngameGameContext> {
        public readonly string State;
        public readonly int? MapId;
        public readonly string Character;

        public IngameGameContext(string state, int? mapId, string character) {
            State = state;
            MapId = mapId;
            Character = character;
        }

        public bool Equals(IngameGameContext other) => State == other.State && MapId == other.MapId && Character == other.Character;

        public override bool Equals(object obj) => obj is IngameGameContext other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                var hash = State?.GetHashCode() ?? 0;
                hash = (hash * 397) ^ (MapId?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ (Character?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}
