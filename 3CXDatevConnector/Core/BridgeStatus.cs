namespace DatevConnector.Core
{
    /// <summary>
    /// Bridge connection status
    /// </summary>
    public enum BridgeStatus
    {
        /// <summary>
        /// Not connected to 3CX
        /// </summary>
        Disconnected,

        /// <summary>
        /// Attempting to connect
        /// </summary>
        Connecting,

        /// <summary>
        /// Connected and operational
        /// </summary>
        Connected
    }
}
