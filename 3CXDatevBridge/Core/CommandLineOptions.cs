using System;

namespace DatevBridge.Core
{
    /// <summary>
    /// Command line options parser for 3CX-DATEV Bridge.
    /// Supports silent mode, custom config, and other deployment options.
    /// </summary>
    public class CommandLineOptions
    {
        /// <summary>Start without tray balloon notification</summary>
        public bool Silent { get; set; }

        /// <summary>Start without showing status window</summary>
        public bool Minimized { get; set; }

        /// <summary>Path to custom configuration file</summary>
        public string ConfigPath { get; set; }

        /// <summary>Custom log directory path</summary>
        public string LogDirectory { get; set; }

        /// <summary>Reset settings to defaults and exit</summary>
        public bool Reset { get; set; }

        /// <summary>Show help/usage information and exit</summary>
        public bool ShowHelp { get; set; }

        /// <summary>Enable verbose/debug logging</summary>
        public bool Verbose { get; set; }

        /// <summary>
        /// Parse command line arguments into options
        /// </summary>
        /// <param name="args">Command line arguments</param>
        /// <returns>Parsed options</returns>
        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();

            if (args == null || args.Length == 0)
                return options;

            foreach (var arg in args)
            {
                var lower = arg.ToLowerInvariant();

                if (lower == "/silent" || lower == "-silent" || lower == "--silent")
                {
                    options.Silent = true;
                }
                else if (lower == "/minimized" || lower == "-minimized" || lower == "--minimized")
                {
                    options.Minimized = true;
                }
                else if (lower == "/reset" || lower == "-reset" || lower == "--reset")
                {
                    options.Reset = true;
                }
                else if (lower == "/help" || lower == "-help" || lower == "--help" || lower == "/?")
                {
                    options.ShowHelp = true;
                }
                else if (lower == "/verbose" || lower == "-verbose" || lower == "--verbose" || lower == "/v" || lower == "-v")
                {
                    options.Verbose = true;
                }
                else if (lower.StartsWith("/config=") || lower.StartsWith("-config=") || lower.StartsWith("--config="))
                {
                    options.ConfigPath = GetValueAfterEquals(arg);
                }
                else if (lower.StartsWith("/logdir=") || lower.StartsWith("-logdir=") || lower.StartsWith("--logdir="))
                {
                    options.LogDirectory = GetValueAfterEquals(arg);
                }
            }

            return options;
        }

        private static string GetValueAfterEquals(string arg)
        {
            int equalsIndex = arg.IndexOf('=');
            if (equalsIndex >= 0 && equalsIndex < arg.Length - 1)
            {
                var value = arg.Substring(equalsIndex + 1);
                // Remove surrounding quotes if present
                if (value.StartsWith("\"") && value.EndsWith("\""))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                return value;
            }
            return null;
        }

        /// <summary>
        /// Get help text showing usage information
        /// </summary>
        public static string GetHelpText()
        {
            return @"3CX-DATEV Bridge - Kommandozeilenoptionen

Verwendung: 3CXDatevBridge.exe [Optionen]

Optionen:
  /silent       Startet ohne Tray-Benachrichtigung
  /minimized    Startet ohne Statusfenster anzuzeigen
  /config=X     Verwendet angegebene Konfigurationsdatei
  /logdir=X     Überschreibt Log-Verzeichnis
  /reset        Setzt Einstellungen auf Standard zurück
  /verbose      Aktiviert ausführliche Protokollierung
  /help         Zeigt diese Hilfe an

Beispiele:
  3CXDatevBridge.exe /silent /minimized
  3CXDatevBridge.exe /config=""C:\Config\custom.config""
  3CXDatevBridge.exe /logdir=""D:\Logs\3CXDatevBridge""

Für automatischen Start mit Windows:
  3CXDatevBridge.exe /silent /minimized

Hinweis: Optionen können mit /, - oder -- beginnen.
";
        }

        /// <summary>
        /// Returns a string representation for logging purposes
        /// </summary>
        public override string ToString()
        {
            var parts = new System.Collections.Generic.List<string>();

            if (Silent) parts.Add("Silent");
            if (Minimized) parts.Add("Minimized");
            if (Verbose) parts.Add("Verbose");
            if (!string.IsNullOrEmpty(ConfigPath)) parts.Add($"Config={ConfigPath}");
            if (!string.IsNullOrEmpty(LogDirectory)) parts.Add($"LogDir={LogDirectory}");

            return parts.Count > 0 ? string.Join(", ", parts) : "(default)";
        }
    }
}
