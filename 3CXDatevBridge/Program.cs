using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevBridge.Core;
using DatevBridge.Core.Config;
using DatevBridge.Datev.Managers;
using DatevBridge.Tapi;
using DatevBridge.UI;
using DatevBridge.Webclient;

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
            // Console test mode: --test-webclient
            // Starts WebclientTelephonyProvider without UI, reads JSON CALL_EVENT from stdin
            if (args.Length > 0 && args[0] == "--test-webclient")
            {
                RunWebclientTestMode(args);
                return;
            }

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
        /// Console test mode for the Webclient provider.
        /// Reads JSON CALL_EVENT lines from stdin and logs DATEV notifications that would be emitted.
        /// Usage: 3CXDatevBridge.exe --test-webclient [extension]
        /// </summary>
        private static void RunWebclientTestMode(string[] args)
        {
            string extension = args.Length > 1 ? args[1] : "101";

            Console.WriteLine("=== 3CX-DATEV Bridge: Webclient Test Mode ===");
            Console.WriteLine("Extension: {0}", extension);
            Console.WriteLine("Reading JSON CALL_EVENT lines from stdin...");
            Console.WriteLine("(Paste one JSON message per line, Ctrl+C to exit)");
            Console.WriteLine();

            // Create a minimal webclient provider for event mapping
            var provider = new WebclientTelephonyProvider(extension);
            provider.CallStateChanged += (callEvent) =>
            {
                string direction = callEvent.IsIncoming ? "INBOUND" : "OUTBOUND";
                string caller = callEvent.CallerNumber ?? "-";
                string called = callEvent.CalledNumber ?? "-";

                Console.WriteLine("[TAPI EVENT] {0} | direction={1} | caller={2} | called={3} | callId={4}",
                    callEvent.CallStateString, direction, caller, called, callEvent.CallId);

                // Log what DATEV notifications would be emitted
                switch (callEvent.CallState)
                {
                    case 0x00000002: // LINECALLSTATE_OFFERING
                    case 0x00000020: // LINECALLSTATE_RINGBACK
                        Console.WriteLine("  -> DATEV: NewCall(eCSOffered, {0}, {1})",
                            callEvent.IsIncoming ? "eDirIncoming" : "eDirOutgoing", caller);
                        break;
                    case 0x00000100: // LINECALLSTATE_CONNECTED
                        Console.WriteLine("  -> DATEV: CallStateChanged(eCSConnected)");
                        break;
                    case 0x00004000: // LINECALLSTATE_DISCONNECTED
                        Console.WriteLine("  -> DATEV: CallStateChanged({0})",
                            callEvent.CallState == 0x00004000 ? "eCSFinished or eCSAbsence" : "?");
                        break;
                }
                Console.WriteLine();
            };

            provider.Connected += () => Console.WriteLine("[PROVIDER] Connected");
            provider.Disconnected += () => Console.WriteLine("[PROVIDER] Disconnected");

            // Read JSON lines from stdin and simulate extension messages
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var msg = ExtensionMessage.Parse(line);
                if (msg == null)
                {
                    Console.WriteLine("[ERROR] Failed to parse JSON: {0}", line);
                    continue;
                }

                Console.WriteLine("[INPUT] type={0} callId={1} state={2} direction={3} remote={4}",
                    msg.Type, msg.CallId, msg.State, msg.Direction, msg.RemoteNumber);

                // Simulate the event through the provider
                if (string.Equals(msg.Type, Protocol.TypeCallEvent, StringComparison.OrdinalIgnoreCase))
                {
                    // Fire the internal event handler by simulating a call event
                    // Invoke the provider's internal mapped handler via reflection
                    SimulateCallEvent(provider, msg);
                }
            }

            Console.WriteLine("=== Test mode ended ===");
        }

        /// <summary>
        /// Simulate a call event in the provider for test mode.
        /// This invokes the provider's internal mapping handler so test behavior
        /// stays aligned with production behavior.
        /// </summary>
        private static void SimulateCallEvent(WebclientTelephonyProvider provider, ExtensionMessage msg)
        {
            var onExtensionCallEvent = typeof(WebclientTelephonyProvider)
                .GetMethod("OnExtensionCallEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (onExtensionCallEvent == null)
            {
                Console.WriteLine("[ERROR] Could not find WebclientTelephonyProvider.OnExtensionCallEvent");
                return;
            }

            onExtensionCallEvent.Invoke(provider, new object[] { msg });
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
