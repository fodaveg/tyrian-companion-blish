using System;
using System.Text;
using TyrianCompanion.BlishBridge;

namespace TyrianCompanion.BlishBridge.Tests {

    /// <summary>
    /// Exercises the v2 wire contract from `docs/SPEC-puente-ingame.md` without a Blish HUD host:
    /// encodes one line of each addon-to-plugin message type and diffs it byte-for-byte against
    /// the spec's own "Ejemplo completo" trace, then round-trips a handful of plugin-to-addon
    /// lines through the decoder — a well-formed one of each type, and the tolerance cases the
    /// addon contract requires (an extra key, an unknown `type`, a newer `v`). Prints the first
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
