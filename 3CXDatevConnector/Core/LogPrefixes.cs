namespace DatevConnector.Core
{
    /// <summary>
    /// Consistent log message prefixes for structured logging.
    /// Use these prefixes at the start of log messages for easy filtering and readability.
    ///
    /// Usage:
    /// LogManager.Log($"[{LogPrefixes.DatevToBridge}] Contact lookup for {number}");
    /// </summary>
    public static class LogPrefixes
    {
        /// <summary>Messages received from DATEV (callbacks, notifications)</summary>
        public const string DatevToBridge = "DATEV -> Bridge";

        /// <summary>Messages sent to DATEV (journal entries, lookups)</summary>
        public const string BridgeToDatev = "Bridge -> DATEV";

        /// <summary>TAPI events and call state changes</summary>
        public const string TapiEvent = "TAPI";

        /// <summary>User-initiated actions (button clicks, menu selections)</summary>
        public const string UserAction = "User";

        /// <summary>System events (startup, shutdown, timers)</summary>
        public const string System = "System";

        /// <summary>Configuration changes and loading</summary>
        public const string Config = "Config";

        /// <summary>Error conditions (should also use LogManager.Warning or Error)</summary>
        public const string Error = "ERROR";

        /// <summary>Settings form actions</summary>
        public const string Settings = "Settings";

        /// <summary>Contact cache operations</summary>
        public const string Cache = "Cache";

        /// <summary>Circuit breaker state changes</summary>
        public const string CircuitBreaker = "CircuitBreaker";

        /// <summary>Session management events</summary>
        public const string Session = "Session";

        /// <summary>Popup and notification events</summary>
        public const string Notification = "Notification";

        /// <summary>Journal entry operations</summary>
        public const string Journal = "Journal";

        /// <summary>Call history operations</summary>
        public const string CallHistory = "CallHistory";
    }
}
