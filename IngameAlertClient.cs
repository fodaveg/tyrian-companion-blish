using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Controls;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// The one-way loopback TCP client. Connects to the Tyrian Companion plugin's server, sends
    /// exactly one line (its `hello`), and after that only ever reads — there is no method here
    /// a caller could use to write to the socket a second time. Runs its own connect/read loop on
    /// a background thread for as long as the module is loaded; never touches Blish HUD's render
    /// loop directly, and never blocks it: <see cref="RunAsync"/> is meant to be started with
    /// <c>Task.Run</c>, not awaited by <c>Update</c>.
    ///
    /// Independent of Blish HUD beyond <see cref="Logger"/> (for its own diagnostics) and
    /// <see cref="ScreenNotification.NotificationType"/> (the shape the caller's alert callback
    /// expects) — it does not touch <c>SettingEntry</c> or any other Blish type, so the settings
    /// wiring stays entirely in <see cref="TyrianCompanionBridgeModule"/>.
    /// </summary>
    internal sealed class IngameAlertClient : IDisposable {

        /// <summary>H8's reconnect table, reused verbatim per `docs/SPEC-puente-ingame.md`.</summary>
        private static readonly int[] BackoffDelaysMs = { 250, 500, 1_000, 2_000, 5_000 };

        /// <summary>How often the loop rechecks the enabled flag while the bridge is turned off.</summary>
        private const int DisabledPollIntervalMs = 1_000;

        /// <summary>The wire contract's cap on an incoming alert line, from `alert-ingame.ts`.</summary>
        private const int MaxIncomingLineBytes = 512;

        /// <summary>The wire contract's cap on the outgoing `hello` line, from `alert-ingame.ts`.</summary>
        private const int MaxHelloBytes = 128;

        private const string LoopbackHost = "127.0.0.1";

        private static readonly DataContractJsonSerializer AlertSerializer = new DataContractJsonSerializer(typeof(AlertMessage));
        private static readonly DataContractJsonSerializer HelloSerializer = new DataContractJsonSerializer(typeof(HelloMessage));

        private readonly Func<bool> _enabledProvider;
        private readonly Func<int> _portProvider;
        private readonly Action<string, ScreenNotification.NotificationType> _onAlert;
        private readonly Logger _logger;
        private readonly string _clientVersion;

        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private readonly object _syncLock = new object();

        private TcpClient _activeClient;
        private long? _lastSeq;
        private bool _outdatedWarningShown;
        private bool _disposed;

        public IngameAlertClient(
            Func<bool> enabledProvider,
            Func<int> portProvider,
            Action<string, ScreenNotification.NotificationType> onAlert,
            Logger logger,
            string clientVersion) {
            _enabledProvider = enabledProvider;
            _portProvider = portProvider;
            _onAlert = onAlert;
            _logger = logger;
            _clientVersion = clientVersion;
        }

        /// <summary>
        /// Runs until <paramref name="token"/> is cancelled. Never throws on a normal shutdown:
        /// cancellation ends the loop, it does not fault the returned task.
        /// </summary>
        public async Task RunAsync(CancellationToken token) {
            using (token.Register(AbortActiveConnection)) {
                var attempt = 0;

                while (!token.IsCancellationRequested) {
                    if (!_enabledProvider()) {
                        await WaitAsync(DisabledPollIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    var port = _portProvider();

                    try {
                        using (var tcpClient = new TcpClient()) {
                            lock (_syncLock) { _activeClient = tcpClient; }

                            await tcpClient.ConnectAsync(LoopbackHost, port).ConfigureAwait(false);
                            _logger.Debug("Connected to the Tyrian Companion plugin on {0}:{1}.", LoopbackHost, port);

                            await SendHelloAsync(tcpClient, token).ConfigureAwait(false);

                            // A completed connect (hello accepted, at least one byte or an orderly
                            // close later) resets the saturated backoff — a connection that drops
                            // after running for a while should not be punished with the 5 s tail
                            // of the table on its very next attempt.
                            attempt = 0;

                            await ReadLoopAsync(tcpClient, token).ConfigureAwait(false);
                        }
                    } catch (Exception ex) when (!token.IsCancellationRequested) {
                        // A SocketException because nothing is listening yet is the expected
                        // steady state before Obsidian starts (`docs/SPEC-puente-ingame.md`: "the
                        // user can start Blish before Obsidian"). Anything else — a reset mid-read,
                        // a rejected hello — lands here too, and gets the same answer: log it
                        // quietly and let the backoff below retry. This is a client with nothing
                        // to report to the player when the other end is not there yet; only an
                        // actual alert reaches `ScreenNotification`.
                        _logger.Debug(ex, "The in-game alert bridge could not stay connected on port {0}.", port);
                    } catch (Exception) {
                        // Cancellation (module Unload) tore the socket down from under us via
                        // AbortActiveConnection; this is a shutdown, not a failure, so it is not
                        // logged as one. The loop condition below ends the run.
                    } finally {
                        lock (_syncLock) { _activeClient = null; }
                    }

                    if (token.IsCancellationRequested) break;

                    var delayMs = BackoffDelaysMs[Math.Min(attempt, BackoffDelaysMs.Length - 1)];
                    attempt++;
                    await WaitAsync(delayMs, token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Called when the enabled flag or the port setting changes, so a player editing either
        /// mid-session does not have to wait out a stale backoff delay or a 1 s disabled-poll tick.
        /// </summary>
        public void WakeUp() {
            if (_disposed) return;
            try {
                if (_wake.CurrentCount == 0) _wake.Release();
            } catch (ObjectDisposedException) {
                // Racing Dispose(); nothing left to wake.
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            AbortActiveConnection();
            _wake.Dispose();
        }

        private void AbortActiveConnection() {
            lock (_syncLock) {
                try { _activeClient?.Close(); } catch (Exception) { /* best-effort teardown */ }
            }
        }

        private async Task WaitAsync(int milliseconds, CancellationToken token) {
            var delayTask = Task.Delay(milliseconds, token);
            var wakeTask = _wake.WaitAsync(token);
            // Whichever finishes first — the backoff/poll elapsing, or a settings change waking us
            // early. Not observing the loser's exception here is deliberate: a cancelled `token`
            // is checked by every caller of `WaitAsync` right after it returns.
            await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends the addon's one and only line. `docs/SPEC-puente-ingame.md`: "Addon → plugin,
        /// exactly one line at connect, 128 bytes at most, and after that the plugin stops reading."
        /// </summary>
        private async Task SendHelloAsync(TcpClient tcpClient, CancellationToken token) {
            var hello = new HelloMessage { V = 1, Client = "blish", ClientVersion = _clientVersion };

            byte[] body;
            using (var buffer = new MemoryStream()) {
                HelloSerializer.WriteObject(buffer, hello);
                body = buffer.ToArray();
            }

            var line = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, line, 0, body.Length);
            line[body.Length] = (byte)'\n';

            if (line.Length > MaxHelloBytes) {
                // Should not happen for this fixed three-field object; fail loudly in the log
                // instead of silently sending a line the plugin's framer is built to reject.
                throw new InvalidOperationException(
                    $"The hello line is {line.Length} bytes, over the {MaxHelloBytes}-byte cap the spec fixes.");
            }

            var stream = tcpClient.GetStream();
            await stream.WriteAsync(line, 0, line.Length, token).ConfigureAwait(false);

            // Nothing below this point ever writes to `stream` again — that is the whole
            // one-direction guarantee at this end of the wire.
        }

        /// <summary>
        /// Reads lines off <paramref name="tcpClient"/>'s stream until it closes or
        /// <paramref name="token"/> cancels. A line over <see cref="MaxIncomingLineBytes"/> is
        /// dropped in full (buffered bytes discarded, then resynced on the next `\n`) instead of
        /// being buffered without bound or tearing the connection down over one bad message.
        /// </summary>
        private async Task ReadLoopAsync(TcpClient tcpClient, CancellationToken token) {
            var stream = tcpClient.GetStream();
            var readBuffer = new byte[4096];
            var line = new List<byte>(MaxIncomingLineBytes);
            var resyncing = false;

            while (!token.IsCancellationRequested) {
                var read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, token).ConfigureAwait(false);
                if (read == 0) return; // The plugin closed the connection (Obsidian quit, or rejected us).

                for (var i = 0; i < read; i++) {
                    var b = readBuffer[i];

                    if (b == (byte)'\n') {
                        if (!resyncing) HandleLine(line);
                        line.Clear();
                        resyncing = false;
                        continue;
                    }

                    if (resyncing) continue;

                    line.Add(b);
                    if (line.Count > MaxIncomingLineBytes) {
                        _logger.Debug("Discarded an oversized line from the in-game alert bridge (over {0} bytes).", MaxIncomingLineBytes);
                        line.Clear();
                        resyncing = true;
                    }
                }
            }
        }

        /// <summary>
        /// Parses one complete line and, if it is a usable v1 alert, shows it. Every early
        /// `return` below is a documented "ignore what you do not understand" case from the spec,
        /// not an error path: broken JSON, an unsupported `v`, or a missing `kind`/`content` all
        /// just drop this one line and let the loop keep reading.
        /// </summary>
        private void HandleLine(List<byte> lineBytes) {
            if (lineBytes.Count == 0) return;

            AlertMessage message;
            try {
                using (var stream = new MemoryStream(lineBytes.ToArray())) {
                    message = (AlertMessage)AlertSerializer.ReadObject(stream);
                }
            } catch (Exception ex) {
                _logger.Debug(ex, "Discarded a line from the in-game alert bridge that was not valid JSON.");
                return;
            }

            if (message == null) return;

            if (message.V > 1) {
                WarnOutdatedOnce();
                return; // "no interpretes el resto": a newer protocol version than this module knows.
            }
            if (message.V != 1) return; // Missing or malformed version: not a message this contract defines.

            if (string.IsNullOrEmpty(message.Kind) || string.IsNullOrEmpty(message.Content)) return;

            if (message.Seq.HasValue) {
                // Reconnect dedupe: `seq` is a per-process counter on the plugin's side, so a
                // sequence number at or below the last one this connection already showed is a
                // repeat, not a new alert.
                if (_lastSeq.HasValue && message.Seq.Value <= _lastSeq.Value) return;
                _lastSeq = message.Seq;
            }

            _onAlert(message.Content, NotificationTypeFor(message.Kind));
        }

        private void WarnOutdatedOnce() {
            if (_outdatedWarningShown) return;
            _outdatedWarningShown = true;
            _logger.Warn("The Tyrian Companion plugin is speaking a newer alert protocol than this module understands. Update the Blish HUD module.");
            _onAlert("Tyrian Companion: update this Blish HUD module to see new alerts.", ScreenNotification.NotificationType.Warning);
        }

        /// <summary>
        /// `kind` only ever selects a color per the spec ("Solo elige color y título"); this
        /// mapping is this module's own choice, not part of the signed contract, and is fine to
        /// revisit. `default` deliberately still shows the notification (as <c>Info</c>) rather
        /// than dropping it: an unrecognized `kind` from a future plugin version is exactly the
        /// forward-compatible case bullet 5 of the spec's addon contract describes.
        /// </summary>
        private static ScreenNotification.NotificationType NotificationTypeFor(string kind) {
            switch (kind) {
                case "valuable_loot": return ScreenNotification.NotificationType.Green;
                case "sell_signal": return ScreenNotification.NotificationType.Blue;
                case "hold_signal": return ScreenNotification.NotificationType.Warning;
                case "always_alert": return ScreenNotification.NotificationType.Info;
                default: return ScreenNotification.NotificationType.Info;
            }
        }
    }
}
