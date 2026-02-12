namespace DatevConnector.Tapi
{
    /// <summary>
    /// Internal call states for tracking
    /// </summary>
    public enum TapiCallState
    {
        /// <summary>
        /// Initial state - call just created
        /// </summary>
        Initializing,

        /// <summary>
        /// Incoming call ringing locally
        /// </summary>
        Ringing,

        /// <summary>
        /// Outgoing call - remote party ringing
        /// </summary>
        Ringback,

        /// <summary>
        /// Call is connected/active
        /// </summary>
        Connected,

        /// <summary>
        /// Call has ended
        /// </summary>
        Disconnected
    }
}
