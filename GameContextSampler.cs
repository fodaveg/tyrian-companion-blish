using Blish_HUD;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// Reads Blish HUD's own <see cref="GameService"/> singletons into an <see cref="IngameGameContext"/>
    /// — the one place in this module that touches Mumble Link, and only the three fields
    /// `docs/SPEC-puente-ingame.md` fixes: map, character, and whether the player is in gameplay,
    /// on a loading screen, or at character select. Never inventory, never coordinates, never
    /// combat, never the account: that ceiling is the spec's, not a limitation of what
    /// <c>GameService.Gw2Mumble</c> happens to expose.
    /// </summary>
    internal static class GameContextSampler {

        /// <summary>
        /// <c>GameService.GameIntegration.Gw2Instance.IsInGame</c> is Blish HUD's own name for
        /// "actively in game" (its doc comment: "If false it indicates we're on a loading screen,
        /// in a cinematic, or on the character selection screen") — it does not by itself
        /// distinguish loading from character select, so this method adds one signal Mumble Link
        /// already carries: a character name is only populated once one has been chosen. That
        /// mapping is this repo's own decision, not the spec's (which fixes only "what is sent and
        /// when", per its own words); it has not been verified against a live client (README's
        /// Windows QA item), so a wrong classification of "loading" vs. "character_select" is
        /// exactly what a build 14 QA pass would catch.
        /// </summary>
        public static IngameGameContext Sample() {
            var mumble = GameService.Gw2Mumble;
            if (mumble == null || !mumble.IsAvailable) return new IngameGameContext("character_select", null, null);

            var character = NormalizeCharacterName(mumble.PlayerCharacter?.Name);
            var mapId = NormalizeMapId(mumble.CurrentMap?.Id);

            if (GameService.GameIntegration?.Gw2Instance != null && GameService.GameIntegration.Gw2Instance.IsInGame) {
                return new IngameGameContext("gameplay", mapId, character);
            }

            return character != null
                ? new IngameGameContext("loading", mapId, character)
                : new IngameGameContext("character_select", null, null);
        }

        /// <summary>
        /// <c>null</c>/empty means "no character yet" (character select or very first tick before
        /// Mumble Link populates). Trimmed and bounded to 32 characters (the spec's
        /// `INGAME_CHARACTER_NAME_MAX_CHARACTERS`) defensively — Guild Wars 2 itself caps names
        /// well below that — because this value is about to become an untrusted-by-the-plugin wire
        /// field, not because a legitimate name is expected to need it.
        /// </summary>
        private static string NormalizeCharacterName(string name) {
            if (string.IsNullOrEmpty(name)) return null;
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return null;
            const int maxCharacters = 32;
            return trimmed.Length > maxCharacters ? trimmed.Substring(0, maxCharacters) : trimmed;
        }

        /// <summary>
        /// <c>Gw2Mumble.CurrentMap.Id</c> reads <c>0</c> before Mumble Link has ever populated a
        /// map (matching the spec's "null si el anfitrión no tiene mapa"); anything else is passed
        /// through as-is.
        /// </summary>
        private static int? NormalizeMapId(int? mapId) {
            if (!mapId.HasValue || mapId.Value <= 0) return null;
            return mapId;
        }
    }
}
