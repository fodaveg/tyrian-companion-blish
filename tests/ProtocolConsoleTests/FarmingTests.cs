using System;
using System.Text;

namespace TyrianCompanion.BlishBridge.Tests {

    internal static class FarmingTests {
        private const string Nonce = "Zk3m1Qw9Lr0aT7yUc2Vb5g";
        private const string State = "{\"v\":3,\"type\":\"farming_state\",\"tag\":\"farm1\",\"nonce\":\"" + Nonce + "\",\"seq\":1,\"ttl\":15,\"phase\":\"active\",\"err\":null,\"elapsed\":1716,\"observed\":248,\"net\":null,\"lo\":480,\"hi\":560,\"age\":18,\"slots\":8,\"slotSrc\":\"ingame\",\"slotAge\":18,\"goal\":\"bags\",\"target\":1000,\"progress\":248,\"eta\":5120,\"mf\":150,\"mfKind\":\"partial\",\"prep\":\"partial\"}";

        /// <summary>Exercises strict frame validation plus nonce, sequence and freshness without a game process.</summary>
        public static void Run() {
            var parsed = Parse(State);
            Expect(parsed.Kind == IngameBridgeProtocol.IncomingKind.FarmingState, "flat snapshot accepted");
            Expect(parsed.Farming.Observed == 248 && parsed.Farming.Slots == 8, "snapshot metrics decoded");
            Expect(Encoding.UTF8.GetByteCount(State) <= 512, "representative snapshot fits 512-byte cap");
            Expect(Parse(State + new string(' ', 512 - Encoding.UTF8.GetByteCount(State))).Kind == IngameBridgeProtocol.IncomingKind.FarmingState, "exactly 512 bytes accepted");
            Drop(State + new string(' ', 513 - Encoding.UTF8.GetByteCount(State)), "513 bytes rejected");
            foreach (var key in new[] { "elapsed", "observed", "lo", "hi", "age", "slots", "slotAge", "target", "progress", "eta", "mf" }) {
                FlatJsonLine.TryParse(State, out var record);
                var original = "\"" + key + "\":" + record[key];
                Drop(State.Replace(original, "\"" + key + "\":-1"), key + " negative rejected");
                Drop(State.Replace(original, "\"" + key + "\":2147483648"), key + " overflow rejected");
                Expect(Parse(State.Replace(original, "\"" + key + "\":null")).Kind == IngameBridgeProtocol.IncomingKind.FarmingState, key + " unknown accepted");
            }
            Expect(Parse(State.Replace("\"net\":null", "\"net\":-2147483648")).Farming.Net == int.MinValue, "signed minimum net accepted");
            Expect(Parse(State.Replace("\"observed\":248", "\"observed\":2147483647")).Farming.Observed == int.MaxValue, "maximum metric accepted");
            Drop(State.Replace("\"seq\":1", "\"seq\":0"), "zero sequence rejected");
            Drop(State.Replace("\"seq\":1", "\"seq\":2147483648"), "sequence overflow rejected");
            Drop(State.Replace("\"ttl\":15", "\"ttl\":14"), "different TTL rejected");
            foreach (var pair in new[] { new[] { "phase", "active" }, new[] { "slotSrc", "ingame" }, new[] { "goal", "bags" }, new[] { "mfKind", "partial" }, new[] { "prep", "partial" } }) {
                Drop(State.Replace("\"" + pair[0] + "\":\"" + pair[1] + "\"", "\"" + pair[0] + "\":\"arbitrary\""), pair[0] + " unknown enum rejected");
            }
            Drop(State.Replace("\"err\":null", "\"err\":\"arbitrary\""), "error enum rejected");
            foreach (var raw in new[] { "{}", "[]", "true", "1.5", "1e2" }) Drop(State.Replace("\"observed\":248", "\"observed\":" + raw), "noninteger metric " + raw + " rejected");
            Drop(State.Replace("\"v\":3", "\"v\":2"), "farm1 requires v3");
            Drop(State.Replace("\"tag\":\"farm1\"", "\"tag\":\"farm2\""), "different tag ignored");
            Drop(State.Replace("\"nonce\":\"" + Nonce + "\"", "\"nonce\":\"wrong\""), "malformed nonce ignored");
            Drop(State.Replace("\"err\":null,", ""), "missing key ignored");
            Drop(State.Replace("\"err\":null,", "\"err\":null,\"extra\":null,"), "extra key ignored");
            Drop(State.Replace("\"err\":null,", "\"err\":null,\"err\":null,"), "duplicate key ignored");
            Expect(Parse("{\"v\":3,\"type\":\"farming_cap\",\"nonce\":\"" + Nonce + "\",\"tag\":\"farm1\"}").Kind == IngameBridgeProtocol.IncomingKind.FarmingCapability, "capability decoded");
            Drop("{\"v\":2,\"type\":\"farming_cap\",\"nonce\":\"" + Nonce + "\",\"tag\":\"farm1\"}", "v2 capability ignored");
            var sub = Encoding.UTF8.GetString(IngameBridgeProtocol.EncodeFarmingSubscription(Nonce, 4));
            Expect(sub == "{\"v\":3,\"type\":\"farming_sub\",\"nonce\":\"" + Nonce + "\",\"seq\":4,\"tag\":\"farm1\"}\n", "subscription sequenced on existing counter");

            double now = 100;
            var channel = new FarmingChannel(() => now);
            Expect(!channel.Receive(parsed.Farming), "snapshot before welcome ignored");
            channel.Welcome(Nonce);
            Expect(channel.Read().Connected && !channel.Read().Capable, "older server remains connected without capability");
            Expect(!channel.Receive(parsed.Farming), "snapshot before capability ignored");
            Expect(!channel.Capability("OtherNonce0000000000000"), "wrong capability nonce ignored");
            Expect(channel.Capability(Nonce) && !channel.Capability(Nonce), "one idempotent subscription per connection");
            Expect(channel.Receive(parsed.Farming), "fresh snapshot accepted");
            Expect(!channel.Receive(parsed.Farming), "duplicate snapshot ignored");
            now = 114.999;
            Expect(channel.Read().Fresh, "snapshot fresh before 15 seconds");
            Expect(channel.Read().ReadingAge(parsed.Farming.Age) == 32, "API age grows from its supplied age");
            now = 115;
            Expect(!channel.Read().Fresh, "snapshot expires at exactly 15 seconds");
            var texts = FarmingPanelText.From(channel.Read(), false);
            Expect(texts.Eta == "ETA not available yet" && texts.Elapsed == "Duration · 28:36", "stale ETA removed and measured duration frozen");
            Expect(texts.Rate.StartsWith("Last rate"), "stale rate labeled historical");
            var seq2 = Parse(State.Replace("\"seq\":1", "\"seq\":2")).Farming;
            Expect(channel.Receive(seq2), "higher snapshot sequence renews transport freshness");
            Expect(channel.Read().ReadingAge(seq2.Age) == 18, "transport refresh uses supplied API age");
            channel.Disconnect();
            Expect(!channel.Read().Fresh && channel.Read().Snapshot != null, "disconnect immediately invalidates but preserves last reading");
            var secondNonce = "Ak3m1Qw9Lr0aT7yUc2Vb5g";
            channel.Welcome(secondNonce);
            channel.Capability(secondNonce);
            Expect(!channel.Receive(seq2), "old connection nonce never accepted after reconnect");
            Expect(!channel.Read().Fresh, "reconnect does not revalidate old snapshot");
            Expect(channel.Receive(Parse(State.Replace(Nonce, secondNonce)).Farming), "new nonce starts its own farming sequence");
            channel.Disconnect();
            channel.Welcome(secondNonce);
            channel.Capability(secondNonce);
            Expect(!channel.Read().Fresh, "reused welcome nonce cannot rejuvenate retained snapshot before new frame");
            // Farming does not encode an ack or consume the alert decoder's sequence: a lower
            // alert sequence is still parsed independently after arbitrarily many snapshots.
            var alert = Parse("{\"v\":3,\"type\":\"alert\",\"seq\":1,\"kind\":\"valuable_loot\",\"name\":\"X\",\"quantity\":1,\"totalCopper\":null,\"content\":\"X\"}");
            Expect(alert.Kind == IngameBridgeProtocol.IncomingKind.Alert && alert.AlertSeq == 1 && parsed.AlertSeq == 0, "farming and alert sequence/ACK fields independent");
            var final = Parse(State.Replace("\"phase\":\"active\"", "\"phase\":\"complete\"").Replace("\"net\":null", "\"net\":-5")).Farming;
            var finalText = FarmingPanelText.From(new FarmingView { Snapshot = final, Fresh = true, Connected = true, Capable = true }, false);
            Expect(finalText.Observed.Contains("248") && finalText.Net.Contains("-5"), "observed and signed net remain separate at close");
            var activeError = Parse(State.Replace("\"err\":null", "\"err\":\"observe\"")).Farming;
            var activeErrorText = FarmingPanelText.From(new FarmingView { Snapshot = activeError, Fresh = true, Connected = true, Capable = true }, false);
            Expect(activeErrorText.Phase.Contains("Measuring") && activeErrorText.Phase.Contains("Could not update"), "observation error visible independently of active phase");
            Expect(activeErrorText.Eta == "ETA not available yet", "observation error suppresses a supplied numeric bag estimate");
            final.Error = "save";
            Expect(FarmingPanelText.From(new FarmingView { Snapshot = final, Fresh = true }, false).Phase.Contains("Could not save"), "save error remains visible on complete phase");
            Expect(FarmingPanelText.From(new FarmingView { Snapshot = activeError, Fresh = false }, false).Phase.Contains("Could not update"), "stale snapshot preserves specific error");
            Expect(FarmingPanelText.From(new FarmingView { Snapshot = final, Fresh = true }, false).Eta == "ETA not available yet", "nonactive session suppresses estimate even when supplied");
            final.Progress = final.Target;
            final.Eta = null;
            Expect(FarmingPanelText.From(new FarmingView { Snapshot = final, Fresh = true }, false).Eta == "Goal reached", "reached bag goal survives unavailable ETA");
            final.Progress = 248;
            final.Eta = 5120;
            final.Phase = "active";
            final.Error = null;
            final.Goal = "duration";
            Expect(FarmingPanelText.From(new FarmingView { Snapshot = final, Fresh = true }, false).Eta.StartsWith("Remaining"), "duration goal uses countdown rather than approximate ETA");
            final.MagicFind = null;
            final.MagicFindKind = "unknown";
            Expect(FarmingPanelText.From(new FarmingView { Snapshot = final, Fresh = true }, false).Preparation.StartsWith("Magic Find · — · No reading"), "unknown Magic Find never labeled a partial known reading");
            Console.WriteLine("All farming checks passed.");
        }

        private static IngameBridgeProtocol.IncomingLine Parse(string text) => IngameBridgeProtocol.ParseIncomingLine(Encoding.UTF8.GetBytes(text));
        private static void Drop(string text, string label) => Expect(Parse(text).Kind == IngameBridgeProtocol.IncomingKind.Unknown, label);
        private static void Expect(bool condition, string label) {
            if (!condition) throw new InvalidOperationException("Farming check failed: " + label);
            Console.WriteLine("PASS  " + label);
        }
    }
}
