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
        public IConnectionMethod Provider { get; set; }
        public ConnectionMode SelectedMode { get; set; }
        public string Reason { get; set; }
        public string DiagnosticSummary { get; set; }
        public bool Success => Provider != null;
    }

    /// <summary>
    /// Single selection point for connection methods.
    ///
    /// Explicit mode (Tapi/Pipe/Webclient): creates that provider, fails with guided error if unavailable.
    /// Auto mode: attempts detection in priority order:
    ///   1. Terminal Server (TAPI) (3CX Softphone Named Pipe handshake)
    ///   2. Desktop (TAPI) (enumerate and open 3CX TAPI lines)
    ///   3. Webclient (browser extension via WebSocket)
    /// If none detected, returns failure with diagnostic summary for the Setup Wizard.
    /// </summary>
    public static class ConnectionMethodSelector
    {
        public static async Task<ProviderSelectionResult> SelectProviderAsync(
            string extension,
            CancellationToken cancellationToken,
            Action<string> progressText = null)
        {
            var mode = AppConfig.GetConnectionMode();
            int totalTimeoutSec = AppConfig.GetInt(ConfigKeys.AutoDetectionTimeoutSec, 10);
            string lineFilter = AppConfig.GetString(ConfigKeys.TapiLineFilter, "");

            LogManager.Debug("ConnectionMethodSelector: Mode={0}, Extension={1}, Timeout={2}s",
                mode, extension, totalTimeoutSec);

            switch (mode)
            {
                case ConnectionMode.Desktop:
                    return SelectExplicit_Tapi(extension, lineFilter, progressText);

                case ConnectionMode.TerminalServer:
                    return SelectExplicit_Pipe(extension, progressText);

                case ConnectionMode.WebClient:
                    return await SelectExplicit_WebclientAsync(extension, cancellationToken, progressText);

                case ConnectionMode.Auto:
                default:
                    return await AutoDetectAsync(extension, lineFilter, totalTimeoutSec, cancellationToken, progressText);
            }
        }

        // ===== Explicit Mode: TAPI =====

        private static ProviderSelectionResult SelectExplicit_Tapi(string extension, string lineFilter, Action<string> progressText)
        {
            progressText?.Invoke("Modus: TAPI (explizit konfiguriert)");
            LogManager.Debug("ConnectionMethodSelector: Expliziter TAPI-Modus");

            var provider = new TapiLineMonitor(lineFilter, extension);
            return new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = ConnectionMode.Desktop,
                Reason = "ConnectionMode explicitly set to Desktop",
                DiagnosticSummary = "Mode: Desktop (configured)"
            };
        }

        // ===== Explicit Mode: TerminalServer =====

        private static ProviderSelectionResult SelectExplicit_Pipe(string extension, Action<string> progressText)
        {
            progressText?.Invoke("Modus: Terminal Server (explizit konfiguriert)");
            LogManager.Debug("ConnectionMethodSelector: Expliziter Terminal Server (TAPI)-Modus");

            var provider = new PipeConnectionMethod(extension);
            return new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = ConnectionMode.TerminalServer,
                Reason = "ConnectionMode explicitly set to TerminalServer",
                DiagnosticSummary = "Mode: TerminalServer (configured)"
            };
        }

        // ===== Explicit Mode: Webclient =====

        private static Task<ProviderSelectionResult> SelectExplicit_WebclientAsync(
            string extension, CancellationToken cancellationToken, Action<string> progressText)
        {
            progressText?.Invoke("Modus: WebClient (explizit konfiguriert)");
            LogManager.Debug("ConnectionMethodSelector: Expliziter WebClient-Modus");

            var provider = new WebclientConnectionMethod(extension);
            return Task.FromResult(new ProviderSelectionResult
            {
                Provider = provider,
                SelectedMode = ConnectionMode.WebClient,
                Reason = "ConnectionMode explicitly set to WebClient",
                DiagnosticSummary = "Mode: WebClient (configured)"
            });
        }

        // ===== Auto Detection =====

        private static async Task<ProviderSelectionResult> AutoDetectAsync(
            string extension, string lineFilter, int totalTimeoutSec,
            CancellationToken cancellationToken, Action<string> progressText)
        {
            LogManager.Debug("ConnectionMethodSelector: Auto-Erkennung wird gestartet (Timeout={0}s)", totalTimeoutSec);
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("Auto-Erkennung:");

            int webclientTimeoutSec = AppConfig.GetInt(ConfigKeys.WebclientConnectTimeoutSec, 8);
            bool webclientEnabled = AppConfig.GetBool(ConfigKeys.WebclientEnabled, true);

            // ── Check if TAPI driver is installed ──
            bool tapiInstalled = TapiConfigReader.IsTapiInstalled();

            if (tapiInstalled)
            {
                // ── (A) Try TAPI (Desktop) ──
                progressText?.Invoke("Auto-Erkennung: Prüfe Desktop (TAPI)...");
                LogManager.Debug("ConnectionMethodSelector: [A] Versuche TAPI");

                try
                {
                    var tapiProvider = new TapiLineMonitor(lineFilter, extension);

                    if (tapiProvider.ProbeLines())
                    {
                        // TAPI lines found — but is the 3CX Softphone actually running?
                        bool is3CXRunning = SessionManager.Is3CXProcessRunning();
                        if (is3CXRunning)
                        {
                            string reason = "Desktop environment - TAPI lines available, 3CX Softphone running";
                            LogManager.Debug("ConnectionMethodSelector: TAPI ausgewählt - {0}", reason);
                            diagnostics.AppendLine("Desktop (TAPI): Ausgewählt (Leitungen + Softphone verfügbar)");

                            return new ProviderSelectionResult
                            {
                                Provider = tapiProvider,
                                SelectedMode = ConnectionMode.Desktop,
                                Reason = reason,
                                DiagnosticSummary = diagnostics.ToString()
                            };
                        }

                        // TAPI installed but Softphone not running — fall through to WebClient
                        tapiProvider.Dispose();
                        diagnostics.AppendLine("Desktop (TAPI): Leitungen vorhanden aber 3CX Softphone nicht gestartet");
                        LogManager.Log("Auto-Erkennung: TAPI-Treiber installiert aber 3CX Softphone läuft nicht - versuche WebClient");
                    }
                    else
                    {
                        tapiProvider.Dispose();
                        diagnostics.AppendLine("Desktop (TAPI): Keine Leitungen verfügbar");
                        LogManager.Debug("ConnectionMethodSelector: [A] TAPI hat keine Leitungen - überspringe");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("Desktop (TAPI): Fehler - " + ex.Message);
                    LogManager.Debug("ConnectionMethodSelector: [A] TAPI-Fehler - {0}", ex.Message);
                }

                // ── (B) Try Terminal Server ──
                progressText?.Invoke("Auto-Erkennung: Prüfe Terminal Server...");
                LogManager.Debug("ConnectionMethodSelector: [B] Versuche Terminal Server");

                bool isTerminalSession = SessionManager.IsTerminalSession;
                bool pipeAvailable = SessionManager.Is3CXPipeAvailable(extension);
                bool softphoneRunning = SessionManager.Is3CXProcessRunning();

                if (isTerminalSession || pipeAvailable)
                {
                    diagnostics.AppendFormat("Terminal Server: Sitzung={0}, Pipe={1}, 3CX={2}",
                        isTerminalSession ? "Ja" : "Nein", pipeAvailable ? "Ja" : "Nein", softphoneRunning ? "Ja" : "Nein");
                    diagnostics.AppendLine();

                    if (isTerminalSession)
                    {
                        string reason = "Terminal server session detected";
                        LogManager.Debug("ConnectionMethodSelector: Terminal Server ausgewählt - {0}", reason);
                        diagnostics.AppendLine("Terminal Server: Ausgewählt");

                        return new ProviderSelectionResult
                        {
                            Provider = new PipeConnectionMethod(extension),
                            SelectedMode = ConnectionMode.TerminalServer,
                            Reason = reason,
                            DiagnosticSummary = diagnostics.ToString()
                        };
                    }
                }
                else
                {
                    diagnostics.AppendLine("Terminal Server: Nicht zutreffend (keine Terminal-Server-Sitzung)");
                    LogManager.Debug("ConnectionMethodSelector: [B] Terminal Server nicht anwendbar");
                }
            }
            else
            {
                LogManager.Log("3CX TAPI Treiber: Nicht installiert");
                diagnostics.AppendLine("TAPI & Terminal Server: Übersprungen (3CX TAPI nicht installiert)");
            }

            // ── (C) Try Webclient ──
            if (webclientEnabled)
            {
                progressText?.Invoke("Auto-Erkennung: Prüfe WebClient...");
                LogManager.Log("WebClient = Detection..");
                LogManager.Debug("ConnectionMethodSelector: [C] Versuche WebClient (Timeout={0}s)", webclientTimeoutSec);

                try
                {
                    var webclientProvider = new WebclientConnectionMethod(extension);
                    bool connected = await webclientProvider.TryConnectAsync(cancellationToken,
                        Math.Min(webclientTimeoutSec, totalTimeoutSec));

                    if (connected)
                    {
                        string reason = "Extension connected via WebClient (browser extension)";
                        LogManager.Debug("ConnectionMethodSelector: WebClient erkannt - {0}", reason);
                        diagnostics.AppendLine("WebClient: Erkannt (Erweiterung verbunden)");

                        return new ProviderSelectionResult
                        {
                            Provider = webclientProvider,
                            SelectedMode = ConnectionMode.WebClient,
                            Reason = reason,
                            DiagnosticSummary = diagnostics.ToString()
                        };
                    }

                    webclientProvider.Dispose();
                    diagnostics.AppendLine("WebClient: Nicht gefunden (Keine Antwort erhalten)");
                    LogManager.Debug("ConnectionMethodSelector: [C] WebClient nicht erkannt");
                    LogManager.Debug("ConnectionMethodSelector: [C] Hinweis: WebClient-Erkennung erfordert Browser-Erweiterung verbunden via WebSocket (Port {0})", AppConfig.GetInt(ConfigKeys.WebclientWebSocketPort, 19800));
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("WebClient: Fehler - " + ex.Message);
                    LogManager.Debug("ConnectionMethodSelector: [C] WebClient-Fehler - {0}", ex.Message);
                }
            }
            else
            {
                diagnostics.AppendLine("WebClient: Deaktiviert (Webclient.Enabled=false)");
                LogManager.Debug("ConnectionMethodSelector: [C] WebClient deaktiviert");
            }

            // ── (D) None detected ──
            LogManager.Debug("ConnectionMethodSelector: Kein Provider erkannt");
            diagnostics.AppendLine("Ergebnis: Kein Dienst verfügbar");

            return new ProviderSelectionResult
            {
                Provider = null,
                SelectedMode = ConnectionMode.Auto,
                Reason = "No connection method could be detected",
                DiagnosticSummary = diagnostics.ToString()
            };
        }

        public static string GetModeDescription(ConnectionMode mode)
        {
            switch (mode)
            {
                case ConnectionMode.Desktop:
                    return "Desktop (TAPI)";
                case ConnectionMode.TerminalServer:
                    return "Terminal Server (TAPI)";
                case ConnectionMode.WebClient:
                    return "WebClient (Browser-Erweiterung)";
                case ConnectionMode.Auto:
                default:
                    return "Automatisch";
            }
        }

        public static string GetModeShortName(ConnectionMode mode)
        {
            switch (mode)
            {
                case ConnectionMode.Desktop:
                    return UIStrings.SettingsLabels.ConnectionModeDesktop;
                case ConnectionMode.TerminalServer:
                    return UIStrings.SettingsLabels.ConnectionModeTerminalServer;
                case ConnectionMode.WebClient:
                    return UIStrings.SettingsLabels.ConnectionModeWebclient;
                case ConnectionMode.Auto:
                default:
                    return UIStrings.SettingsLabels.ConnectionModeAuto;
            }
        }
    }
}
