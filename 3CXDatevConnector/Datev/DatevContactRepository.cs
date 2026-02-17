using Datev.Sdd.Data.ClientInterfaces;
using Datev.Sdd.Data.ClientPlugIn;
using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.DatevData;
using DatevConnector.Datev.DatevData.Enums;
using DatevConnector.Datev.DatevData.Institutions;
using DatevConnector.Datev.DatevData.Recipients;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace DatevConnector.Datev
{
    /// <summary>
    /// Unified repository for DATEV contacts: fetches from SDD, caches locally,
    /// and provides phone-number lookup.
    /// </summary>
    public static class DatevContactRepository
    {
        private static List<DatevContact> _datevContacts;
        private static SortedDictionary<string, List<DatevContactInfo>> _datevContactsSDict;
        private static int? _maxCompareLength;
        private static bool _debugLogging;

        // Configuration / state (thread-safe)
        private static readonly object _lock = new object();
        private static bool _filterActiveContactsOnly;
        private static DateTime? _lastSyncTimestamp;

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
                Init();

                string normalizedNumber = PhoneNumberNormalizer.NormalizeForComparison(
                    contactNumber,
                    _maxCompareLength ?? 10);

                LogManager.Log("Connector: Kontaktsuche - Eingabe='{0}' Normalisiert='{1}'",
                    LogManager.Mask(contactNumber), LogManager.Mask(normalizedNumber));

                List<DatevContactInfo> result = null;

                // Try exact match first
                if (_datevContactsSDict != null && _datevContactsSDict.ContainsKey(normalizedNumber))
                {
                    result = _datevContactsSDict[normalizedNumber];
                    LogManager.Debug("Connector: Kontaktsuche - Exakte Übereinstimmung auf Schlüssel '{0}'",
                        LogManager.Mask(normalizedNumber));
                }

                // Suffix matching for shorter DATEV numbers (min 6 digits overlap)
                const int minSuffixMatchLength = 6;
                if ((result == null || result.Count == 0) && _datevContactsSDict != null &&
                    normalizedNumber.Length >= minSuffixMatchLength)
                {
                    foreach (var kvp in _datevContactsSDict)
                    {
                        if (kvp.Key.Length < minSuffixMatchLength)
                            continue;

                        if (normalizedNumber.EndsWith(kvp.Key) || kvp.Key.EndsWith(normalizedNumber))
                        {
                            result = kvp.Value;
                            LogManager.Debug("Connector: Kontaktsuche - Suffixübereinstimmung: '{0}' <-> '{1}'",
                                LogManager.Mask(normalizedNumber), LogManager.Mask(kvp.Key));
                            break;
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

        // ===== Internals — Init & Build =====

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

        private static bool CheckCommunications(Communication[] communications)
        {
            return communications != null &&
                   communications.Any(c => c.Medium == Medium.Phone &&
                                          !string.IsNullOrWhiteSpace(c.EffectiveNormalizedNumber));
        }

        private static Communication[] FilterPhoneCommunications(Communication[] communications)
        {
            if (communications == null || communications.Length == 0)
                return Array.Empty<Communication>();

            var phoneComms = communications
                .Where(c => c.Medium == Medium.Phone && !string.IsNullOrWhiteSpace(c.EffectiveNormalizedNumber))
                .ToArray();

            return phoneComms.Length > 0 ? phoneComms : Array.Empty<Communication>();
        }

        private static List<DatevContact> FetchContacts(int startProgress, int endProgress,
            IProgress<int> progress, Action<string> progressText)
        {
            int delta = endProgress - startProgress;
            progress?.Report(startProgress);

            // Get Recipients (Adressaten)
            progressText?.Invoke("Suche Adressaten...");
            List<DatevContact> recipients;
            try
            {
                recipients = GetRecipients()
                    .Where(s => s.Contact != null && CheckCommunications(s.Contact.Communications))
                    .Select(s => new DatevContact
                    {
                        Id = s.Id,
                        Name = s.Contact.Name,
                        IsPrivatePerson = s.Contact.Type == ContactType.Person,
                        IsRecipient = true,
                        Communications = FilterPhoneCommunications(s.Contact.Communications)
                    })
                    .ToList();
                progressText?.Invoke($"{recipients.Count} Adressaten gefunden");
            }
            catch (Exception ex)
            {
                LogManager.Log("Fehler beim Abrufen der Adressaten: {0}", ex.Message);
                progressText?.Invoke("Fehler beim Laden der Adressaten");
                recipients = new List<DatevContact>();
            }

            progress?.Report(startProgress + delta / 2);

            // Get Institutions (Institutionen)
            progressText?.Invoke("Suche Institutionen...");
            List<DatevContact> institutions;
            try
            {
                institutions = GetInstitutions()
                    .Where(s => CheckCommunications(s.Communications))
                    .Select(s => new DatevContact
                    {
                        Id = s.Id,
                        Name = s.Name,
                        IsPrivatePerson = false,
                        IsRecipient = false,
                        Communications = FilterPhoneCommunications(s.Communications)
                    })
                    .ToList();
                progressText?.Invoke($"{institutions.Count} Institutionen gefunden");
            }
            catch (Exception ex)
            {
                LogManager.Log("Fehler beim Abrufen der Institutionen: {0}", ex.Message);
                progressText?.Invoke("Fehler beim Laden der Institutionen");
                institutions = new List<DatevContact>();
            }

            progress?.Report(endProgress);
            LastSyncTimestamp = DateTime.Now;

            var allContacts = recipients.Union(institutions).ToList();
            int totalComms = allContacts.Sum(c => c.Communications?.Length ?? 0);
            LogManager.Log("SDD: {0} Kontakte mit {1} Rufnummnern synchronisiert.", allContacts.Count, totalComms);
            progressText?.Invoke($"{allContacts.Count} Kontakte geladen");

            return allContacts;
        }

        private static RecipientContactDetail[] GetRecipients()
        {
            const string contractIdentifier = "Datev.Sdd.Contract.Browse.1.2";
            const string elementName = "KontaktDetail";

            RecipientsContactList contactList = GetItemsList<RecipientsContactList>(
                contractIdentifier, elementName, string.Empty);

            if (contactList?.ContactDetails != null)
            {
                var contacts = contactList.ContactDetails;

                if (FilterActiveContactsOnly)
                {
                    contacts = contacts.Where(c => c.Status != 0).ToArray();
                    LogManager.Log("SDD: {0} Adressaten (aktiv, gefiltert von {1})",
                        contacts.Length, contactList.ContactDetails.Length);
                }
                else
                {
                    LogManager.Log("SDD: {0} Adressaten", contacts.Length);
                }

                return contacts;
            }

            return Array.Empty<RecipientContactDetail>();
        }

        private static InstitutionContactDetail[] GetInstitutions()
        {
            const string contractIdentifier = "Datev.Inst.Contract.1.0";
            const string elementName = "TELEFONIE";

            InstitutionsContactList contactList = GetItemsList<InstitutionsContactList>(
                contractIdentifier, elementName, string.Empty);

            if (contactList?.ContactDetails != null)
            {
                LogManager.Log("SDD: {0} Institutionen", contactList.ContactDetails.Length);
                return contactList.ContactDetails;
            }

            return Array.Empty<InstitutionContactDetail>();
        }

        private static T GetItemsList<T>(string contractIdentifier, string elementName, string filterExpression)
            where T : class
        {
            const string dataEnvironment = "Datev.DataEnvironment.Default";

            return RetryHelper.ExecuteWithRetry(
                () => GetItemsListInternal<T>(contractIdentifier, elementName, dataEnvironment, filterExpression),
                $"SDD fetch ({elementName})",
                shouldRetry: RetryHelper.IsTransientError);
        }

        private static T GetItemsListInternal<T>(string contractIdentifier, string elementName,
            string dataEnvironment, string filterExpression) where T : class
        {
            using (Proxy proxy = Proxy.Instance)
            {
                IRequestHandler requestHandler = proxy.RequestHandler;
                IRequestHelper requestHelper = proxy.RequestHelper;

                Request readRequest =
                    requestHelper.CreateDataObjectCollectionAccessReadRequest(
                        elementName,
                        contractIdentifier,
                        dataEnvironment,
                        filterExpression ?? string.Empty);

                using (Response response = requestHandler.Execute(readRequest))
                {
                    if (!response.HasData)
                    {
                        throw new XmlException($"SDD returned no data ({elementName}, HasData=false)");
                    }

                    try
                    {
                        XmlSerializer xmlSerializer = new XmlSerializer(typeof(T));
                        using (XmlReader xmlReader = response.CreateReader())
                        {
                            return (T)xmlSerializer.Deserialize(xmlReader);
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.InnerException is XmlException xmlEx)
                    {
                        throw new XmlException($"SDD XML invalid ({elementName}): {xmlEx.Message}", xmlEx);
                    }
                }
            }
        }
    }
}
