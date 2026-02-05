using System;
using System.Threading;
using System.Windows.Forms;
using DatevBridge.Core;
using DatevBridge.Core.Config;
using DatevBridge.Datev.Managers;
using DatevBridge.Tapi;
using DatevBridge.UI;

namespace DatevBridge
{
    /// <summary>
    /// 3CX-DATEV Bridge - Entry Point
    /// System tray application that bridges 3CX Windows Client with DATEV Telefonie
    /// </summary>
    static class Program
    {
        /// <summary>
        /// Application entry point
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Check for single instance
            bool createdNew;
            using (var mutex = new Mutex(true, "DatevBridge_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "3CX-DATEV Bridge is already running.",
                        "3CX-DATEV Bridge",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                // Initialize INI-based configuration before anything else
                AppConfig.Initialize();

                LogManager.Log("========================================");
                LogManager.Log("3CX-DATEV Bridge Starting...");
                LogManager.Log("========================================");

                // Resolve extension: INI config -> 3CXTAPI.ini -> empty (auto-detect from TAPI line)
                string extension = AppConfig.GetString(ConfigKeys.ExtensionNumber);
                if (string.IsNullOrEmpty(extension))
                {
                    extension = TapiConfigReader.DetectExtension() ?? "";
                }

                // Auto-set MinCallerIdLength from extension length (desktop + TS)
                if (!string.IsNullOrEmpty(extension))
                {
                    int currentMin = AppConfig.GetInt(ConfigKeys.MinCallerIdLength, 2);
                    if (extension.Length > currentMin)
                    {
                        LogManager.Log("MinCallerIdLength auto-adjusted: {0} -> {1} (extension length)", currentMin, extension.Length);
                        AppConfig.SetInt(ConfigKeys.MinCallerIdLength, extension.Length);
                    }
                }

                // Enable visual styles for modern look
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Handle unhandled exceptions
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                try
                {
                    // Run the tray application
                    using (var trayApp = new TrayApplication(extension))
                    {
                        Application.Run(trayApp);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Log("Fatal error: {0}", ex);
                    MessageBox.Show(
                        $"Fatal error: {ex.Message}",
                        "3CX-DATEV Bridge Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                LogManager.Log("3CX-DATEV Bridge stopped.");
            }
        }

        /// <summary>
        /// Handle UI thread exceptions
        /// </summary>
        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            LogManager.Log("UI Thread Exception: {0}", e.Exception);
        }

        /// <summary>
        /// Handle non-UI thread exceptions
        /// </summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogManager.Log("Unhandled Exception: {0}", ex);
        }
    }
}

