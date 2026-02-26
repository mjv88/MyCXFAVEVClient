using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;

namespace DatevConnector.Core
{
    public static class CallStateMachine
    {
        public static bool IsValidTransition(TapiCallState from, TapiCallState to)
        {
            switch (from)
            {
                case TapiCallState.Initializing:
                    // Can go to Ringing, Ringback, or Connected (direct connect)
                    return to == TapiCallState.Ringing 
                        || to == TapiCallState.Ringback
                        || to == TapiCallState.Connected
                        || to == TapiCallState.Disconnected;

                case TapiCallState.Ringing:
                    // Can go to Connected or Disconnected (missed call)
                    return to == TapiCallState.Connected 
                        || to == TapiCallState.Disconnected;

                case TapiCallState.Ringback:
                    // Can go to Connected or Disconnected (no answer)
                    return to == TapiCallState.Connected 
                        || to == TapiCallState.Disconnected;

                case TapiCallState.Connected:
                    // Can only go to Disconnected
                    return to == TapiCallState.Disconnected;

                case TapiCallState.Disconnected:
                    // Terminal state - no transitions allowed
                    return false;

                default:
                    return false;
            }
        }

        public static bool TryTransition(CallRecord record, TapiCallState newState)
        {
            if (record == null)
                return false;

            lock (record.SyncLock)
            {
                TapiCallState currentState = record.TapiState;

                if (IsValidTransition(currentState, newState))
                {
                    record.TapiState = newState;
                    LogManager.Log("Connector: Call {0} state {1} -> {2}",
                        record.TapiCallId, currentState, newState);
                    return true;
                }
                else
                {
                    LogManager.Log("Connector: Anruf {0} ungültiger Status {1} -> {2} (ignoriert)",
                        record.TapiCallId, currentState, newState);
                    return false;
                }
            }
        }
    }
}
