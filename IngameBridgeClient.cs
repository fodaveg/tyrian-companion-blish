using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Controls;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// The bidirectional, authenticated loopback TCP client for protocol v2
    /// (`docs/SPEC-puente-ingame.md` in the `tyrian-companion` repo). Connects to the Tyrian
    /// Companion plugin, authenticates with a per-installation token, paints the plugin's `alert`
    /// lines, and reports this addon's own view of the game (map, character, gameplay/loading/
    /// character-select) as `context`/`heartbeat`/`bye`. Runs its own connect/read/send loop on a
    /// background thread for as long as the module is loaded; never touches Blish HUD's render
    /// loop directly (<see cref="RunAsync"/> is meant to be started with <c>Task.Run</c>, not
    /// awaited by <c>Update</c>), and the one place it samples <c>GameService</c> is
    /// <see cref="GameContextSampler"/>, read-only.
    ///
    /// v1 (0.1.0) sent one `hello` and then only ever read. v2 authenticates that `hello` with a
    /// token, and after the plugin's `welcome` also writes: this is why the wire is now guarded by
    /// a <see cref="ConnectionState.WriteGate"/> — a settings change or the game closing can ask
    /// for a `bye` (<see cref="RequestGracefulBye"/>) from a different thread than the one running
    /// the connect loop.
    /// </summary>
    internal sealed class IngameBridgeClient : IDisposable {

        /// <summary>How often the loop rechecks the enabled flag, or a paused auth/version state, before trying again.</summary>
        private const int DisabledPollIntervalMs = 1_000;

        private const string LoopbackHost = "127.0.0.1";

        private readonly Func<bool> _enabledProvider;
        private readonly Func<int> _portProvider;
        private readonly Func<string> _tokenProvider;
        private readonly Action<string, ScreenNotification.NotificationType> _onAlert;
        private readonly Logger _logger;
        private readonly string _clientVersion;

        /// <summary>Generated once per <see cref="IngameBridgeClient"/> (one per module load) and reused across every reconnect it makes, per the spec's `instance` field.</summary>
        private readonly string _instanceId;

        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private readonly object _syncLock = new object();

        private TcpClient _activeClient;
        private ConnectionState _activeConnection;
        private bool _disposed;

        /// <summary>
        /// Dedupe of the plugin's `alert` lines across reconnects, keyed by `(server, seq)` per
        /// the spec: "tras reiniciar Obsidian el contador vuelve a 1 con otro server. Este es el
        /// fallo del módulo de Blish 0.1.0, que descartaba los avisos siguientes." `_lastSeenAlertSeq`
        /// resets to -1 only when a `welcome`'s `server` differs from the one already on file, so a
        /// same-server reconnect (the addon crashed, or the game itself, not Obsidian) keeps the
        /// high-water mark instead of showing anything again.
        /// </summary>
        private string _lastSeenAlertServer;
        private long _lastSeenAlertSeq = -1;

        /// <summary>Set by `auth_rejected`/`version_unsupported`; cleared by any settings change. "No se reintenta hasta que el usuario cambie los ajustes del addon."</summary>
        private volatile string _pauseReasonCode;
        private bool _pauseWarningShown;
        private bool _outdatedWarningShown;
        private bool _missingTokenWarningShown;

        public IngameBridgeClient(
            Func<bool> enabledProvider,
            Func<int> portProvider,
            Func<string> tokenProvider,
            Action<string, ScreenNotification.NotificationType> onAlert,
            Logger logger,
            string clientVersion) {
            _enabledProvider = enabledProvider;
            _portProvider = portProvider;
            _tokenProvider = tokenProvider;
            _onAlert = onAlert;
            _logger = logger;
            _clientVersion = clientVersion;
            _instanceId = IngameBridgeProtocol.CreateInstanceId(FillRandom);
        }

        /// <summary>
        /// Runs until <paramref name="token"/> is cancelled. Never throws on a normal shutdown:
        /// cancellation ends the loop, it does not fault the returned task.
        /// </summary>
        public async Task RunAsync(CancellationToken token) {
            using (token.Register(AbortActiveConnection)) {
                var attempt = 0;

                while (!token.IsCancellationRequested) {
                    if (!_enabledProvider() || _pauseReasonCode != null) {
                        await WaitAsync(DisabledPollIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    // Checked here, once, and handed to the `hello` as is: empty, an API key or a
                    // malformed value means "no token", and the plugin would reject every `hello`
                    // without one anyway, so there is nothing to connect for.
                    var helloToken = TokenGuard.UsableOrNull(_tokenProvider());
                    if (helloToken == null) {
                        WarnMissingTokenOnce();
                        await WaitAsync(DisabledPollIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    var port = _portProvider();

                    try {
                        using (var tcpClient = new TcpClient()) {
                            lock (_syncLock) { _activeClient = tcpClient; }

                            await tcpClient.ConnectAsync(LoopbackHost, port).ConfigureAwait(false);
                            _logger.Debug("Connected to the Tyrian Companion plugin on {0}:{1}.", LoopbackHost, port);

                            attempt = 0;

                            await DriveConnectionAsync(tcpClient, helloToken, token).ConfigureAwait(false);
                        }
                    } catch (Exception ex) when (!token.IsCancellationRequested) {
                        // A SocketException because nothing is listening yet is the expected
                        // steady state before Obsidian starts. Anything else — a reset mid-read, a
                        // rejected hello whose socket then closed — lands here too, and gets the
                        // same answer: log it quietly and let the backoff below retry.
                        _logger.Debug(ex, "The in-game bridge could not stay connected on port {0}.", port);
                    } catch (Exception) {
                        // Cancellation (module Unload) tore the socket down from under us via
                        // AbortActiveConnection; this is a shutdown, not a failure.
                    } finally {
                        lock (_syncLock) { _activeClient = null; _activeConnection = null; }
                    }

                    if (token.IsCancellationRequested) break;
                    if (_pauseReasonCode != null) continue; // do not spend a backoff slot on a pause the user has to clear

                    var delayMs = IngameBridgeProtocol.ReconnectBackoffDelaysMs[Math.Min(attempt, IngameBridgeProtocol.ReconnectBackoffDelaysMs.Length - 1)];
                    attempt++;
                    await WaitAsync(delayMs, token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Called when the enabled flag, the port or the token setting changes: interrupts a stale
        /// backoff or disabled-poll wait, and — this is the only way out of it — clears a pause set
        /// by `auth_rejected`/`version_unsupported`, since a settings change is exactly the "user
        /// changes ajustes" condition the spec's error table names.
        /// </summary>
        public void WakeUp() {
            if (_disposed) return;
            _pauseReasonCode = null;
            _pauseWarningShown = false;
            _missingTokenWarningShown = false;
            try {
                if (_wake.CurrentCount == 0) _wake.Release();
            } catch (ObjectDisposedException) {
                // Racing Dispose(); nothing left to wake.
            }
        }

        /// <summary>
        /// Best-effort `bye` on the current authenticated connection, then closes it. Called from
        /// Blish HUD's own event/module thread (<c>GameIntegration.Gw2Closed</c>, or
        /// <c>Module.Unload</c>), never from the connect loop itself, so it cannot await inline —
        /// it fires a background write and returns immediately. "Si no puede enviarlo (cuelgue,
        /// crash), no pasa nada": a failed write here is swallowed, not surfaced.
        /// </summary>
        public void RequestGracefulBye(string reason) {
            ConnectionState connection;
            TcpClient client;
            lock (_syncLock) { connection = _activeConnection; client = _activeClient; }
            if (connection == null || client == null || !connection.Authenticated) return;

            _ = Task.Run(async () => {
                try {
                    var stream = client.GetStream();
                    await connection.WriteGate.WaitAsync().ConfigureAwait(false);
                    try {
                        var seq = connection.NextOutboundSeq();
                        var line = IngameBridgeProtocol.EncodeBye(connection.Nonce, seq, reason);
                        await stream.WriteAsync(line, 0, line.Length).ConfigureAwait(false);
                    } finally {
                        connection.WriteGate.Release();
                    }
                } catch (Exception) {
                    // Best-effort, per the spec: the connection is about to close anyway.
                } finally {
                    AbortActiveConnection();
                }
            });
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
            await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        }

        /// <summary>
        /// Drives one TCP connection end to end: send `hello`, then alternate reading the
        /// plugin's lines with checking whether this addon's own game context needs reporting.
        /// Returns (or throws, same as v1) when the plugin closes the socket; the caller's own
        /// try/catch and backoff handle reconnecting.
        /// </summary>
        private async Task DriveConnectionAsync(TcpClient tcpClient, string helloToken, CancellationToken token) {
            var stream = tcpClient.GetStream();
            var connection = new ConnectionState();
            lock (_syncLock) { _activeConnection = connection; }

            await connection.WriteGate.WaitAsync(token).ConfigureAwait(false);
            try {
                var hello = IngameBridgeProtocol.EncodeHello(_clientVersion, _instanceId, helloToken);
                await stream.WriteAsync(hello, 0, hello.Length, token).ConfigureAwait(false);
                connection.LastSentAtUtc = DateTime.UtcNow;
            } finally {
                connection.WriteGate.Release();
            }

            var readBuffer = new byte[4096];
            var line = new List<byte>(IngameBridgeProtocol.MaxLineBytes);
            var resyncing = false;
            var pendingRead = stream.ReadAsync(readBuffer, 0, readBuffer.Length, token);

            while (!token.IsCancellationRequested) {
                using (var tickCts = new CancellationTokenSource()) {
                    var pollDelayMs = connection.Authenticated ? IngameBridgeProtocol.GameContextPollIntervalMs : DisabledPollIntervalMs;
                    var delayTask = Task.Delay(pollDelayMs, tickCts.Token);
                    var finished = await Task.WhenAny(pendingRead, delayTask).ConfigureAwait(false);
                    tickCts.Cancel();

                    if (finished == pendingRead) {
                        var read = await pendingRead.ConfigureAwait(false); // already completed; observes any exception too
                        if (read == 0) return; // The plugin closed the connection.

                        for (var i = 0; i < read; i++) {
                            var b = readBuffer[i];

                            if (b == (byte)'\n') {
                                if (!resyncing) HandleIncomingLine(connection, line);
                                line.Clear();
                                resyncing = false;
                                continue;
                            }

                            if (resyncing) continue;

                            line.Add(b);
                            if (line.Count > IngameBridgeProtocol.MaxLineBytes) {
                                _logger.Debug("Discarded an oversized line from the in-game bridge (over {0} bytes).", IngameBridgeProtocol.MaxLineBytes);
                                line.Clear();
                                resyncing = true;
                            }
                        }

                        pendingRead = stream.ReadAsync(readBuffer, 0, readBuffer.Length, token);
                    }
                }

                if (connection.Authenticated) {
                    await SendContextOrHeartbeatIfDueAsync(stream, connection, token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Sends `context` if this addon's sampled game state changed since the last line it sent
        /// on this connection (which the initial `welcome` always counts as none), otherwise a
        /// `heartbeat` once <see cref="ConnectionState.HeartbeatIntervalMs"/> has passed in silence.
        /// </summary>
        private async Task SendContextOrHeartbeatIfDueAsync(NetworkStream stream, ConnectionState connection, CancellationToken token) {
            var sampled = GameContextSampler.Sample();
            if (!connection.LastSentContext.HasValue || !connection.LastSentContext.Value.Equals(sampled)) {
                await WriteSequencedLineAsync(stream, connection, seq => IngameBridgeProtocol.EncodeContext(connection.Nonce, seq, sampled), token).ConfigureAwait(false);
                connection.LastSentContext = sampled;
                return;
            }

            var elapsedMs = (DateTime.UtcNow - connection.LastSentAtUtc).TotalMilliseconds;
            if (elapsedMs >= connection.HeartbeatIntervalMs) {
                await WriteSequencedLineAsync(stream, connection, seq => IngameBridgeProtocol.EncodeHeartbeat(connection.Nonce, seq), token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Allocates the next `seq` and writes the resulting line under <see cref="ConnectionState.WriteGate"/>
        /// in one step, so a concurrent <see cref="RequestGracefulBye"/> can never interleave with — or
        /// steal a sequence number from — a write this loop is in the middle of.
        /// </summary>
        private static async Task WriteSequencedLineAsync(NetworkStream stream, ConnectionState connection, Func<long, byte[]> buildLine, CancellationToken token) {
            await connection.WriteGate.WaitAsync(token).ConfigureAwait(false);
            try {
                var seq = connection.NextOutboundSeq();
                var encoded = buildLine(seq);
                await stream.WriteAsync(encoded, 0, encoded.Length, token).ConfigureAwait(false);
                connection.LastSentAtUtc = DateTime.UtcNow;
            } finally {
                connection.WriteGate.Release();
            }
        }

        /// <summary>
        /// Parses one complete line and acts on it. Every "ignore" branch below is a documented
        /// case from the spec's addon contract, not an error path: an unknown `type`, a known
        /// `type` with the wrong keys, or a `v` this module does not speak all just drop this one
        /// line and let the loop keep reading.
        /// </summary>
        private void HandleIncomingLine(ConnectionState connection, List<byte> lineBytes) {
            if (lineBytes.Count == 0) return;

            var parsed = IngameBridgeProtocol.ParseIncomingLine(lineBytes.ToArray());
            switch (parsed.Kind) {
                case IngameBridgeProtocol.IncomingKind.Welcome:
                    if (parsed.Server != _lastSeenAlertServer) {
                        // A new plugin run (Obsidian restarted, or the port's first connection):
                        // its alert `seq` starts over, so the dedupe high-water mark must too.
                        _lastSeenAlertServer = parsed.Server;
                        _lastSeenAlertSeq = -1;
                    }
                    connection.Nonce = parsed.Nonce;
                    connection.Server = parsed.Server;
                    connection.HeartbeatIntervalMs = parsed.HeartbeatIntervalMs > 0 ? parsed.HeartbeatIntervalMs : IngameBridgeProtocol.DefaultHeartbeatIntervalMs;
                    connection.LastSentAtUtc = DateTime.UtcNow;
                    connection.Authenticated = true;
                    _pauseReasonCode = null;
                    _pauseWarningShown = false;
                    return;
                case IngameBridgeProtocol.IncomingKind.Alert:
                    if (parsed.AlertSeq <= _lastSeenAlertSeq) return; // already shown, from this same plugin run
                    _lastSeenAlertSeq = parsed.AlertSeq;
                    _onAlert(parsed.AlertContent, NotificationTypeFor(parsed.AlertKind));
                    return;
                case IngameBridgeProtocol.IncomingKind.Error:
                    HandleError(parsed.ErrorCode);
                    return;
                case IngameBridgeProtocol.IncomingKind.ProtocolNewer:
                    WarnOutdatedOnce();
                    return;
                default:
                    return; // Unknown: ignore, per the spec's tolerance rule.
            }
        }

        private void HandleError(string code) {
            _logger.Warn("The in-game bridge closed the connection with error code {0}.", code);
            switch (code) {
                case "auth_rejected":
                    SetPause(code, TokenGuard.RejectedMessage);
                    break;
                case "version_unsupported":
                    SetPause(code, "Tyrian Companion: update this Blish HUD module to talk to the plugin.");
                    break;
                default:
                    // hello_timeout, liveness_timeout, capacity, frame_length, frame_utf8,
                    // frame_json, frame_schema, nonce_mismatch, sequence_mismatch,
                    // unexpected_message: the spec's own table says "reintentar con backoff" for
                    // every one of these — nothing else to do; the connect loop's backoff already
                    // covers it once this connection's socket closes.
                    break;
            }
        }

        private void SetPause(string code, string message) {
            _pauseReasonCode = code;
            if (_pauseWarningShown) return;
            _pauseWarningShown = true;
            _onAlert(message, ScreenNotification.NotificationType.Error);
        }

        /// <summary>
        /// For the module's load, when it has just told the player that a saved API key was removed:
        /// the "no token yet" notice would only repeat that. The next settings change re-arms it.
        /// </summary>
        public void SuppressMissingTokenWarning() => _missingTokenWarningShown = true;

        /// <summary>Once until the settings change: <see cref="WakeUp"/> re-arms it, so a cleared token is announced again.</summary>
        private void WarnMissingTokenOnce() {
            if (_missingTokenWarningShown) return;
            _missingTokenWarningShown = true;
            _logger.Info("No usable token in this module's settings; not connecting to the Tyrian Companion plugin.");
            _onAlert(TokenGuard.MissingMessage, ScreenNotification.NotificationType.Warning);
        }

        private void WarnOutdatedOnce() {
            if (_outdatedWarningShown) return;
            _outdatedWarningShown = true;
            _logger.Warn("The Tyrian Companion plugin is speaking a newer bridge protocol than this module understands. Update the Blish HUD module.");
            _onAlert("Tyrian Companion: update this Blish HUD module to see new alerts.", ScreenNotification.NotificationType.Warning);
        }

        /// <summary>
        /// `kind` only ever selects a color per the spec ("Solo elige color y título"); this
        /// mapping is this module's own choice, not part of the signed contract. `default`
        /// deliberately still shows the notification (as <c>Info</c>) rather than dropping it: an
        /// unrecognized `kind` from a future plugin version is the forward-compatible case the
        /// addon contract describes.
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

        private static void FillRandom(byte[] bytes) {
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(bytes);
            }
        }

        /// <summary>Everything about one TCP connection that the read loop and a concurrent <see cref="RequestGracefulBye"/> both touch.</summary>
        private sealed class ConnectionState {
            /// <summary>Serializes every write on this connection's stream: the read loop's own `context`/`heartbeat` and a `bye` fired from another thread must never interleave.</summary>
            public readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);

            public bool Authenticated;
            public string Nonce;
            public string Server;
            public int HeartbeatIntervalMs = IngameBridgeProtocol.DefaultHeartbeatIntervalMs;
            public DateTime LastSentAtUtc = DateTime.UtcNow;
            public IngameGameContext? LastSentContext;

            private long _outboundSeq;

            /// <summary>Caller must already hold <see cref="WriteGate"/>.</summary>
            public long NextOutboundSeq() {
                var seq = _outboundSeq;
                _outboundSeq += 1;
                return seq;
            }
        }
    }
}
