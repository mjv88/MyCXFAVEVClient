using System.Windows.Forms;
using DatevConnector.UI.Strings;

namespace DatevConnector.Core
{
    /// <summary>
    /// Keyboard shortcut definitions and matching helpers.
    /// All keyboard shortcuts used in the application should be defined here.
    /// </summary>
    public static class Shortcuts
    {
        // ========== SHORTCUT DEFINITIONS ==========

        public static readonly Keys TestAll = Keys.Control | Keys.T;
        public static readonly Keys ReloadContacts = Keys.Control | Keys.R;
        public static readonly Keys OpenLog = Keys.Control | Keys.L;
        public static readonly Keys CallHistory = Keys.Control | Keys.H;
        public static readonly Keys SaveSettings = Keys.Control | Keys.S;
        public static readonly Keys CloseWindow = Keys.Escape;
        public static readonly Keys Refresh = Keys.F5;

        public static bool Matches(KeyEventArgs e, Keys shortcut)
        {
            // Build the key combination from the event
            var eventKeys = e.KeyCode;
            if (e.Control) eventKeys |= Keys.Control;
            if (e.Alt) eventKeys |= Keys.Alt;
            if (e.Shift) eventKeys |= Keys.Shift;

            return eventKeys == shortcut;
        }

        // ========== SHORTCUT REFERENCE ==========

        public static string GetShortcutDescription(Keys keys)
        {
            if (keys == TestAll) return UIStrings.ShortcutLabels.TestAll;
            if (keys == ReloadContacts) return UIStrings.ShortcutLabels.ReloadContacts;
            if (keys == OpenLog) return UIStrings.ShortcutLabels.OpenLog;
            if (keys == CallHistory) return UIStrings.ShortcutLabels.CallHistory;
            if (keys == SaveSettings) return UIStrings.ShortcutLabels.SaveSettings;
            if (keys == Refresh) return UIStrings.ShortcutLabels.Refresh;
            if (keys == CloseWindow) return UIStrings.ShortcutLabels.CloseWindow;
            return keys.ToString();
        }
    }
}
