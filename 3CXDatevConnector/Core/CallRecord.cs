using System;
using DatevConnector.Datev.COMs;
using DatevConnector.Tapi;

namespace DatevConnector.Core
{
    public class CallRecord
    {
        public readonly object SyncLock = new object();

        public string TapiCallId { get; set; }

        public bool IsIncoming { get; set; }

        public string RemoteNumber { get; set; }

        public DateTime StartTime { get; set; }

        public DateTime? ConnectedTime { get; set; }

        public DateTime? EndTime { get; set; }

        public ENUM_CALLSTATE State { get; set; }

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

        public TimeSpan? GetDuration()
        {
            if (!EndTime.HasValue)
                return null;

            DateTime start = ConnectedTime ?? StartTime;
            return EndTime.Value - start;
        }
    }
}
