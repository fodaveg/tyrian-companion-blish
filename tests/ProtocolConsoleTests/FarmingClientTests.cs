using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Blish_HUD;

namespace TyrianCompanion.BlishBridge.Tests {
    internal static class FarmingClientTests {
        /// <summary>Dispatch through the real client: farming must never queue an alert ACK or steal alert dedupe.</summary>
        public static void Run() {
            const string nonce = "Zk3m1Qw9Lr0aT7yUc2Vb5g";
            var farming = new FarmingChannel(() => 0);
            var displayed = 0;
            using (var client = new IngameBridgeClient(() => true, () => 47823, () => "", (text, type) => displayed++, new Logger(), "0.4.0", farming)) {
                var stateType = typeof(IngameBridgeClient).GetNestedType("ConnectionState", BindingFlags.NonPublic);
                var state = Activator.CreateInstance(stateType, true);
                var handler = typeof(IngameBridgeClient).GetMethod("HandleIncomingLine", BindingFlags.Instance | BindingFlags.NonPublic);
                Action<string> receive = line => handler.Invoke(client, new[] { state, new List<byte>(Encoding.UTF8.GetBytes(line)) });
                var queue = (Queue<long>)stateType.GetField("PendingAlertAcks").GetValue(state);
                receive("{\"v\":3,\"type\":\"welcome\",\"server\":\"server\",\"nonce\":\"" + nonce + "\",\"heartbeatIntervalMs\":5000}");
                Expect(queue.Count == 0 && !farming.Read().Capable, "legacy welcome leaves alert channel working and farming absent");
                receive("{\"v\":3,\"type\":\"farming_cap\",\"nonce\":\"" + nonce + "\",\"tag\":\"farm1\"}");
                receive("{\"v\":3,\"type\":\"live_cap\",\"nonce\":\"" + nonce + "\",\"tag\":\"live1\"}");
                Expect(queue.Count == 0 && displayed == 0 && farming.Read().Snapshot == null, "live producer capability ignored by Blish without corrupting farm1 or alert state");
                var pending = stateType.GetField("PendingFarmingSubscription");
                Expect((bool)pending.GetValue(state), "capability queues subscription on real client");
                pending.SetValue(state, false); // represent the normal sender having consumed it
                receive("{\"v\":3,\"type\":\"farming_cap\",\"nonce\":\"" + nonce + "\",\"tag\":\"farm1\"}");
                Expect(!(bool)pending.GetValue(state), "repeated capability does not queue another subscription");
                receive("{\"v\":3,\"type\":\"farming_state\",\"tag\":\"farm1\",\"nonce\":\"" + nonce + "\",\"seq\":2147483647,\"ttl\":15,\"phase\":\"active\",\"err\":null,\"elapsed\":0,\"observed\":0,\"net\":null,\"lo\":null,\"hi\":null,\"age\":0,\"slots\":0,\"slotSrc\":\"ingame\",\"slotAge\":0,\"goal\":\"none\",\"target\":null,\"progress\":null,\"eta\":null,\"mf\":null,\"mfKind\":\"unknown\",\"prep\":\"unknown\"}");
                Expect(queue.Count == 0 && displayed == 0 && farming.Read().Fresh, "farming snapshot produces no notification or ACK");
                var alert = "{\"v\":3,\"type\":\"alert\",\"seq\":1,\"kind\":\"valuable_loot\",\"name\":\"X\",\"quantity\":1,\"totalCopper\":null,\"content\":\"X\"}";
                receive(alert);
                receive(alert);
                Expect(displayed == 1 && queue.Count == 1 && queue.Peek() == 1, "lower alert sequence still displays and ACKs exactly once");
                var next = stateType.GetMethod("NextOutboundSeq");
                Expect((long)next.Invoke(state, null) == 0 && (long)next.Invoke(state, null) == 1, "subscription/context/ACK use one consecutive outgoing counter");
            }
            Console.WriteLine("All farming client checks passed.");
        }

        private static void Expect(bool condition, string label) {
            if (!condition) throw new InvalidOperationException("Client check failed: " + label);
            Console.WriteLine("PASS  " + label);
        }
    }
}
