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

        /// <summary>
        /// Check whether autostart is currently enabled.
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, false))
                {
                    return key?.GetValue(AutoStartValue) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Enable or disable autostart.
        /// </summary>
        public static void SetEnabled(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(AutoStartValue, $"\"{exePath}\"");
                        LogManager.Log("Autostart enabled");
                    }
                    else
                    {
                        key.DeleteValue(AutoStartValue, false);
                        LogManager.Log("Autostart disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Error setting autostart: {0}", ex.Message);
            }
        }
    }
}
