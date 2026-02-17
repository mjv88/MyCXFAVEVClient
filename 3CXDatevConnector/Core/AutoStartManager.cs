using System;
using Microsoft.Win32;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Manages Windows autostart registration via HKCU registry key.
    /// </summary>
    internal static class AutoStartManager
    {
        private const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValue = "3CXDATEVConnector";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, false))
                {
                    return key?.GetValue(AutoStartValue) != null;
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug("AutoStartManager: Fehler beim Überprüfen des Autostarts: {0}", ex.Message);
                return false;
            }
        }

        public static void SetEnabled(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        string exePath = Environment.ProcessPath;
                        key.SetValue(AutoStartValue, $"\"{exePath}\"");
                        LogManager.Log("Autostart aktiviert");
                    }
                    else
                    {
                        key.DeleteValue(AutoStartValue, false);
                        LogManager.Log("Autostart deaktiviert");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Fehler beim Setzen des Autostarts: {0}", ex.Message);
            }
        }
    }
}
