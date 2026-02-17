using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DatevConnector.Core.Config;
using DatevConnector.Datev;
using DatevConnector.Datev.COMs;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Interop;
using DatevConnector.Tapi;
using DatevConnector.UI;

namespace DatevConnector.Core
{
    /// <summary>
    /// Handles TAPI call state events (Offering, Ringback, Connected, Disconnected)
    /// and dispatches DATEV notifications + UI popups.
    ///
    /// Extracted from ConnectorService to give a single responsibility:
    /// translating telephony events into DATEV notifications.
    /// </summary>
    internal class CallEventProcessor
    {
        private readonly CallTracker _callTracker;
        private readonly NotificationManager _notificationManager;
        private readonly CallHistoryStore _callHistory;
        private readonly string _extension;

        // Settings (read from AppConfig)
        private bool _enableJournaling;
        private bool _enableJournalPopup;
        private bool _enableJournalPopupOutbound;
        private bool _enableCallerPopup;
        private bool _enableCallerPopupOutbound;
        private CallerPopupMode _callerPopupMode;
        private int _minCallerIdLength;
        private int _contactReshowDelaySeconds;
        private bool _isMuted;

        public bool IsMuted { get => _isMuted; set => _isMuted = value; }

        public CallEventProcessor(
            CallTracker callTracker,
            NotificationManager notificationManager,
            CallHistoryStore callHistory,
            string extension,
            int minCallerIdLength)
        {
            _callTracker = callTracker;
            _notificationManager = notificationManager;
            _callHistory = callHistory;
            _extension = extension;
            _minCallerIdLength = minCallerIdLength;

            _enableJournaling = AppConfig.GetBool(ConfigKeys.EnableJournaling, true);
            _enableJournalPopup = AppConfig.GetBool(ConfigKeys.EnableJournalPopup, true);
            _enableJournalPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableJournalPopupOutbound, false);
            _enableCallerPopup = AppConfig.GetBool(ConfigKeys.EnableCallerPopup, true);
            _enableCallerPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableCallerPopupOutbound, false);
            _callerPopupMode = AppConfig.GetEnum(ConfigKeys.CallerPopupMode, CallerPopupMode.Form);
            _contactReshowDelaySeconds = AppConfig.GetIntClamped(ConfigKeys.ContactReshowDelaySeconds, 3, 0, 30);
        }

        /// <summary>
        /// Create a new CallData with the given direction. Eliminates the 3x duplicated pattern.
        /// </summary>
        private CallData CreateCallData(CallRecord record, ENUM_DIRECTION direction)
        {
            return new CallData
            {
                CallID = CallIdGenerator.Next(),
                CallState = ENUM_CALLSTATE.eCSOffered,
                Direction = direction,
                Begin = record.StartTime,
                End = record.StartTime
            };
        }

        /// <summary>
        /// Main entry point — dispatches TAPI call state events.
        /// </summary>
        public void OnTapiCallStateChanged(TapiCallEvent callEvent)
        {
            try
            {
                string callId = callEvent.CallId.ToString();
                int state = callEvent.CallState;

                switch (state)
                {
                    case TapiInterop.LINECALLSTATE_OFFERING:
                        HandleOffering(callId, callEvent);
                        break;
                    case TapiInterop.LINECALLSTATE_RINGBACK:
                        HandleRingback(callId, callEvent);
                        break;
                    case TapiInterop.LINECALLSTATE_CONNECTED:
                        HandleConnected(callId, callEvent);
                        break;
                    case TapiInterop.LINECALLSTATE_DISCONNECTED:
                        HandleDisconnected(callId, callEvent);
                        break;
                    case TapiInterop.LINECALLSTATE_IDLE:
                        break;
                    case TapiInterop.LINECALLSTATE_DIALING:
                    case TapiInterop.LINECALLSTATE_PROCEEDING:
                        LogManager.Log("TAPI: Anruf {0} Status={1}", callId, callEvent.CallStateString);
                        break;
                    case TapiInterop.LINECALLSTATE_BUSY:
                        LogManager.Log("TAPI: Anruf {0} BESETZT", callId);
                        HandleDisconnected(callId, callEvent);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Fehler bei TAPI Anrufereignis: {0}", ex);
            }
        }

        /// <summary>
        /// Look up a DATEV contact by phone number, apply routing, fill CallData, and record usage.
        /// </summary>
        private DatevContactInfo LookupAndFillContact(CallRecord record, CallData callData, string remoteNumber)
        {
            DatevContactInfo contact = null;
            if (!string.IsNullOrEmpty(remoteNumber) && remoteNumber.Length >= _minCallerIdLength)
            {
                List<DatevContactInfo> contacts = DatevContactRepository.GetContactByNumber(remoteNumber);
                if (contacts.Count > 1)
                    contacts = ContactRoutingCache.ApplyRouting(remoteNumber, contacts);
                if (contacts.Count > 0)
                    contact = contacts[0];
            }

            CallDataManager.Fill(callData, remoteNumber, contact);
            record.CallData = callData;

            if (contact?.DatevContact?.Id != null)
                ContactRoutingCache.RecordUsage(remoteNumber, contact.DatevContact.Id);

            return contact;
        }

        private void HandleOffering(string callId, TapiCallEvent callEvent)
        {
            string callerNumber = callEvent.CallerNumber;

            var existingRecord = _callTracker.GetCall(callId);
            if (existingRecord != null)
            {
                CallStateMachine.TryTransition(existingRecord, TapiCallState.Ringing);
                return;
            }

            var record = _callTracker.AddCall(callId, isIncoming: true);
            CallStateMachine.TryTransition(record, TapiCallState.Ringing);
            record.RemoteNumber = callerNumber;

            var callData = CreateCallData(record, ENUM_DIRECTION.eDirIncoming);
            var contact = LookupAndFillContact(record, callData, callerNumber);

            if (_enableCallerPopup && !_isMuted)
            {
                CallerPopupForm.ShowPopup(callerNumber, callEvent.CallerName, contact,
                    isIncoming: true, _callerPopupMode, _extension);
            }

            LogManager.Log("Connector: Eingehender Anruf {0} von {1} (Kontakt={2})",
                callId, LogManager.Mask(callerNumber), LogManager.MaskName(contact?.DatevContact?.Name) ?? "unbekannt");
            _notificationManager.NewCall(callData);
        }

        private void HandleRingback(string callId, TapiCallEvent callEvent)
        {
            string calledNumber = callEvent.CalledNumber;

            var existingRecord = _callTracker.GetCall(callId);
            if (existingRecord != null)
            {
                CallStateMachine.TryTransition(existingRecord, TapiCallState.Ringback);
                return;
            }

            // Check if this is a DATEV-initiated call (pending call matching by number)
            var pendingRecord = _callTracker.FindPendingCallByNumber(calledNumber);
            CallRecord record;

            if (pendingRecord != null && pendingRecord.CallData != null)
            {
                record = _callTracker.PromotePendingCall(pendingRecord.TapiCallId, callId);
                if (record == null)
                    record = _callTracker.AddCall(callId, isIncoming: false);
                CallStateMachine.TryTransition(record, TapiCallState.Ringback);
                record.RemoteNumber = calledNumber;
                record.CallData.Begin = record.StartTime;
                record.CallData.End = record.StartTime;

                LogManager.Log("Connector: DATEV-initiierter ausgehender Anruf {0} an {1} (SyncID={2}, Kontakt={3})",
                    callId, LogManager.Mask(calledNumber), record.CallData.SyncID, record.CallData.Adressatenname);
                _notificationManager.NewCall(record.CallData);
                return;
            }

            // Normal outgoing call
            record = _callTracker.AddCall(callId, isIncoming: false);
            CallStateMachine.TryTransition(record, TapiCallState.Ringback);
            record.RemoteNumber = calledNumber;

            var callData = CreateCallData(record, ENUM_DIRECTION.eDirOutgoing);
            var contact = LookupAndFillContact(record, callData, calledNumber);

            if (_enableCallerPopupOutbound && !_isMuted)
            {
                CallerPopupForm.ShowPopup(calledNumber, callEvent.CalledName, contact,
                    isIncoming: false, _callerPopupMode, _extension);
            }

            LogManager.Log("Connector: Ausgehender Anruf {0} an {1} (Kontakt={2})",
                callId, LogManager.Mask(calledNumber), contact?.DatevContact?.Name ?? "unbekannt");
            _notificationManager.NewCall(callData);
        }

        private void HandleConnected(string callId, TapiCallEvent callEvent)
        {
            var record = _callTracker.GetCall(callId);

            if (record == null)
            {
                LogManager.Log("Connector: Erstelle Datensatz für unbekannten Anruf {0}", callId);
                bool isIncoming = callEvent.IsIncoming;
                record = _callTracker.AddCall(callId, isIncoming);
                string remoteNumber = isIncoming ? callEvent.CallerNumber : callEvent.CalledNumber;
                record.RemoteNumber = remoteNumber;

                var callData = CreateCallData(record, isIncoming ? ENUM_DIRECTION.eDirIncoming : ENUM_DIRECTION.eDirOutgoing);
                LookupAndFillContact(record, callData, remoteNumber);
                _notificationManager.NewCall(callData);
            }

            if (!CallStateMachine.TryTransition(record, TapiCallState.Connected))
                return;

            record.ConnectedTime = DateTime.Now;
            record.State = ENUM_CALLSTATE.eCSConnected;

            if (_enableCallerPopup)
                CallerPopupForm.CloseCurrentPopup();

            if (record.CallData != null)
            {
                record.CallData.CallState = ENUM_CALLSTATE.eCSConnected;
                record.CallData.Begin = record.ConnectedTime.Value;
                record.CallData.End = record.ConnectedTime.Value;

                LogManager.Log("Connector: Call {0}", callId);
                _notificationManager.CallStateChanged(record.CallData);

                int reshowDelay = DebugConfigWatcher.GetInt(
                    DebugConfigWatcher.Instance?.ContactReshowDelaySeconds,
                    "ContactReshowDelaySeconds", _contactReshowDelaySeconds);
                if (reshowDelay > 0)
                    ScheduleContactReshow(record, reshowDelay);
            }
        }

        private void ScheduleContactReshow(CallRecord record, int reshowDelaySeconds)
        {
            string remoteNumber = record.RemoteNumber;
            if (string.IsNullOrEmpty(remoteNumber) || remoteNumber.Length < _minCallerIdLength)
                return;

            var contacts = DatevContactRepository.GetContactByNumber(remoteNumber);
            if (contacts.Count <= 1)
                return;

            contacts = ContactRoutingCache.ApplyRouting(remoteNumber, contacts);

            string callId = record.TapiCallId;
            int delayMs = reshowDelaySeconds * 1000;

            LogManager.Debug("Connector: Scheduling contact reshow in {0}s for call {1} ({2} contacts)",
                reshowDelaySeconds, callId, contacts.Count);

            _ = ContactReshowAfterDelayAsync(callId, remoteNumber, contacts, record.IsIncoming, delayMs);
        }

        private async Task ContactReshowAfterDelayAsync(string callId, string remoteNumber,
            List<DatevContactInfo> contacts, bool isIncoming, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs);

                var currentRecord = _callTracker.GetCall(callId);
                if (currentRecord == null || currentRecord.TapiState != TapiCallState.Connected)
                    return;

                ContactSelectionForm.SelectContact(
                    remoteNumber, contacts, isIncoming,
                    selectedContact =>
                    {
                        // Verify call is still active (may have disconnected while dialog was open)
                        if (_callTracker.GetCall(callId) == null) return;

                        if (selectedContact != null && currentRecord.CallData != null)
                        {
                            string existingSyncId = currentRecord.CallData.SyncID;
                            string previousId = currentRecord.CallData.AdressatenId;

                            CallDataManager.Fill(currentRecord.CallData, remoteNumber, selectedContact);

                            if (!string.IsNullOrEmpty(existingSyncId))
                                currentRecord.CallData.SyncID = existingSyncId;

                            if (currentRecord.CallData.AdressatenId != previousId)
                            {
                                LogManager.Log("Kontaktauswahl: Kontakt geändert für Anruf {0} - neu={1} (SyncID={2})",
                                    callId, currentRecord.CallData.Adressatenname, currentRecord.CallData.SyncID);
                                _notificationManager.CallAdressatChanged(currentRecord.CallData);
                                ContactRoutingCache.RecordUsage(remoteNumber, currentRecord.CallData.AdressatenId);
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                LogManager.Log("Kontaktauswahl Fehler für Anruf {0}: {1}", callId, ex.Message);
            }
        }

        private void HandleDisconnected(string callId, TapiCallEvent callEvent)
        {
            var record = _callTracker.GetCall(callId);
            if (record == null)
            {
                LogManager.Log("Connector: Unbekannter Anruf {0} (ignoriert)", callId);
                return;
            }

            if (!CallStateMachine.TryTransition(record, TapiCallState.Disconnected))
                return;

            _callTracker.RemoveCall(callId);
            CallerPopupForm.CloseCurrentPopup();
            ContactSelectionForm.CloseCurrentDialog();

            record.EndTime = DateTime.Now;
            record.State = record.WasConnected
                ? ENUM_CALLSTATE.eCSFinished
                : ENUM_CALLSTATE.eCSAbsence;

            var duration = record.GetDuration();
            string durationStr = FormatDuration(duration);

            if (record.CallData != null)
            {
                record.CallData.CallState = record.State;
                record.CallData.End = record.EndTime.Value;

                LogManager.Log("Connector: Call {0} (wasConnected={1}, duration={2})",
                    callId, record.WasConnected, durationStr);
                _notificationManager.CallStateChanged(record.CallData);

                if (!string.IsNullOrEmpty(record.CallData.AdressatenId))
                    _callHistory.AddEntry(CallHistoryEntry.FromCallRecord(record));

                if (ShouldShowJournalPopup(record))
                    ShowJournalPopup(callId, record);
            }
        }

        /// <summary>
        /// Format a nullable TimeSpan as HH:MM:SS.
        /// </summary>
        private static string FormatDuration(TimeSpan? duration)
        {
            if (!duration.HasValue) return "N/A";
            return string.Format("{0:D2}:{1:D2}:{2:D2}",
                (int)duration.Value.TotalHours, duration.Value.Minutes, duration.Value.Seconds);
        }

        private bool ShouldShowJournalPopup(CallRecord record)
        {
            return _enableJournaling && _enableJournalPopup && !_isMuted
                && record.WasConnected
                && !string.IsNullOrEmpty(record.CallData.AdressatenId)
                && (record.IsIncoming || _enableJournalPopupOutbound);
        }

        private void ShowJournalPopup(string callId, CallRecord record)
        {
            if (string.IsNullOrEmpty(record.CallData.DataSource))
            {
                LogManager.Warning("Journal: DataSource leer, verwende Standard 3CX");
                record.CallData.DataSource = DatevDataSource.ThirdParty;
            }

            var journalCallData = record.CallData;
            JournalForm.ShowJournal(
                journalCallData.Adressatenname,
                journalCallData.CalledNumber,
                journalCallData.DataSource,
                journalCallData.Begin,
                journalCallData.End,
                note =>
                {
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        if (_callTracker.Count > 0)
                        {
                            LogManager.Warning("Journal: Blockiert - {0} aktive(r) Anruf(e)", _callTracker.Count);
                            return;
                        }

                        journalCallData.CallID = CallIdGenerator.Next();
                        journalCallData.SyncID = string.Empty;
                        journalCallData.Note = note;
                        _notificationManager.NewJournal(journalCallData);
                        LogManager.Log("Journal gesendet für Anruf {0} (NewCallID={1}, {2} Zeichen)",
                            callId, journalCallData.CallID, note.Length);
                    }
                    else
                    {
                        LogManager.Log("Journal übersprungen für Anruf {0} - leere Notiz", callId);
                    }
                });
        }

        /// <summary>
        /// Re-read popup/journal settings from INI.
        /// </summary>
        public void ApplySettings()
        {
            _enableJournaling = AppConfig.GetBool(ConfigKeys.EnableJournaling, true);
            _enableJournalPopup = AppConfig.GetBool(ConfigKeys.EnableJournalPopup, true);
            _enableJournalPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableJournalPopupOutbound, false);
            _enableCallerPopup = AppConfig.GetBool(ConfigKeys.EnableCallerPopup, true);
            _enableCallerPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableCallerPopupOutbound, false);
            _callerPopupMode = AppConfig.GetEnum(ConfigKeys.CallerPopupMode, CallerPopupMode.Form);
            _contactReshowDelaySeconds = AppConfig.GetIntClamped(ConfigKeys.ContactReshowDelaySeconds, 3, 0, 30);
        }
    }
}
