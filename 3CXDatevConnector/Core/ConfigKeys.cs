namespace DatevConnector.Core
{
    /// <summary>
    /// Centralized configuration key constants for INI config settings.
    /// Using constants prevents typos and enables refactoring safety.
    /// </summary>
    public static class ConfigKeys
    {
        // ===== General =====
        public const string ExtensionNumber = "ExtensionNumber";

        // ===== Journaling =====
        public const string EnableJournaling = "EnableJournaling";
        public const string EnableJournalPopup = "EnableJournalPopup";
        public const string EnableJournalPopupOutbound = "EnableJournalPopupOutbound";

        // ===== Caller Popup =====
        public const string EnableCallerPopup = "EnableCallerPopup";
        public const string EnableCallerPopupOutbound = "EnableCallerPopupOutbound";
        public const string CallerPopupMode = "CallerPopupMode";

        // ===== Call Handling =====
        public const string MinCallerIdLength = "MinCallerIdLength";
        public const string ContactReshowDelaySeconds = "ContactReshowDelaySeconds";
        public const string TapiLineFilter = "TapiLineFilter";

        // ===== Call History =====
        public const string CallHistoryInbound = "CallHistoryInbound";
        public const string CallHistoryOutbound = "CallHistoryOutbound";
        public const string CallHistoryMaxEntries = "CallHistoryMaxEntries";

        // ===== Contact Routing =====
        public const string LastContactRoutingMinutes = "LastContactRoutingMinutes";

        // ===== Stale Call Cleanup =====
        public const string StaleCallTimeoutMinutes = "StaleCallTimeoutMinutes";
        public const string StalePendingTimeoutSeconds = "StalePendingTimeoutSeconds";

        // ===== Connection =====
        public const string ReconnectIntervalSeconds = "ReconnectIntervalSeconds";
        public const string ConnectionTimeoutSeconds = "ConnectionTimeoutSeconds";
        public const string ReadTimeoutSeconds = "ReadTimeoutSeconds";
        public const string WriteTimeoutSeconds = "WriteTimeoutSeconds";

        // ===== Contact Loading =====
        public const string ContactLoadTimeoutSeconds = "ContactLoadTimeoutSeconds";

        // ===== Retry / Resilience =====
        public const string SddMaxRetries = "SddMaxRetries";
        public const string SddRetryDelaySeconds = "SddRetryDelaySeconds";
        public const string DatevCircuitBreakerThreshold = "DatevCircuitBreakerThreshold";
        public const string DatevCircuitBreakerTimeoutSeconds = "DatevCircuitBreakerTimeoutSeconds";

        // ===== Logging =====
        public const string LogLevel = "LogLevel";
        public const string DebugLogging = "DebugLogging";
        public const string LogMaxSizeMB = "LogMaxSizeMB";
        public const string LogMaxFiles = "LogMaxFiles";
        public const string LogAsync = "LogAsync";
        public const string LogMaskDigits = "LogMaskDigits";

        // ===== DATEV Cache =====
        public const string MaxCompareLength = "MaxCompareLength";

        // ===== DATEV Contacts =====
        public const string ActiveContactsOnly = "ActiveContactsOnly";

        // ===== Tray Behavior =====
        public const string TrayDoubleClickCallHistory = "TrayDoubleClickCallHistory";

        // ===== Notifications =====
        public const string MuteNotifications = "MuteNotifications";

        // ===== Connection Mode =====
        public const string ConnectionMode = "TelephonyMode";
        public const string TelephonyMode = "TelephonyMode";
        public const string AutoDetectionTimeoutSec = "Auto.DetectionTimeoutSec";
        public const string WebclientConnectTimeoutSec = "Webclient.ConnectTimeoutSec";
        public const string WebclientEnabled = "Webclient.Enabled";
        public const string WebclientWebSocketPort = "Webclient.WebSocketPort";
    }
}
