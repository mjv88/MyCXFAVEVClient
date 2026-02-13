namespace DatevConnector.Core
{
    /// <summary>
    /// Telephony provider mode selection.
    /// Auto (default) attempts detection in priority order: Webclient -> Pipe -> TAPI.
    /// </summary>
    public enum TelephonyMode
    {
        /// <summary>
        /// Auto-detect the best available provider at startup.
        /// Priority: Webclient (browser extension) -> Pipe (3CX Softphone) -> TAPI (desktop).
        /// </summary>
        Auto,

        /// <summary>
        /// TAPI 2.x via 3CX Multi-Line TAPI driver (desktop environments).
        /// </summary>
        Tapi,

        /// <summary>
        /// Named Pipe protocol with 3CX Softphone (terminal server environments).
        /// </summary>
        Pipe,

        /// <summary>
        /// Browser extension via Native Messaging (3CX Webclient / WebRTC only).
        /// </summary>
        WebClient
    }
}
