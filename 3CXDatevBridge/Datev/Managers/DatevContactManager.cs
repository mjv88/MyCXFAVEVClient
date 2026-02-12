using Datev.Sdd.Data.ClientInterfaces;
using Datev.Sdd.Data.ClientPlugIn;
using DatevBridge.Core;
using DatevBridge.Datev.DatevData;
using DatevBridge.Datev.DatevData.Enums;
using DatevBridge.Datev.DatevData.Institutions;
using DatevBridge.Datev.DatevData.Recipients;
using DatevBridge.Datev.PluginData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

// Memory optimization: Only load phone communications (Medium == Phone)
// This reduces Communication objects from ~68,000 to ~17,000

namespace DatevBridge.Datev.Managers
{
    /// <summary>
    /// Fetches contacts from DATEV SDD (Stammdatendienst)
    /// </summary>
    internal static class DatevContactManager
    {
        // Thread-safe backing fields
        private static readonly object _lock = new object();
        private static bool _filterActiveContactsOnly = false;
        private static DateTime? _lastSyncTimestamp;

        /// <summary>
        /// When true, only active contacts (Status != 0) are fetched from DATEV.
        /// Status 0 means the contact is inactive/disabled. Thread-safe.
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

        /// <summary>
        /// Check if communications array has valid phone numbers.
        /// Only checks phone-type communications (Medium == Phone) for matching.
        /// Uses EffectiveNormalizedNumber to ensure both Recipients and Institutions
        /// with valid phone numbers are included, even if DATEV's NormierteNummer is empty.
        /// </summary>
        public static bool CheckCommunications(Communication[] communications)
        {
            return communications != null &&
                   communications.Any(c => c.Medium == Medium.Phone &&
                                          !string.IsNullOrWhiteSpace(c.EffectiveNormalizedNumber));
        }

        /// <summary>
        /// Filters communications to only include phone numbers (Medium == Phone).
        /// This significantly reduces memory usage by excluding fax, email, website, etc.
        /// Returns Array.Empty if no phone communications exist.
        /// </summary>
        public static Communication[] FilterPhoneCommunications(Communication[] communications)
        {
            if (communications == null || communications.Length == 0)
                return Array.Empty<Communication>();

            var phoneComms = communications
                .Where(c => c.Medium == Medium.Phone && !string.IsNullOrWhiteSpace(c.EffectiveNormalizedNumber))
                .ToArray();

            return phoneComms.Length > 0 ? phoneComms : Array.Empty<Communication>();
        }

        /// <summary>
        /// Get all contacts from DATEV (Recipients + Institutions)
        /// </summary>
        /// <param name="startProgress">Progress bar start percentage</param>
        /// <param name="endProgress">Progress bar end percentage</param>
        /// <param name="progress">Progress reporter</param>
        /// <returns>List of all contacts</returns>
        public static List<DatevContact> GetContacts(int startProgress = 0, int endProgress = 100, IProgress<int> progress = null)
        {
            return GetContacts(startProgress, endProgress, progress, null);
        }

        /// <summary>
        /// Get all contacts from DATEV (Recipients + Institutions) with text progress
        /// </summary>
        /// <param name="startProgress">Progress bar start percentage</param>
        /// <param name="endProgress">Progress bar end percentage</param>
        /// <param name="progress">Progress reporter</param>
        /// <param name="progressText">Optional callback for progress text updates</param>
        /// <returns>List of all contacts</returns>
        public static List<DatevContact> GetContacts(int startProgress, int endProgress, IProgress<int> progress, Action<string> progressText)
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
                        // Filter to phone-only communications to reduce memory usage
                        Communications = FilterPhoneCommunications(s.Contact.Communications)
                    })
                    .ToList();

                progressText?.Invoke($"{recipients.Count} Adressaten gefunden");
            }
            catch (Exception ex)
            {
                LogManager.Log("Error fetching Recipients: {0}", ex.Message);
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
                        // Filter to phone-only communications to reduce memory usage
                        Communications = FilterPhoneCommunications(s.Communications)
                    })
                    .ToList();

                progressText?.Invoke($"{institutions.Count} Institutionen gefunden");
            }
            catch (Exception ex)
            {
                LogManager.Log("Error fetching Institutions: {0}", ex.Message);
                progressText?.Invoke("Fehler beim Laden der Institutionen");
                institutions = new List<DatevContact>();
            }

            progress?.Report(endProgress);

            // Record successful sync timestamp
            LastSyncTimestamp = DateTime.Now;

            var allContacts = recipients.Union(institutions).ToList();
            int totalComms = allContacts.Sum(c => c.Communications?.Length ?? 0);
            LogManager.Log("SDD: {0} Kontakte mit {1} Rufnummnern syncronisiert.", allContacts.Count, totalComms);

            progressText?.Invoke($"{allContacts.Count} Kontakte geladen");

            return allContacts;
        }

        /// <summary>
        /// Get Recipients from DATEV SDD
        /// </summary>
        public static RecipientContactDetail[] GetRecipients()
        {
            const string contractIdentifier = "Datev.Sdd.Contract.Browse.1.2";
            const string elementName = "KontaktDetail";

            // Load all contacts (SDD filter syntax is unreliable, filter in memory instead)
            RecipientsContactList contactList = GetItemsList<RecipientsContactList>(
                contractIdentifier, elementName, string.Empty);

            if (contactList?.ContactDetails != null)
            {
                var contacts = contactList.ContactDetails;

                // Filter active contacts in memory if requested
                if (FilterActiveContactsOnly)
                {
                    contacts = contacts.Where(c => c.Status != 0).ToArray();
                    LogManager.Log("SDD: {0} Addressaten (aktiv, gefiltert von {1})",
                        contacts.Length, contactList.ContactDetails.Length);
                }
                else
                {
                    LogManager.Log("SDD: {0} Addressaten", contacts.Length);
                }

                return contacts;
            }

            return Array.Empty<RecipientContactDetail>();
        }

        /// <summary>
        /// Get Institutions from DATEV SDD
        /// </summary>
        public static InstitutionContactDetail[] GetInstitutions()
        {
            const string contractIdentifier = "Datev.Inst.Contract.1.0";
            const string elementName = "TELEFONIE";

            // No status filter for Institutions (they have different schema)
            InstitutionsContactList contactList = GetItemsList<InstitutionsContactList>(
                contractIdentifier, elementName, string.Empty);

            if (contactList?.ContactDetails != null)
            {
                LogManager.Log("SDD: {0} Institutionen", contactList.ContactDetails.Length);
                return contactList.ContactDetails;
            }

            return Array.Empty<InstitutionContactDetail>();
        }

        /// <summary>
        /// Generic method to fetch data from DATEV SDD with retry support.
        /// Retries on transient errors with exponential backoff.
        /// </summary>
        /// <param name="filterExpression">SDD filter expression (e.g., "@Status NOT EQUAL TO \"0\"")</param>
        private static T GetItemsList<T>(string contractIdentifier, string elementName, string filterExpression)
            where T : class
        {
            const string dataEnvironment = "Datev.DataEnvironment.Default";

            return RetryHelper.ExecuteWithRetry(
                () => GetItemsListInternal<T>(contractIdentifier, elementName, dataEnvironment, filterExpression),
                $"SDD fetch ({elementName})",
                shouldRetry: RetryHelper.IsTransientError);
        }

        /// <summary>
        /// Internal method that performs the actual SDD fetch.
        /// </summary>
        /// <param name="filterExpression">SDD filter expression</param>
        private static T GetItemsListInternal<T>(string contractIdentifier, string elementName, string dataEnvironment, string filterExpression)
            where T : class
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
                        // HasData=false - SDD not ready, throw to trigger retry
                        throw new System.Xml.XmlException($"SDD returned no data ({elementName}, HasData=false)");
                    }

                    try
                    {
                        XmlSerializer xmlSerializer = new XmlSerializer(typeof(T));

                        using (XmlReader xmlReader = response.CreateReader())
                        {
                            return (T)xmlSerializer.Deserialize(xmlReader);
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.InnerException is System.Xml.XmlException xmlEx)
                    {
                        // XmlSerializer wraps XmlException in InvalidOperationException
                        // Re-throw as XmlException with source info for retry logic
                        throw new System.Xml.XmlException($"SDD XML invalid ({elementName}): {xmlEx.Message}", xmlEx);
                    }
                }
            }
        }
    }
}
