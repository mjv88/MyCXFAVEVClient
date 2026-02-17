using System;
using DatevConnector.Datev.COMs;
using DatevConnector.Tapi;

namespace DatevConnector.Core
{
    public class CallRecord
    {
        /// <summary>
        /// TAPI call ID (from 3CX)
        /// </summary>
        public string TapiCallId { get; set; }

        public bool IsIncoming { get; set; }

        /// <summary>
        /// Remote party number (caller for incoming, called for outgoing)
        /// </summary>
        public string RemoteNumber { get; set; }

        /// <summary>
        /// When the call started (ringing/dialing)
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// When the call connected (answered)
        /// </summary>
        public DateTime? ConnectedTime { get; set; }

        public DateTime? EndTime { get; set; }

        /// <summary>
        /// Current call state (for DATEV)
        /// </summary>
        public ENUM_CALLSTATE State { get; set; }

        /// <summary>
        /// Current TAPI call state (for state machine validation)
        /// </summary>
        public TapiCallState TapiState { get; set; }

        public bool WasConnected => ConnectedTime.HasValue;

        public CallData CallData { get; set; }

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
