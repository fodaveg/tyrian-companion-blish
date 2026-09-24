using System;
using System.Text;
using TyrianCompanion.BlishBridge;

namespace TyrianCompanion.BlishBridge.Tests {

    /// <summary>
    /// Exercises the v2 wire contract from `docs/SPEC-puente-ingame.md` without a Blish HUD host:
    /// encodes one line of each addon-to-plugin message type and diffs it byte-for-byte against
    /// the spec's own "Ejemplo completo" trace, then round-trips a handful of plugin-to-addon
    /// lines through the decoder — a well-formed one of each type, and the tolerance cases the
    /// addon contract requires (an extra key, an unknown `type`, a newer `v`). Then checks
    /// `TokenGuard.cs`, what the token setting may hold: a Guild Wars 2 API key refused, cleared on
    /// load and never encoded in a `hello`, a 43-character token accepted, whitespace trimmed, and
    /// too short, too long or spaced values refused. Prints the first
    /// mismatch and exits 1 on any failure; exits 0 and prints a summary otherwise.
    /// </summary>
    internal static class Program {

        private static int _failures;

        private static int Main() {
            EncodesTheSpecExampleTrace();
            DecodesAWellFormedWelcome();
            DecodesAWellFormedAlert();
            DecodesAWellFormedError();
            IgnoresAKnownTypeWithAnExtraKey();
            IgnoresAKnownTypeWithAMissingKey();
            IgnoresAnUnknownType();
            FlagsANewerProtocolVersion();
            RejectsADuplicateTopLevelKey();
            InstanceIdIsCanonicalBase64Url();

            RejectsAGw2ApiKeyAsTheToken();
            AcceptsA43CharacterToken();
            TrimsWhitespaceAroundTheToken();
            RejectsATokenTooShortTooLongOrWithSpaces();
            DiscardsAnApiKeySavedBefore();
            NeverSendsAnApiKeyInAHello();
            CorrectsAChangedTokenSetting();
            TheRejectedMessageSaysWhatToDo();

            if (_failures > 0) {
                Console.Error.WriteLine($"{_failures} check(s) failed.");
                return 1;
            }

            Console.WriteLine("All protocol checks passed.");
            return 0;
        }

        /// <summary>
        /// The exact lines from the spec's "Ejemplo completo" section, byte-for-byte. `hello`'s
        /// `clientVersion` and `instance` are the spec's own example values, not this build's real
        /// ones, so the encoded line can be diffed directly against the document.
        /// </summary>
        private static void EncodesTheSpecExampleTrace() {
            Check(
                "hello line matches the spec's example",
                Utf8Line(IngameBridgeProtocol.EncodeHello("0.2.0", "q8Hq3n2t0dQyYf0nJ1p0Aw", "<token>")),
                "{\"v\":2,\"type\":\"hello\",\"client\":\"blish\",\"clientVersion\":\"0.2.0\",\"instance\":\"q8Hq3n2t0dQyYf0nJ1p0Aw\",\"token\":\"<token>\"}\n");

            const string nonce = "Zk3m1Qw9Lr0aT7yUc2Vb5g";

            Check(
                "context line (character_select) matches the spec's example",
                Utf8Line(IngameBridgeProtocol.EncodeContext(nonce, 0, new IngameGameContext("character_select", null, null))),
                "{\"v\":2,\"type\":\"context\",\"nonce\":\"Zk3m1Qw9Lr0aT7yUc2Vb5g\",\"seq\":0,\"state\":\"character_select\",\"mapId\":null,\"character\":null}\n");

            Check(
                "context line (loading) matches the spec's example",
                Utf8Line(IngameBridgeProtocol.EncodeContext(nonce, 1, new IngameGameContext("loading", 50, "Astra Uno"))),
                "{\"v\":2,\"type\":\"context\",\"nonce\":\"Zk3m1Qw9Lr0aT7yUc2Vb5g\",\"seq\":1,\"state\":\"loading\",\"mapId\":50,\"character\":\"Astra Uno\"}\n");

            Check(
                "context line (gameplay, Labyrinth map) matches the spec's example",
                Utf8Line(IngameBridgeProtocol.EncodeContext(nonce, 4, new IngameGameContext("gameplay", 866, "Astra Uno"))),
                "{\"v\":2,\"type\":\"context\",\"nonce\":\"Zk3m1Qw9Lr0aT7yUc2Vb5g\",\"seq\":4,\"state\":\"gameplay\",\"mapId\":866,\"character\":\"Astra Uno\"}\n");

            Check(
                "heartbeat line matches the spec's example",
                Utf8Line(IngameBridgeProtocol.EncodeHeartbeat(nonce, 3)),
                "{\"v\":2,\"type\":\"heartbeat\",\"nonce\":\"Zk3m1Qw9Lr0aT7yUc2Vb5g\",\"seq\":3}\n");

            Check(
                "bye line matches the spec's example",
                Utf8Line(IngameBridgeProtocol.EncodeBye(nonce, 5, "game_exit")),
                "{\"v\":2,\"type\":\"bye\",\"nonce\":\"Zk3m1Qw9Lr0aT7yUc2Vb5g\",\"seq\":5,\"reason\":\"game_exit\"}\n");
        }

        private static void DecodesAWellFormedWelcome() {
            var line = "{\"v\":2,\"type\":\"welcome\",\"server\":\"Pq0v4c3Wm9Xs1Ya7Tb2NeQ\",\"nonce\":\"Zk3m1Qw9Lr0aT7yUc2Vb5g\",\"heartbeatIntervalMs\":5000}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("welcome decodes to Kind.Welcome", parsed.Kind, IngameBridgeProtocol.IncomingKind.Welcome);
            Check("welcome server matches", parsed.Server, "Pq0v4c3Wm9Xs1Ya7Tb2NeQ");
            Check("welcome nonce matches", parsed.Nonce, "Zk3m1Qw9Lr0aT7yUc2Vb5g");
            Check("welcome heartbeatIntervalMs matches", parsed.HeartbeatIntervalMs, 5000);
        }

        private static void DecodesAWellFormedAlert() {
            var line = "{\"v\":2,\"type\":\"alert\",\"seq\":1,\"kind\":\"valuable_loot\",\"name\":\"Mystic Coin\",\"quantity\":3,\"totalCopper\":123456,\"content\":\"Mystic Coin ×3 · 12g 34s 56c\"}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("alert decodes to Kind.Alert", parsed.Kind, IngameBridgeProtocol.IncomingKind.Alert);
            Check("alert seq matches", parsed.AlertSeq, 1L);
            Check("alert kind matches", parsed.AlertKind, "valuable_loot");
            Check("alert content matches", parsed.AlertContent, "Mystic Coin ×3 · 12g 34s 56c");
        }

        private static void DecodesAWellFormedError() {
            var line = "{\"v\":2,\"type\":\"error\",\"code\":\"auth_rejected\"}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("error decodes to Kind.Error", parsed.Kind, IngameBridgeProtocol.IncomingKind.Error);
            Check("error code matches", parsed.ErrorCode, "auth_rejected");
        }

        private static void IgnoresAKnownTypeWithAnExtraKey() {
            var line = "{\"v\":2,\"type\":\"welcome\",\"server\":\"s\",\"nonce\":\"n\",\"heartbeatIntervalMs\":5000,\"extra\":true}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("welcome with an extra key is dropped, not partially accepted", parsed.Kind, IngameBridgeProtocol.IncomingKind.Unknown);
        }

        private static void IgnoresAKnownTypeWithAMissingKey() {
            var line = "{\"v\":2,\"type\":\"welcome\",\"server\":\"s\",\"nonce\":\"n\"}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("welcome missing heartbeatIntervalMs is dropped, not defaulted", parsed.Kind, IngameBridgeProtocol.IncomingKind.Unknown);
        }

        private static void IgnoresAnUnknownType() {
            var line = "{\"v\":2,\"type\":\"future_message\",\"whatever\":1}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("an unrecognized type is ignored, not an error", parsed.Kind, IngameBridgeProtocol.IncomingKind.Unknown);
        }

        private static void FlagsANewerProtocolVersion() {
            var line = "{\"v\":3,\"type\":\"welcome\",\"server\":\"s\",\"nonce\":\"n\",\"heartbeatIntervalMs\":5000}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("v greater than 2 flags ProtocolNewer, regardless of type", parsed.Kind, IngameBridgeProtocol.IncomingKind.ProtocolNewer);
        }

        private static void RejectsADuplicateTopLevelKey() {
            var line = "{\"v\":2,\"type\":\"error\",\"code\":\"auth_rejected\",\"code\":\"capacity\"}";
            var parsed = IngameBridgeProtocol.ParseIncomingLine(Utf8Bytes(line));
            Check("a duplicate top-level key is unparseable, not \"last one wins\"", parsed.Kind, IngameBridgeProtocol.IncomingKind.Unknown);
        }

        private static void InstanceIdIsCanonicalBase64Url() {
            var id = IngameBridgeProtocol.CreateInstanceId(bytes => { for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i; });
            Check("instance id is 22 characters (16 bytes, base64url, no padding)", id.Length, 22);
            foreach (var character in id) {
                if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-' || character == '_')) {
                    Check($"instance id character '{character}' is base64url", false, true);
                }
            }
        }

        // --- The token guard (`TokenGuard.cs`) ---

        /// <summary>Made up, with the real shape: `8-4-4-4-20-4-4-4-12` hex, 72 characters.</summary>
        private const string ApiKey = "0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F90A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9";

        /// <summary>43 base64url characters, the shape `createIngameBridgeSecret` produces.</summary>
        private const string Token = "k2VnU0bq9mRjYp8tXwH3cL5sA7dF1gJ4hN6zQ0eT2uB";

        private static void RejectsAGw2ApiKeyAsTheToken() {
            Check("the API key fixture is 72 characters", ApiKey.Length, 72);
            Check("an API key is refused", TokenGuard.Check(ApiKey, out _), TokenGuard.Verdict.Gw2ApiKey);
            Check("a lowercase API key is refused", TokenGuard.Check(ApiKey.ToLowerInvariant(), out _), TokenGuard.Verdict.Gw2ApiKey);
            Check("an API key with clipboard whitespace around it is refused", TokenGuard.Check("  " + ApiKey + "\r\n", out _), TokenGuard.Verdict.Gw2ApiKey);
            Check("a refused API key leaves nothing to keep", TokenGuard.Check(ApiKey, out var kept) == TokenGuard.Verdict.Gw2ApiKey && kept == null, true);
            Check("one group short is not an API key", TokenGuard.IsGw2ApiKey(ApiKey.Substring(0, ApiKey.LastIndexOf('-'))), false);
            Check("a non-hex digit is not an API key", TokenGuard.IsGw2ApiKey("G" + ApiKey.Substring(1)), false);
            Check("the token is not an API key", TokenGuard.IsGw2ApiKey(Token), false);
        }

        private static void AcceptsA43CharacterToken() {
            Check("the token fixture is 43 characters", Token.Length, 43);
            Check("a 43-character token is usable", TokenGuard.Check(Token, out var kept), TokenGuard.Verdict.Usable);
            Check("a 43-character token is kept as is", kept, Token);
            Check("a 43-character token goes into the hello", TokenGuard.UsableOrNull(Token), Token);
        }

        private static void TrimsWhitespaceAroundTheToken() {
            Check("whitespace around a token is trimmed", TokenGuard.UsableOrNull(" \t" + Token + "\r\n"), Token);
            Check("only whitespace is an empty token", TokenGuard.Check(" \n", out var kept), TokenGuard.Verdict.Empty);
            Check("an empty token keeps an empty string", kept, string.Empty);
        }

        private static void RejectsATokenTooShortTooLongOrWithSpaces() {
            Check("31 characters is refused", TokenGuard.Check(new string('a', 31), out _), TokenGuard.Verdict.Malformed);
            Check("129 characters is refused", TokenGuard.Check(new string('a', 129), out _), TokenGuard.Verdict.Malformed);
            Check("32 characters is usable", TokenGuard.Check(new string('a', 32), out _), TokenGuard.Verdict.Usable);
            Check("128 characters is usable", TokenGuard.Check(new string('a', 128), out _), TokenGuard.Verdict.Usable);
            Check("an inner space is refused", TokenGuard.Check(new string('a', 20) + " " + new string('b', 20), out _), TokenGuard.Verdict.Malformed);
            Check("a control character is refused", TokenGuard.Check(new string('a', 40) + "\u007f", out _), TokenGuard.Verdict.Malformed);
            Check("a non-ASCII character is refused", TokenGuard.Check(new string('a', 40) + "é", out _), TokenGuard.Verdict.Malformed);
            Check("a malformed token never goes into a hello", TokenGuard.UsableOrNull(new string('a', 31)), (string)null);
        }

        private static void DiscardsAnApiKeySavedBefore() {
            var kept = TokenGuard.ScrubStored(" " + ApiKey.ToLowerInvariant() + "\n", out var discarded);
            Check("an API key saved by 0.2.0 is discarded on load", discarded, true);
            Check("an API key saved by 0.2.0 loads as an empty token", kept, string.Empty);
            kept = TokenGuard.ScrubStored(Token, out discarded);
            Check("a saved token is not discarded", discarded, false);
            Check("a saved token loads unchanged", kept, Token);
        }

        private static void NeverSendsAnApiKeyInAHello() {
            Check("an API key never becomes the hello token", TokenGuard.UsableOrNull(ApiKey), (string)null);
            var threw = false;
            try {
                IngameBridgeProtocol.EncodeHello("0.2.1", "q8Hq3n2t0dQyYf0nJ1p0Aw", ApiKey);
            } catch (InvalidOperationException ex) {
                threw = !ex.Message.Contains(ApiKey);
            }
            Check("the hello encoder refuses an API key, without echoing it", threw, true);
        }

        private static void CorrectsAChangedTokenSetting() {
            Check("an API key pasted over a token is refused", TokenGuard.Correct(ApiKey, Token, out var replacement), TokenGuard.Verdict.Gw2ApiKey);
            Check("an API key pasted over a token is cleared, not kept", replacement, string.Empty);
            TokenGuard.Correct(new string('a', 31), Token, out replacement);
            Check("a malformed value falls back to the previous token", replacement, Token);
            TokenGuard.Correct(new string('a', 31), ApiKey, out replacement);
            Check("a malformed value never falls back to an API key", replacement, string.Empty);
            TokenGuard.Correct(" " + Token + "\n", string.Empty, out replacement);
            Check("a usable value is stored trimmed", replacement, Token);
        }

        private static void TheRejectedMessageSaysWhatToDo() {
            Check("after auth_rejected the message names the button to press", TokenGuard.RejectedMessage.Contains("\"Copy token\""), true);
            Check("after auth_rejected the message says where to paste it", TokenGuard.RejectedMessage.Contains("paste it in this module's settings"), true);
        }

        private static byte[] Utf8Line(byte[] encodedLine) => encodedLine; // already the exact bytes EncodeXxx sends

        private static byte[] Utf8Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void Check(string label, byte[] actual, string expectedText) {
            var expected = Encoding.UTF8.GetBytes(expectedText);
            var actualText = Encoding.UTF8.GetString(actual);
            if (actual.Length == expected.Length) {
                var equal = true;
                for (var i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) { equal = false; break; }
                if (equal) { Console.WriteLine($"PASS  {label}"); return; }
            }
            _failures++;
            Console.WriteLine($"FAIL  {label}");
            Console.WriteLine($"      expected: {expectedText.TrimEnd('\n')}");
            Console.WriteLine($"      actual:   {actualText.TrimEnd('\n')}");
        }

        private static void Check<T>(string label, T actual, T expected) {
            if (Equals(actual, expected)) { Console.WriteLine($"PASS  {label}"); return; }
            _failures++;
            Console.WriteLine($"FAIL  {label}");
            Console.WriteLine($"      expected: {expected}");
            Console.WriteLine($"      actual:   {actual}");
        }
    }
}
