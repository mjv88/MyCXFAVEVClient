using System.Collections.Generic;
using System.Windows.Forms;

namespace DatevConnector.Core
{
    /// <summary>
    /// Keyboard shortcut definitions and display string helpers.
    /// All keyboard shortcuts used in the application should be defined here.
    /// </summary>
    public static class Shortcuts
    {
        // ========== SHORTCUT DEFINITIONS ==========

        public static readonly Keys TestAll = Keys.Control | Keys.T;
        public static readonly Keys ReloadContacts = Keys.Control | Keys.R;
        public static readonly Keys OpenLog = Keys.Control | Keys.L;
        public static readonly Keys CallHistory = Keys.Control | Keys.H;
        public static readonly Keys Settings = Keys.Control | Keys.Oemcomma;
        public static readonly Keys SaveSettings = Keys.Control | Keys.S;
        public static readonly Keys CloseWindow = Keys.Escape;
        public static readonly Keys Refresh = Keys.F5;

        // ========== DISPLAY STRING HELPERS ==========

        public static string GetDisplayString(Keys keys)
        {
            var parts = new List<string>();

            if (keys.HasFlag(Keys.Control))
                parts.Add("Strg");
            if (keys.HasFlag(Keys.Alt))
                parts.Add("Alt");
            if (keys.HasFlag(Keys.Shift))
                parts.Add("Umschalt");

            var keyCode = keys & Keys.KeyCode;
            var keyString = GetKeyDisplayName(keyCode);
            parts.Add(keyString);

            return string.Join("+", parts);
        }

        private static string GetKeyDisplayName(Keys keyCode)
        {
            switch (keyCode)
            {
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "+";
                case Keys.Back: return "Rücktaste";
                case Keys.Tab: return "Tab";
                case Keys.Enter: return "Eingabe";
                case Keys.Escape: return "Esc";
                case Keys.Space: return "Leertaste";
                case Keys.Delete: return "Entf";
                case Keys.Insert: return "Einfg";
                case Keys.Home: return "Pos1";
                case Keys.End: return "Ende";
                case Keys.PageUp: return "Bild↑";
                case Keys.PageDown: return "Bild↓";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                default: return keyCode.ToString();
            }
        }

        public static bool Matches(KeyEventArgs e, Keys shortcut)
        {
            // Build the key combination from the event
            var eventKeys = e.KeyCode;
            if (e.Control) eventKeys |= Keys.Control;
            if (e.Alt) eventKeys |= Keys.Alt;
            if (e.Shift) eventKeys |= Keys.Shift;

            return eventKeys == shortcut;
        }

        public static string GetTooltipWithShortcut(string baseText, Keys shortcut)
        {
            return $"{baseText} ({GetDisplayString(shortcut)})";
        }

        public static string GetMenuItemText(string baseText, Keys shortcut)
        {
            return $"{baseText}\t{GetDisplayString(shortcut)}";
        }

        // ========== SHORTCUT REFERENCE ==========

        public static string GetShortcutDescription(Keys keys)
        {
            if (keys == TestAll) return "Alle Verbindungen testen";
            if (keys == ReloadContacts) return "Kontakte neu laden";
            if (keys == OpenLog) return "Log-Datei öffnen";
            if (keys == CallHistory) return "Anrufliste";
            if (keys == SaveSettings) return "Einstellungen speichern";
            if (keys == Refresh) return "Aktualisieren";
            if (keys == CloseWindow) return "Fenster schließen";
            return keys.ToString();
        }

        public static string GetHelpText()
        {
            return @"Tastenkürzel:
  Strg+T    Alle Verbindungen testen
  Strg+R    Kontakte neu laden
  Strg+L    Log-Datei öffnen
  Strg+H    Anrufliste
  Strg+S    Einstellungen speichern
  F5        Aktualisieren
  Esc       Fenster schließen";
        }
    }
}
