using DatevConnector.Core;
using DatevConnector.Core.Config;
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
    /// Unified repository for DATEV contacts: orchestrates the net48 SDD proxy
    /// subprocess for fetching, caches the result locally, and provides
    /// phone-number lookup.
    ///
    /// All direct DATEV SDD type usage has moved into
    /// 3CXDatevConnector.SddProxy (net48). This file now speaks JSON over a
    /// named pipe to the proxy and is otherwise a drop-in replacement for the
    /// older implementation — the public API (method names, return types) is
    /// preserved so callers don't need to change.
    /// </summary>
    public static class DatevContactRepository
    {
        private static List<DatevContact> _datevContacts;
        private static SortedDictionary<string, List<DatevContactInfo>> _datevContactsSDict;
        private static int? _maxCompareLength;
        private static bool _debugLogging;

        private static readonly object _lock = new object();
        private static bool _filterActiveContactsOnly;
        private static DateTime? _lastSyncTimestamp;

        // Lazily-created, long-lived proxy client. Disposed on process exit via
        // an AppDomain hook so the proxy subprocess gets a clean EXIT.
        private static SddProxyClient _proxy;
        private static readonly object _proxyLock = new object();
        private static bool _shutdownHookInstalled;

        // ===== Public Properties =====

        public static int ContactCount => _datevContacts?.Count ?? 0;

        public static int PhoneNumberKeyCount => _datevContactsSDict?.Count ?? 0;

        /// <summary>
        /// When true, only active contacts (Status != 0) are fetched from DATEV.
        /// Thread-safe.
        /// </summary>
        public static bool FilterActiveContactsOnly
        {
            get { lock (_lock) return _filterActiveContactsOnly; }
            set { lock (_lock) _filterActiveContactsOnly = value; }
        }

        /// <summary>
        /// Timestamp of the last successful contact sync from DATEV. Thread-safe.
        /// </summary>
        public static DateTime? LastSyncTimestamp
        {
            get { lock (_lock) return _lastSyncTimestamp; }
            private set { lock (_lock) _lastSyncTimestamp = value; }
        }

        // ===== Load / Reload =====

        public static Task StartLoadAsync(IProgress<int> progress = null)
        {
            return StartLoadAsync(progress, null);
        }

        public static Task StartLoadAsync(IProgress<int> progress, Action<string> progressText)
        {
            return Task.Factory.StartNew(() =>
            {
                lock (_lock)
                {
                    Init(true, progress, progressText);
                }
            });
        }

        // ===== Lookup =====

        public static List<DatevContactInfo> GetContactByNumber(string contactNumber)
        {
            lock (_lock)
            {
                if (_datevContacts == null) Init();

                string normalizedNumber = PhoneNumberNormalizer.NormalizeForComparison(
                    contactNumber,
                    _maxCompareLength ?? 10);

                LogManager.Log("Connector: Kontaktsuche - Eingabe='{0}' Normalisiert='{1}'",
                    LogManager.Mask(contactNumber), LogManager.Mask(normalizedNumber));

                List<DatevContactInfo> result = null;

                if (_datevContactsSDict != null && _datevContactsSDict.ContainsKey(normalizedNumber))
                {
                    result = _datevContactsSDict[normalizedNumber];
                    LogManager.Debug("Connector: Kontaktsuche - Exakte Übereinstimmung auf Schlüssel '{0}'",
                        LogManager.Mask(normalizedNumber));
                }

                const int minSuffixMatchLength = 6;
                if ((result == null || result.Count == 0) && _datevContactsSDict != null &&
                    normalizedNumber.Length >= minSuffixMatchLength)
                {
                    int bestMatchLength = 0;

                    foreach (var kvp in _datevContactsSDict)
                    {
                        if (kvp.Key.Length < minSuffixMatchLength)
                            continue;

                        int matchLen = 0;
                        if (normalizedNumber.EndsWith(kvp.Key))
                            matchLen = kvp.Key.Length;
                        else if (kvp.Key.EndsWith(normalizedNumber))
                            matchLen = normalizedNumber.Length;

                        if (matchLen > bestMatchLength)
                        {
                            bestMatchLength = matchLen;
                            result = kvp.Value;
                            LogManager.Debug("Connector: Kontaktsuche - Suffixübereinstimmung: '{0}' <-> '{1}' (Länge={2})",
                                LogManager.Mask(normalizedNumber), LogManager.Mask(kvp.Key), matchLen);
                        }
                    }
                }

                if (result == null || result.Count == 0)
                {
                    result = new List<DatevContactInfo>();
                    LogManager.Log("Connector: Kontaktsuche - Keine Übereinstimmung gefunden");
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (DatevContactInfo info in result)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(LogManager.MaskName(info.DatevContact.Name));
                    }
                    LogManager.Log("Connector: Kontaktsuche - {0} Treffer gefunden: {1}", result.Count, sb);
                }

                return result;
            }
        }

        public static List<DatevContactInfo> GetAllContacts()
        {
            lock (_lock)
            {
                if (_datevContactsSDict == null)
                    return new List<DatevContactInfo>();

                return _datevContactsSDict.Values
                    .SelectMany(list => list)
                    .ToList();
            }
        }

        // ===== Internals - Init & Build =====

        private static void Init(bool isForceReload = false, IProgress<int> progress = null, Action<string> progressText = null)
        {
            _debugLogging = AppConfig.GetBool(ConfigKeys.DebugLogging, false);
            int configMaxCompareLength = AppConfig.GetIntClamped(ConfigKeys.MaxCompareLength, 10, 4, 20);
            bool contactListUpdated = false;

            if (isForceReload || _datevContacts == null)
            {
                if (_datevContacts != null)
                {
                    _datevContacts = null;
                    _datevContactsSDict = null;
                    MemoryOptimizer.CollectGen2();
                }

                LogManager.Log("Kontakte werden von DATEV Stamm Daten Dienst (SDD) geladen...");
                _datevContacts = FetchContacts(10, 90, progress, progressText);
                contactListUpdated = true;

                LogManager.Debug("{0} Kontakte von DATEV SDD geladen", _datevContacts.Count);
                if (_debugLogging)
                    DatevContactDiagnostics.LogContactList(_datevContacts);
            }

            if (_maxCompareLength != configMaxCompareLength || contactListUpdated)
            {
                _maxCompareLength = configMaxCompareLength;
                LogManager.Log("Kontaktverzeichnis wird erstellt...");
                progressText?.Invoke("Erstelle Telefonverzeichnis...");

                BuildLookupDictionary();
                progressText?.Invoke($"{PhoneNumberKeyCount} Telefonnummern indexiert");

                if (_debugLogging)
                    DatevContactDiagnostics.LogLookupDictionary(_datevContactsSDict);
            }

            if (contactListUpdated)
                MemoryOptimizer.CollectAndTrim();

            progress?.Report(100);
        }

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
            LogManager.Log("Kontaktsuchverzeichnis mit {0} einmalige Telefonnummern erstellt",
                _datevContactsSDict.Count);
        }

        // ===== SDD Fetching =====
        //
        // All DATEV-SDD calls now live in the net48 proxy subprocess; this
        // method just talks to it over the pipe and hands the tray the same
        // DatevContact shape the old implementation did.

        private static List<DatevContact> FetchContacts(int startProgress, int endProgress,
            IProgress<int> progress, Action<string> progressText)
        {
            int delta = endProgress - startProgress;
            progress?.Report(startProgress);
            progressText?.Invoke("Suche Kontakte...");

            SddProxyClient client = GetOrCreateProxy();

            List<DatevContact> contacts;
            try
            {
                contacts = client.GetContacts(FilterActiveContactsOnly);
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Fehler beim Abrufen der Kontakte über SDD-Proxy");
                progressText?.Invoke("Fehler beim Laden der Kontakte");
                contacts = new List<DatevContact>();
            }

            int recipients = contacts.Count(c => c.IsRecipient);
            int institutions = contacts.Count - recipients;
            LogManager.Log("SDD: {0} Adressaten, {1} Institutionen", recipients, institutions);

            progress?.Report(endProgress);
            LastSyncTimestamp = DateTime.Now;

            int totalComms = contacts.Sum(c => c.Communications?.Length ?? 0);
            LogManager.Log("SDD: {0} Kontakte mit {1} Rufnummnern synchronisiert.",
                contacts.Count, totalComms);
            progressText?.Invoke($"{contacts.Count} Kontakte geladen");

            return contacts;
        }

        private static SddProxyClient GetOrCreateProxy()
        {
            lock (_proxyLock)
            {
                if (_proxy == null)
                {
                    _proxy = new SddProxyClient();
                    _proxy.Start();
                    InstallShutdownHookLocked();
                }
                else if (!_proxy.IsConnected)
                {
                    // IsConnected is false after a broken pipe — Start() will
                    // re-launch if needed. Request() also handles recovery
                    // on its own, but we call Start() here so the first GET
                    // after a reconnect doesn't pay the startup cost inline.
                    _proxy.Start();
                }
                return _proxy;
            }
        }

        private static void InstallShutdownHookLocked()
        {
            if (_shutdownHookInstalled) return;
            _shutdownHookInstalled = true;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ShutdownProxy();
            AppDomain.CurrentDomain.DomainUnload += (s, e) => ShutdownProxy();
        }

        private static void ShutdownProxy()
        {
            lock (_proxyLock)
            {
                try { _proxy?.Dispose(); } catch { }
                _proxy = null;
            }
        }
    }
}
