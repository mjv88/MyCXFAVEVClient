using System;
using System.Threading.Tasks;
using Datev.Cti.Buddylib;
using DatevConnector.Datev;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.Enums;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Tapi;

namespace DatevConnector.Core
{
    /// <summary>
    /// Handles DATEV Click-to-Dial and Click-to-Drop commands.
    /// Extracted from ConnectorService to reduce its size and keep a single responsibility per class.
    /// </summary>
    internal class DatevCommandHandler
    {
        private readonly CallTracker _callTracker;
        private readonly Func<ITelephonyProvider> _getProvider;

        public DatevCommandHandler(CallTracker callTracker, Func<ITelephonyProvider> getProvider)
        {
            _callTracker = callTracker;
            _getProvider = getProvider;
        }

        /// <summary>
        /// Entry point wired into DatevAdapter. Dispatches to the correct handler
        /// on a background thread with proper exception handling.
        /// </summary>
        public void OnDatevEvent(IDatevCtiData ctiData, DatevEventType eventType)
        {
            Task.Run(async () =>
            {
                try
                {
                    switch (eventType)
                    {
                        case DatevEventType.Dial:
                            await HandleDialCommandAsync(ctiData);
                            break;

                        case DatevEventType.Drop:
                            await HandleDropCommandAsync(ctiData);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Log("Fehler bei DATEV Ereignis {0}: {1}", eventType, ex);
                }
            });
        }

        private Task HandleDialCommandAsync(IDatevCtiData ctiData)
        {
            string destination = ctiData.CalledNumber;

            if (string.IsNullOrWhiteSpace(destination))
            {
                LogManager.Log("DATEV Dial: Keine Zielnummer angegeben");
                return Task.CompletedTask;
            }

            LogManager.Log("DATEV Dial: Anruf einleiten an {0} (SyncID={1}, Kontakt={2})",
                LogManager.Mask(destination), ctiData.SyncID ?? "null", ctiData.Adressatenname ?? "null");

            var provider = _getProvider();
            if (provider == null || !provider.IsMonitoring)
            {
                LogManager.Log("DATEV Dial: Nicht verbunden (Provider={0}, Monitoring={1})",
                    provider?.GetType().Name ?? "null",
                    provider?.IsMonitoring.ToString() ?? "N/A");
                return Task.CompletedTask;
            }

            // Preserve DATEV-provided data (SyncID, contact, datasource) for when TAPI event fires
            ctiData.CallState = ENUM_CALLSTATE.eCSOffered;
            ctiData.Direction = ENUM_DIRECTION.eDirOutgoing;
            ctiData.Begin = DateTime.Now;
            ctiData.End = DateTime.Now;

            var preservedData = CallDataManager.CreateFromDatev(ctiData);

            // Store as pending call so HandleRingback can find it by number
            string tempId = _callTracker.GenerateTempCallId();
            var pendingRecord = _callTracker.AddPendingCall(tempId, isIncoming: false);
            pendingRecord.RemoteNumber = destination;
            pendingRecord.CallData = preservedData;
            _callTracker.UpdatePendingPhoneIndex(tempId, destination);

            LogManager.Log("DATEV Dial: Verbunden={0}", provider.IsMonitoring);
            int result = provider.MakeCall(destination);
            if (result <= 0)
            {
                LogManager.Log("DATEV Dial: MakeCall fehlgeschlagen (Ergebnis={0}, Provider={1})",
                    result, provider.GetType().Name);
                _callTracker.RemovePendingCall(tempId);
            }
            else
            {
                LogManager.Log("DATEV Dial: MakeCall gesendet (Ergebnis={0})", result);
            }

            return Task.CompletedTask;
        }

        private Task HandleDropCommandAsync(IDatevCtiData ctiData)
        {
            string datevCallId = ctiData.CallID;

            if (string.IsNullOrWhiteSpace(datevCallId))
            {
                LogManager.Log("DATEV Drop: Keine CallID angegeben");
                return Task.CompletedTask;
            }

            LogManager.Log("DATEV Drop: Beende Anruf {0}", datevCallId);

            var record = _callTracker.FindCallByDatevCallId(datevCallId);
            if (record == null)
            {
                LogManager.Log("DATEV Drop: Anruf {0} nicht gefunden", datevCallId);
                return Task.CompletedTask;
            }

            var provider = _getProvider();
            if (provider == null || !provider.IsMonitoring)
            {
                LogManager.Log("DATEV Drop: TAPI nicht verbunden, Anruf kann nicht beendet werden");
                return Task.CompletedTask;
            }

            // Find the TAPI call handle by call ID
            int tapiCallId;
            if (int.TryParse(record.TapiCallId, out tapiCallId))
            {
                var callEvent = provider.FindCallById(tapiCallId);
                if (callEvent != null)
                {
                    provider.DropCall(callEvent.CallHandle);
                    LogManager.Log("DATEV Drop: lineDrop aufgerufen für {0} (tapiId={1})", datevCallId, tapiCallId);
                }
                else
                {
                    LogManager.Log("DATEV Drop: Call Handle nicht gefunden für tapiId={0}", tapiCallId);
                }
            }
            else
            {
                LogManager.Log("DATEV Drop: Ungültige TapiCallId: {0}", record.TapiCallId);
            }

            return Task.CompletedTask;
        }
    }
}
