using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.Extensions;

namespace DatevConnector.Core
{
    /// <summary>
    /// Tracks all active calls with automatic cleanup of stale entries
    /// </summary>
    public class CallTracker : IDisposable
    {
        private readonly ConcurrentDictionary<string, CallRecord> _calls;
        private readonly ConcurrentDictionary<string, CallRecord> _pendingCalls;

        // Reverse lookup indexes for O(1) performance
        private readonly ConcurrentDictionary<string, string> _datevCallIdToTapiId;
        private readonly ConcurrentDictionary<string, string> _phoneNumberToPendingId;

        private readonly TimeSpan _staleCallTimeout;
        private readonly TimeSpan _stalePendingTimeout;
        private readonly Timer _cleanupTimer;
        private int _nextTempId = 1000;
        private bool _disposed;

        public CallTracker()
        {
            _calls = new ConcurrentDictionary<string, CallRecord>();
            _pendingCalls = new ConcurrentDictionary<string, CallRecord>();
            _datevCallIdToTapiId = new ConcurrentDictionary<string, string>();
            _phoneNumberToPendingId = new ConcurrentDictionary<string, string>();

            // Read timeout configuration (defaults: 4 hours for active, 5 minutes for pending)
            int staleMinutes = AppConfig.GetIntClamped(ConfigKeys.StaleCallTimeoutMinutes, 240, 30, 1440);
            int stalePendingSeconds = AppConfig.GetIntClamped(ConfigKeys.StalePendingTimeoutSeconds, 300, 30, 3600);

            _staleCallTimeout = TimeSpan.FromMinutes(staleMinutes);
            _stalePendingTimeout = TimeSpan.FromSeconds(stalePendingSeconds);

            // Start cleanup timer (runs every minute)
            _cleanupTimer = new Timer(CleanupStaleCalls, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            LogManager.Debug("CallTracker initialisiert: StaleCallTimeout={0}min, StalePendingTimeout={1}sec",
                staleMinutes, stalePendingSeconds);
        }

        /// <summary>
        /// Generate a temporary call ID for DATEV-initiated calls
        /// </summary>
        public string GenerateTempCallId()
        {
            return $"DATEV_{Interlocked.Increment(ref _nextTempId)}";
        }

        /// <summary>
        /// Add a pending call (DATEV-initiated, waiting for TAPI ID)
        /// </summary>
        public CallRecord AddPendingCall(string tempId, bool isIncoming = false)
        {
            var record = new CallRecord(tempId, isIncoming);

            if (_pendingCalls.TryAdd(tempId, record))
            {
                LogManager.Log("Connector: Ausstehender Anruf hinzugefügt {0}", tempId);
                return record;
            }

            return _pendingCalls[tempId];
        }

        /// <summary>
        /// Promote a pending call to active (when we get the real TAPI ID)
        /// </summary>
        public CallRecord PromotePendingCall(string tempId, string tapiCallId)
        {
            if (_pendingCalls.TryRemove(tempId, out var record))
            {
                // Remove from phone number index (normalized)
                if (!string.IsNullOrEmpty(record.RemoteNumber))
                {
                    string normalized = PhoneNumberNormalizer.Normalize(record.RemoteNumber);
                    _phoneNumberToPendingId.TryRemove(normalized, out _);
                }

                record.TapiCallId = tapiCallId;

                if (_calls.TryAdd(tapiCallId, record))
                {
                    // Add to DATEV CallID index if available
                    if (record.CallData?.CallID != null)
                    {
                        _datevCallIdToTapiId[record.CallData.CallID] = tapiCallId;
                    }

                    LogManager.Log("Connector: Ausstehender Anruf übernommen {0} -> {1}", tempId, tapiCallId);
                    return record;
                }
            }

            return null;
        }

        /// <summary>
        /// Remove a pending call (e.g., if MAKE-CALL fails)
        /// </summary>
        public CallRecord RemovePendingCall(string tempId)
        {
            if (_pendingCalls.TryRemove(tempId, out var record))
            {
                // Remove from phone number index (normalized)
                if (!string.IsNullOrEmpty(record.RemoteNumber))
                {
                    string normalized = PhoneNumberNormalizer.Normalize(record.RemoteNumber);
                    _phoneNumberToPendingId.TryRemove(normalized, out _);
                }

                LogManager.Log("Connector: Ausstehender Anruf entfernt {0}", tempId);
                return record;
            }
            return null;
        }

        /// <summary>
        /// Add a new call
        /// </summary>
        public CallRecord AddCall(string tapiCallId, bool isIncoming)
        {
            var record = new CallRecord(tapiCallId, isIncoming);

            if (_calls.TryAdd(tapiCallId, record))
            {
                LogManager.Log("Connector: Anruf hinzugefügt {0} (eingehend={1})", tapiCallId, isIncoming);
                return record;
            }

            return _calls[tapiCallId];
        }

        /// <summary>
        /// Get a call by TAPI call ID
        /// </summary>
        public CallRecord GetCall(string tapiCallId)
        {
            _calls.TryGetValue(tapiCallId, out var record);
            return record;
        }

        /// <summary>
        /// Update the phone number index for a pending call (normalizes number for matching)
        /// </summary>
        public void UpdatePendingPhoneIndex(string tempId, string phoneNumber)
        {
            if (!string.IsNullOrEmpty(phoneNumber) && !string.IsNullOrEmpty(tempId))
            {
                string normalized = PhoneNumberNormalizer.Normalize(phoneNumber);
                if (!string.IsNullOrEmpty(normalized))
                    _phoneNumberToPendingId[normalized] = tempId;
            }
        }

        /// <summary>
        /// Find a call by DATEV CallID (O(1) lookup using index)
        /// </summary>
        public CallRecord FindCallByDatevCallId(string datevCallId)
        {
            // Fast path: use index
            if (_datevCallIdToTapiId.TryGetValue(datevCallId, out var tapiId))
            {
                if (_calls.TryGetValue(tapiId, out var record))
                    return record;

                // Index is stale, remove it
                _datevCallIdToTapiId.TryRemove(datevCallId, out _);
            }

            // Fallback: linear search (for cases where index wasn't updated)
            foreach (var kvp in _calls)
            {
                if (kvp.Value.CallData?.CallID == datevCallId)
                {
                    // Update index for next time
                    _datevCallIdToTapiId[datevCallId] = kvp.Key;
                    return kvp.Value;
                }
            }

            // Check pending calls
            foreach (var kvp in _pendingCalls)
            {
                if (kvp.Value.CallData?.CallID == datevCallId)
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// Find a pending call by remote number (O(1) lookup using normalized index)
        /// </summary>
        public CallRecord FindPendingCallByNumber(string number)
        {
            string normalized = PhoneNumberNormalizer.Normalize(number);
            if (string.IsNullOrEmpty(normalized))
                return null;

            // Fast path: use index
            if (_phoneNumberToPendingId.TryGetValue(normalized, out var tempId))
            {
                if (_pendingCalls.TryGetValue(tempId, out var record))
                    return record;

                // Index is stale, remove it
                _phoneNumberToPendingId.TryRemove(normalized, out _);
            }

            // Fallback: linear search (normalize stored numbers for comparison)
            foreach (var kvp in _pendingCalls)
            {
                string storedNormalized = PhoneNumberNormalizer.Normalize(kvp.Value.RemoteNumber);
                if (storedNormalized == normalized)
                {
                    _phoneNumberToPendingId[normalized] = kvp.Key;
                    return kvp.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Remove a call
        /// </summary>
        public CallRecord RemoveCall(string tapiCallId)
        {
            if (_calls.TryRemove(tapiCallId, out var record))
            {
                // Remove from DATEV CallID index
                if (record.CallData?.CallID != null)
                {
                    _datevCallIdToTapiId.TryRemove(record.CallData.CallID, out _);
                }

                LogManager.Log("Connector: Anruf entfernt {0}", tapiCallId);
                return record;
            }
            return null;
        }

        /// <summary>
        /// Get count of active calls
        /// </summary>
        public int Count => _calls.Count;

        /// <summary>
        /// Cleanup stale calls that never received DISCONNECTED
        /// </summary>
        private void CleanupStaleCalls(object state)
        {
            try
            {
                var now = DateTime.Now;
                int removedActive = 0;
                int removedPending = 0;

                // Cleanup stale active calls
                var staleActiveCalls = _calls
                    .Where(kvp => (now - kvp.Value.StartTime) > _staleCallTimeout)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var tapiId in staleActiveCalls)
                {
                    if (_calls.TryRemove(tapiId, out var record))
                    {
                        // Remove from DATEV CallID index
                        if (record.CallData?.CallID != null)
                        {
                            _datevCallIdToTapiId.TryRemove(record.CallData.CallID, out _);
                        }

                        removedActive++;
                        LogManager.Log("Connector: Veralteter Anruf entfernt {0} (Alter: {1})",
                            tapiId, now - record.StartTime);
                    }
                }

                // Cleanup stale pending calls
                var stalePendingCalls = _pendingCalls
                    .Where(kvp => (now - kvp.Value.StartTime) > _stalePendingTimeout)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var tempId in stalePendingCalls)
                {
                    if (_pendingCalls.TryRemove(tempId, out var record))
                    {
                        // Remove from phone number index (normalized)
                        if (!string.IsNullOrEmpty(record.RemoteNumber))
                        {
                            string normalized = PhoneNumberNormalizer.Normalize(record.RemoteNumber);
                            _phoneNumberToPendingId.TryRemove(normalized, out _);
                        }

                        removedPending++;
                        LogManager.Log("Connector: Veralteter ausstehender Anruf entfernt {0} (Alter: {1})",
                            tempId, now - record.StartTime);
                    }
                }

                if (removedActive > 0 || removedPending > 0)
                {
                    LogManager.Log("Connector: Bereinigung abgeschlossen - {0} aktive, {1} ausstehende veraltete Anrufe entfernt",
                        removedActive, removedPending);
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Connector: Fehler bei Bereinigung: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cleanupTimer?.Dispose();
        }
    }
}
