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
// UI references removed — call event handling delegated to CallEventProcessor

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
        private volatile ITelephonyProvider _tapiMonitor;
        private volatile CancellationTokenSource _cts;
        private volatile bool _disposed;
        private Task _pipeServerTask;

        // Settings
        private int _minCallerIdLength;
        private bool _isMuted;

        // Call history
        private readonly CallHistoryStore _callHistory;

        // Telephony mode selection
        private volatile TelephonyMode _selectedMode = TelephonyMode.Auto;
        private TelephonyMode _configuredTelephonyMode = TelephonyMode.Auto;
        private string _detectionDiagnostics;

        /// <summary>
        /// Event fired when connection status changes
        /// </summary>
        public event Action<ConnectorStatus> StatusChanged;

        /// <summary>
        /// Event fired when the selected telephony mode changes (for immediate UI updates).
        /// </summary>
        public event Action<TelephonyMode> ModeChanged;

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
                LogManager.Log("Connector: Silent mode {0}", value ? "enabled" : "disabled");
            }
        }

        public int ContactCount => DatevContactRepository.ContactCount;

        /// <summary>
        /// Call history store for re-journaling
        /// </summary>
        public CallHistoryStore CallHistory => _callHistory;

        /// <summary>
        /// The selected telephony mode (after auto-detection or explicit config).
        /// </summary>
        public TelephonyMode SelectedTelephonyMode => _selectedMode;

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

            // ── Step 1: Log startup info ─────────────────────────────────
            SessionManager.LogSessionInfo();

            _configuredTelephonyMode = AppConfig.GetEnum(ConfigKeys.TelephonyMode, TelephonyMode.Auto);

            // ── Step 2: Provider selection / auto-detection ──────────────
            LogManager.Log("3CX Telefonie Modus Initialisierung...");
            LogManager.Log("3CX Telefonie Modus: {0} (configured)",
                _configuredTelephonyMode == TelephonyMode.Auto ? "Auto-Detection" : _configuredTelephonyMode.ToString());

            var selectionResult = await TelephonyProviderSelector.SelectProviderAsync(
                _extension, _cts.Token);

            _selectedMode = selectionResult.SelectedMode;
            _detectionDiagnostics = selectionResult.DiagnosticSummary;

            LogManager.Log("Telefonie Modus: {0}", _selectedMode);
            LogManager.Debug("Telefonie Modus Grund: {0}", selectionResult.Reason);

            // For Pipe mode on Terminal Server, start the pipe FIRST before DATEV init
            // so the 3CX Softphone can find it while we load contacts
            bool isPipeMode = _selectedMode == TelephonyMode.Pipe;
            if (isPipeMode && selectionResult.Success)
            {
                LogManager.Log("========================================");
                LogManager.Log("3CX Pipe Server (early start)");
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
                LogManager.Log("Failed to register DatevAdapter: {0}", ex.Message);
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
                LogManager.Warning("No telephony provider detected!");
                LogManager.Warning("========================================");
                LogManager.Warning(selectionResult.DiagnosticSummary ?? "No diagnostics available");
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
                LogManager.Log("3CX Telefonie Modus ({0})", _selectedMode);
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
                LogManager.Log("DATEV Kontaktsynchronisation");
                LogManager.Log("========================================");

                int timeoutSec = AppConfig.GetIntClamped(ConfigKeys.ContactLoadTimeoutSeconds, 120, 30, 600);
                var loadTask = DatevContactRepository.StartLoadAsync();
                var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));

                if (completed != loadTask)
                {
                    LogManager.Warning("Contact load timed out after {0}s - continuing without contacts", timeoutSec);
                    return;
                }

                await loadTask; // propagate exceptions
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Failed to load contacts from DATEV SDD");
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
                        LogManager.Log("DATEV auto-detected as available");
                        DatevConnectionChecker.CheckAndLogDatevStatus();
                        await LoadContactsAsync();
                        DatevBecameAvailable?.Invoke(this, EventArgs.Empty);
                        interval = LongInterval;
                    }
                    else if (!available && DatevAvailable)
                    {
                        // DATEV became unavailable
                        DatevAvailable = false;
                        LogManager.Warning("DATEV no longer available");
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
        /// Create the appropriate telephony provider based on the selected mode.
        /// Supports TAPI 2.x, Named Pipe, and Webclient (browser extension).
        /// </summary>
        private ITelephonyProvider CreateTelephonyProvider(string lineFilter)
        {
            switch (_selectedMode)
            {
                case TelephonyMode.WebClient:
                    LogManager.Log("WebClient-Modus - verwende WebclientTelephonyProvider");
                    return new WebclientTelephonyProvider(_extension);

                case TelephonyMode.Pipe:
                    LogManager.Log("Terminal Server / Pipe-Modus - verwende Named Pipe Provider");
                    return new PipeTelephonyProvider(_extension);

                case TelephonyMode.Tapi:
                default:
                    LogManager.Log("Desktop / TAPI-Modus - verwende TapiLineMonitor");
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
                LogManager.Debug("Extension auto-detected from {0}: {1}", source, firstLine.Extension);
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
        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken, ITelephonyProvider initialProvider = null)
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
                    ITelephonyProvider providerToUse = null;

                    if (initialProvider != null)
                    {
                        // First iteration: reuse provider from initial auto-detection
                        providerToUse = initialProvider;
                        initialProvider = null; // consumed
                    }
                    else
                    {
                        var configuredModeNow = AppConfig.GetEnum(ConfigKeys.TelephonyMode, TelephonyMode.Auto);
                        if (configuredModeNow != _configuredTelephonyMode)
                        {
                            LogManager.Log("TelephonyMode config changed during runtime: {0} -> {1}",
                                _configuredTelephonyMode, configuredModeNow);
                            _configuredTelephonyMode = configuredModeNow;
                        }

                        if (_configuredTelephonyMode == TelephonyMode.Auto)
                        {
                            var autoSelection = await TelephonyProviderSelector.SelectProviderAsync(_extension, cancellationToken);
                            if (autoSelection.Success)
                            {
                                if (_selectedMode != autoSelection.SelectedMode)
                                {
                                    LogManager.Log("Auto mode selected telephony provider for reconnect cycle: {0} -> {1} (reason: {2})",
                                        _selectedMode, autoSelection.SelectedMode, autoSelection.Reason);
                                }
                                _selectedMode = autoSelection.SelectedMode;
                                _detectionDiagnostics = autoSelection.DiagnosticSummary;
                                providerToUse = autoSelection.Provider;
                            }
                            else
                            {
                                LogManager.Log("Auto mode selection found no available provider; retrying in {0} seconds", reconnectInterval);
                                Status = ConnectorStatus.Disconnected;
                                await Task.Delay(TimeSpan.FromSeconds(reconnectInterval), cancellationToken);
                                continue;
                            }
                        }
                        else
                        {
                            _selectedMode = _configuredTelephonyMode;
                            _detectionDiagnostics = string.Format("TelephonyMode explicitly configured: {0}", _configuredTelephonyMode);
                            LogManager.Log("Reconnect cycle using explicit TelephonyMode: {0}", _configuredTelephonyMode);
                            providerToUse = CreateTelephonyProvider(lineFilter);
                        }
                    }

                    Status = ConnectorStatus.Connecting;

                    _tapiMonitor?.Dispose();
                    _tapiMonitor = providerToUse;
                    _tapiMonitor.CallStateChanged += _callEventProcessor.OnTapiCallStateChanged;

                    // Per-line events for multi-line support
                    _tapiMonitor.LineDisconnected += (line) =>
                    {
                        LogManager.Log("TAPI line disconnected: {0}", line.Extension);
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
                        LogManager.Log("Telephony provider disconnected (all lines)");
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
                    LogManager.Log("Telephony provider connection failed: {0}", ex.Message);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    LogManager.Log("Reconnecting telephony provider in {0} seconds...", reconnectInterval);
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
                LogManager.Warning("Cannot reload contacts - DATEV not available");
                progressText?.Invoke("DATEV nicht verfügbar");
                return;
            }

            LogManager.Log("Reloading contacts from DATEV SDD...");
            await DatevContactRepository.StartLoadAsync(null, progressText);
            LogManager.Log("Contacts reloaded: {0} contacts, {1} phone number keys",
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
            LogManager.Log("Manual TAPI reconnect requested");

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
                LogManager.Log("Cannot reconnect line {0} - TAPI monitor not running", extension);
                return false;
            }

            LogManager.Log("Manual TAPI line reconnect requested for extension {0}", extension);
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
                LogManager.Log("Cannot reconnect lines - TAPI monitor not running");
                return;
            }

            LogManager.Log("Manual TAPI reconnect all lines requested");
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
                LogManager.Log("Cannot test line {0} - TAPI monitor not running", extension);
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
                LogManager.Warning("CallHistory: Journal blocked - {0} active call(s)", _callTracker.Count);
                return;
            }

            var callData = entry.ToCallData();
            callData.Note = note;
            _notificationManager.NewJournal(callData);
            _callHistory.MarkJournalSent(entry);
            LogManager.Log("CallHistory: Journal sent for {0} (CallID={1}, {2} chars)",
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
            var newMode = AppConfig.GetEnum(ConfigKeys.TelephonyMode, TelephonyMode.Auto);
            if (newMode != _configuredTelephonyMode)
            {
                _configuredTelephonyMode = newMode;
                _selectedMode = newMode;
                ModeChanged?.Invoke(_selectedMode);
            }

            LogManager.Log("Settings applied live: History={0}/{1}/{2}, ActiveOnly={3}",
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
                LogManager.Log("DatevAdapter unregistered from ROT");
            }
            catch (Exception ex)
            {
                LogManager.Log("Failed to unregister DatevAdapter: {0}", ex.Message);
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
