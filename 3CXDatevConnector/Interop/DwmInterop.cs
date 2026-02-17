using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Interop
{
    /// <summary>
    /// DWM (Desktop Window Manager) interop for Windows 11 visual enhancements.
    /// Provides dark title bars and Mica backdrop. Falls back gracefully on older Windows.
    /// </summary>
    internal static class DwmInterop
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int BACKDROP_MICA = 2;

        /// <summary>
        /// Apply dark title bar to the window. No-op on pre-Win11 builds.
        /// </summary>
        public static void ApplyDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int value = 1; // TRUE
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            }
            catch
            {
                // Attribute not supported on this OS version — ignore
            }
        }

        /// <summary>
        /// Apply Mica backdrop to the window. No-op on pre-Win11 22H2 builds.
        /// </summary>
        public static void ApplyMicaBackdrop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int value = BACKDROP_MICA;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
            }
            catch
            {
                // Attribute not supported on this OS version — ignore
            }
        }
    }
}
