using System.Runtime.Serialization;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// One alert line, plugin to addon. Mirrors the `AlertIngamePayload` interface in the
    /// plugin's `src/alerts/alert-ingame.ts`, which is the signed shape of this JSON — this type
    /// exists to deserialize it, not to redefine it.
    ///
    /// <see cref="Kind"/>, <see cref="Quantity"/> and <see cref="TotalCopper"/> are declared for
    /// fidelity to the wire contract even though this module never reads <see cref="Name"/>,
    /// <see cref="Quantity"/> or <see cref="TotalCopper"/> itself: `Content` already carries the
    /// composed line both hosts paint verbatim, and `Kind` only selects a
    /// <see cref="Blish_HUD.Controls.ScreenNotification.NotificationType"/>. See the spec's
    /// module header comment in `alert-ingame.ts` for why: a fourth, already-composed string
    /// input is the one shape this module must never be handed, so there is nothing here that
    /// recomposes a message out of the other fields.
    ///
    /// <see cref="DataContractJsonSerializer"/> ignores JSON members this type does not declare
    /// and leaves absent members at their default value, so this class does not have to defend
    /// itself against a slightly-larger or slightly-different future payload; `IngameAlertClient`
    /// is what decides, after deserializing, whether the result is usable (see its `HandleLine`).
    /// </summary>
    [DataContract]
    internal sealed class AlertMessage {

        [DataMember(Name = "v")]
        public int V { get; set; }

        /// <summary>
        /// A per-connection dedupe counter, not `alertId`. Optional on the wire: a payload built
        /// without a sequence number (the plugin's `alertIngamePayload` without its second
        /// argument) omits the key entirely rather than sending it as `null`, so this has to stay
        /// nullable to tell "absent" apart from "zero".
        /// </summary>
        [DataMember(Name = "seq")]
        public long? Seq { get; set; }

        [DataMember(Name = "kind")]
        public string Kind { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "quantity")]
        public int Quantity { get; set; }

        [DataMember(Name = "totalCopper")]
        public long? TotalCopper { get; set; }

        /// <summary>The line both hosts render as-is. See the type header: never recomposed here.</summary>
        [DataMember(Name = "content")]
        public string Content { get; set; }
    }

    /// <summary>
    /// The one line this module ever sends, and only once, right after connecting. Mirrors the
    /// spec's addon-to-plugin contract exactly: `{"v":1,"client":"blish","clientVersion":"..."}`.
    /// </summary>
    [DataContract]
    internal sealed class HelloMessage {

        [DataMember(Name = "v")]
        public int V { get; set; }

        [DataMember(Name = "client")]
        public string Client { get; set; }

        [DataMember(Name = "clientVersion")]
        public string ClientVersion { get; set; }
    }
}
