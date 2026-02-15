using System.Collections.Generic;
using System.Linq;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// In-memory call history with separate inbound/outbound circular buffers.
    /// Per-process (naturally TS-isolated). Configurable max entries per direction.
    /// </summary>
    public class CallHistoryStore
    {
        private readonly object _lock = new object();
        private readonly LinkedList<CallHistoryEntry> _inbound = new LinkedList<CallHistoryEntry>();
        private readonly LinkedList<CallHistoryEntry> _outbound = new LinkedList<CallHistoryEntry>();

        private int _maxEntries;
        private bool _trackInbound;
        private bool _trackOutbound;

        public CallHistoryStore(int maxEntries = 5, bool trackInbound = true, bool trackOutbound = false)
        {
            _maxEntries = maxEntries;
            _trackInbound = trackInbound;
            _trackOutbound = trackOutbound;
        }

        /// <summary>
        /// Update configuration at runtime (from settings save)
        /// </summary>
        public void UpdateConfig(int maxEntries, bool trackInbound, bool trackOutbound)
        {
            lock (_lock)
            {
                _maxEntries = maxEntries;
                _trackInbound = trackInbound;
                _trackOutbound = trackOutbound;

                // Trim if max reduced
                TrimBuffer(_inbound);
                TrimBuffer(_outbound);
            }
        }

        /// <summary>
        /// Add a completed call to history
        /// </summary>
        public void AddEntry(CallHistoryEntry entry)
        {
            if (entry == null) return;

            lock (_lock)
            {
                if (entry.IsIncoming && _trackInbound)
                {
                    _inbound.AddFirst(entry);
                    TrimBuffer(_inbound);
                    LogManager.Debug("CallHistory: Eingehenden Anruf hinzugefügt von {0} ({1} Einträge)",
                        LogManager.Mask(entry.RemoteNumber), _inbound.Count);
                }
                else if (!entry.IsIncoming && _trackOutbound)
                {
                    _outbound.AddFirst(entry);
                    TrimBuffer(_outbound);
                    LogManager.Debug("CallHistory: Ausgehenden Anruf hinzugefügt an {0} ({1} Einträge)",
                        LogManager.Mask(entry.RemoteNumber), _outbound.Count);
                }
            }
        }

        /// <summary>
        /// Get inbound call history (most recent first)
        /// </summary>
        public List<CallHistoryEntry> GetInbound()
        {
            lock (_lock)
            {
                return _inbound.ToList();
            }
        }

        /// <summary>
        /// Get outbound call history (most recent first)
        /// </summary>
        public List<CallHistoryEntry> GetOutbound()
        {
            lock (_lock)
            {
                return _outbound.ToList();
            }
        }

        /// <summary>
        /// Mark an entry as journal-sent
        /// </summary>
        public void MarkJournalSent(CallHistoryEntry entry)
        {
            lock (_lock)
            {
                entry.JournalSent = true;
            }
        }

        public bool TrackInbound => _trackInbound;
        public bool TrackOutbound => _trackOutbound;

        private void TrimBuffer(LinkedList<CallHistoryEntry> buffer)
        {
            while (buffer.Count > _maxEntries)
                buffer.RemoveLast();
        }
    }
}
