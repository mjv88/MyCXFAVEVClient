using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Extensions;

namespace DatevConnector.Core
{
    /// <summary>
    /// Tracks recent contact usage per phone number for "last agent routing".
    /// When a caller has multiple DATEV contacts, this cache remembers which
    /// AdressatenID was last used and prioritizes it within the configured window.
    /// </summary>
    internal static class ContactRoutingCache
    {
        private static readonly ConcurrentDictionary<string, ContactUsage> _cache
            = new ConcurrentDictionary<string, ContactUsage>();

        private static int _routingWindowMinutes;
        private static readonly Timer _evictionTimer;

        static ContactRoutingCache()
        {
            _routingWindowMinutes = AppConfig.GetIntClamped(ConfigKeys.LastContactRoutingMinutes, 60, 0, 1440);
            _evictionTimer = new Timer(_ => EvictExpired(), null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        }

        private static void EvictExpired()
        {
            if (_routingWindowMinutes <= 0) return;
            var expired = _cache.Where(kv => (DateTime.Now - kv.Value.Timestamp).TotalMinutes > _routingWindowMinutes).ToList();
            foreach (var kv in expired)
                _cache.TryRemove(kv.Key, out _);
            if (expired.Count > 0)
                LogManager.Debug("ContactRouting: Evicted {0} expired entries", expired.Count);
        }

        /// <summary>
        /// Record that a specific contact was used for a phone number.
        /// Call this when a contact is assigned to a call (initial or reshow change).
        /// </summary>
        public static void RecordUsage(string phoneNumber, string adressatenId)
        {
            if (string.IsNullOrEmpty(phoneNumber) || string.IsNullOrEmpty(adressatenId))
                return;

            string normalized = PhoneNumberNormalizer.Normalize(phoneNumber);
            if (string.IsNullOrEmpty(normalized))
                return;

            _cache[normalized] = new ContactUsage(adressatenId, DateTime.Now);
            LogManager.Debug("ContactRouting: Recorded {0} -> AdressatID={1}", normalized, adressatenId);
        }

        /// <summary>
        /// Reorder contacts list so that the recently-used contact is first.
        /// Returns the original list if no recent usage found or window expired.
        /// </summary>
        public static List<DatevContactInfo> ApplyRouting(string phoneNumber, List<DatevContactInfo> contacts)
        {
            if (contacts == null || contacts.Count <= 1 || _routingWindowMinutes <= 0)
                return contacts;

            string normalized = PhoneNumberNormalizer.Normalize(phoneNumber);
            if (string.IsNullOrEmpty(normalized))
                return contacts;

            if (!_cache.TryGetValue(normalized, out var usage))
                return contacts;

            if ((DateTime.Now - usage.Timestamp).TotalMinutes > _routingWindowMinutes)
            {
                _cache.TryRemove(normalized, out _);
                return contacts;
            }

            int preferredIndex = -1;
            for (int i = 0; i < contacts.Count; i++)
            {
                if (contacts[i].DatevContact?.Id == usage.AdressatenId)
                {
                    preferredIndex = i;
                    break;
                }
            }

            if (preferredIndex <= 0) // Already first, or not found
                return contacts;

            var reordered = new List<DatevContactInfo>(contacts.Count);
            reordered.Add(contacts[preferredIndex]);
            for (int i = 0; i < contacts.Count; i++)
            {
                if (i != preferredIndex)
                    reordered.Add(contacts[i]);
            }

            LogManager.Log("ContactRouting: Prioritized AdressatID={0} for {1} (last used {2:F0}min ago)",
                usage.AdressatenId, normalized, (DateTime.Now - usage.Timestamp).TotalMinutes);

            return reordered;
        }

        private class ContactUsage
        {
            public string AdressatenId { get; }
            public DateTime Timestamp { get; }

            public ContactUsage(string adressatenId, DateTime timestamp)
            {
                AdressatenId = adressatenId;
                Timestamp = timestamp;
            }
        }
    }
}
