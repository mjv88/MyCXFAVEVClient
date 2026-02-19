using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core.Config
{
    /// <summary>
    /// Application configuration backed by an INI file in %AppData%\3CXDATEVConnector\3CXDATEVConnector.ini.
    /// Falls back to hardcoded defaults when a key is not present in the INI file.
    /// </summary>
    public static class AppConfig
    {
        private static string _iniPath;
        private static readonly object _lock = new object();

        // ===== Hardcoded defaults (replaces App.config XML) =====
        private static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Core
            { ConfigKeys.ExtensionNumber, "" },

            // Journaling
            { ConfigKeys.EnableJournaling, "true" },
            { ConfigKeys.EnableJournalPopup, "false" },
            { ConfigKeys.EnableJournalPopupOutbound, "false" },

            // Caller Popup
            { ConfigKeys.EnableCallerPopup, "true" },
            { ConfigKeys.EnableCallerPopupOutbound, "false" },
            { ConfigKeys.CallerPopupMode, "Form" },

            // Call Handling
            { ConfigKeys.MinCallerIdLength, "2" },
            { ConfigKeys.MaxCompareLength, "10" },
            { ConfigKeys.ContactReshowDelaySeconds, "3" },
            { ConfigKeys.LastContactRoutingMinutes, "60" },

            // Call History
            { ConfigKeys.CallHistoryInbound, "true" },
            { ConfigKeys.CallHistoryOutbound, "false" },
            { ConfigKeys.CallHistoryMaxEntries, "25" },
            { ConfigKeys.CallHistoryRetentionDays, "7" },

            // Connection
            { ConfigKeys.ReconnectIntervalSeconds, "5" },
            { ConfigKeys.ConnectionTimeoutSeconds, "30" },
            { ConfigKeys.ReadTimeoutSeconds, "60" },
            { ConfigKeys.WriteTimeoutSeconds, "30" },
            { ConfigKeys.DatevCircuitBreakerThreshold, "3" },
            { ConfigKeys.DatevCircuitBreakerTimeoutSeconds, "30" },
            { ConfigKeys.SddMaxRetries, "3" },
            { ConfigKeys.SddRetryDelaySeconds, "1" },

            // Call Tracking
            { ConfigKeys.StaleCallTimeoutMinutes, "240" },
            { ConfigKeys.StalePendingTimeoutSeconds, "300" },

            // Contact Loading
            { ConfigKeys.ContactLoadTimeoutSeconds, "120" },

            // Logging
            { ConfigKeys.LogLevel, "Info" },
            { ConfigKeys.DebugLogging, "false" },
            { ConfigKeys.LogMaxSizeMB, "10" },
            { ConfigKeys.LogMaxFiles, "5" },
            { ConfigKeys.LogAsync, "true" },
            { ConfigKeys.LogRetentionDays, "7" },

            // DATEV
            { ConfigKeys.ActiveContactsOnly, "false" },

            // Connection Mode
            { ConfigKeys.TelephonyMode, "Auto" },
            { ConfigKeys.AutoDetectionTimeoutSec, "10" },
            { ConfigKeys.WebclientConnectTimeoutSec, "8" },
            { ConfigKeys.WebclientEnabled, "true" },
            { ConfigKeys.WebclientWebSocketPort, "19800" },
        };

        // Section grouping for INI file layout
        private static readonly string SectionSettings = "Settings";
        private static readonly string SectionConnection = "Connection";
        private static readonly string SectionLogging = "Logging";

        // Which section each key belongs to
        private static readonly Dictionary<string, string> KeySections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Settings
            { ConfigKeys.ExtensionNumber, SectionSettings },
            { ConfigKeys.EnableJournaling, SectionSettings },
            { ConfigKeys.EnableJournalPopup, SectionSettings },
            { ConfigKeys.EnableJournalPopupOutbound, SectionSettings },
            { ConfigKeys.EnableCallerPopup, SectionSettings },
            { ConfigKeys.EnableCallerPopupOutbound, SectionSettings },
            { ConfigKeys.CallerPopupMode, SectionSettings },
            { ConfigKeys.MinCallerIdLength, SectionSettings },
            { ConfigKeys.MaxCompareLength, SectionSettings },
            { ConfigKeys.ContactReshowDelaySeconds, SectionSettings },
            { ConfigKeys.LastContactRoutingMinutes, SectionSettings },
            { ConfigKeys.CallHistoryInbound, SectionSettings },
            { ConfigKeys.CallHistoryOutbound, SectionSettings },
            { ConfigKeys.CallHistoryMaxEntries, SectionSettings },
            { ConfigKeys.CallHistoryRetentionDays, SectionSettings },
            { ConfigKeys.ActiveContactsOnly, SectionSettings },
            { ConfigKeys.TapiLineFilter, SectionSettings },
            { ConfigKeys.TrayDoubleClickCallHistory, SectionSettings },

            // Connection
            { ConfigKeys.ReconnectIntervalSeconds, SectionConnection },
            { ConfigKeys.ConnectionTimeoutSeconds, SectionConnection },
            { ConfigKeys.ReadTimeoutSeconds, SectionConnection },
            { ConfigKeys.WriteTimeoutSeconds, SectionConnection },
            { ConfigKeys.DatevCircuitBreakerThreshold, SectionConnection },
            { ConfigKeys.DatevCircuitBreakerTimeoutSeconds, SectionConnection },
            { ConfigKeys.SddMaxRetries, SectionConnection },
            { ConfigKeys.SddRetryDelaySeconds, SectionConnection },
            { ConfigKeys.StaleCallTimeoutMinutes, SectionConnection },
            { ConfigKeys.StalePendingTimeoutSeconds, SectionConnection },
            { ConfigKeys.ContactLoadTimeoutSeconds, SectionConnection },

            // Logging
            { ConfigKeys.LogLevel, SectionLogging },
            { ConfigKeys.DebugLogging, SectionLogging },
            { ConfigKeys.LogMaxSizeMB, SectionLogging },
            { ConfigKeys.LogMaxFiles, SectionLogging },
            { ConfigKeys.LogAsync, SectionLogging },
            { ConfigKeys.LogRetentionDays, SectionLogging },

            // Connection Mode
            { ConfigKeys.TelephonyMode, SectionConnection },
            { ConfigKeys.AutoDetectionTimeoutSec, SectionConnection },
            { ConfigKeys.WebclientConnectTimeoutSec, SectionConnection },
            { ConfigKeys.WebclientEnabled, SectionConnection },
            { ConfigKeys.WebclientWebSocketPort, SectionConnection },
        };

        public static string FilePath => _iniPath;

        /// <summary>
        /// True if the INI file was created during this session (first launch).
        /// </summary>
        public static bool IsFirstRun { get; private set; }

        /// <summary>
        /// Initialize AppConfig. Must be called once at application startup,
        /// before any other config access (including LogManager).
        /// </summary>
        public static void Initialize()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "3CXDATEVConnector");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            _iniPath = Path.Combine(folder, "3CXDATEVConnector.ini");

            IniConfig.Initialize(_iniPath);

            IsFirstRun = !File.Exists(_iniPath);
            if (IsFirstRun)
                GenerateDefaultConfig();
        }

        public static string GetString(string key, string defaultValue = null)
        {
            string section = GetSection(key);
            string fallback = defaultValue ?? GetDefault(key) ?? "";

            if (string.IsNullOrEmpty(_iniPath))
                return fallback;

            string value = IniConfig.GetString(section, key, "");
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            string strDefault = GetDefault(key);
            int fallback = strDefault != null && int.TryParse(strDefault, out int d) ? d : defaultValue;

            if (string.IsNullOrEmpty(_iniPath))
                return fallback;

            string section = GetSection(key);
            string value = IniConfig.GetString(section, key, "");
            return !string.IsNullOrEmpty(value) && int.TryParse(value, out int result) ? result : fallback;
        }

        /// <summary>
        /// Get an integer value with range clamping. Logs a warning if the value is out of range.
        /// </summary>
        public static int GetIntClamped(string key, int defaultValue, int min, int max)
        {
            int raw = GetInt(key, defaultValue);
            if (raw < min || raw > max)
            {
                int clamped = Math.Max(min, Math.Min(raw, max));
                LogManager.Warning("Config '{0}' value {1} out of range [{2}..{3}], using {4}",
                    key, raw, min, max, clamped);
                return clamped;
            }
            return raw;
        }

        private static bool? ParseBool(string value) => ConfigParser.ParseBool(value);

        public static bool GetBool(string key, bool defaultValue = false)
        {
            bool fallback = ParseBool(GetDefault(key)) ?? defaultValue;

            if (string.IsNullOrEmpty(_iniPath))
                return fallback;

            string section = GetSection(key);
            string value = IniConfig.GetString(section, key, "");
            return ParseBool(value) ?? fallback;
        }

        public static T GetEnum<T>(string key, T defaultValue) where T : struct
        {
            string value = GetString(key, null);
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Reads ConnectionMode with backward compatibility for renamed enum values.
        /// Accepts legacy "Tapi" (now Desktop) and "Pipe" (now TerminalServer).
        /// </summary>
        public static ConnectionMode GetConnectionMode()
        {
            string raw = GetString(ConfigKeys.TelephonyMode, null);
            if (string.IsNullOrEmpty(raw))
                return ConnectionMode.Auto;
            if (string.Equals(raw, "Tapi", StringComparison.OrdinalIgnoreCase))
                return ConnectionMode.Desktop;
            if (string.Equals(raw, "Pipe", StringComparison.OrdinalIgnoreCase))
                return ConnectionMode.TerminalServer;
            return Enum.TryParse<ConnectionMode>(raw, ignoreCase: true, out var result)
                ? result : ConnectionMode.Auto;
        }

        public static bool Set(string key, string value)
        {
            if (string.IsNullOrEmpty(_iniPath))
                return false;

            string section = GetSection(key);
            return IniConfig.SetString(section, key, value);
        }

        public static bool SetBool(string key, bool value)
        {
            return Set(key, value ? "true" : "false");
        }

        public static bool SetInt(string key, int value)
        {
            return Set(key, value.ToString());
        }

        public static bool HasValue(string key)
        {
            string value = GetString(key, null);
            return !string.IsNullOrEmpty(value);
        }

        public static string GetDefault(string key)
        {
            string value;
            return Defaults.TryGetValue(key, out value) ? value : null;
        }

        private static string GetSection(string key)
        {
            string section;
            return KeySections.TryGetValue(key, out section) ? section : SectionSettings;
        }

        /// <summary>
        /// Write "Key=DefaultValue" for the given config key.
        /// </summary>
        private static string DefaultLine(string key)
        {
            string value;
            Defaults.TryGetValue(key, out value);
            return string.Format("{0}={1}", key, value ?? "");
        }

        /// <summary>
        /// Generate the default INI config file with all settings and documentation.
        /// Values are pulled from the Defaults dictionary to avoid duplication.
        /// </summary>
        private static void GenerateDefaultConfig()
        {
            try
            {
                using (var writer = new StreamWriter(_iniPath, false, Encoding.UTF8))
                {
                    writer.WriteLine("// 3CX - DATEV Connector Configuration");
                    writer.WriteLine("// Edit values below. Delete a line to restore its default.");
                    writer.WriteLine();

                    writer.WriteLine("[Settings]");
                    writer.WriteLine("// Extension number (auto-detected from TAPI if empty)");
                    writer.WriteLine(DefaultLine(ConfigKeys.ExtensionNumber));
                    writer.WriteLine();
                    writer.WriteLine("// Journaling");
                    writer.WriteLine(DefaultLine(ConfigKeys.EnableJournaling));
                    writer.WriteLine(DefaultLine(ConfigKeys.EnableJournalPopup));
                    writer.WriteLine(DefaultLine(ConfigKeys.EnableJournalPopupOutbound));
                    writer.WriteLine();
                    writer.WriteLine("// Call Pop-Up");
                    writer.WriteLine(DefaultLine(ConfigKeys.EnableCallerPopup));
                    writer.WriteLine(DefaultLine(ConfigKeys.EnableCallerPopupOutbound));
                    writer.WriteLine("// CallerPopupMode: Both, Form, Balloon");
                    writer.WriteLine(DefaultLine(ConfigKeys.CallerPopupMode));
                    writer.WriteLine();
                    writer.WriteLine("// Contact matching");
                    writer.WriteLine(DefaultLine(ConfigKeys.MinCallerIdLength));
                    writer.WriteLine(DefaultLine(ConfigKeys.MaxCompareLength));
                    writer.WriteLine(DefaultLine(ConfigKeys.ContactReshowDelaySeconds));
                    writer.WriteLine(DefaultLine(ConfigKeys.LastContactRoutingMinutes));
                    writer.WriteLine();
                    writer.WriteLine("// Call History");
                    writer.WriteLine(DefaultLine(ConfigKeys.CallHistoryInbound));
                    writer.WriteLine(DefaultLine(ConfigKeys.CallHistoryOutbound));
                    writer.WriteLine(DefaultLine(ConfigKeys.CallHistoryMaxEntries));
                    writer.WriteLine("// Aufbewahrung in Tagen (1-90)");
                    writer.WriteLine(DefaultLine(ConfigKeys.CallHistoryRetentionDays));
                    writer.WriteLine();
                    writer.WriteLine("// DATEV Contacts");
                    writer.WriteLine(DefaultLine(ConfigKeys.ActiveContactsOnly));
                    writer.WriteLine();

                    writer.WriteLine("[Connection]");
                    writer.WriteLine("// TelephonyMode: Auto, Desktop, TerminalServer, WebClient");
                    writer.WriteLine("// Auto = detect best provider at startup (Desktop -> Terminal Server -> WebClient)");
                    writer.WriteLine(DefaultLine(ConfigKeys.TelephonyMode));
                    writer.WriteLine("// Reconnect interval in seconds when connection is lost");
                    writer.WriteLine(DefaultLine(ConfigKeys.ReconnectIntervalSeconds));
                    writer.WriteLine("// Auto-detection timeout in seconds");
                    writer.WriteLine(DefaultLine(ConfigKeys.AutoDetectionTimeoutSec));
                    writer.WriteLine("// Webclient extension connect timeout in seconds");
                    writer.WriteLine(DefaultLine(ConfigKeys.WebclientConnectTimeoutSec));
                    writer.WriteLine("// Enable Webclient mode (browser extension via WebSocket)");
                    writer.WriteLine(DefaultLine(ConfigKeys.WebclientEnabled));
                    writer.WriteLine();

                    writer.WriteLine("[Logging]");
                    writer.WriteLine("// Log-Aufbewahrung in Tagen (1-90)");
                    writer.WriteLine(DefaultLine(ConfigKeys.LogRetentionDays));
                }
            }
            catch
            {
                // Silently fail - defaults will be used
            }
        }
    }
}
