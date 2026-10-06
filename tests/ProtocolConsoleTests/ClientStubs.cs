// The protocol runner links the real TCP client, but never starts a game or opens a socket.
// These host-only types make its incoming dispatcher testable without the Blish HUD package.
namespace Blish_HUD {
    internal sealed class Logger {
        public void Debug(string message, params object[] args) { }
        public void Debug(System.Exception exception, string message, params object[] args) { }
        public void Warn(string message, params object[] args) { }
        public void Info(string message, params object[] args) { }
    }
}
namespace Blish_HUD.Controls {
    internal static class ScreenNotification {
        public enum NotificationType { Green, Blue, Warning, Info, Error }
    }
}
namespace TyrianCompanion.BlishBridge {
    internal static class GameContextSampler {
        public static IngameGameContext Sample() => new IngameGameContext("character_select", null, null);
    }
}
