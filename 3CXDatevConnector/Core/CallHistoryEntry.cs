using System;
using Datev.Cti.Buddylib;
using DatevConnector.Datev.COMs;

namespace DatevConnector.Core
{
    /// <summary>
    /// A single call history entry preserving all data needed to re-send a journal.
    /// </summary>
    public class CallHistoryEntry
    {
        public bool IsIncoming { get; set; }
        public string RemoteNumber { get; set; }
        public string ContactName { get; set; }
        public DateTime CallStart { get; set; }
        public DateTime CallEnd { get; set; }
        public TimeSpan Duration { get; set; }
        public bool JournalSent { get; set; }

        // Preserved CallData fields for re-sending journal
        public string AdressatenId { get; set; }
        public string DataSource { get; set; }
        public string CallID { get; set; }
        public string SyncID { get; set; }
        public string CalledNumber { get; set; }

        /// <summary>
        /// Build a CallData object for journal submission.
        /// Per DATEV IDL spec for NewJournal:
        ///   - CallID: new unique ID (journal follows same flow as new calls)
        ///   - SyncID: NICHT GESETZT (must not be set)
        ///   - CalledNumber: must match the number sent in the original NewCall
        /// </summary>
        public CallData ToCallData()
        {
            return new CallData
            {
                AdressatenId = AdressatenId ?? string.Empty,
                Adressatenname = ContactName ?? string.Empty,
                CalledNumber = CalledNumber ?? RemoteNumber ?? string.Empty,
                DataSource = DataSource ?? string.Empty,
                CallID = CallIdGenerator.Next(),
                SyncID = string.Empty,
                Begin = CallStart,
                End = CallEnd,
                Direction = IsIncoming
                    ? ENUM_DIRECTION.eDirIncoming
                    : ENUM_DIRECTION.eDirOutgoing,
                CallState = ENUM_CALLSTATE.eCSFinished
            };
        }

        /// <summary>
        /// Create a history entry from a completed call record
        /// </summary>
        public static CallHistoryEntry FromCallRecord(CallRecord record)
        {
            var entry = new CallHistoryEntry
            {
                IsIncoming = record.IsIncoming,
                RemoteNumber = record.RemoteNumber,
                CallStart = record.StartTime,
                CallEnd = record.EndTime ?? DateTime.Now,
                Duration = record.GetDuration() ?? TimeSpan.Zero,
                JournalSent = false
            };

            if (record.CallData != null)
            {
                entry.ContactName = record.CallData.Adressatenname;
                entry.AdressatenId = record.CallData.AdressatenId;
                entry.DataSource = record.CallData.DataSource;
                entry.CallID = record.CallData.CallID;
                entry.SyncID = record.CallData.SyncID;
                entry.CalledNumber = record.CallData.CalledNumber;
            }

            return entry;
        }
    }
}
