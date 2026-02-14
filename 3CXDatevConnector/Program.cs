using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;
using DatevConnector.UI;
using DatevConnector.Webclient;

namespace DatevConnector
{
    /// <summary>
    /// 3CX - DATEV Connector - Entry Point
    /// System tray application that bridges 3CX Windows App with DATEV Telefonie
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
            using (var mutex = new Mutex(true, "DatevConnector_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "3CX - DATEV Connector is already running.",
                        "3CX - DATEV Connector",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                // Register exception handlers BEFORE any initialization
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                // Initialize INI-based configuration
                AppConfig.Initialize();

                LogManager.Log("========================================");
                LogManager.Log("3CX - DATEV Connector Starting...");
                LogManager.Log("========================================");

                string extension = ResolveExtension();

                // Enable visual styles for modern look
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

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
                        "3CX - DATEV Connector Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                LogManager.Log("3CX - DATEV Connector stopped.");
            }
        }

        /// <summary>
        /// Resolve extension: INI config -> 3CXTAPI.ini -> empty (auto-detect from TAPI line).
        /// Also auto-sets MinCallerIdLength from extension length.
        /// </summary>
        private static string ResolveExtension()
        {
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
                    LogManager.Log("Minimumlänge: {0} -> {1} -stellig (Aufgrund der Nebenstellenlänge)", currentMin, extension.Length);
                    AppConfig.SetInt(ConfigKeys.MinCallerIdLength, extension.Length);
                }
            }

            return extension;
        }

        /// <summary>
        /// Console test mode for the Webclient provider.
        /// Reads JSON CALL_EVENT lines from stdin and logs DATEV notifications that would be emitted.
        /// Usage: 3CXDatevConnector.exe --test-webclient [extension]
        /// </summary>
        private static void RunWebclientTestMode(string[] args)
        {
            string extension = args.Length > 1 ? args[1] : "101";

            Console.WriteLine("=== 3CX - DATEV Connector: Webclient Test Mode ===");
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
        /// </summary>
        private static void SimulateCallEvent(WebclientTelephonyProvider provider, ExtensionMessage msg)
        {
            provider.SimulateCallEvent(msg);
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

        /// <summary>
        /// Handle unobserved task exceptions (fire-and-forget tasks)
        /// </summary>
        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogManager.Log("Unobserved Task Exception: {0}", e.Exception?.Flatten());
            e.SetObserved();
        }
    }
}
