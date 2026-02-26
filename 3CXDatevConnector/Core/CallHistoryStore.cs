using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Persistent call history with separate inbound/outbound circular buffers.
    /// Data is DPAPI-encrypted on disk so only the current Windows user can read it.
    /// </summary>
    public class CallHistoryStore
    {
        private readonly object _lock = new object();
        private readonly LinkedList<CallHistoryEntry> _inbound = new LinkedList<CallHistoryEntry>();
        private readonly LinkedList<CallHistoryEntry> _outbound = new LinkedList<CallHistoryEntry>();

        private int _maxEntries;
        private bool _trackInbound;
        private bool _trackOutbound;
        private int _retentionDays;

        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "3CXDATEVConnector", "call_history.dat");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        public CallHistoryStore(int maxEntries = 5, bool trackInbound = true, bool trackOutbound = false, int retentionDays = 7)
        {
            _maxEntries = maxEntries;
            _trackInbound = trackInbound;
            _trackOutbound = trackOutbound;
            _retentionDays = retentionDays;

            Load();
        }

        public void UpdateConfig(int maxEntries, bool trackInbound, bool trackOutbound, int retentionDays)
        {
            lock (_lock)
            {
                _maxEntries = maxEntries;
                _trackInbound = trackInbound;
                _trackOutbound = trackOutbound;
                _retentionDays = retentionDays;

                PruneExpired();
                TrimBuffer(_inbound);
                TrimBuffer(_outbound);
                Save();
            }
        }

        public void AddEntry(CallHistoryEntry entry)
        {
            if (entry == null) return;

            lock (_lock)
            {
                PruneExpired();

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

                Save();
            }
        }

        public List<CallHistoryEntry> GetInbound()
        {
            lock (_lock)
            {
                return _inbound.ToList();
            }
        }

        public List<CallHistoryEntry> GetOutbound()
        {
            lock (_lock)
            {
                return _outbound.ToList();
            }
        }

        public void MarkJournalSent(CallHistoryEntry entry)
        {
            lock (_lock)
            {
                entry.JournalSent = true;
                Save();
            }
        }

        public bool TrackInbound => _trackInbound;
        public bool TrackOutbound => _trackOutbound;

        /// <summary>
        /// Persist current state to disk (safety-net flush, e.g. on app shutdown).
        /// </summary>
        public void Save()
        {
            try
            {
                var dto = new HistoryDto
                {
                    Inbound = _inbound.Select(EntryToDto).ToList(),
                    Outbound = _outbound.Select(EntryToDto).ToList()
                };

                var json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
                var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);

                var dir = Path.GetDirectoryName(StorePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(StorePath, encrypted);
            }
            catch (Exception ex)
            {
                LogManager.Warning("CallHistory: Speichern fehlgeschlagen: {0}", ex.Message);
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(StorePath))
                    return;

                var encrypted = File.ReadAllBytes(StorePath);
                var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var dto = JsonSerializer.Deserialize<HistoryDto>(json, JsonOptions);

                if (dto == null) return;

                _inbound.Clear();
                _outbound.Clear();

                if (dto.Inbound != null)
                {
                    foreach (var d in dto.Inbound)
                        _inbound.AddLast(DtoToEntry(d));
                }

                if (dto.Outbound != null)
                {
                    foreach (var d in dto.Outbound)
                        _outbound.AddLast(DtoToEntry(d));
                }

                PruneExpired();
                TrimBuffer(_inbound);
                TrimBuffer(_outbound);

                LogManager.Debug("CallHistory: {0} eingehende, {1} ausgehende Einträge geladen",
                    _inbound.Count, _outbound.Count);
            }
            catch (Exception ex)
            {
                LogManager.Warning("CallHistory: Laden fehlgeschlagen: {0}", ex.Message);

                try
                {
                    if (File.Exists(StorePath))
                    {
                        string backupPath = StorePath + ".bak";
                        File.Copy(StorePath, backupPath, overwrite: true);
                        LogManager.Warning("CallHistory: Beschädigte Datei gesichert als {0}", backupPath);
                    }
                }
                catch (Exception backupEx)
                {
                    LogManager.Warning("CallHistory: Backup fehlgeschlagen: {0}", backupEx.Message);
                }

                _inbound.Clear();
                _outbound.Clear();
            }
        }

        private void PruneExpired()
        {
            if (_retentionDays <= 0) return;

            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            PruneList(_inbound, cutoff);
            PruneList(_outbound, cutoff);
        }

        private static void PruneList(LinkedList<CallHistoryEntry> list, DateTime cutoff)
        {
            var node = list.Last;
            while (node != null)
            {
                var prev = node.Previous;
                if (node.Value.CallEnd.ToUniversalTime() < cutoff)
                    list.Remove(node);
                node = prev;
            }
        }

        private void TrimBuffer(LinkedList<CallHistoryEntry> buffer)
        {
            while (buffer.Count > _maxEntries)
                buffer.RemoveLast();
        }

        // ===== DTO for JSON serialization (TimeSpan as ticks for roundtrip safety) =====

        private static EntryDto EntryToDto(CallHistoryEntry e) => new EntryDto
        {
            IsIncoming = e.IsIncoming,
            RemoteNumber = e.RemoteNumber,
            ContactName = e.ContactName,
            CallStart = e.CallStart,
            CallEnd = e.CallEnd,
            DurationTicks = e.Duration.Ticks,
            JournalSent = e.JournalSent,
            AdressatenId = e.AdressatenId,
            DataSource = e.DataSource,
            CalledNumber = e.CalledNumber
        };

        private static CallHistoryEntry DtoToEntry(EntryDto d) => new CallHistoryEntry
        {
            IsIncoming = d.IsIncoming,
            RemoteNumber = d.RemoteNumber,
            ContactName = d.ContactName,
            CallStart = d.CallStart,
            CallEnd = d.CallEnd,
            Duration = TimeSpan.FromTicks(d.DurationTicks),
            JournalSent = d.JournalSent,
            AdressatenId = d.AdressatenId,
            DataSource = d.DataSource,
            CalledNumber = d.CalledNumber
        };

        private class HistoryDto
        {
            [JsonPropertyName("in")]
            public List<EntryDto> Inbound { get; set; }

            [JsonPropertyName("out")]
            public List<EntryDto> Outbound { get; set; }
        }

        private class EntryDto
        {
            [JsonPropertyName("inc")]
            public bool IsIncoming { get; set; }

            [JsonPropertyName("num")]
            public string RemoteNumber { get; set; }

            [JsonPropertyName("name")]
            public string ContactName { get; set; }

            [JsonPropertyName("start")]
            public DateTime CallStart { get; set; }

            [JsonPropertyName("end")]
            public DateTime CallEnd { get; set; }

            [JsonPropertyName("dur")]
            public long DurationTicks { get; set; }

            [JsonPropertyName("jsent")]
            public bool JournalSent { get; set; }

            [JsonPropertyName("aid")]
            public string AdressatenId { get; set; }

            [JsonPropertyName("ds")]
            public string DataSource { get; set; }

            [JsonPropertyName("cnum")]
            public string CalledNumber { get; set; }
        }
    }
}
