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
    /// sell/hold price signals) onto a Guild Wars 2 screen notification, so they are visible while
    /// the game has focus.
    ///
    /// This module is a one-way TCP <em>client</em>, nothing more. `docs/SPEC-puente-ingame.md` in
    /// the `tyrian-companion` repo — the signed spec this was built against — fixes the wire
    /// contract: the plugin opens a loopback TCP server, this module connects to it, sends exactly
    /// one line on connect (its `hello`, built in <see cref="IngameAlertClient.SendHelloAsync"/>),
    /// and after that only ever reads. It never sends anything else to the plugin, never reads
    /// Mumble Link (even though Blish HUD has it on hand via <c>GameService.Gw2Mumble</c>), never
    /// calls the GW2 API, and never automates anything inside the game. Those three "never"s are
    /// not style: they are what keeps this module inside ArenaNet's "utility that helps players
    /// without affecting others" carve-out in its third-party programs policy, which is the
    /// classification the whole in-game bridge lot was authorized under.
    /// </summary>
    [Export(typeof(Module))]
    public class TyrianCompanionBridgeModule : Module {

        private static readonly Logger Logger = Logger.GetLogger<TyrianCompanionBridgeModule>();

        /// <summary>Matches `DEFAULT_ALERT_INGAME_PORT` in the plugin's `src/core/settings.ts`.</summary>
        private const int DefaultPort = 47_823;

        /// <summary>
        /// Sent as `clientVersion` in the `hello` line. The plugin does not parse or validate it
        /// (`alert-ingame-server.ts` only checks the hello line's byte length and that it is
        /// exactly one line); it exists for whoever reads the plugin's own logs. Bump this
        /// together with `manifest.json`'s `version` on a release.
        /// </summary>
        private const string ClientVersion = "0.1.0";

        private SettingEntry<bool> _enabledSetting;
        private SettingEntry<int> _portSetting;

        private IngameAlertClient _client;
        private CancellationTokenSource _lifecycleCts;

        [ImportingConstructor]
        public TyrianCompanionBridgeModule([Import("ModuleParameters")] ModuleParameters moduleParameters)
            : base(moduleParameters) { }

        protected override void DefineSettings(SettingCollection settings) {
            _enabledSetting = settings.DefineSetting(
                "alertBridgeEnabled",
                true,
                () => "Show in-game alerts",
                () => "Connects to the Tyrian Companion Obsidian plugin and shows its loot and price alerts as a screen notification.");

            _portSetting = settings.DefineSetting(
                "alertBridgePort",
                DefaultPort,
                () => "Plugin port",
                () => "Loopback TCP port the Tyrian Companion plugin listens on. Must match the port set in the plugin's own settings (default 47823).");
        }

        protected override void Initialize() {
            // NOOP: nothing needs preparing before settings and content managers are ready.
        }

        protected override Task LoadAsync() {
            _client = new IngameAlertClient(
                () => _enabledSetting.Value,
                () => _portSetting.Value,
                ShowAlert,
                Logger,
                ClientVersion);

            _lifecycleCts = new CancellationTokenSource();

            _enabledSetting.SettingChanged += OnEnabledChanged;
            _portSetting.SettingChanged += OnPortChanged;

            // Fire-and-forget on purpose: the connect/retry loop is meant to run for the module's
            // whole lifetime. Awaiting it here would mean `LoadAsync` — and so the module's
            // transition to `Loaded` — never completes; running it on `Update` would block the
            // overlay's render loop on a TCP connect. `IngameAlertClient.RunAsync` never throws on
            // an ordinary cancellation, so there is nothing here that needs to observe the task's
            // result.
            _ = Task.Run(() => _client.RunAsync(_lifecycleCts.Token));

            return Task.CompletedTask;
        }

        protected override void Update(GameTime gameTime) {
            // NOOP: the client runs its own connect/read loop on a background thread and marshals
            // every notification onto the main thread itself, through `ShowAlert` below. There is
            // nothing left for this module to do once a frame.
        }

        /// <inheritdoc />
        protected override void Unload() {
            _enabledSetting.SettingChanged -= OnEnabledChanged;
            _portSetting.SettingChanged -= OnPortChanged;

            _lifecycleCts?.Cancel();
            _client?.Dispose();

            // All static members must be manually unset. This module keeps none: `Logger` is the
            // one static field, and it holds no per-load state of its own.
            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
            _client = null;
        }

        private void OnEnabledChanged(object sender, ValueChangedEventArgs<bool> e) => _client?.WakeUp();

        private void OnPortChanged(object sender, ValueChangedEventArgs<int> e) => _client?.WakeUp();

        /// <summary>
        /// Paints `content` exactly as the plugin composed it — no recomposing it out of other
        /// fields here, matching the "both hosts show the same message" rule in
        /// `docs/SPEC-puente-ingame.md`. `icon` is left at its default (`null`): this module does
        /// not bundle any icon texture in `ref/` yet, and `ScreenNotification.ShowNotification`
        /// accepts that.
        ///
        /// Queued onto the main thread rather than called directly: <see cref="IngameAlertClient"/>
        /// invokes this from its own background read loop, and `ScreenNotification` is a
        /// `Blish_HUD.Controls.Control` meant to be created and mutated from the game's Update
        /// thread, the same way every other Blish HUD control is.
        /// </summary>
        private void ShowAlert(string content, ScreenNotification.NotificationType type) {
            GameService.Overlay.QueueMainThreadUpdate(gameTime => ScreenNotification.ShowNotification(content, type));
        }
    }
}
