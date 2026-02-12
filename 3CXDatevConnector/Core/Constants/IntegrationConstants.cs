namespace DatevConnector.Core.Constants
{
    /// <summary>
    /// Centralized integration constants for TAPI, DATEV, and Bridge operations.
    /// Provides consistent configuration values across all integration layers.
    /// </summary>
    public static class IntegrationConstants
    {
        /// <summary>
        /// TAPI-related constants
        /// </summary>
        public static class Tapi
        {
            /// <summary>Named pipe format for 3CX TSP server</summary>
            public const string PipeNameFormat = @"\\.\pipe\3CX_tsp_server_{0}";

            /// <summary>Separator between extension and name in line name</summary>
            public const string LineNameSeparator = " : ";

            /// <summary>Default timeout for line open operations (ms)</summary>
            public const int DefaultLineOpenTimeout = 5000;

            /// <summary>Maximum retry attempts for transient errors</summary>
            public const int MaxRetryAttempts = 4;

            /// <summary>Initial retry delay (ms)</summary>
            public const int InitialRetryDelayMs = 500;

            /// <summary>Maximum retry delay (ms)</summary>
            public const int MaxRetryDelayMs = 4000;

            /// <summary>Default line name filter for 3CX lines</summary>
            public const string DefaultLineFilter = "3CX";
        }

        /// <summary>
        /// DATEV-related constants
        /// </summary>
        public static class Datev
        {
            /// <summary>DATEV Portal process name</summary>
            public const string PortalProcessName = "Datev.Framework.Portal";

            /// <summary>DATEV Telephony process name</summary>
            public const string TelephonyProcessName = "Datev.Telephony";

            /// <summary>Data source name for recipients</summary>
            public const string DataSourceRecipients = "DATEV_Adressaten";

            /// <summary>Data source name for institutions</summary>
            public const string DataSourceInstitutions = "DATEV_Institutionen";

            /// <summary>External data source identifier</summary>
            public const string DataSourceExternal = "3CX";

            /// <summary>Filter for active contacts only</summary>
            public const string ActiveContactsFilter = "@Status NOT EQUAL TO \"0\"";

            /// <summary>ROT moniker prefix for DATEV CTI</summary>
            public const string RotMonikerPrefix = "!{";
        }

        /// <summary>
        /// Call ID generation constants
        /// </summary>
        public static class CallId
        {
            /// <summary>Call ID format string</summary>
            public const string Format = "{0}-{1:ddMMyyyy}-{1:HHmm}-{2}";

            /// <summary>Number of random digits in call ID</summary>
            public const int RandomDigits = 7;
        }

        /// <summary>
        /// Timeout values (in milliseconds)
        /// </summary>
        public static class Timeouts
        {
            /// <summary>Connection timeout (ms)</summary>
            public const int ConnectionMs = 30000;

            /// <summary>Read operation timeout (ms)</summary>
            public const int ReadMs = 60000;

            /// <summary>Write operation timeout (ms)</summary>
            public const int WriteMs = 30000;

            /// <summary>Reconnection attempt interval (ms)</summary>
            public const int ReconnectIntervalMs = 5000;

            /// <summary>Status update fallback interval (ms)</summary>
            public const int StatusUpdateFallbackMs = 6000;

            /// <summary>Message loop wait timeout (ms)</summary>
            public const int MessageLoopWaitMs = 500;

            /// <summary>Async task default timeout (ms)</summary>
            public const int AsyncTaskDefaultMs = 10000;
        }

        /// <summary>
        /// Circuit breaker settings
        /// </summary>
        public static class CircuitBreaker
        {
            /// <summary>Default failure threshold before opening circuit</summary>
            public const int DefaultThreshold = 3;

            /// <summary>Default timeout in seconds before attempting reset</summary>
            public const int DefaultTimeoutSeconds = 30;

            /// <summary>Maximum timeout in seconds</summary>
            public const int MaxTimeoutSeconds = 300;
        }

        /// <summary>
        /// Retry policy settings
        /// </summary>
        public static class Retry
        {
            /// <summary>Maximum retry attempts for SDD operations</summary>
            public const int SddMaxAttempts = 3;

            /// <summary>Initial delay between SDD retries (seconds)</summary>
            public const int SddDelaySeconds = 1;

            /// <summary>Backoff multiplier for exponential retry</summary>
            public const double BackoffMultiplier = 2.0;

            /// <summary>Maximum delay cap (seconds)</summary>
            public const int MaxDelaySeconds = 30;
        }

        /// <summary>
        /// Phone number handling constants
        /// </summary>
        public static class PhoneNumber
        {
            /// <summary>Default minimum caller ID length for matching</summary>
            public const int DefaultMinLength = 2;

            /// <summary>Default maximum digits to compare</summary>
            public const int DefaultMaxCompareLength = 10;

            /// <summary>International prefix (00)</summary>
            public const string InternationalPrefix = "00";

            /// <summary>Plus prefix (+)</summary>
            public const string PlusPrefix = "+";
        }

        /// <summary>
        /// Call history settings
        /// </summary>
        public static class CallHistory
        {
            /// <summary>Default maximum entries in call history</summary>
            public const int DefaultMaxEntries = 5;

            /// <summary>Maximum allowed entries</summary>
            public const int MaxAllowedEntries = 20;

            /// <summary>Default stale call timeout (minutes)</summary>
            public const int StaleCallTimeoutMinutes = 60;
        }

        /// <summary>
        /// Popup behavior settings
        /// </summary>
        public static class Popup
        {
            /// <summary>Default contact reshow delay (seconds)</summary>
            public const int DefaultReshowDelaySeconds = 3;

            /// <summary>Default last contact routing memory (minutes)</summary>
            public const int DefaultLastContactRoutingMinutes = 30;

            /// <summary>Balloon notification display time (ms)</summary>
            public const int BalloonDisplayTimeMs = 3000;
        }
    }
}
