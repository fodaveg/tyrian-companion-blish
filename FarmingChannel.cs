using System;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// Tracks farming independently of alert dedupe and acknowledgements. The clock is monotonic
    /// seconds (Stopwatch in production); a transport refresh never makes an API reading younger.
    /// All methods are protected because the socket thread writes and the Update thread reads.
    /// </summary>
    internal sealed class FarmingChannel {
        private readonly object _gate = new object();
        private readonly Func<double> _clock;
        private string _nonce;
        private bool _connected, _capable;
        private int _lastSeq;
        private double _receivedAt;
        private FarmingSnapshot _snapshot;

        public FarmingChannel(Func<double> clock) { _clock = clock; }

        /// <summary>Each welcome creates a new nonce scope, even after reconnecting to the same server.</summary>
        public void Welcome(string nonce) {
            lock (_gate) { _nonce = nonce; _connected = true; _capable = false; _lastSeq = 0; }
        }

        /// <summary>Returns true only once per connection: the client then queues one sequenced subscription.</summary>
        public bool Capability(string nonce) {
            lock (_gate) {
                if (!_connected || _capable || nonce != _nonce) return false;
                _capable = true;
                return true;
            }
        }

        public bool Receive(FarmingSnapshot snapshot) {
            lock (_gate) {
                if (!_connected || !_capable || snapshot.Nonce != _nonce || snapshot.Seq <= _lastSeq) return false;
                _lastSeq = snapshot.Seq;
                _snapshot = snapshot;
                _receivedAt = _clock();
                return true;
            }
        }

        /// <summary>Disconnect invalidates freshness immediately, preserving the last identified reading.</summary>
        public void Disconnect() {
            lock (_gate) { _connected = false; _capable = false; _nonce = null; }
        }

        public FarmingView Read() {
            lock (_gate) {
                var seconds = Math.Max(0, _clock() - _receivedAt);
                return new FarmingView {
                    Snapshot = _snapshot, Connected = _connected, Capable = _capable,
                    Fresh = _snapshot != null && _lastSeq > 0 && _connected && _capable && _snapshot.Nonce == _nonce && seconds < 15,
                    SecondsSinceFrame = seconds,
                };
            }
        }
    }

    /// <summary>A render-thread copy of transport state, with the last snapshot held unchanged.</summary>
    internal sealed class FarmingView {
        public FarmingSnapshot Snapshot;
        public bool Connected, Capable, Fresh;
        public double SecondsSinceFrame;

        /// <summary>Reading ages grow between frames; the server still owns their original API age.</summary>
        public int? ReadingAge(int? baseAge) {
            if (!baseAge.HasValue) return null;
            return (int)Math.Min(int.MaxValue, baseAge.Value + SecondsSinceFrame);
        }
    }
}
