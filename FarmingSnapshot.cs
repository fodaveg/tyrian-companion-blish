using System;
using System.Collections.Generic;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// One farm1 snapshot. Values are supplied by the plugin; the addon never estimates loot,
    /// elapsed time, Magic Find buffs or an ETA from its own game observations.
    /// </summary>
    internal sealed class FarmingSnapshot {
        public string Nonce;
        public int Seq;
        public string Phase;
        public string Error;
        public int? Elapsed, Observed, Net, RateLow, RateHigh, Age, Slots, SlotAge;
        public string SlotSource;
        public string Goal;
        public int? Target, Progress, Eta, MagicFind;
        public string MagicFindKind;
        public string Preparation;

        private static readonly string[] Keys = {
            "v", "type", "tag", "nonce", "seq", "ttl", "phase", "err", "elapsed", "observed",
            "net", "lo", "hi", "age", "slots", "slotSrc", "slotAge", "goal", "target",
            "progress", "eta", "mf", "mfKind", "prep",
        };

        /// <summary>Exact keys, bounded integers and closed enums; malformed snapshots are ignored.</summary>
        public static bool TryParse(Dictionary<string, object> record, out FarmingSnapshot snapshot) {
            snapshot = null;
            if (record.Count != Keys.Length) return false;
            foreach (var key in Keys) if (!record.ContainsKey(key)) return false;
            if (!Equals(record["v"], 3L) || !Equals(record["tag"], "farm1") ||
                !Equals(record["ttl"], 15L) || !(record["nonce"] is string nonce) || !ValidNonce(nonce)) return false;
            if (!(record["seq"] is long seq) || seq < 1 || seq > int.MaxValue) return false;
            if (!OneOf(record["phase"], "idle", "starting", "active", "stopping", "provisional", "complete", "error", "abandoned") ||
                (record["err"] != null && !OneOf(record["err"], "start", "observe", "stop", "save", "other")) ||
                !OneOf(record["slotSrc"], "ingame", "recent", "unknown") ||
                !OneOf(record["goal"], "none", "bags", "duration") ||
                !OneOf(record["mfKind"], "partial", "unknown") ||
                !OneOf(record["prep"], "partial", "attention", "unknown")) return false;
            var value = new FarmingSnapshot { Nonce = nonce, Seq = (int)seq, Phase = (string)record["phase"],
                Error = (string)record["err"], SlotSource = (string)record["slotSrc"], Goal = (string)record["goal"],
                MagicFindKind = (string)record["mfKind"], Preparation = (string)record["prep"] };
            if (!Number(record["elapsed"], false, out value.Elapsed) || !Number(record["observed"], false, out value.Observed) ||
                !Number(record["net"], true, out value.Net) || !Number(record["lo"], false, out value.RateLow) ||
                !Number(record["hi"], false, out value.RateHigh) || !Number(record["age"], false, out value.Age) ||
                !Number(record["slots"], false, out value.Slots) || !Number(record["slotAge"], false, out value.SlotAge) ||
                !Number(record["target"], false, out value.Target) || !Number(record["progress"], false, out value.Progress) ||
                !Number(record["eta"], false, out value.Eta) || !Number(record["mf"], false, out value.MagicFind)) return false;
            snapshot = value;
            return true;
        }

        internal static bool ValidNonce(string nonce) {
            if (nonce.Length != 22) return false;
            foreach (var c in nonce) {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            }
            return true;
        }

        private static bool Number(object raw, bool signed, out int? value) {
            value = null;
            if (raw == null) return true;
            if (!(raw is long n) || n > int.MaxValue || n < (signed ? int.MinValue : 0)) return false;
            value = (int)n;
            return true;
        }

        private static bool OneOf(object raw, params string[] values) {
            if (!(raw is string text)) return false;
            return Array.IndexOf(values, text) >= 0;
        }
    }
}
