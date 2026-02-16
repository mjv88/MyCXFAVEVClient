namespace DatevConnector.Core
{
    /// <summary>
    /// Telephony provider mode selection.
    /// Auto (default) attempts detection in priority order: Desktop (TAPI) -> Terminal Server (TAPI) -> Webclient.
    /// </summary>
    public enum TelephonyMode
    {
        /// <summary>
        /// Auto-detect the best available provider at startup.
        /// Priority: Desktop (TAPI) -> Terminal Server (TAPI) -> Webclient (browser extension).
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
