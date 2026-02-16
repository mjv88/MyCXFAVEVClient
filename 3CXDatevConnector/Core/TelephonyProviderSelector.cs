using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;
using DatevConnector.UI.Strings;
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
    ///   1. Pipe / Terminal Server (TAPI) (3CX Softphone Named Pipe handshake)
    ///   2. TAPI / Desktop (TAPI) (enumerate and open 3CX TAPI lines)
    ///   3. Webclient (browser extension via WebSocket)
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

                case TelephonyMode.WebClient:
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
            LogManager.Debug("TelephonyProviderSelector: Expliziter TAPI-Modus");

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
            LogManager.Debug("TelephonyProviderSelector: Expliziter Pipe-Modus");

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
            progressText?.Invoke("Modus: WebClient (explizit konfiguriert)");
            LogManager.Debug("TelephonyProviderSelector: Expliziter WebClient-Modus");

            var provider = new WebclientTelephonyProvider(extension);
            return Task.FromResult(new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = TelephonyMode.WebClient,
                Reason = "TelephonyMode explicitly set to WebClient",
                DiagnosticSummary = "Mode: WebClient (configured)"
            });
        }

        // ===== Auto Detection =====

        private static async Task<ProviderSelectionResult> AutoDetectAsync(
            string extension, string lineFilter, int totalTimeoutSec,
            CancellationToken cancellationToken, Action<string> progressText)
        {
            LogManager.Debug("TelephonyProviderSelector: Auto-Erkennung wird gestartet (Timeout={0}s)", totalTimeoutSec);
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Auto-Detection Results:");

            int webclientTimeoutSec = AppConfig.GetInt(ConfigKeys.WebclientConnectTimeoutSec, 8);
            bool webclientEnabled = AppConfig.GetBool(ConfigKeys.WebclientEnabled, true);

            // ── (A) Try Pipe (Terminal Server) ──
            progressText?.Invoke("Auto-Erkennung: Prüfe Terminal Server (TAPI)...");
            LogManager.Debug("TelephonyProviderSelector: [A] Versuche Pipe");

            bool isTerminalSession = SessionManager.IsTerminalSession;
            bool pipeAvailable = SessionManager.Is3CXPipeAvailable(extension);
            bool softphoneRunning = SessionManager.Is3CXProcessRunning();

            if (isTerminalSession || pipeAvailable)
            {
                diagnostics.AppendFormat("  [A] Pipe: Terminal session={0}, Pipe available={1}, 3CX running={2}",
                    isTerminalSession, pipeAvailable, softphoneRunning);
                diagnostics.AppendLine();

                if (isTerminalSession)
                {
                    // Terminal server — Pipe is the preferred mode
                    string reason = "Terminal server session detected - using Named Pipe";
                    LogManager.Debug("TelephonyProviderSelector: Pipe ausgewählt - {0}", reason);
                    diagnostics.AppendLine("  [A] Pipe: SELECTED (terminal server session)");

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
                diagnostics.AppendLine("  [A] Pipe: Not applicable (not terminal server, no pipe found)");
                LogManager.Debug("TelephonyProviderSelector: [A] Pipe nicht anwendbar");
            }

            // ── (B) Try TAPI (Desktop) ──
            progressText?.Invoke("Auto-Erkennung: Prüfe Desktop (TAPI)...");
            LogManager.Debug("TelephonyProviderSelector: [B] Versuche TAPI");

            try
            {
                // On desktop environments, TAPI is the standard choice
                var tapiProvider = new TapiLineMonitor(lineFilter, extension);
                string reason = "Desktop environment - using TAPI";
                LogManager.Debug("TelephonyProviderSelector: TAPI ausgewählt - {0}", reason);
                diagnostics.AppendLine("  [B] TAPI: SELECTED (desktop environment)");

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
                diagnostics.AppendLine("  [B] TAPI: Error - " + ex.Message);
                LogManager.Debug("TelephonyProviderSelector: [B] TAPI-Fehler - {0}", ex.Message);
            }

            // ── (C) Try Webclient ──
            if (webclientEnabled)
            {
                progressText?.Invoke("Auto-Erkennung: Prüfe WebClient...");
                LogManager.Log("WebClient = Detection..");
                LogManager.Debug("TelephonyProviderSelector: [C] Versuche WebClient (Timeout={0}s)", webclientTimeoutSec);

                try
                {
                    var webclientProvider = new WebclientTelephonyProvider(extension);
                    bool connected = await webclientProvider.TryConnectAsync(cancellationToken,
                        Math.Min(webclientTimeoutSec, totalTimeoutSec));

                    if (connected)
                    {
                        string reason = "Extension connected via WebClient (browser extension)";
                        LogManager.Debug("TelephonyProviderSelector: WebClient erkannt - {0}", reason);
                        diagnostics.AppendLine("  [C] WebClient: DETECTED (extension connected)");

                        return new ProviderSelectionResult
                        {
                            Provider = webclientProvider,
                            SelectedMode = TelephonyMode.WebClient,
                            Reason = reason,
                            DiagnosticSummary = diagnostics.ToString()
                        };
                    }

                    webclientProvider.Dispose();
                    diagnostics.AppendLine("  [C] WebClient: Not detected (no extension connected within timeout)");
                    LogManager.Debug("TelephonyProviderSelector: [C] WebClient nicht erkannt");
                    LogManager.Debug("TelephonyProviderSelector: [C] Hinweis: WebClient-Erkennung erfordert Browser-Erweiterung verbunden via WebSocket (Port {0})", AppConfig.GetInt(ConfigKeys.WebclientWebSocketPort, 19800));
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("  [C] WebClient: Error - " + ex.Message);
                    LogManager.Debug("TelephonyProviderSelector: [C] WebClient-Fehler - {0}", ex.Message);
                }
            }
            else
            {
                diagnostics.AppendLine("  [C] WebClient: Disabled (Webclient.Enabled=false)");
                LogManager.Debug("TelephonyProviderSelector: [C] WebClient deaktiviert");
            }

            // ── (D) None detected ──
            LogManager.Debug("TelephonyProviderSelector: Kein Provider erkannt");
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
                    return "Desktop (TAPI)";
                case TelephonyMode.Pipe:
                    return "Terminal Server (TAPI)";
                case TelephonyMode.WebClient:
                    return "WebClient (Browser-Erweiterung)";
                case TelephonyMode.Auto:
                default:
                    return "Automatisch (Desktop / Terminal Server -> WebClient)";
            }
        }

        /// <summary>
        /// Get the short UI label for a telephony mode (matches Settings dropdown text).
        /// </summary>
        public static string GetModeShortName(TelephonyMode mode)
        {
            switch (mode)
            {
                case TelephonyMode.Tapi:
                    return UIStrings.SettingsLabels.TelephonyModeTapi;
                case TelephonyMode.Pipe:
                    return UIStrings.SettingsLabels.TelephonyModePipe;
                case TelephonyMode.WebClient:
                    return UIStrings.SettingsLabels.TelephonyModeWebclient;
                case TelephonyMode.Auto:
                default:
                    return UIStrings.SettingsLabels.TelephonyModeAuto;
            }
        }
    }
}
