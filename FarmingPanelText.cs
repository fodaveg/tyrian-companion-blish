using System;
using System.Globalization;

namespace TyrianCompanion.BlishBridge {

    /// <summary>Localized read-only labels, shared with console tests; unknown values remain unknown.</summary>
    internal sealed class FarmingPanelText {
        public string Phase, Observed, Elapsed, Rate, Age, Slots, Goal, Eta, Net, Preparation, Connection, Source, Coverage;

        public static FarmingPanelText From(FarmingView view, bool spanish) {
            var text = new FarmingPanelText();
            var s = view.Snapshot;
            text.Connection = !view.Connected ? Pick(spanish, "Sin conexión", "Offline") :
                !view.Capable ? Pick(spanish, "Host conectado · panel no disponible", "Host connected · panel unavailable") :
                Pick(spanish, "Host conectado", "Host connected");
            // A farm1 transport tick says nothing about the underlying inventory reading.
            var age = view.ReadingAge(s?.Age);
            text.Source = Pick(spanish, "Captura propia: requiere Nexus local", "Own capture: local Nexus required") + "\n" +
                Pick(spanish, "Fuente y ámbito no identificados", "Source and scope not identified") + "\n" +
                (s?.Age == null ? Pick(spanish, "Fuente sin lectura", "Source has no reading") :
                !view.Fresh || age >= 5 ? Pick(spanish, "Lectura antigua", "Stale reading") :
                s.Error != null ? Pick(spanish, "Lectura interrumpida", "Reading interrupted") : Pick(spanish, "Lectura reciente", "Recent reading"));
            var readingFresh = view.Fresh && s?.Age != null && age < 5 && s.Error == null;
            text.Coverage = Pick(spanish, "Causa de los cambios desconocida", "Cause of changes unknown") + "\n" +
                Pick(spanish, "Monedas no incluidas en este panel", "Currencies not included in this panel");
            text.Phase = s == null ? (view.Connected && view.Capable ? Pick(spanish, "Sin medición", "Not measuring") : Pick(spanish, "Sin lectura", "No reading")) :
                !view.Fresh ? Pick(spanish, "Datos antiguos", "Stale data") : PhaseText(s, spanish);
            if (s?.Error != null && (s.Phase != "error" || !view.Fresh)) text.Phase += "\n" + ErrorText(s.Error, spanish);
            text.Observed = Pick(spanish, "Bolsas observadas", "Observed bags") + "\n" + Number(s?.Observed);
            text.Elapsed = Pick(spanish, "Duración", "Duration") + " · " + Duration(s?.Elapsed);
            text.Rate = s?.RateLow == null ? Pick(spanish, "Ritmo aún no disponible", "Rate not available yet") :
                (!readingFresh ? Pick(spanish, "Último ritmo · ", "Last rate · ") : "") +
                (s.RateHigh.HasValue ? Number(s.RateLow) + "–" + Number(s.RateHigh) : "≥ " + Number(s.RateLow)) + Pick(spanish, " bolsas/h", " bags/h");
            text.Age = AgeText(view.ReadingAge(s?.Age), spanish);
            text.Slots = Pick(spanish, "Huecos del personaje", "Character bag slots") + " · " + Number(s?.Slots) +
                (s?.Slots != null ? Pick(spanish, " libres", " free") : "") + "\n" +
                (s?.SlotSource == "recent" ? Pick(spanish, "Personaje reciente · ", "Recent character · ") : "") + AgeText(view.ReadingAge(s?.SlotAge), spanish);
            text.Goal = s == null || s.Goal == "none" ? "" : Pick(spanish, "Objetivo", "Goal") + " · " +
                (s.Goal == "duration" ? Duration(s.Progress) + " / " + Duration(s.Target) : Number(s.Progress) + " / " + Number(s.Target) + Pick(spanish, " bolsas", " bags"));
            text.Eta = text.Goal == "" ? "" : !view.Fresh ? UnavailableEta(s.Goal, spanish) :
                s.Target > 0 && s.Progress >= s.Target ? Pick(spanish, "Objetivo alcanzado", "Goal reached") :
                s.Phase != "active" || !readingFresh || s.Error != null || !s.Eta.HasValue ? UnavailableEta(s.Goal, spanish) : s.Goal == "duration" ?
                Pick(spanish, "Quedan ", "Remaining · ") + Duration(s.Eta) :
                Pick(spanish, "Quedan aprox. ", "Approx. ") + Duration(s.Eta) + (spanish ? "" : " left");
            text.Net = s == null || (s.Phase != "complete" && s.Phase != "provisional" && s.Phase != "stopping") ? "" :
                Pick(spanish, "Bolsas netas al cierre", "Net bags at close") + " · " + Number(s.Net);
            text.Preparation = Pick(spanish, "Hallazgo mágico", "Magic Find") + " · " + Number(s?.MagicFind) +
                (s?.MagicFind.HasValue == true ? "%" : "") + " · " + (s?.MagicFind == null ? Pick(spanish, "Sin lectura", "No reading") : s.MagicFindKind == "partial" ? Pick(spanish, "Parcial", "Partial") : Pick(spanish, "Desconocido", "Unknown")) + "\n" +
                Pick(spanish, "Buffs temporales sin verificar", "Temporary buffs unverified") + "\n" +
                Pick(spanish, "Preparación", "Preparation") + " · " + (s?.Preparation == "attention" ? Pick(spanish, "Revisar en el host", "Review in the host") :
                s?.Preparation == "partial" ? Pick(spanish, "Parcial", "Partial") : Pick(spanish, "Sin lectura", "No reading"));
            return text;
        }

        private static string PhaseText(FarmingSnapshot s, bool es) {
            switch (s.Phase) {
                case "starting": return Pick(es, "Preparando medición", "Preparing measurement");
                case "active": return Pick(es, "Midiendo", "Measuring");
                case "stopping": return Pick(es, "Terminando sesión", "Finishing session");
                case "provisional": return Pick(es, "Cierre provisional · esperando lectura final", "Provisional close · waiting for final reading");
                case "complete": return Pick(es, "Sesión terminada", "Session complete");
                case "abandoned": return Pick(es, "Medición abandonada", "Measurement abandoned");
                case "error": return ErrorText(s.Error, es);
                default: return Pick(es, "Sin medición", "Not measuring");
            }
        }

        private static string ErrorText(string error, bool es) {
            switch (error) {
                case "start": return Pick(es, "No se pudo empezar", "Could not start");
                case "observe": return Pick(es, "No se pudo actualizar", "Could not update");
                case "stop": return Pick(es, "No se pudo finalizar", "Could not finish");
                case "save": return Pick(es, "No se pudo guardar", "Could not save");
                default: return Pick(es, "Error en la medición", "Measurement error");
            }
        }

        private static string UnavailableEta(string goal, bool es) => goal == "duration" ?
            Pick(es, "Cuenta atrás no disponible", "Countdown unavailable") : Pick(es, "ETA aún no disponible", "ETA not available yet");

        private static string AgeText(int? age, bool es) => age.HasValue ?
            Pick(es, "Última lectura hace ", "Last reading ") + Duration(age) + (es ? "" : " ago") : Pick(es, "Sin lectura", "No reading");
        private static string Number(int? number) => number.HasValue ? number.Value.ToString(CultureInfo.InvariantCulture) : "—";
        private static string Pick(bool es, string spanish, string english) => es ? spanish : english;

        /// <summary>Supports the full int32 duration range without DateTime or TimeSpan day wrapping.</summary>
        private static string Duration(int? seconds) {
            if (!seconds.HasValue) return "—";
            var n = seconds.Value;
            return n >= 3600 ? (n / 3600) + ":" + ((n / 60) % 60).ToString("00") + ":" + (n % 60).ToString("00") :
                (n / 60) + ":" + (n % 60).ToString("00");
        }
    }
}
