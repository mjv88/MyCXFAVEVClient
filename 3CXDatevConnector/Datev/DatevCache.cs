using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.DatevData;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
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

                LogManager.Log("Bridge: Contact lookup - Input='{0}' Normalized='{1}'", LogManager.Mask(contactNumber), LogManager.Mask(normalizedNumber));

                List<DatevContactInfo> result = null;

                // Try exact match first
                if (_datevContactsSDict != null && _datevContactsSDict.ContainsKey(normalizedNumber))
                {
                    result = _datevContactsSDict[normalizedNumber];
                    LogManager.Debug("Bridge: Contact lookup - Exact match on key '{0}'", LogManager.Mask(normalizedNumber));
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
                            LogManager.Debug("Bridge: Contact lookup - Suffix match: '{0}' <-> '{1}'", LogManager.Mask(normalizedNumber), LogManager.Mask(kvp.Key));
                            break;
                        }
                    }
                }

                if (result == null || result.Count == 0)
                {
                    result = new List<DatevContactInfo>();
                    LogManager.Log("Bridge: Contact lookup - No match found");
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (DatevContactInfo info in result)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(LogManager.MaskName(info.DatevContact.Name));
                    }
                    LogManager.Log("Bridge: Contact lookup - Found {0} match(es): {1}", result.Count, sb);
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
                // Then force a GC so the old data is actually freed before we allocate the new
                // deserialized XML graph (~68K objects).
                if (_datevContacts != null)
                {
                    _datevContacts = null;
                    _datevContactsSDict = null;
                    GC.Collect(2, GCCollectionMode.Forced);
                    GC.WaitForPendingFinalizers();
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
            //
            // Why this is necessary:
            // 1. XML deserialization creates ~68K Communication objects + wrapper types.
            //    Phone-only filtering keeps ~17K; the other ~51K sit in Gen2 until a rare
            //    full GC — that can take minutes in Workstation GC mode.
            // 2. GCCollectionMode.Forced (not Optimized) guarantees the collection runs.
            //    Optimized may skip entirely if the GC considers it unproductive.
            // 3. Double-collect: first pass queues Release()‑bound finalizers;
            //    WaitForPendingFinalizers runs them; second pass frees the now-dead objects.
            // 4. The CLR does NOT return freed heap pages to the OS by default.
            //    SetProcessWorkingSetSize(-1,-1) trims the working set so the reduction
            //    is visible in Task Manager / memory profilers.
            if (contactListUpdated)
            {
                long wsBefore = Environment.WorkingSet / (1024 * 1024);

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

                // Return freed physical pages to the OS
                TrimWorkingSet();

                long wsAfter = Environment.WorkingSet / (1024 * 1024);
                long managedMB = GC.GetTotalMemory(false) / (1024 * 1024);
                LogManager.Debug("Post-cache GC: working set {0} MB -> {1} MB (freed {2} MB), managed heap {3} MB",
                    wsBefore, wsAfter, wsBefore - wsAfter, managedMB);
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

            LogManager.Log("Kontaktsuchverzeichnis mit {0} einmaligen Telefonnummern erstellt", _datevContactsSDict.Count);
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

        #region Working set management

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        /// <summary>
        /// Ask the OS to trim the process working set.
        /// The CLR does not return freed heap pages to the OS after GC.Collect.
        /// Passing (-1, -1) tells Windows to trim pages not currently in use,
        /// making the freed memory visible in Task Manager.
        /// </summary>
        private static void TrimWorkingSet()
        {
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("TrimWorkingSet failed: {0}", ex.Message);
            }
        }

        #endregion
    }
}
