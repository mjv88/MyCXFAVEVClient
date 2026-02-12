using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;
using DatevConnector.Webclient;

namespace DatevConnector.Core
{
    /// <summary>
    /// Result of provider selection / auto-detection.
    /// </summary>
    public class ProviderSelectionResult
    {
        /// <summary>
        /// The selected provider (null if none available).
        /// </summary>
        public ITelephonyProvider Provider { get; set; }

        /// <summary>
        /// The mode that was selected.
        /// </summary>
        public TelephonyMode SelectedMode { get; set; }

        /// <summary>
        /// Human-readable reason for the selection.
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// Diagnostic summary of what was detected and what failed.
        /// </summary>
        public string DiagnosticSummary { get; set; }

        /// <summary>
        /// Whether detection succeeded and a provider is available.
        /// </summary>
        public bool Success => Provider != null;
    }

    /// <summary>
    /// Single selection point for telephony providers.
    ///
    /// Explicit mode (Tapi/Pipe/Webclient): creates that provider, fails with guided error if unavailable.
    /// Auto mode: attempts detection in priority order:
    ///   1. Webclient (browser extension via WebSocket)
    ///   2. Pipe (3CX Softphone Named Pipe handshake)
    ///   3. TAPI (enumerate and open 3CX TAPI lines)
    /// If none detected, returns failure with diagnostic summary for the Setup Wizard.
    /// </summary>
    public static class TelephonyProviderSelector
    {
        /// <summary>
        /// Select a telephony provider based on configuration and environment.
        /// </summary>
        /// <param name="extension">Extension number</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="progressText">Optional progress callback for UI updates</param>
        /// <returns>Selection result with provider or diagnostic information</returns>
        public static async Task<ProviderSelectionResult> SelectProviderAsync(
            string extension,
            CancellationToken cancellationToken,
            Action<string> progressText = null)
        {
            var mode = AppConfig.GetEnum(ConfigKeys.TelephonyMode, TelephonyMode.Auto);
            int totalTimeoutSec = AppConfig.GetInt(ConfigKeys.AutoDetectionTimeoutSec, 10);
            string lineFilter = AppConfig.GetString(ConfigKeys.TapiLineFilter, "");

            LogManager.Debug("TelephonyProviderSelector: Mode={0}, Extension={1}, Timeout={2}s",
                mode, extension, totalTimeoutSec);

            switch (mode)
            {
                case TelephonyMode.Tapi:
                    return SelectExplicit_Tapi(extension, lineFilter, progressText);

                case TelephonyMode.Pipe:
                    return SelectExplicit_Pipe(extension, progressText);

                case TelephonyMode.Webclient:
                    return await SelectExplicit_WebclientAsync(extension, cancellationToken, progressText);

                case TelephonyMode.Auto:
                default:
                    return await AutoDetectAsync(extension, lineFilter, totalTimeoutSec, cancellationToken, progressText);
            }
        }

        // ===== Explicit Mode: TAPI =====

        private static ProviderSelectionResult SelectExplicit_Tapi(string extension, string lineFilter, Action<string> progressText)
        {
            progressText?.Invoke("Modus: TAPI (explizit konfiguriert)");
            LogManager.Debug("TelephonyProviderSelector: Explicit TAPI mode");

            var provider = new TapiLineMonitor(lineFilter, extension);
            return new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = TelephonyMode.Tapi,
                Reason = "TelephonyMode explicitly set to Tapi",
                DiagnosticSummary = "Mode: Tapi (configured)"
            };
        }

        // ===== Explicit Mode: Pipe =====

        private static ProviderSelectionResult SelectExplicit_Pipe(string extension, Action<string> progressText)
        {
            progressText?.Invoke("Modus: Terminal Server (explizit konfiguriert)");
            LogManager.Debug("TelephonyProviderSelector: Explicit Pipe mode");

            var provider = new PipeTelephonyProvider(extension);
            return new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = TelephonyMode.Pipe,
                Reason = "TelephonyMode explicitly set to Pipe",
                DiagnosticSummary = "Mode: Pipe (configured)"
            };
        }

        // ===== Explicit Mode: Webclient =====

        private static Task<ProviderSelectionResult> SelectExplicit_WebclientAsync(
            string extension, CancellationToken cancellationToken, Action<string> progressText)
        {
            progressText?.Invoke("Modus: Webclient (explizit konfiguriert)");
            LogManager.Debug("TelephonyProviderSelector: Explicit Webclient mode");

            var provider = new WebclientTelephonyProvider(extension);
            return Task.FromResult(new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = TelephonyMode.Webclient,
                Reason = "TelephonyMode explicitly set to Webclient",
                DiagnosticSummary = "Mode: Webclient (configured)"
            });
        }

        // ===== Auto Detection =====

        private static async Task<ProviderSelectionResult> AutoDetectAsync(
            string extension, string lineFilter, int totalTimeoutSec,
            CancellationToken cancellationToken, Action<string> progressText)
        {
            LogManager.Debug("TelephonyProviderSelector: Auto-detection starting (timeout={0}s)", totalTimeoutSec);
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Auto-Detection Results:");

            int webclientTimeoutSec = AppConfig.GetInt(ConfigKeys.WebclientConnectTimeoutSec, 8);
            bool webclientEnabled = AppConfig.GetBool(ConfigKeys.WebclientEnabled, true);

            // ── (A) Try Webclient ──
            if (webclientEnabled)
            {
                progressText?.Invoke("Auto-Erkennung: Prüfe Webclient...");
                LogManager.Log("WebClient = Auto-Detect");
                LogManager.Debug("TelephonyProviderSelector: [A] Trying Webclient (timeout={0}s)", webclientTimeoutSec);

                try
                {
                    var webclientProvider = new WebclientTelephonyProvider(extension);
                    bool connected = await webclientProvider.TryConnectAsync(cancellationToken,
                        Math.Min(webclientTimeoutSec, totalTimeoutSec));

                    if (connected)
                    {
                        string reason = "Extension connected via Webclient (browser extension)";
                        LogManager.Debug("TelephonyProviderSelector: Webclient detected - {0}", reason);
                        diagnostics.AppendLine("  [A] Webclient: DETECTED (extension connected)");

                        return new ProviderSelectionResult
                        {
                            Provider = webclientProvider,
                            SelectedMode = TelephonyMode.Webclient,
                            Reason = reason,
                            DiagnosticSummary = diagnostics.ToString()
                        };
                    }

                    webclientProvider.Dispose();
                    diagnostics.AppendLine("  [A] Webclient: Not detected (no extension connected within timeout)");
                    LogManager.Debug("TelephonyProviderSelector: [A] Webclient not detected");
                    LogManager.Debug("TelephonyProviderSelector: [A] Hint: Webclient detection requires browser extension connected via WebSocket (port {0})", AppConfig.GetInt(ConfigKeys.WebclientWebSocketPort, 19800));
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("  [A] Webclient: Error - " + ex.Message);
                    LogManager.Debug("TelephonyProviderSelector: [A] Webclient error - {0}", ex.Message);
                }
            }
            else
            {
                diagnostics.AppendLine("  [A] Webclient: Disabled (Webclient.Enabled=false)");
                LogManager.Debug("TelephonyProviderSelector: [A] Webclient disabled");
            }

            // ── (B) Try Pipe (Terminal Server) ──
            progressText?.Invoke("Auto-Erkennung: Prüfe Terminal Server...");
            LogManager.Debug("TelephonyProviderSelector: [B] Trying Pipe");

            bool isTerminalSession = SessionManager.IsTerminalSession;
            bool pipeAvailable = SessionManager.Is3CXPipeAvailable(extension);
            bool softphoneRunning = SessionManager.Is3CXProcessRunning();

            if (isTerminalSession || pipeAvailable)
            {
                diagnostics.AppendFormat("  [B] Pipe: Terminal session={0}, Pipe available={1}, 3CX running={2}",
                    isTerminalSession, pipeAvailable, softphoneRunning);
                diagnostics.AppendLine();

                if (isTerminalSession)
                {
                    // Terminal server — Pipe is the preferred mode
                    string reason = "Terminal server session detected - using Named Pipe";
                    LogManager.Debug("TelephonyProviderSelector: Pipe selected - {0}", reason);
                    diagnostics.AppendLine("  [B] Pipe: SELECTED (terminal server session)");

                    return new ProviderSelectionResult
                    {
                        Provider = new PipeTelephonyProvider(extension),
                        SelectedMode = TelephonyMode.Pipe,
                        Reason = reason,
                        DiagnosticSummary = diagnostics.ToString()
                    };
                }
            }
            else
            {
                diagnostics.AppendLine("  [B] Pipe: Not applicable (not terminal server, no pipe found)");
                LogManager.Debug("TelephonyProviderSelector: [B] Pipe not applicable");
            }

            // ── (C) Try TAPI ──
            progressText?.Invoke("Auto-Erkennung: Prüfe TAPI...");
            LogManager.Debug("TelephonyProviderSelector: [C] Trying TAPI");

            try
            {
                // On desktop environments, TAPI is the standard choice
                var tapiProvider = new TapiLineMonitor(lineFilter, extension);
                string reason = "Desktop environment - using TAPI";
                LogManager.Debug("TelephonyProviderSelector: TAPI selected - {0}", reason);
                if (webclientEnabled)
                {
                    LogManager.Debug("TelephonyProviderSelector: TAPI fallback active. If Webclient is expected, set TelephonyMode=Webclient and verify browser extension is installed");
                }
                diagnostics.AppendLine("  [C] TAPI: SELECTED (desktop environment)");

                return new ProviderSelectionResult
                {
                    Provider = tapiProvider,
                    SelectedMode = TelephonyMode.Tapi,
                    Reason = reason,
                    DiagnosticSummary = diagnostics.ToString()
                };
            }
            catch (Exception ex)
            {
                diagnostics.AppendLine("  [C] TAPI: Error - " + ex.Message);
                LogManager.Debug("TelephonyProviderSelector: [C] TAPI error - {0}", ex.Message);
            }

            // ── (D) None detected ──
            LogManager.Debug("TelephonyProviderSelector: No provider detected");
            diagnostics.AppendLine("  Result: No provider available");

            return new ProviderSelectionResult
            {
                Provider = null,
                SelectedMode = TelephonyMode.Auto,
                Reason = "No telephony provider could be detected",
                DiagnosticSummary = diagnostics.ToString()
            };
        }

        /// <summary>
        /// Get a human-readable description of a telephony mode.
        /// </summary>
        public static string GetModeDescription(TelephonyMode mode)
        {
            switch (mode)
            {
                case TelephonyMode.Tapi:
                    return "TAPI 2.x (3CX Windows Client)";
                case TelephonyMode.Pipe:
                    return "Terminal Server (3CX Softphone)";
                case TelephonyMode.Webclient:
                    return "Webclient (Browser-Erweiterung)";
                case TelephonyMode.Auto:
                default:
                    return "Automatisch (Webclient -> Terminal Server -> TAPI)";
            }
        }
    }
}
