namespace DatevConnector.Core
{
    /// <summary>
    /// Connector connection status
    /// </summary>
    public enum ConnectorStatus
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
