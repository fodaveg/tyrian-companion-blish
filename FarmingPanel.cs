using System;
using System.Globalization;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Microsoft.Xna.Framework;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// Optional, movable farm1 panel. Created and refreshed only from Module.Update on the main
    /// thread, with the host's fonts and window chrome. It captures no game keyboard shortcut.
    /// </summary>
    internal sealed class FarmingPanel : IDisposable {
        private readonly FarmingWindow _window;
        private readonly Label[] _rows;
        private readonly Panel _background;
        private readonly int _chromeHeight;
        private readonly Action<Point> _onPositionChanged;
        private bool _requestedVisibility;
        private string _previous;

        public FarmingPanel(Point position, Action<Point> onPositionChanged, Action onClose) {
            _onPositionChanged = onPositionChanged;
            _window = new FarmingWindow(onClose) {
                Parent = GameService.Graphics.SpriteScreen,
                Title = "Tyrian·Laberinto",
                Location = position,
                Id = "tyrian-companion-farm1",
                SavesPosition = false, // the module persists moves and resets together in its hidden settings
                CanResize = false,
                CanClose = true,
                CanCloseWithEscape = false,
            };
            // The native frame texture is decorative; the reading area stays opaque over any map.
            _chromeHeight = _window.Height - _window.ContentRegion.Height;
            _background = new Panel { Parent = _window, Size = new Point(264, 568), BackgroundColor = new Color(28, 25, 21) };
            _rows = new Label[13];
            for (var i = 0; i < _rows.Length; i++) {
                _rows[i] = new Label {
                    Parent = _background, Width = 240, Location = new Point(12, 0),
                    Font = i == 1 ? GameService.Content.DefaultFont18 : GameService.Content.DefaultFont14,
                    WrapText = true, AutoSizeHeight = true,
                };
            }
            _window.Moved += OnMoved;
        }

        public void ResetPosition() { _window.Location = new Point(40, 140); }

        public void Update(FarmingView view, bool visible) {
            // Native Hide is animated, and Dynamic HUD can hide windows temporarily. Only react
            // to a settings transition; re-showing each frame would override both behaviors.
            _window.AllowShow = visible;
            if (visible != _requestedVisibility) {
                _requestedVisibility = visible;
                if (visible) _window.Show(); else _window.Hide();
            }
            var t = FarmingPanelText.From(view, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es");
            var rows = new[] { t.Phase, t.Observed, t.Elapsed, t.Rate, t.Age, t.Slots, t.Goal, t.Eta, t.Net, t.Source, t.Coverage, t.Preparation, t.Connection };
            var combined = string.Join("\n", rows);
            if (_previous == combined) return;
            _previous = combined;
            var top = 8;
            for (var i = 0; i < rows.Length; i++) {
                var row = _rows[i];
                row.Visible = rows[i].Length > 0;
                if (!row.Visible) continue;
                row.Text = rows[i];
                row.Location = new Point(12, top);
                // AutoSizeHeight wraps labels at the available width; leave a gap between readings.
                top += row.Height + 8;
            }
            // Extreme int32 metrics and translated errors may add wrapped lines. Grow the window
            // with its native content region so the connection/footer cannot be clipped off.
            _background.Height = top + 8;
            _window.Height = _chromeHeight + _background.Height;
        }

        private void OnMoved(object sender, MovedEventArgs e) { _onPositionChanged(_window.Location); }

        public void Dispose() { _window.AllowShow = false; _window.Moved -= OnMoved; _window.Dispose(); }

        /// <summary>Only an explicit close click disables the optional setting; Dynamic HUD hides do not.</summary>
        private sealed class FarmingWindow : StandardWindow {
            private readonly Action _onClose;
            public bool AllowShow;
            public FarmingWindow(Action onClose) : base(GameService.Content.DatAssetCache.GetTextureFromAssetId(155985),
                new Rectangle(0, 0, 288, 620), new Rectangle(12, 40, 264, 568)) { _onClose = onClose; }

            public override void Show() {
                // A pending Dynamic HUD restore must not undo an explicit visibility switch-off.
                if (AllowShow) base.Show();
            }

            protected override void OnLeftMouseButtonPressed(MouseEventArgs e) {
                if (MouseOverExitButton && CanClose) _onClose();
                base.OnLeftMouseButtonPressed(e);
            }
        }
    }
}
