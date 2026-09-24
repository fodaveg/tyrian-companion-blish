using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Modules;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// Mirrors the in-game alerts the "Tyrian Companion" Obsidian plugin emits (valuable loot,
    /// sell/hold price signals) onto a Guild Wars 2 screen notification, and reports this addon's
    /// own view of the game (map, character, gameplay/loading/character-select) back to the
    /// plugin, so it can mark a play session without a click.
    ///
    /// This is protocol v2 (`docs/SPEC-puente-ingame.md` in the `tyrian-companion` repo, the
    /// signed spec this was built against): a bidirectional, authenticated loopback TCP
    /// connection — <see cref="IngameBridgeClient"/> owns the socket and the wire, this module
    /// owns settings and Blish HUD's own lifecycle/game-close events. It never calls the GW2 API,
    /// never reads Mumble Link beyond what <see cref="GameContextSampler"/> reports (map,
    /// character, in-gameplay), never automates anything inside the game, and never sends the
    /// plugin anything the wire contract does not define. Those limits are not style: they are
    /// what keeps this module inside ArenaNet's "utility that helps players without affecting
    /// others" carve-out in its third-party programs policy, the classification the whole in-game
    /// bridge lot was authorized under.
    /// </summary>
    [Export(typeof(Module))]
    public class TyrianCompanionBridgeModule : Module {

        private static readonly Logger Logger = Logger.GetLogger<TyrianCompanionBridgeModule>();

        /// <summary>Matches `DEFAULT_ALERT_INGAME_PORT` in the plugin's `src/core/settings.ts`.</summary>
        private const int DefaultPort = 47_823;

        /// <summary>
        /// Sent as `clientVersion` in the `hello` line, and shown to whoever reads this module's
        /// own logs; the plugin does not parse it beyond the character-class check every
        /// `clientVersion` gets. Bump this together with `manifest.json`'s `version` on a release.
        /// </summary>
        private const string ClientVersion = "0.2.0";

        private SettingEntry<bool> _enabledSetting;
        private SettingEntry<int> _portSetting;
        private SettingEntry<string> _tokenSetting;

        private IngameBridgeClient _client;
        private CancellationTokenSource _lifecycleCts;

        [ImportingConstructor]
        public TyrianCompanionBridgeModule([Import("ModuleParameters")] ModuleParameters moduleParameters)
            : base(moduleParameters) { }

        protected override void DefineSettings(SettingCollection settings) {
            _enabledSetting = settings.DefineSetting(
                "alertBridgeEnabled",
                true,
                () => "Show in-game alerts",
                () => "Connects to the Tyrian Companion Obsidian plugin, shows its loot and price alerts as a screen notification, and reports your map/character to it so it can mark a play session.");

            _portSetting = settings.DefineSetting(
                "alertBridgePort",
                DefaultPort,
                () => "Plugin port",
                () => "Loopback TCP port the Tyrian Companion plugin listens on. Must match the port set in the plugin's own settings (default 47823).");

            _tokenSetting = settings.DefineSetting(
                "alertBridgeToken",
                "",
                () => "Plugin token",
                () => "Paste the token from the plugin's settings in Obsidian (row \"Token del addon\", button \"Copiar token\"). Required: without a matching token the plugin rejects this module's connection. Never logged by this module.");
        }

        protected override void Initialize() {
            // NOOP: nothing needs preparing before settings and content managers are ready.
        }

        protected override Task LoadAsync() {
            _client = new IngameBridgeClient(
                () => _enabledSetting.Value,
                () => _portSetting.Value,
                () => _tokenSetting.Value?.Trim(),
                ShowAlert,
                Logger,
                ClientVersion);

            _lifecycleCts = new CancellationTokenSource();

            _enabledSetting.SettingChanged += OnSettingChanged;
            _portSetting.SettingChanged += OnSettingChanged;
            _tokenSetting.SettingChanged += OnSettingChanged;
            GameService.GameIntegration.Gw2Instance.Gw2Closed += OnGw2Closed;

            // Fire-and-forget on purpose: the connect/retry loop is meant to run for the module's
            // whole lifetime. Awaiting it here would mean `LoadAsync` — and so the module's
            // transition to `Loaded` — never completes; running it on `Update` would block the
            // overlay's render loop on a TCP connect. `IngameBridgeClient.RunAsync` never throws
            // on an ordinary cancellation, so there is nothing here that needs to observe the
            // task's result.
            _ = Task.Run(() => _client.RunAsync(_lifecycleCts.Token));

            return Task.CompletedTask;
        }

        protected override void Update(GameTime gameTime) {
            // NOOP: the client runs its own connect/read/send loop on a background thread and
            // marshals every notification onto the main thread itself, through `ShowAlert` below.
            // There is nothing left for this module to do once a frame.
        }

        /// <inheritdoc />
        protected override void Unload() {
            _enabledSetting.SettingChanged -= OnSettingChanged;
            _portSetting.SettingChanged -= OnSettingChanged;
            _tokenSetting.SettingChanged -= OnSettingChanged;
            GameService.GameIntegration.Gw2Instance.Gw2Closed -= OnGw2Closed;

            // Best-effort `bye reason:"addon_unload"` before the socket goes away underneath it;
            // fire this first so it has a moment's head start over the cancellation/dispose below.
            _client?.RequestGracefulBye("addon_unload");

            _lifecycleCts?.Cancel();
            _client?.Dispose();

            // All static members must be manually unset. This module keeps none: `Logger` is the
            // one static field, and it holds no per-load state of its own.
            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
            _client = null;
        }

        private void OnSettingChanged(object sender, ValueChangedEventArgs<bool> e) => _client?.WakeUp();

        private void OnSettingChanged(object sender, ValueChangedEventArgs<int> e) => _client?.WakeUp();

        private void OnSettingChanged(object sender, ValueChangedEventArgs<string> e) => _client?.WakeUp();

        /// <summary>
        /// `GameService.GameIntegration.Gw2Instance.Gw2Closed`: "the Guild Wars 2 process has
        /// terminated" — the positive evidence the spec's `bye reason:"game_exit"` requires, as
        /// opposed to this module or its connection merely going away.
        /// </summary>
        private void OnGw2Closed(object sender, System.EventArgs e) => _client?.RequestGracefulBye("game_exit");

        /// <summary>
        /// Paints `content` exactly as the plugin composed it — no recomposing it out of other
        /// fields here, matching the "both hosts show the same message" rule in
        /// `docs/SPEC-puente-ingame.md`. `icon` is left at its default (`null`): this module does
        /// not bundle any icon texture in `ref/` yet, and `ScreenNotification.ShowNotification`
        /// accepts that.
        ///
        /// Queued onto the main thread rather than called directly: <see cref="IngameBridgeClient"/>
        /// invokes this from its own background read loop, and `ScreenNotification` is a
        /// `Blish_HUD.Controls.Control` meant to be created and mutated from the game's Update
        /// thread, the same way every other Blish HUD control is.
        /// </summary>
        private void ShowAlert(string content, ScreenNotification.NotificationType type) {
            GameService.Overlay.QueueMainThreadUpdate(gameTime => ScreenNotification.ShowNotification(content, type));
        }
    }
}
