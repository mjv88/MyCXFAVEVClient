using System;
using Datev.Cti.Buddylib;
using DatevBridge.Datev.COMs;
using DatevBridge.Tapi;

namespace DatevBridge.Core
{
    /// <summary>
    /// Tracks state for a single call
    /// </summary>
    public class CallRecord
    {
        /// <summary>
        /// TAPI call ID (from 3CX)
        /// </summary>
        public string TapiCallId { get; set; }

        /// <summary>
        /// Is this an incoming call
        /// </summary>
        public bool IsIncoming { get; set; }

        /// <summary>
        /// Remote party number (caller for incoming, called for outgoing)
        /// </summary>
        public string RemoteNumber { get; set; }

        /// <summary>
        /// Remote party name (if provided by 3CX)
        /// </summary>
        public string RemoteName { get; set; }

        /// <summary>
        /// Local extension number
        /// </summary>
        public string LocalNumber { get; set; }

        /// <summary>
        /// When the call started (ringing/dialing)
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// When the call connected (answered)
        /// </summary>
        public DateTime? ConnectedTime { get; set; }

        /// <summary>
        /// When the call ended
        /// </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// Current call state (for DATEV)
        /// </summary>
        public ENUM_CALLSTATE State { get; set; }

        /// <summary>
        /// Current TAPI call state (for state machine validation)
        /// </summary>
        public TapiCallState TapiState { get; set; }

        /// <summary>
        /// Was the call ever connected
        /// </summary>
        public bool WasConnected => ConnectedTime.HasValue;

        /// <summary>
        /// The CallData object for DATEV notifications
        /// </summary>
        public CallData CallData { get; set; }

        /// <summary>
        /// Create a new call record
        /// </summary>
        public CallRecord(string tapiCallId, bool isIncoming)
        {
            TapiCallId = tapiCallId;
            IsIncoming = isIncoming;
            StartTime = DateTime.Now;
            State = ENUM_CALLSTATE.eCSOffered;
            TapiState = TapiCallState.Initializing;
        }

        /// <summary>
        /// Calculate call duration (from connect to end, or start to end if never connected)
        /// </summary>
        public TimeSpan? GetDuration()
        {
            if (!EndTime.HasValue)
                return null;

            DateTime start = ConnectedTime ?? StartTime;
            return EndTime.Value - start;
        }
    }
}
