using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.DatevData;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DatevConnector.Datev
{
    /// <summary>
    /// Cache for DATEV contacts with phone number lookup
    /// </summary>
    public static class DatevCache
    {
        private static List<DatevContact> _datevContacts;
        private static SortedDictionary<string, List<DatevContactInfo>> _datevContactsSDict;
        private static int? _maxCompareLength;
        private static bool _debugLogging;

        private static readonly object LockObj = new object();

        /// <summary>
        /// Number of contacts loaded
        /// </summary>
        public static int ContactCount => _datevContacts?.Count ?? 0;

        /// <summary>
        /// Number of unique phone number keys in lookup dictionary
        /// </summary>
        public static int PhoneNumberKeyCount => _datevContactsSDict?.Count ?? 0;

        /// <summary>
        /// Start async loading of contacts
        /// </summary>
        public static Task StartLoadAsync(IProgress<int> progress = null)
        {
            return StartLoadAsync(progress, null);
        }

        /// <summary>
        /// Start async loading of contacts with text progress
        /// </summary>
        /// <param name="progress">Numeric progress reporter</param>
        /// <param name="progressText">Text progress callback</param>
        public static Task StartLoadAsync(IProgress<int> progress, Action<string> progressText)
        {
            return Task.Factory.StartNew(() =>
            {
                lock (LockObj)
                {
                    Init(true, progress, progressText);
                }
            });
        }

        /// <summary>
        /// Get contacts matching a phone number
        /// </summary>
        /// <param name="contactNumber">Phone number to search for</param>
        /// <returns>List of matching contacts (may be empty)</returns>
        public static List<DatevContactInfo> GetContactByNumber(string contactNumber)
        {
            lock (LockObj)
            {
                Init();

                // Use unified normalization for consistent matching
                string normalizedNumber = PhoneNumberNormalizer.NormalizeForComparison(
                    contactNumber,
                    _maxCompareLength ?? 10);

                LogManager.Log("Connector: Contact lookup - Input='{0}' Normalized='{1}'", LogManager.Mask(contactNumber), LogManager.Mask(normalizedNumber));

                List<DatevContactInfo> result = null;

                // Try exact match first
                if (_datevContactsSDict != null && _datevContactsSDict.ContainsKey(normalizedNumber))
                {
                    result = _datevContactsSDict[normalizedNumber];
                    LogManager.Debug("Connector: Contact lookup - Exact match on key '{0}'", LogManager.Mask(normalizedNumber));
                }

                // If no exact match, try suffix matching for shorter DATEV numbers
                // This handles cases where DATEV stores numbers without area code
                // Require at least 6 digits overlap to avoid false positives
                const int minSuffixMatchLength = 6;
                if ((result == null || result.Count == 0) && _datevContactsSDict != null && normalizedNumber.Length >= minSuffixMatchLength)
                {
                    foreach (var kvp in _datevContactsSDict)
                    {
                        // Skip keys that are too short for reliable matching
                        if (kvp.Key.Length < minSuffixMatchLength)
                            continue;

                        // Check if the incoming number ends with a stored key (DATEV has shorter number)
                        // or if a stored key ends with the incoming number (incoming is shorter)
                        if (normalizedNumber.EndsWith(kvp.Key) || kvp.Key.EndsWith(normalizedNumber))
                        {
                            result = kvp.Value;
                            LogManager.Debug("Connector: Contact lookup - Suffix match: '{0}' <-> '{1}'", LogManager.Mask(normalizedNumber), LogManager.Mask(kvp.Key));
                            break;
                        }
                    }
                }

                if (result == null || result.Count == 0)
                {
                    result = new List<DatevContactInfo>();
                    LogManager.Log("Connector: Contact lookup - No match found");
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (DatevContactInfo info in result)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(LogManager.MaskName(info.DatevContact.Name));
                    }
                    LogManager.Log("Connector: Contact lookup - Found {0} match(es): {1}", result.Count, sb);
                }

                return result;
            }
        }

        /// <summary>
        /// Get all cached contacts (for developer diagnostics)
        /// </summary>
        /// <returns>Flat list of all DatevContactInfo entries</returns>
        public static List<DatevContactInfo> GetAllContacts()
        {
            lock (LockObj)
            {
                if (_datevContactsSDict == null)
                    return new List<DatevContactInfo>();

                return _datevContactsSDict.Values
                    .SelectMany(list => list)
                    .ToList();
            }
        }

        /// <summary>
        /// Initialize or reload the contact cache
        /// </summary>
        private static void Init(bool isForceReload = false, IProgress<int> progress = null, Action<string> progressText = null)
        {
            // Read configuration
            _debugLogging = AppConfig.GetBool(ConfigKeys.DebugLogging, false);
            int configMaxCompareLength = AppConfig.GetInt(ConfigKeys.MaxCompareLength, 10);

            bool contactListUpdated = false;

            // Load contacts if needed
            if (isForceReload || _datevContacts == null)
            {
                // Release old cache data before loading new to reduce peak memory during reload.
                if (_datevContacts != null)
                {
                    _datevContacts = null;
                    _datevContactsSDict = null;
                    MemoryOptimizer.CollectGen2();
                }

                LogManager.Log("Kontakte werden von DATEV Stamm Daten Dienst (SDD) geladen...");

                _datevContacts = DatevContactManager.GetContacts(10, 90, progress, progressText);
                contactListUpdated = true;

                LogManager.Debug("Loaded {0} contacts from DATEV SDD", _datevContacts.Count);

                // Debug logging of all contacts
                if (_debugLogging)
                {
                    LogContactList();
                }
            }

            // Rebuild lookup dictionary if needed
            if (_maxCompareLength != configMaxCompareLength || contactListUpdated)
            {
                _maxCompareLength = configMaxCompareLength;

                LogManager.Log("Kontaktverzeichnis wird erstellt...");

                progressText?.Invoke("Erstelle Telefonverzeichnis...");
                BuildLookupDictionary();
                progressText?.Invoke($"{PhoneNumberKeyCount} Telefonnummern indexiert");

                if (_debugLogging)
                {
                    LogLookupDictionary();
                }
            }

            // Reclaim memory after large batch load/transform.
            // See MemoryOptimizer for the full rationale (LOH compaction + working set trim).
            if (contactListUpdated)
            {
                MemoryOptimizer.CollectAndTrim();
            }

            progress?.Report(100);
        }

        /// <summary>
        /// Build the phone number lookup dictionary.
        /// Uses EffectiveNormalizedNumber to ensure ALL contacts (Recipients and Institutions)
        /// can be looked up, even if DATEV's NormierteNummer field is empty.
        /// Builds the SortedDictionary directly to avoid allocating an intermediate
        /// Dictionary + anonymous GroupBy objects.
        /// </summary>
        private static void BuildLookupDictionary()
        {
            if (_datevContacts == null)
            {
                _datevContactsSDict = new SortedDictionary<string, List<DatevContactInfo>>();
                return;
            }

            int maxLen = _maxCompareLength ?? 10;
            var sorted = new SortedDictionary<string, List<DatevContactInfo>>();

            foreach (var contact in _datevContacts)
            {
                if (contact.Communications == null)
                    continue;

                foreach (var comm in contact.Communications)
                {
                    string effectiveNum = comm.EffectiveNormalizedNumber;
                    if (string.IsNullOrWhiteSpace(effectiveNum))
                        continue;

                    string key = PhoneNumberNormalizer.NormalizeForComparison(effectiveNum, maxLen);
                    if (string.IsNullOrEmpty(key))
                        continue;

                    List<DatevContactInfo> list;
                    if (!sorted.TryGetValue(key, out list))
                    {
                        list = new List<DatevContactInfo>();
                        sorted[key] = list;
                    }
                    list.Add(new DatevContactInfo(comm, contact));
                }
            }

            _datevContactsSDict = sorted;

            LogManager.Log("Kontaktsuchverzeichnis mit {0} einmalige Telefonnummern erstellt", _datevContactsSDict.Count);
        }

        /// <summary>
        /// Log all contacts (debug)
        /// </summary>
        private static void LogContactList()
        {
            LogManager.Log("----------- Contact list from DATEV:");

            foreach (DatevContact contact in _datevContacts)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("ID=").Append(contact.Id);
                sb.Append(" Name=").Append(contact.Name);
                sb.Append(" IsRecipient=").Append(contact.IsRecipient);
                sb.Append(" IsPrivate=").Append(contact.IsPrivatePerson);

                if (contact.Communications != null)
                {
                    foreach (Communication comm in contact.Communications)
                    {
                        sb.Append(" [").Append(comm.Medium);
                        sb.Append(": ").Append(comm.Number);
                        // Show both DATEV's NormierteNummer and our EffectiveNormalizedNumber
                        if (!string.IsNullOrWhiteSpace(comm.NormalizedNumber))
                        {
                            sb.Append(" -> DATEV:").Append(comm.NormalizedNumber);
                        }
                        sb.Append(" -> Effective:").Append(comm.EffectiveNormalizedNumber);
                        sb.Append("]");
                    }
                }

                LogManager.Log(sb.ToString());
            }

            LogManager.Log("----------- End contact list");
        }

        /// <summary>
        /// Log lookup dictionary (debug)
        /// </summary>
        private static void LogLookupDictionary()
        {
            LogManager.Log("----------- Lookup dictionary:");

            foreach (var kvp in _datevContactsSDict)
            {
                foreach (DatevContactInfo info in kvp.Value)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Key=").Append(kvp.Key);
                    sb.Append(" -> ID=").Append(info.DatevContact.Id);
                    sb.Append(" Name=").Append(info.DatevContact.Name);
                    sb.Append(" Number=").Append(info.Communication.Number);
                    sb.Append(" EffNorm=").Append(info.Communication.EffectiveNormalizedNumber);

                    LogManager.Log(sb.ToString());
                }
            }

            LogManager.Log("----------- End lookup dictionary");
        }

    }
}
