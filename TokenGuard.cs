using System;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// What this module lets the "Plugin token" setting hold, decided before the value is kept or
    /// reaches a `hello`. Pure, like <see cref="IngameBridgeProtocol"/>: no <c>SettingEntry</c>, no
    /// Blish HUD type, so `tests/ProtocolConsoleTests` links it and runs it on its own.
    ///
    /// Why it exists: on 2026-09-24 a Guild Wars 2 API key got pasted into the token field of the
    /// Nexus addon, which saved it in clear and sent it to the plugin as the token; the plugin only
    /// answered a bare `auth_rejected`. This module has the same field, next to where players keep
    /// their API key. An API key is a credential for the player's account, not for this bridge, so a
    /// value with its shape is refused, cleared if it was saved by 0.2.0, and never goes out. A value
    /// outside the plugin's own format (`isUsableIngameBridgeSecret`: 32 to 128 printable ASCII
    /// characters, no spaces) is refused too, with its own message, instead of failing the next
    /// connection with the same bare rejection.
    ///
    /// Nothing here returns or formats the rejected value: a caller that logs a <see cref="Verdict"/>
    /// or a message logs nothing secret.
    /// </summary>
    internal static class TokenGuard {

        public const int MinCharacters = 32;
        public const int MaxCharacters = 128;

        /// <summary>Hex digits per group of a Guild Wars 2 API key: `8-4-4-4-20-4-4-4-12`, 72 characters with the dashes.</summary>
        private static readonly int[] Gw2ApiKeyGroups = { 8, 4, 4, 4, 20, 4, 4, 4, 12 };

        public enum Verdict {
            /// <summary>Nothing pasted: clears the token on purpose.</summary>
            Empty,
            /// <summary>Something the plugin would accept as its token.</summary>
            Usable,
            /// <summary>The shape of a Guild Wars 2 API key.</summary>
            Gw2ApiKey,
            /// <summary>Outside the plugin's format.</summary>
            Malformed,
        }

        public const string Gw2ApiKeyMessage =
            "Tyrian Companion: that is your Guild Wars 2 API key, not the token. In Obsidian: Tyrian Companion settings > \"Copy token\" (\"Copiar token\"), then paste it in this module's settings.";

        public const string MalformedMessage =
            "Tyrian Companion: that is not the token, which has 32 to 128 characters and no spaces. In Obsidian: Tyrian Companion settings > \"Copy token\" (\"Copiar token\"), then paste it in this module's settings.";

        public const string DiscardedApiKeyMessage =
            "Tyrian Companion: the saved token was your Guild Wars 2 API key, so it has been removed. In Obsidian: Tyrian Companion settings > \"Copy token\" (\"Copiar token\"), then paste it in this module's settings.";

        /// <summary>After `auth_rejected`: what to do, not only what happened.</summary>
        public const string RejectedMessage =
            "Tyrian Companion: the plugin rejected the token. In Obsidian: Tyrian Companion settings > \"Copy token\" (\"Copiar token\"), then paste it in this module's settings.";

        /// <summary>No usable token saved, so the module does not connect.</summary>
        public const string MissingMessage =
            "Tyrian Companion: no token yet. In Obsidian: Tyrian Companion settings > \"Copy token\" (\"Copiar token\"), then paste it in this module's settings.";

        /// <summary>
        /// <c>true</c> if <paramref name="value"/> has the exact shape of a Guild Wars 2 API key: hex
        /// groups of `8-4-4-4-20-4-4-4-12`, in either case. Does not trim; <see cref="Check"/> does.
        /// </summary>
        public static bool IsGw2ApiKey(string value) {
            if (value == null) return false;
            var groups = value.Split('-');
            if (groups.Length != Gw2ApiKeyGroups.Length) return false;
            for (var index = 0; index < groups.Length; index++) {
                if (groups[index].Length != Gw2ApiKeyGroups[index]) return false;
                foreach (var character in groups[index]) {
                    if (!Uri.IsHexDigit(character)) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Classifies a pasted value after trimming the whitespace around it (a copy from Obsidian or a
        /// browser often drags a newline or a space along). <paramref name="trimmed"/> is the value to
        /// keep when the verdict is <see cref="Verdict.Usable"/> or <see cref="Verdict.Empty"/>, and
        /// <c>null</c> otherwise, so a refused value cannot be picked up from it by mistake.
        /// </summary>
        public static Verdict Check(string raw, out string trimmed) {
            var candidate = (raw ?? string.Empty).Trim();
            trimmed = null;
            if (candidate.Length == 0) {
                trimmed = string.Empty;
                return Verdict.Empty;
            }
            if (IsGw2ApiKey(candidate)) return Verdict.Gw2ApiKey;
            if (!IsPluginFormat(candidate)) return Verdict.Malformed;
            trimmed = candidate;
            return Verdict.Usable;
        }

        /// <summary>The token to send in a `hello`, or <c>null</c> if the setting holds nothing usable (empty, an API key, or malformed).</summary>
        public static string UsableOrNull(string raw) {
            return Check(raw, out var trimmed) == Verdict.Usable ? trimmed : null;
        }

        /// <summary>
        /// What the setting should hold after the user changed it from <paramref name="previousValue"/>
        /// to <paramref name="newValue"/>: the trimmed value if it is usable or empty; nothing at all
        /// for an API key; and, for a malformed value, the previous one, as if the change had not been
        /// saved (unless the previous one was an API key too, which is cleared).
        /// </summary>
        public static Verdict Correct(string newValue, string previousValue, out string replacement) {
            var verdict = Check(newValue, out var trimmed);
            switch (verdict) {
                case Verdict.Usable:
                case Verdict.Empty:
                    replacement = trimmed;
                    break;
                case Verdict.Gw2ApiKey:
                    replacement = string.Empty;
                    break;
                default:
                    replacement = IsGw2ApiKey((previousValue ?? string.Empty).Trim()) ? string.Empty : previousValue ?? string.Empty;
                    break;
            }
            return verdict;
        }

        /// <summary>
        /// What a stored value loads as: unchanged, or empty if it has the shape of an API key (what
        /// 0.2.0 saved when one was pasted). <paramref name="discarded"/> says which, so the caller can
        /// write the empty value back and tell the player why the token is gone.
        /// </summary>
        public static string ScrubStored(string stored, out bool discarded) {
            discarded = IsGw2ApiKey((stored ?? string.Empty).Trim());
            return discarded ? string.Empty : stored;
        }

        public static string MessageFor(Verdict verdict) {
            switch (verdict) {
                case Verdict.Gw2ApiKey: return Gw2ApiKeyMessage;
                case Verdict.Malformed: return MalformedMessage;
                default: return null;
            }
        }

        /// <summary>The plugin's own rule: 32 to 128 printable ASCII characters (0x21 to 0x7E), no spaces.</summary>
        private static bool IsPluginFormat(string value) {
            if (value.Length < MinCharacters || value.Length > MaxCharacters) return false;
            foreach (var character in value) {
                if (character < 0x21 || character > 0x7e) return false;
            }
            return true;
        }
    }
}
