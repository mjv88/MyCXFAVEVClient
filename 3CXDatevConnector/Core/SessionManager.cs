using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Detects terminal server sessions and provides session metadata.
    /// Windows ROT is already per-session, so no GUID modification is needed.
    /// </summary>
    internal static class SessionManager
    {
        private static int _sessionId = -1;
        private static bool _isTerminalSession;

        // Known 3CX desktop app process names (without .exe)
        private static readonly string[] ThreeCXProcessNames = new[]
        {
            "3CXDesktopApp",
            "3CXWin8Phone",
            "3CXPhone",
            "3CX Phone"
        };

        /// <summary>
        /// Current Windows session ID
        /// </summary>
        internal static int SessionId
        {
            get
            {
                if (_sessionId < 0)
                    Initialize();
                return _sessionId;
            }
        }

        /// <summary>
        /// Whether running in a terminal server (RDP) session
        /// </summary>
        internal static bool IsTerminalSession
        {
            get
            {
                if (_sessionId < 0)
                    Initialize();
                return _isTerminalSession;
            }
        }

        /// <summary>
        /// Session key for path resolution and routing.
        /// Returns "Session_{id}" on terminal servers, "Console" otherwise.
        /// </summary>
        internal static string CurrentSessionKey
        {
            get
            {
                if (_sessionId < 0)
                    Initialize();
                return _isTerminalSession
                    ? string.Format("Session_{0}", _sessionId)
                    : "Console";
            }
        }

        private static void Initialize()
        {
            try
            {
                _sessionId = Process.GetCurrentProcess().SessionId;
                // Session 0 is the services session, sessions > 0 on a TS are user sessions
                // Console session (local login) is typically session 1 on modern Windows
                // On a terminal server, multiple sessions > 0 exist simultaneously
                _isTerminalSession = _sessionId > 0 && Environment.GetEnvironmentVariable("SESSIONNAME") != "Console";
            }
            catch (Exception ex)
            {
                LogManager.Log("SessionManager: Error getting session info - {0}", ex.Message);
                _sessionId = 0;
                _isTerminalSession = false;
            }
        }

        /// <summary>
        /// Log session information at startup
        /// </summary>
        internal static void LogSessionInfo()
        {
            LogManager.Log("Terminal Server = {0}", IsTerminalSession);
        }

        /// <summary>
        /// Check if a 3CX desktop app process is running in the current session.
        /// The 3CX TAPI TSP requires the dialer/softphone to be active.
        /// </summary>
        internal static bool Is3CXProcessRunning()
        {
            try
            {
                int currentSession = SessionId;
                foreach (string name in ThreeCXProcessNames)
                {
                    var procs = Process.GetProcessesByName(name);
                    try
                    {
                        if (procs.Any(p => p.SessionId == currentSession))
                            return true;
                    }
                    finally
                    {
                        foreach (var p in procs)
                            p.Dispose();
                    }
                }

                // Also check for any process starting with "3CX" in our session
                var allProcs = Process.GetProcesses();
                try
                {
                    return allProcs.Any(p =>
                        p.SessionId == currentSession &&
                        p.ProcessName.StartsWith("3CX", StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    foreach (var p in allProcs)
                        p.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug("Error checking 3CX process: {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Check if the 3CX TAPI named pipe exists for a given extension.
        /// The pipe "3CX_tsp_server_{extension}" is created by the 3CX dialer.
        /// </summary>
        internal static bool Is3CXPipeAvailable(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return false;

            try
            {
                string pipeName = "3CX_tsp_server_" + extension;
                // Check if the pipe exists by looking in the pipe filesystem
                string pipePath = Path.Combine(@"\\.\pipe", pipeName);
                return File.Exists(pipePath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Log diagnostic information about 3CX TAPI readiness.
        /// Call when LINEERR_NOTREGISTERED is encountered.
        /// </summary>
        internal static void Log3CXTapiDiagnostics(string extension)
        {
            bool is3CXRunning = Is3CXProcessRunning();
            bool isPipeAvailable = Is3CXPipeAvailable(extension);

            LogManager.Warning("=== 3CX TAPI Diagnostik ===");
            LogManager.Warning("  Session: {0} ({1})",
                CurrentSessionKey,
                IsTerminalSession ? "Terminal Server / RDP" : "Lokale Konsole");
            LogManager.Warning("  3CX Prozess läuft: {0}", is3CXRunning ? "Ja" : "Nein");
            LogManager.Warning("  3CX TAPI Pipe ({0}): {1}",
                "3CX_tsp_server_" + (extension ?? "?"),
                isPipeAvailable ? "Verfügbar" : "Nicht gefunden");

            if (!is3CXRunning)
            {
                LogManager.Warning("  -> 3CX Desktop App muss in dieser Sitzung gestartet werden!");
                if (IsTerminalSession)
                    LogManager.Warning("  -> Auf dem Terminal Server muss die 3CX App pro RDP-Sitzung laufen.");
            }
            else if (!isPipeAvailable)
            {
                LogManager.Warning("  -> 3CX läuft, aber TAPI-Pipe nicht gefunden. TAPI-Treiber prüfen.");
            }
            else
            {
                LogManager.Warning("  -> 3CX und Pipe vorhanden, aber TSP meldet 'nicht registriert'. Neustart der 3CX App versuchen.");
            }

            LogManager.Warning("===========================");
        }
    }
}
