using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DatevBridge.Core.Config;
using DatevBridge.Datev.Managers;
using DatevBridge.Tapi;
using DatevBridge.Webclient;

namespace DatevBridge.Core
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
    ///   1. Webclient (browser extension via Named Pipe IPC)
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

            LogManager.Log("TelephonyProviderSelector: Mode={0}, Extension={1}, Timeout={2}s",
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
            LogManager.Log("TelephonyProviderSelector: Explicit TAPI mode");

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
            progressText?.Invoke("Modus: Named Pipe (explizit konfiguriert)");
            LogManager.Log("TelephonyProviderSelector: Explicit Pipe mode");

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
            LogManager.Log("TelephonyProviderSelector: Explicit Webclient mode");

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
            LogManager.Log("TelephonyProviderSelector: Auto-detection starting (timeout={0}s)", totalTimeoutSec);
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Auto-Detection Results:");

            int webclientTimeoutSec = AppConfig.GetInt(ConfigKeys.WebclientConnectTimeoutSec, 8);
            bool nativeMessagingEnabled = AppConfig.GetBool(ConfigKeys.WebclientNativeMessagingEnabled, true);

            // ── (A) Try Webclient ──
            if (nativeMessagingEnabled)
            {
                progressText?.Invoke("Auto-Erkennung: Prüfe Webclient...");
                LogManager.Log("TelephonyProviderSelector: [A] Trying Webclient (timeout={0}s)", webclientTimeoutSec);

                try
                {
                    var webclientProvider = new WebclientTelephonyProvider(extension);
                    bool connected = await webclientProvider.TryConnectAsync(cancellationToken,
                        Math.Min(webclientTimeoutSec, totalTimeoutSec));

                    if (connected)
                    {
                        string reason = "Extension connected via Webclient (browser extension)";
                        LogManager.Log("TelephonyProviderSelector: Webclient detected - {0}", reason);
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
                    LogManager.Log("TelephonyProviderSelector: [A] Webclient not detected");
                    LogManager.Log("TelephonyProviderSelector: [A] Hint: Webclient detection does not use PBX/FQDN; it requires browser extension + native host + matching local session pipe");
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("  [A] Webclient: Error - " + ex.Message);
                    LogManager.Log("TelephonyProviderSelector: [A] Webclient error - {0}", ex.Message);
                }
            }
            else
            {
                diagnostics.AppendLine("  [A] Webclient: Disabled (NativeMessagingEnabled=false)");
                LogManager.Log("TelephonyProviderSelector: [A] Webclient disabled");
            }

            // ── (B) Try Pipe (Terminal Server) ──
            progressText?.Invoke("Auto-Erkennung: Prüfe Named Pipe...");
            LogManager.Log("TelephonyProviderSelector: [B] Trying Pipe");

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
                    LogManager.Log("TelephonyProviderSelector: Pipe selected - {0}", reason);
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
                LogManager.Log("TelephonyProviderSelector: [B] Pipe not applicable");
            }

            // ── (C) Try TAPI ──
            progressText?.Invoke("Auto-Erkennung: Prüfe TAPI...");
            LogManager.Log("TelephonyProviderSelector: [C] Trying TAPI");

            try
            {
                // On desktop environments, TAPI is the standard choice
                var tapiProvider = new TapiLineMonitor(lineFilter, extension);
                string reason = "Desktop environment - using TAPI";
                LogManager.Log("TelephonyProviderSelector: TAPI selected - {0}", reason);
                if (nativeMessagingEnabled)
                {
                    LogManager.Log("TelephonyProviderSelector: TAPI fallback active. If Webclient is expected, set TelephonyMode=Webclient and verify extension/native-host registration in this Windows user session");
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
                LogManager.Log("TelephonyProviderSelector: [C] TAPI error - {0}", ex.Message);
            }

            // ── (D) None detected ──
            LogManager.Log("TelephonyProviderSelector: No provider detected");
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
                    return "Named Pipe (3CX Softphone / Terminal Server)";
                case TelephonyMode.Webclient:
                    return "Webclient (Browser-Erweiterung)";
                case TelephonyMode.Auto:
                default:
                    return "Automatisch (Webclient -> Pipe -> TAPI)";
            }
        }
    }
}
