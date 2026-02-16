using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core.Config;
using DatevConnector.Datev;
using DatevConnector.Datev.COMs;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Tapi;
using DatevConnector.Webclient;

namespace DatevConnector.Core
{
    /// <summary>
    /// Main orchestrator - connects 3CX TAPI and DATEV Telefonie
    /// </summary>
    public class ConnectorService : IDisposable
    {
        private string _extension;
        private readonly CallTracker _callTracker;
        private readonly NotificationManager _notificationManager;
        private readonly DatevAdapter _datevAdapter;
        private readonly DatevCommandHandler _datevCommandHandler;
        private readonly CallEventProcessor _callEventProcessor;
        private readonly object _statusLock = new object();
        private ConnectorStatus _status = ConnectorStatus.Disconnected;
        private static readonly IReadOnlyList<TapiLineInfo> EmptyLineList = new List<TapiLineInfo>().AsReadOnly();

        // Thread-safe mutable state (volatile ensures visibility across threads)
        private volatile IConnectionMethod _tapiMonitor;
        private volatile CancellationTokenSource _cts;
        private volatile bool _disposed;
        private Task _pipeServerTask;

        // Settings
        private int _minCallerIdLength;
        private bool _isMuted;

        // Call history
        private readonly CallHistoryStore _callHistory;

        // Connection mode selection
        private volatile ConnectionMode _selectedMode = ConnectionMode.Auto;
        private ConnectionMode _configuredConnectionMode = ConnectionMode.Auto;
        private string _detectionDiagnostics;

        /// <summary>
        /// Event fired when connection status changes
        /// </summary>
        public event Action<ConnectorStatus> StatusChanged;

        /// <summary>
        /// Event fired when the selected connection mode changes (for immediate UI updates).
        /// </summary>
        public event Action<ConnectionMode> ModeChanged;

        /// <summary>
        /// Current connection status (thread-safe)
        /// </summary>
        public ConnectorStatus Status
        {
            get
            {
                lock (_statusLock)
                {
                    return _status;
                }
            }
            private set
            {
                Action<ConnectorStatus> handler = null;
                lock (_statusLock)
                {
                    if (_status != value)
                    {
                        _status = value;
                        handler = StatusChanged;
                    }
                }
                // Invoke outside lock to prevent deadlocks
                handler?.Invoke(value);
            }
        }

        /// <summary>
        /// Extension number being monitored (first connected line for backward compatibility)
        /// </summary>
        public string Extension => _extension;

        /// <summary>
        /// All connected TAPI lines
        /// </summary>
        public IReadOnlyList<TapiLineInfo> TapiLines => _tapiMonitor?.Lines ?? EmptyLineList;

        /// <summary>
        /// Number of connected TAPI lines
        /// </summary>
        public int ConnectedLineCount => _tapiMonitor?.ConnectedLineCount ?? 0;

        /// <summary>
        /// Whether DATEV is currently available (ROT check)
        /// </summary>
        public bool DatevAvailable { get; private set; }

        /// <summary>
        /// Whether TAPI is currently connected (at least one line)
        /// </summary>
        public bool TapiConnected => Status == ConnectorStatus.Connected;

        /// <summary>
        /// When true, all popups and notifications are suppressed (silent mode).
        /// </summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                _callEventProcessor.IsMuted = value;
                LogManager.Log("Connector: Stummschaltung {0}", value ? "aktiviert" : "deaktiviert");
            }
        }

        public int ContactCount => DatevContactRepository.ContactCount;

        /// <summary>
        /// Call history store for re-journaling
        /// </summary>
        public CallHistoryStore CallHistory => _callHistory;

        /// <summary>
        /// The selected connection mode (after auto-detection or explicit config).
        /// </summary>
        public ConnectionMode SelectedConnectionMode => _selectedMode;

        /// <summary>
        /// Diagnostic summary from provider auto-detection (for Setup Wizard / troubleshooting).
        /// </summary>
        public string DetectionDiagnostics => _detectionDiagnostics;

        /// <summary>
        /// Get all cached contacts (for developer diagnostics)
        /// </summary>
        public List<DatevContactInfo> GetCachedContacts() => DatevContactRepository.GetAllContacts();

        public ConnectorService(string extension)
        {
            _extension = extension;
            _callTracker = new CallTracker();
            _minCallerIdLength = AppConfig.GetIntClamped(ConfigKeys.MinCallerIdLength, 2, 1, 20);

            // Use base GUID - Windows ROT is already per-session on terminal servers
            _notificationManager = new NotificationManager(CommonParameters.ClsIdDatev);

            // Create command handler and DatevAdapter for receiving Dial/Drop commands from DATEV
            _datevCommandHandler = new DatevCommandHandler(_callTracker, () => _tapiMonitor);
            _datevAdapter = new DatevAdapter(_datevCommandHandler.OnDatevEvent);

            // Call history
            bool histInbound = AppConfig.GetBool(ConfigKeys.CallHistoryInbound, true);
            bool histOutbound = AppConfig.GetBool(ConfigKeys.CallHistoryOutbound, false);
            int histMax = AppConfig.GetIntClamped(ConfigKeys.CallHistoryMaxEntries, 5, 1, 100);
            _callHistory = new CallHistoryStore(histMax, histInbound, histOutbound);

            // Call event processing (handles all TAPI state transitions + DATEV notifications + UI popups)
            _callEventProcessor = new CallEventProcessor(
                _callTracker, _notificationManager, _callHistory, _extension, _minCallerIdLength);

            // Contact filter (must be set before LoadContactsAsync)
            DatevContactRepository.FilterActiveContactsOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            LogManager.Debug("Configuration: MinCallerIdLength={0}, ActiveContactsOnly={1}",
                _minCallerIdLength, DatevContactRepository.FilterActiveContactsOnly);
        }

        /// <summary>
        /// Start the bridge service
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // ── Step 1: Mode header first, then environment info ─────────
            _configuredConnectionMode = AppConfig.GetEnum(ConfigKeys.ConnectionMode, ConnectionMode.Auto);

            LogManager.Log("3CX Telefonie Modus Initialisierung...");
            LogManager.Log("3CX Telefonie Modus: {0} (konfiguriert)",
                _configuredConnectionMode == ConnectionMode.Auto ? "Auto-Detection" : _configuredConnectionMode.ToString());

            SessionManager.LogSessionInfo();

            // ── Step 2: Provider selection / auto-detection ──────────────

            var selectionResult = await ConnectionMethodSelector.SelectProviderAsync(
                _extension, _cts.Token);

            _selectedMode = selectionResult.SelectedMode;
            _detectionDiagnostics = selectionResult.DiagnosticSummary;

            LogManager.Log("Telefonie Modus: {0}", _selectedMode);
            LogManager.Debug("Telefonie Modus Grund: {0}", selectionResult.Reason);

            // For Pipe mode on Terminal Server, start the pipe FIRST before DATEV init
            // so the 3CX Softphone can find it while we load contacts
            bool isPipeMode = _selectedMode == ConnectionMode.Pipe;
            if (isPipeMode && selectionResult.Success)
            {
                LogManager.Log("========================================");
                LogManager.Log("  3CX Terminal Server (early start)");
                LogManager.Log("========================================");
                _pipeServerTask = ConnectWithRetryAsync(_cts.Token, selectionResult.Provider);
                await Task.Delay(100, cancellationToken);
            }

            // ── Step 3: DATEV initialization ─────────────────────────────
            try
            {
                AdapterManager.Register(_datevAdapter);
            }
            catch (Exception ex)
            {
                LogManager.Log("DatevAdapter Registrierung fehlgeschlagen: {0}", ex.Message);
            }

            DatevAvailable = DatevConnectionChecker.CheckAndLogDatevStatus();

            if (DatevAvailable)
            {
                await LoadContactsAsync();
            }
            else
            {
                ShowDatevUnavailableNotification();
            }

            StartDatevAutoDetect();
            DebugConfigWatcher.Start();

            // ── Step 4: Telephony connection ─────────────────────────────
            if (!selectionResult.Success)
            {
                // No provider detected — log diagnostics and wait for wizard
                LogManager.Warning("========================================");
                LogManager.Warning("  Kein Connector erkannt!");
                LogManager.Warning("========================================");
                LogManager.Warning(selectionResult.DiagnosticSummary ?? "Keine Diagnoseinformationen verfügbar");
                Status = ConnectorStatus.Disconnected;
                return;
            }

            if (isPipeMode)
            {
                // Pipe server already running — await it
                await _pipeServerTask;
            }
            else
            {
                LogManager.Log("========================================");
                LogManager.Log("  3CX Telefonie Modus ({0})", _selectedMode);
                LogManager.Log("========================================");
                await ConnectWithRetryAsync(_cts.Token, selectionResult.Provider);
            }
        }

        /// <summary>
        /// Load contacts from DATEV SDD with proper logging
        /// </summary>
        private async Task LoadContactsAsync()
        {
            try
            {
                LogManager.Log("========================================");
                LogManager.Log("  DATEV Kontaktsynchronisation");
                LogManager.Log("========================================");

                int timeoutSec = AppConfig.GetIntClamped(ConfigKeys.ContactLoadTimeoutSeconds, 120, 30, 600);
                var loadTask = DatevContactRepository.StartLoadAsync();
                var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));

                if (completed != loadTask)
                {
                    LogManager.Warning("Kontakte laden Zeitüberschreitung nach {0}s - fahre ohne Kontakte fort", timeoutSec);
                    return;
                }

                await loadTask; // propagate exceptions
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Kontakte konnten nicht von DATEV SDD geladen werden");
            }
        }

        /// <summary>
        /// Show balloon notification that DATEV is not available
        /// </summary>
        private void ShowDatevUnavailableNotification()
        {
            DatevUnavailableNotified?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Event raised when DATEV is not available (for UI notification)
        /// </summary>
        public event EventHandler DatevUnavailableNotified;

        /// <summary>
        /// Event raised when DATEV becomes available (for UI notification)
        /// </summary>
        public event EventHandler DatevBecameAvailable;

        /// <summary>
        /// Start adaptive background polling for DATEV availability.
        /// Short intervals (5s) when unavailable, longer (60s) when connected.
        /// </summary>
        private void StartDatevAutoDetect()
        {
            const int ShortInterval = 5000;   // 5s when unavailable
            const int LongInterval = 60000;   // 60s when available

            LogManager.Log("Start des 3CX-DATEV-Erkennungsdienst...");

            Task.Run(async () =>
            {
                int interval = ShortInterval;

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException) { break; }

                    if (_cts.Token.IsCancellationRequested) break;

                    bool available = DatevConnectionChecker.CheckDatevAvailability();

                    if (available && !DatevAvailable)
                    {
                        // DATEV just became available
                        DatevAvailable = true;
                        LogManager.Log("DATEV automatisch als verfügbar erkannt");
                        DatevConnectionChecker.CheckAndLogDatevStatus();
                        await LoadContactsAsync();
                        DatevBecameAvailable?.Invoke(this, EventArgs.Empty);
                        interval = LongInterval;
                    }
                    else if (!available && DatevAvailable)
                    {
                        // DATEV became unavailable
                        DatevAvailable = false;
                        LogManager.Warning("DATEV nicht mehr verfügbar");
                        ShowDatevUnavailableNotification();
                        interval = ShortInterval;
                    }
                    else if (!available)
                    {
                        // Still unavailable - keep short interval
                        interval = ShortInterval;
                    }
                    else
                    {
                        // Still available - keep long interval
                        interval = LongInterval;
                    }
                }
            }, _cts.Token);
        }

        /// <summary>
        /// Create the appropriate connection method based on the selected mode.
        /// Supports TAPI 2.x, Named Pipe, and Webclient (browser extension).
        /// </summary>
        private IConnectionMethod CreateConnectionMethod(string lineFilter)
        {
            switch (_selectedMode)
            {
                case ConnectionMode.WebClient:
                    LogManager.Log("WebClient-Modus ausgewählt");
                    return new WebclientConnectionMethod(_extension);

                case ConnectionMode.Pipe:
                    LogManager.Log("Terminal Server (TAPI)-Modus ausgewählt");
                    return new PipeConnectionMethod(_extension);

                case ConnectionMode.Tapi:
                default:
                    LogManager.Log("Desktop (TAPI)-Modus ausgewählt");
                    return new TapiLineMonitor(lineFilter, _extension);
            }
        }

        /// <summary>
        /// Auto-detect and adopt extension number from the first connected line.
        /// </summary>
        private void AdoptExtensionFromProvider(string source)
        {
            var firstLine = System.Linq.Enumerable.FirstOrDefault(_tapiMonitor.Lines, l => l.IsConnected);
            if (firstLine != null && firstLine.Extension != _extension)
            {
                LogManager.Debug("Nebenstelle erkannt von {0}: {1}", source, firstLine.Extension);
                _extension = firstLine.Extension;
                CallIdGenerator.Initialize(_extension);

                if (_extension.Length > _minCallerIdLength)
                {
                    LogManager.Log("Minimumlänge: {0} -> {1} -stellig (Aufgrund der Nebenstellenlänge)",
                        _minCallerIdLength, _extension.Length);
                    _minCallerIdLength = _extension.Length;
                    AppConfig.SetInt(ConfigKeys.MinCallerIdLength, _extension.Length);
                }
            }
        }

        /// <summary>
        /// Connect to 3CX via Windows TAPI with automatic retry
        /// </summary>
        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken, IConnectionMethod initialProvider = null)
        {
            var ini = DebugConfigWatcher.Instance;
            int reconnectInterval = DebugConfigWatcher.GetInt(
                ini?.ReconnectIntervalSeconds, "ReconnectIntervalSeconds", 5);

            // Read optional line name filter (empty = first available line)
            string lineFilter = AppConfig.GetString(ConfigKeys.TapiLineFilter, "");

            // Initialize CallID generator (will be re-initialized with extension after TAPI connect)
            CallIdGenerator.Initialize(_extension);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    IConnectionMethod providerToUse = null;

                    if (initialProvider != null)
                    {
                        // First iteration: reuse provider from initial auto-detection
                        providerToUse = initialProvider;
                        initialProvider = null; // consumed
                    }
                    else
                    {
                        var configuredModeNow = AppConfig.GetEnum(ConfigKeys.ConnectionMode, ConnectionMode.Auto);
                        if (configuredModeNow != _configuredConnectionMode)
                        {
                            LogManager.Log("ConnectionMode Konfiguration zur Laufzeit geändert: {0} -> {1}",
                                _configuredConnectionMode, configuredModeNow);
                            _configuredConnectionMode = configuredModeNow;
                        }

                        if (_configuredConnectionMode == ConnectionMode.Auto)
                        {
                            var autoSelection = await ConnectionMethodSelector.SelectProviderAsync(_extension, cancellationToken);
                            if (autoSelection.Success)
                            {
                                if (_selectedMode != autoSelection.SelectedMode)
                                {
                                    LogManager.Log("Auto-Modus Connector für Verbindungszyklus gewählt: {0} -> {1} (Grund: {2})",
                                        _selectedMode, autoSelection.SelectedMode, autoSelection.Reason);
                                }
                                _selectedMode = autoSelection.SelectedMode;
                                _detectionDiagnostics = autoSelection.DiagnosticSummary;
                                providerToUse = autoSelection.Provider;
                            }
                            else
                            {
                                LogManager.Log("Auto-Modus hat keinen verfügbaren Connector gefunden; erneuter Versuch in {0} Sekunden", reconnectInterval);
                                Status = ConnectorStatus.Disconnected;
                                await Task.Delay(TimeSpan.FromSeconds(reconnectInterval), cancellationToken);
                                continue;
                            }
                        }
                        else
                        {
                            _selectedMode = _configuredConnectionMode;
                            _detectionDiagnostics = string.Format("ConnectionMode explicitly configured: {0}", _configuredConnectionMode);
                            LogManager.Log("Verbindungszyklus mit explizitem ConnectionMode: {0}", _configuredConnectionMode);
                            providerToUse = CreateConnectionMethod(lineFilter);
                        }
                    }

                    Status = ConnectorStatus.Connecting;

                    _tapiMonitor?.Dispose();
                    _tapiMonitor = providerToUse;
                    _tapiMonitor.CallStateChanged += _callEventProcessor.OnTapiCallStateChanged;

                    // Per-line events for multi-line support
                    _tapiMonitor.LineDisconnected += (line) =>
                    {
                        LogManager.Log("TAPI Leitung getrennt: {0}", line.Extension);
                        // Fire status changed to update UI
                        StatusChanged?.Invoke(Status);
                    };

                    _tapiMonitor.Connected += () =>
                    {
                        AdoptExtensionFromProvider("connected line");
                        Status = ConnectorStatus.Connected;
                    };
                    _tapiMonitor.Disconnected += () =>
                    {
                        Status = ConnectorStatus.Disconnected;
                        LogManager.Log("Connector getrennt (alle Leitungen)");
                    };

                    // Provider from auto-detection may already be connected (TryConnect succeeded)
                    if (_tapiMonitor.IsMonitoring)
                    {
                        AdoptExtensionFromProvider("initial provider");
                        Status = ConnectorStatus.Connected;
                    }

                    // StartAsync blocks until cancelled or line closed
                    await _tapiMonitor.StartAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogManager.Log("Connector Verbindung fehlgeschlagen: {0}", ex.Message);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    LogManager.Log("Connector Neuverbindung in {0} Sekunden...", reconnectInterval);
                    await Task.Delay(TimeSpan.FromSeconds(reconnectInterval), cancellationToken);
                }
            }
        }

        // Call event processing is delegated to CallEventProcessor
        // DATEV command handling (Dial/Drop) is delegated to DatevCommandHandler

        /// <summary>
        /// Reload contacts from DATEV SDD
        /// </summary>
        public async Task ReloadContactsAsync()
        {
            await ReloadContactsAsync(null);
        }

        /// <summary>
        /// Reload contacts from DATEV SDD with progress callback
        /// </summary>
        /// <param name="progressText">Optional callback for progress text updates</param>
        public async Task ReloadContactsAsync(Action<string> progressText)
        {
            // First check if DATEV is available
            if (!DatevConnectionChecker.CheckAndLogDatevStatus(progressText))
            {
                LogManager.Warning("Kontakte können nicht neu geladen werden - DATEV nicht verfügbar");
                progressText?.Invoke("DATEV nicht verfügbar");
                return;
            }

            LogManager.Log("Kontakte werden von DATEV SDD geladen...");
            await DatevContactRepository.StartLoadAsync(null, progressText);
            LogManager.Log("Kontakte geladen: {0} Kontakte, {1} Telefonnummern-Schlüssel",
                DatevContactRepository.ContactCount, DatevContactRepository.PhoneNumberKeyCount);
        }

        /// <summary>
        /// Force TAPI reconnection by disposing current monitor
        /// </summary>
        public Task ReconnectTapiAsync()
        {
            return ReconnectTapiAsync(null);
        }

        /// <summary>
        /// Force TAPI reconnection with progress callback
        /// </summary>
        /// <param name="progressText">Optional callback for progress text updates</param>
        public Task ReconnectTapiAsync(Action<string> progressText)
        {
            LogManager.Log("Manuelle TAPI Neuverbindung angefordert");

            if (_tapiMonitor != null)
            {
                string ext = Extension ?? "—";
                progressText?.Invoke($"Trenne 3CX TAPI (Nst: {ext})...");
                _tapiMonitor.Dispose();
                _tapiMonitor = null;
                progressText?.Invoke("3CX TAPI getrennt");
            }

            // Immediately update status so UI reflects disconnected state
            Status = ConnectorStatus.Disconnected;

            progressText?.Invoke("Warte auf Neuverbindung...");

            // The ConnectWithRetryAsync loop will automatically reconnect
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reconnect a specific TAPI line by extension
        /// </summary>
        /// <param name="extension">Extension to reconnect</param>
        /// <param name="progressText">Optional progress callback</param>
        /// <returns>True if reconnected successfully</returns>
        public bool ReconnectTapiLine(string extension, Action<string> progressText = null)
        {
            if (_tapiMonitor == null)
            {
                LogManager.Log("Leitung {0} kann nicht neu verbunden werden - TAPI Monitor nicht aktiv", extension);
                return false;
            }

            LogManager.Log("Manuelle TAPI Leitungs-Neuverbindung für Nebenstelle {0} angefordert", extension);
            return _tapiMonitor.ReconnectLine(extension, progressText);
        }

        /// <summary>
        /// Reconnect all TAPI lines without full TAPI restart
        /// </summary>
        /// <param name="progressText">Optional progress callback</param>
        public void ReconnectAllTapiLines(Action<string> progressText = null)
        {
            if (_tapiMonitor == null)
            {
                LogManager.Log("Leitungen können nicht neu verbunden werden - TAPI Monitor nicht aktiv");
                return;
            }

            LogManager.Log("Manuelle TAPI Neuverbindung aller Leitungen angefordert");
            _tapiMonitor.ReconnectAllLines(progressText);

            // Trigger status update
            StatusChanged?.Invoke(Status);
        }

        /// <summary>
        /// Test a specific TAPI line to verify it's connected and responsive
        /// </summary>
        /// <param name="extension">Extension to test</param>
        /// <param name="progressText">Optional progress callback</param>
        /// <returns>True if line is connected and responsive</returns>
        public bool TestTapiLine(string extension, Action<string> progressText = null)
        {
            if (_tapiMonitor == null)
            {
                progressText?.Invoke("TAPI nicht aktiv");
                LogManager.Log("Leitung {0} kann nicht getestet werden - TAPI Monitor nicht aktiv", extension);
                return false;
            }

            return _tapiMonitor.TestLine(extension, progressText);
        }

        /// <summary>
        /// Send a journal from call history (re-journal)
        /// </summary>
        public void SendHistoryJournal(CallHistoryEntry entry, string note)
        {
            if (entry == null || string.IsNullOrWhiteSpace(note)) return;

            // Per DATEV spec: NewJournal must not be sent while a call is active
            if (_callTracker.Count > 0)
            {
                LogManager.Warning("CallHistory: Journal blockiert - {0} aktive(r) Anruf(e)", _callTracker.Count);
                return;
            }

            var callData = entry.ToCallData();
            callData.Note = note;
            _notificationManager.NewJournal(callData);
            _callHistory.MarkJournalSent(entry);
            LogManager.Log("CallHistory: Journal gesendet für {0} (CallID={1}, {2} Zeichen)",
                LogManager.Mask(entry.RemoteNumber), callData.CallID, note.Length);
        }

        /// <summary>
        /// Re-read settings from INI after a save.
        /// </summary>
        public void ApplySettings()
        {
            // Delegate popup/journal settings to CallEventProcessor
            _callEventProcessor.ApplySettings();

            // Update call history store live
            bool histIn = AppConfig.GetBool(ConfigKeys.CallHistoryInbound, true);
            bool histOut = AppConfig.GetBool(ConfigKeys.CallHistoryOutbound, false);
            int histMax = AppConfig.GetIntClamped(ConfigKeys.CallHistoryMaxEntries, 5, 1, 100);
            _callHistory.UpdateConfig(histMax, histIn, histOut);

            // DATEV Active contacts filter
            DatevContactRepository.FilterActiveContactsOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            // Telephony mode — update immediately for UI feedback
            var newMode = AppConfig.GetEnum(ConfigKeys.ConnectionMode, ConnectionMode.Auto);
            if (newMode != _configuredConnectionMode)
            {
                _configuredConnectionMode = newMode;
                _selectedMode = newMode;
                ModeChanged?.Invoke(_selectedMode);
            }

            LogManager.Log("Einstellungen live angewendet: History={0}/{1}/{2}, ActiveOnly={3}",
                histIn, histOut, histMax, DatevContactRepository.FilterActiveContactsOnly);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            GC.SuppressFinalize(this);

            // Unregister DatevAdapter from ROT
            try
            {
                AdapterManager.Unregister();
                LogManager.Log("DatevAdapter aus ROT abgemeldet");
            }
            catch (Exception ex)
            {
                LogManager.Log("DatevAdapter Abmeldung fehlgeschlagen: {0}", ex.Message);
            }

            // Cancel and dispose the cancellation token source
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }

            _tapiMonitor?.Dispose();
            _callTracker?.Dispose();
            DebugConfigWatcher.Stop();
        }
    }
}
