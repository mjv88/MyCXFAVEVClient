using System;
using System.IO;
using System.Linq;
using DatevBridge.Core.Config;
using DatevBridge.Datev;
using DatevBridge.Datev.Managers;
using DatevBridge.Datev.PluginData;

namespace DatevBridge.Core
{
    /// <summary>
    /// Watches 3CXDATEVBridge.ini in %AppData%\3CXDATEVBridge for [Debug] section changes at runtime.
    /// The INI file is managed by AppConfig; this watcher handles hot-reload of debug overrides.
    ///
    /// [Debug] section keys:
    ///   VerboseLogging=true      - Enable debug-level logging
    ///   Contacts=true            - Dump all cached contacts to contacts.txt
    ///   AddressatContacts=true   - Dump recipient (Adressat) contacts to addressat_contacts.txt
    ///   InstitutionContacts=true - Dump institution contacts to institution_contacts.txt
    ///   TAPIDebug=1|2|3          - TAPI debug level (1=calls, 2=+states, 3=+raw)
    ///   DATEVDebug=1|2|3         - DATEV debug level (1=COM, 2=+data, 3=+raw)
    /// </summary>
    public class DebugConfigWatcher : IDisposable
    {
        private const int FILE_CHANGE_DEBOUNCE_MS = 300;

        private static DebugConfigWatcher _instance;
        private static readonly object _instanceLock = new object();

        private readonly FileSystemWatcher _watcher;
        private readonly System.Timers.Timer _debounceTimer;
        private readonly string _iniFilePath;
        private readonly string _iniFolder;
        private readonly string _contactsFilePath;
        private readonly string _addressatContactsFilePath;
        private readonly string _institutionContactsFilePath;

        /// <summary>
        /// TAPI debug level (0=off, 1=calls, 2=+states, 3=+raw events)
        /// </summary>
        public int TAPIDebugLevel { get; private set; }

        /// <summary>
        /// DATEV debug level (0=off, 1=COM calls, 2=+data, 3=+raw)
        /// </summary>
        public int DATEVDebugLevel { get; private set; }

        // Connection settings (nullable = not overridden, use INI config default)
        public int? ReconnectIntervalSeconds { get; private set; }
        public int? ConnectionTimeoutSeconds { get; private set; }
        public int? ReadTimeoutSeconds { get; private set; }
        public int? WriteTimeoutSeconds { get; private set; }
        public int? DatevCircuitBreakerThreshold { get; private set; }
        public int? DatevCircuitBreakerTimeoutSeconds { get; private set; }
        public int? SddMaxRetries { get; private set; }
        public int? SddRetryDelaySeconds { get; private set; }

        // Contacts settings
        public int? StaleCallTimeoutMinutes { get; private set; }
        public int? StalePendingTimeoutSeconds { get; private set; }
        public int? ContactReshowDelaySeconds { get; private set; }
        public int? LastContactRoutingMinutes { get; private set; }

        /// <summary>
        /// Singleton instance (null until Start is called)
        /// </summary>
        public static DebugConfigWatcher Instance => _instance;

        /// <summary>
        /// Start watching for 3CXDATEVBridge.ini
        /// </summary>
        public static void Start()
        {
            lock (_instanceLock)
            {
                if (_instance != null) return;
                _instance = new DebugConfigWatcher();
            }
        }

        /// <summary>
        /// Stop watching
        /// </summary>
        public static void Stop()
        {
            lock (_instanceLock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        private DebugConfigWatcher()
        {
            // Use the unified INI file managed by AppConfig
            _iniFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "3CXDATEVBridge");

            if (!Directory.Exists(_iniFolder))
                Directory.CreateDirectory(_iniFolder);

            _iniFilePath = Path.Combine(_iniFolder, "3CXDATEVBridge.ini");
            _contactsFilePath = Path.Combine(_iniFolder, "contacts.txt");
            _addressatContactsFilePath = Path.Combine(_iniFolder, "addressat_contacts.txt");
            _institutionContactsFilePath = Path.Combine(_iniFolder, "institution_contacts.txt");

            // Apply current config
            ApplyConfig();

            // Setup debounce timer (avoids rapid-fire events and file lock issues)
            _debounceTimer = new System.Timers.Timer(FILE_CHANGE_DEBOUNCE_MS);
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += (s, args) => ApplyConfig();

            // Watch for changes to the unified INI file
            _watcher = new FileSystemWatcher(_iniFolder, "3CXDATEVBridge.ini");
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.EnableRaisingEvents = true;

            LogManager.Debug("Debug config watcher started (monitoring {0})", _iniFilePath);
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Reset debounce timer - delays ApplyConfig until file changes settle
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            ResetDefaults();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (string.Equals(e.OldName, "3CXDATEVBridge.ini", StringComparison.OrdinalIgnoreCase))
                ResetDefaults();
        }

        private void ApplyConfig()
        {
            try
            {
                if (!File.Exists(_iniFilePath))
                    return;

                var lines = File.ReadAllLines(_iniFilePath);
                string currentSection = "";
                bool contactsDump = false;
                bool addressatContactsDump = false;
                bool institutionContactsDump = false;

                // Reset overrides before re-parsing
                ReconnectIntervalSeconds = null;
                ConnectionTimeoutSeconds = null;
                ReadTimeoutSeconds = null;
                WriteTimeoutSeconds = null;
                DatevCircuitBreakerThreshold = null;
                DatevCircuitBreakerTimeoutSeconds = null;
                SddMaxRetries = null;
                SddRetryDelaySeconds = null;
                StaleCallTimeoutMinutes = null;
                StalePendingTimeoutSeconds = null;
                ContactReshowDelaySeconds = null;
                LastContactRoutingMinutes = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                        continue;

                    // Section header
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLower();
                        continue;
                    }

                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim().ToLower();
                    var value = parts[1].Trim();

                    switch (currentSection)
                    {
                        case "debug":
                            ApplyDebugSetting(key, value, ref contactsDump, ref addressatContactsDump, ref institutionContactsDump);
                            break;
                        case "connection":
                        case "datev - connection":
                            ApplyConnectionSetting(key, value);
                            break;
                        case "settings":
                            ApplySettingsSetting(key, value);
                            break;
                        case "contacts":
                        case "datev - contacts":
                            ApplyContactsSetting(key, value);
                            break;
                        case "logging":
                            // Handled by AppConfig via P/Invoke — no hot-reload action needed
                            break;
                        default:
                            // Keys without a section are treated as debug settings (backwards compat)
                            ApplyDebugSetting(key, value, ref contactsDump, ref addressatContactsDump, ref institutionContactsDump);
                            break;
                    }
                }

                // Log config applied with cache statistics
                var allContacts = DatevCache.GetAllContacts();
                int addressatCount = allContacts.Count(c => c.DatevContact.IsRecipient);
                int institutionCount = allContacts.Count(c => !c.DatevContact.IsRecipient);
                LogManager.Debug("3CXDATEVBridge.ini applied: TAPIDebug={0}, DATEVDebug={1}, Cache={2} addressat/{3} institution",
                    TAPIDebugLevel, DATEVDebugLevel, addressatCount, institutionCount);

                // Dump contacts after all config is parsed
                if (contactsDump)
                    DumpContacts();
                if (addressatContactsDump)
                    DumpAddressatContacts();
                if (institutionContactsDump)
                    DumpInstitutionContacts();
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Error reading 3CXDATEVBridge.ini");
            }
        }

        private void ApplyDebugSetting(string key, string value, ref bool contactsDump, ref bool addressatContactsDump, ref bool institutionContactsDump)
        {
            switch (key)
            {
                case "verboselogging":
                    if (bool.TryParse(value, out bool verbose))
                        LogManager.SetDebugMode(verbose);
                    break;

                case "contacts":
                    if (bool.TryParse(value, out bool dump) && dump)
                        contactsDump = true;
                    break;

                case "addressatcontacts":
                    if (bool.TryParse(value, out bool addressatDump) && addressatDump)
                        addressatContactsDump = true;
                    break;

                case "institutioncontacts":
                    if (bool.TryParse(value, out bool institutionDump) && institutionDump)
                        institutionContactsDump = true;
                    break;

                case "tapidebug":
                    if (int.TryParse(value, out int tapiLevel))
                        TAPIDebugLevel = Math.Min(Math.Max(tapiLevel, 0), 3);
                    break;

                case "datevdebug":
                    if (int.TryParse(value, out int datevLevel))
                        DATEVDebugLevel = Math.Min(Math.Max(datevLevel, 0), 3);
                    break;

                default:
                    // Silently ignore — AppConfig handles all other keys via P/Invoke
                    break;
            }
        }

        private void ApplyConnectionSetting(string key, string value)
        {
            int v;
            switch (key)
            {
                case "reconnectintervalseconds":
                    if (int.TryParse(value, out v)) ReconnectIntervalSeconds = Math.Max(1, Math.Min(v, 300));
                    break;
                case "connectiontimeoutseconds":
                    if (int.TryParse(value, out v)) ConnectionTimeoutSeconds = Math.Max(5, Math.Min(v, 300));
                    break;
                case "readtimeoutseconds":
                    if (int.TryParse(value, out v)) ReadTimeoutSeconds = Math.Max(10, Math.Min(v, 600));
                    break;
                case "writetimeoutseconds":
                    if (int.TryParse(value, out v)) WriteTimeoutSeconds = Math.Max(5, Math.Min(v, 300));
                    break;
                case "datevcircuitbreakerthreshold":
                    if (int.TryParse(value, out v)) DatevCircuitBreakerThreshold = Math.Max(1, Math.Min(v, 10));
                    break;
                case "datevcircuitbreakertimeoutseconds":
                    if (int.TryParse(value, out v)) DatevCircuitBreakerTimeoutSeconds = Math.Max(10, Math.Min(v, 300));
                    break;
                case "sddmaxretries":
                    if (int.TryParse(value, out v)) SddMaxRetries = Math.Max(0, Math.Min(v, 10));
                    break;
                case "sddretrydelayseconds":
                    if (int.TryParse(value, out v)) SddRetryDelaySeconds = Math.Max(1, Math.Min(v, 30));
                    break;
                case "stalecalltimeoutminutes":
                    if (int.TryParse(value, out v)) StaleCallTimeoutMinutes = Math.Max(30, Math.Min(v, 1440));
                    break;
                case "stalependingtimeoutseconds":
                    if (int.TryParse(value, out v)) StalePendingTimeoutSeconds = Math.Max(30, Math.Min(v, 3600));
                    break;
            }
        }

        private void ApplySettingsSetting(string key, string value)
        {
            // Only handle keys that DebugConfigWatcher tracks for hot-reload.
            // All other [Settings] keys are read by AppConfig via P/Invoke on demand.
            int v;
            switch (key)
            {
                case "contactreshowdelayseconds":
                    if (int.TryParse(value, out v)) ContactReshowDelaySeconds = Math.Max(0, Math.Min(v, 30));
                    break;
                case "lastcontactroutingminutes":
                    if (int.TryParse(value, out v)) LastContactRoutingMinutes = Math.Max(0, Math.Min(v, 1440));
                    break;
            }
        }

        private void ApplyContactsSetting(string key, string value)
        {
            // Backwards compatibility for old [Contacts] / [DATEV - Contacts] sections
            int v;
            switch (key)
            {
                case "stalecalltimeoutminutes":
                    if (int.TryParse(value, out v)) StaleCallTimeoutMinutes = Math.Max(30, Math.Min(v, 1440));
                    break;
                case "stalependingtimeoutseconds":
                    if (int.TryParse(value, out v)) StalePendingTimeoutSeconds = Math.Max(30, Math.Min(v, 3600));
                    break;
                case "contactreshowdelayseconds":
                    if (int.TryParse(value, out v)) ContactReshowDelaySeconds = Math.Max(0, Math.Min(v, 30));
                    break;
                case "lastcontactroutingminutes":
                    if (int.TryParse(value, out v)) LastContactRoutingMinutes = Math.Max(0, Math.Min(v, 1440));
                    break;
            }
        }

        private void DumpContacts() =>
            DumpContactsFiltered(null, _contactsFilePath, "DATEV Contacts Dump");

        private void DumpAddressatContacts() =>
            DumpContactsFiltered(c => c.DatevContact.IsRecipient, _addressatContactsFilePath, "DATEV Addressat (Recipient) Contacts");

        private void DumpInstitutionContacts() =>
            DumpContactsFiltered(c => !c.DatevContact.IsRecipient, _institutionContactsFilePath, "DATEV Institution Contacts");

        private void DumpContactsFiltered(Func<DatevContactInfo, bool> filter, string filePath, string header)
        {
            try
            {
                var allContacts = DatevCache.GetAllContacts();
                var contacts = filter != null ? allContacts.Where(filter).ToList() : allContacts;

                using (var writer = new StreamWriter(filePath, false))
                {
                    writer.WriteLine("; {0} - {1:yyyy-MM-dd HH:mm:ss}", header, DateTime.Now);
                    writer.WriteLine("; Total: {0} entries", contacts.Count);
                    writer.WriteLine();

                    foreach (var contact in contacts)
                    {
                        string type = contact.DatevContact.IsRecipient ? "Recipient" : "Institution";
                        string person = contact.DatevContact.IsPrivatePerson ? "Private" : "Business";
                        writer.WriteLine("[{0}/{1}] {2}", type, person, contact.DatevContact.Name);
                        writer.WriteLine("  ID: {0}", contact.DatevContact.Id);
                        writer.WriteLine("  Phone: {0}", contact.Communication.Number);
                        writer.WriteLine("  Normalized: {0}", contact.Communication.EffectiveNormalizedNumber);
                        writer.WriteLine();
                    }
                }

                LogManager.Log("{0} dumped to {1} ({2} entries)", header, filePath, contacts.Count);
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Error dumping contacts to {0}", filePath);
            }
        }

        private void ResetDefaults()
        {
            TAPIDebugLevel = 0;
            DATEVDebugLevel = 0;

            // Clear all overrides
            ReconnectIntervalSeconds = null;
            ConnectionTimeoutSeconds = null;
            ReadTimeoutSeconds = null;
            WriteTimeoutSeconds = null;
            DatevCircuitBreakerThreshold = null;
            DatevCircuitBreakerTimeoutSeconds = null;
            SddMaxRetries = null;
            SddRetryDelaySeconds = null;
            StaleCallTimeoutMinutes = null;
            StalePendingTimeoutSeconds = null;
            ContactReshowDelaySeconds = null;
            LastContactRoutingMinutes = null;

            LogManager.SetDebugMode(false);
            LogManager.Log("3CXDATEVBridge.ini removed - restored defaults");
        }

        /// <summary>
        /// Helper to get an int setting with INI override, falling back to AppConfig then default.
        /// </summary>
        public static int GetInt(int? iniValue, string appConfigKey, int defaultValue)
        {
            if (iniValue.HasValue)
                return iniValue.Value;

            return AppConfig.GetInt(appConfigKey, defaultValue);
        }

        public void Dispose()
        {
            _debounceTimer?.Dispose();

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
        }
    }
}
