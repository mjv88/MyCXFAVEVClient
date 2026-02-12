using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Datev.Cti.Buddylib;
using DatevConnector.Core.Config;
using DatevConnector.Datev;
using DatevConnector.Datev.COMs;
using DatevConnector.Datev.Constants;
using DatevConnector.Datev.Enums;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using DatevConnector.Interop;
using DatevConnector.Tapi;
using DatevConnector.UI;
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
        private readonly object _statusLock = new object();
        private ConnectorStatus _status = ConnectorStatus.Disconnected;

        // Thread-safe mutable state (volatile ensures visibility across threads)
        private volatile ITelephonyProvider _tapiMonitor;
        private volatile CancellationTokenSource _cts;
        private volatile bool _disposed;
        private Task _pipeServerTask;

        // Settings (written only during constructor and ApplySettings on UI thread)
        private bool _enableJournaling;
        private bool _enableJournalPopup;
        private bool _enableJournalPopupOutbound;
        private bool _enableCallerPopup;
        private bool _enableCallerPopupOutbound;
        private CallerPopupMode _callerPopupMode;
        private int _minCallerIdLength;
        private int _contactReshowDelaySeconds;
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
        public IReadOnlyList<TapiLineInfo> TapiLines => _tapiMonitor?.Lines ?? (IReadOnlyList<TapiLineInfo>)new List<TapiLineInfo>();

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
                LogManager.Log("Bridge: Silent mode {0}", value ? "enabled" : "disabled");
            }
        }

        public int ContactCount => DatevCache.ContactCount;

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
        public List<DatevContactInfo> GetCachedContacts() => DatevCache.GetAllContacts();

        public ConnectorService(string extension)
        {
            _extension = extension;
            _callTracker = new CallTracker();

            // Use base GUID - Windows ROT is already per-session on terminal servers
            _notificationManager = new NotificationManager(CommonParameters.ClsIdDatev);
            
            // Create and register DatevAdapter for receiving Dial/Drop commands from DATEV
            _datevAdapter = new DatevAdapter(DatevEventHandler);
            
            // Read configuration
            _enableJournaling = AppConfig.GetBool(ConfigKeys.EnableJournaling, true);
            _minCallerIdLength = AppConfig.GetInt(ConfigKeys.MinCallerIdLength, 3);

            // UI Popup settings
            _enableJournalPopup = AppConfig.GetBool(ConfigKeys.EnableJournalPopup, true);
            _enableJournalPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableJournalPopupOutbound, false);
            _enableCallerPopup = AppConfig.GetBool(ConfigKeys.EnableCallerPopup, true);
            _enableCallerPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableCallerPopupOutbound, false);
            _callerPopupMode = AppConfig.GetEnum(ConfigKeys.CallerPopupMode, CallerPopupMode.Form);
            _contactReshowDelaySeconds = AppConfig.GetInt(ConfigKeys.ContactReshowDelaySeconds, 3);

            // Call history
            bool histInbound = AppConfig.GetBool(ConfigKeys.CallHistoryInbound, true);
            bool histOutbound = AppConfig.GetBool(ConfigKeys.CallHistoryOutbound, false);
            int histMax = AppConfig.GetInt(ConfigKeys.CallHistoryMaxEntries, 5);
            _callHistory = new CallHistoryStore(histMax, histInbound, histOutbound);

            // Contact filter (must be set before LoadContactsAsync)
            DatevContactManager.FilterActiveContactsOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            LogManager.Debug("Configuration: EnableJournaling={0}, EnableJournalPopup={1}, MinCallerIdLength={2}, EnableCallerPopup={3}, ActiveContactsOnly={4}",
                _enableJournaling, _enableJournalPopup, _minCallerIdLength, _enableCallerPopup, DatevContactManager.FilterActiveContactsOnly);
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
            LogManager.Log("3CX Telefonie Modus: {0} (configured)", _configuredTelephonyMode);

            // ── Step 2: Provider selection / auto-detection ──────────────
            LogManager.Log("3CX Telefonie Modus Initialisierung...");

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
                LogManager.Log("DATEV Kontaktsyncronisation");
                LogManager.Log("========================================");
                await DatevCache.StartLoadAsync();
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

            LogManager.Log("Beginn der DATEV-Erkennungsdienst...");

            Task.Run(async () =>
            {
                int interval = ShortInterval;

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(interval, _cts.Token);
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
                case TelephonyMode.Webclient:
                    LogManager.Log("Webclient-Modus - verwende WebclientTelephonyProvider");
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
                    _tapiMonitor.CallStateChanged += OnTapiCallStateChanged;

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

        #region DATEV Event Handlers (Click-to-Dial)

        /// <summary>
        /// Handle events from DATEV (Dial, Drop)
        /// </summary>
        private void DatevEventHandler(IDatevCtiData ctiData, DatevEventType eventType)
        {
            // Fire-and-forget with proper exception handling
            _ = HandleDatevEventAsync(ctiData, eventType);
        }

        /// <summary>
        /// Async handler for DATEV events with proper exception handling
        /// </summary>
        private async Task HandleDatevEventAsync(IDatevCtiData ctiData, DatevEventType eventType)
        {
            try
            {
                switch (eventType)
                {
                    case DatevEventType.Dial:
                        await HandleDialCommandAsync(ctiData);
                        break;

                    case DatevEventType.Drop:
                        await HandleDropCommandAsync(ctiData);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Error handling DATEV event {0}: {1}", eventType, ex);
            }
        }

        /// <summary>
        /// Handle Dial command from DATEV - initiate outgoing call via TAPI
        /// </summary>
        private Task HandleDialCommandAsync(IDatevCtiData ctiData)
        {
            string destination = ctiData.CalledNumber;

            if (string.IsNullOrWhiteSpace(destination))
            {
                LogManager.Log("DATEV Dial: No destination number provided");
                return Task.CompletedTask;
            }

            LogManager.Log("DATEV Dial: Initiating call to {0} (SyncID={1}, Contact={2})",
                LogManager.Mask(destination), ctiData.SyncID ?? "null", ctiData.Adressatenname ?? "null");

            if (_tapiMonitor == null || !_tapiMonitor.IsMonitoring)
            {
                LogManager.Log("DATEV Dial: Not connected (provider={0}, monitoring={1})",
                    _tapiMonitor?.GetType().Name ?? "null",
                    _tapiMonitor?.IsMonitoring.ToString() ?? "N/A");
                return Task.CompletedTask;
            }

            // Preserve DATEV-provided data (SyncID, contact, datasource) for when TAPI event fires
            ctiData.CallState = ENUM_CALLSTATE.eCSOffered;
            ctiData.Direction = ENUM_DIRECTION.eDirOutgoing;
            ctiData.Begin = DateTime.Now;
            ctiData.End = DateTime.Now;

            var preservedData = CallDataManager.CreateFromDatev(ctiData);

            // Store as pending call so HandleRingback can find it by number
            string tempId = _callTracker.GenerateTempCallId();
            var pendingRecord = _callTracker.AddPendingCall(tempId, isIncoming: false);
            pendingRecord.RemoteNumber = destination;
            pendingRecord.CallData = preservedData;
            _callTracker.UpdatePendingPhoneIndex(tempId, destination);

            LogManager.Log("DATEV Dial: Sending to {0} (connected={1})",
                _tapiMonitor.GetType().Name, _tapiMonitor.IsMonitoring);
            int result = _tapiMonitor.MakeCall(destination);
            if (result <= 0)
            {
                LogManager.Log("DATEV Dial: MakeCall failed (result={0}, provider={1})",
                    result, _tapiMonitor.GetType().Name);
                _callTracker.RemovePendingCall(tempId);
            }
            else
            {
                LogManager.Log("DATEV Dial: MakeCall sent (result={0})", result);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle Drop command from DATEV - terminate call via TAPI
        /// </summary>
        private Task HandleDropCommandAsync(IDatevCtiData ctiData)
        {
            string datevCallId = ctiData.CallID;

            if (string.IsNullOrWhiteSpace(datevCallId))
            {
                LogManager.Log("DATEV Drop: No CallID provided");
                return Task.CompletedTask;
            }

            LogManager.Log("DATEV Drop: Terminating call {0}", datevCallId);

            var record = _callTracker.FindCallByDatevCallId(datevCallId);
            if (record == null)
            {
                LogManager.Log("DATEV Drop: Call {0} not found", datevCallId);
                return Task.CompletedTask;
            }

            if (_tapiMonitor == null || !_tapiMonitor.IsMonitoring)
            {
                LogManager.Log("DATEV Drop: TAPI not connected, cannot drop call");
                return Task.CompletedTask;
            }

            // Find the TAPI call handle by call ID
            int tapiCallId;
            if (int.TryParse(record.TapiCallId, out tapiCallId))
            {
                var callEvent = _tapiMonitor.FindCallById(tapiCallId);
                if (callEvent != null)
                {
                    _tapiMonitor.DropCall(callEvent.CallHandle);
                    LogManager.Log("DATEV Drop: lineDrop called for {0} (tapiId={1})", datevCallId, tapiCallId);
                }
                else
                {
                    LogManager.Log("DATEV Drop: Call handle not found for tapiId={0}", tapiCallId);
                }
            }
            else
            {
                LogManager.Log("DATEV Drop: Invalid TapiCallId: {0}", record.TapiCallId);
            }

            return Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// Look up a DATEV contact by phone number, apply routing, fill CallData, and record usage.
        /// Returns the matched contact (or null).
        /// </summary>
        private DatevContactInfo LookupAndFillContact(CallRecord record, CallData callData, string remoteNumber)
        {
            DatevContactInfo contact = null;
            if (!string.IsNullOrEmpty(remoteNumber) && remoteNumber.Length >= _minCallerIdLength)
            {
                List<DatevContactInfo> contacts = DatevCache.GetContactByNumber(remoteNumber);
                if (contacts.Count > 1)
                    contacts = ContactRoutingCache.ApplyRouting(remoteNumber, contacts);
                if (contacts.Count > 0)
                    contact = contacts[0];
            }

            CallDataManager.Fill(callData, remoteNumber, contact);
            record.CallData = callData;

            if (contact?.DatevContact?.Id != null)
                ContactRoutingCache.RecordUsage(remoteNumber, contact.DatevContact.Id);

            return contact;
        }

        #region TAPI Event Handlers

        /// <summary>
        /// Handle call state change events from the telephony provider
        /// </summary>
        private void OnTapiCallStateChanged(TapiCallEvent callEvent)
        {
            try
            {
                string callId = callEvent.CallId.ToString();
                int state = callEvent.CallState;

                switch (state)
                {
                    case TapiInterop.LINECALLSTATE_OFFERING:
                        HandleOffering(callId, callEvent);
                        break;

                    case TapiInterop.LINECALLSTATE_RINGBACK:
                        HandleRingback(callId, callEvent);
                        break;

                    case TapiInterop.LINECALLSTATE_CONNECTED:
                        HandleConnected(callId, callEvent);
                        break;

                    case TapiInterop.LINECALLSTATE_DISCONNECTED:
                        HandleDisconnected(callId, callEvent);
                        break;

                    case TapiInterop.LINECALLSTATE_IDLE:
                        // Call handle deallocated - no action needed
                        break;

                    case TapiInterop.LINECALLSTATE_DIALING:
                    case TapiInterop.LINECALLSTATE_PROCEEDING:
                        // Intermediate outgoing states - just log
                        LogManager.Log("TAPI: Call {0} state={1}", callId, callEvent.CallStateString);
                        break;

                    case TapiInterop.LINECALLSTATE_BUSY:
                        LogManager.Log("TAPI: Call {0} BUSY", callId);
                        HandleDisconnected(callId, callEvent);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogManager.Log("Error processing TAPI call event: {0}", ex);
            }
        }

        /// <summary>
        /// Handle incoming call offering (ringing)
        /// </summary>
        private void HandleOffering(string callId, TapiCallEvent callEvent)
        {
            string callerNumber = callEvent.CallerNumber;

            var existingRecord = _callTracker.GetCall(callId);
            if (existingRecord != null)
            {
                CallStateMachine.TryTransition(existingRecord, TapiCallState.Ringing);
                return;
            }

            var record = _callTracker.AddCall(callId, isIncoming: true);
            CallStateMachine.TryTransition(record, TapiCallState.Ringing);

            record.RemoteNumber = callerNumber;
            record.RemoteName = callEvent.CallerName;
            record.LocalNumber = callEvent.CalledNumber ?? _extension;

            var callData = new CallData
            {
                CallID = CallIdGenerator.Next(),
                CallState = ENUM_CALLSTATE.eCSOffered,
                Direction = ENUM_DIRECTION.eDirIncoming,
                Begin = record.StartTime,
                End = record.StartTime
            };

            var contact = LookupAndFillContact(record, callData, callerNumber);

            if (_enableCallerPopup && !_isMuted)
            {
                CallerPopupForm.ShowPopup(
                    callerNumber,
                    callEvent.CallerName,
                    contact,
                    isIncoming: true,
                    _callerPopupMode,
                    _extension);
            }

            LogManager.Log("Bridge: Incoming call {0} from {1} (contact={2})",
                callId, LogManager.Mask(callerNumber), LogManager.MaskName(contact?.DatevContact?.Name) ?? "unknown");
            _notificationManager.NewCall(callData);
        }

        /// <summary>
        /// Handle outgoing call ringback
        /// </summary>
        private void HandleRingback(string callId, TapiCallEvent callEvent)
        {
            string calledNumber = callEvent.CalledNumber;

            var existingRecord = _callTracker.GetCall(callId);
            if (existingRecord != null)
            {
                CallStateMachine.TryTransition(existingRecord, TapiCallState.Ringback);
                return;
            }

            // Check if this is a DATEV-initiated call (pending call matching by number)
            var pendingRecord = _callTracker.FindPendingCallByNumber(calledNumber);
            CallRecord record;

            if (pendingRecord != null && pendingRecord.CallData != null)
            {
                // Promote the pending call - use preserved DATEV data
                record = _callTracker.PromotePendingCall(pendingRecord.TapiCallId, callId);
                if (record == null)
                {
                    record = _callTracker.AddCall(callId, isIncoming: false);
                }
                CallStateMachine.TryTransition(record, TapiCallState.Ringback);

                record.RemoteNumber = calledNumber;
                record.RemoteName = callEvent.CalledName;
                record.LocalNumber = callEvent.CallerNumber ?? _extension;

                // Use preserved DATEV CallData (CallID, SyncID, contact, datasource already set)
                // Do NOT override CallID - it was assigned in Dial() and DATEV expects consistency
                record.CallData.Begin = record.StartTime;
                record.CallData.End = record.StartTime;

                LogManager.Log("Bridge: DATEV-initiated outgoing call {0} to {1} (SyncID={2}, Contact={3})",
                    callId, LogManager.Mask(calledNumber), record.CallData.SyncID, record.CallData.Adressatenname);
                _notificationManager.NewCall(record.CallData);
                return;
            }

            // Normal outgoing call (not DATEV-initiated)
            record = _callTracker.AddCall(callId, isIncoming: false);
            CallStateMachine.TryTransition(record, TapiCallState.Ringback);

            record.RemoteNumber = calledNumber;
            record.RemoteName = callEvent.CalledName;
            record.LocalNumber = callEvent.CallerNumber ?? _extension;

            var callData = new CallData
            {
                CallID = CallIdGenerator.Next(),
                CallState = ENUM_CALLSTATE.eCSOffered,
                Direction = ENUM_DIRECTION.eDirOutgoing,
                Begin = record.StartTime,
                End = record.StartTime
            };

            var contact = LookupAndFillContact(record, callData, calledNumber);

            if (_enableCallerPopupOutbound && !_isMuted)
            {
                CallerPopupForm.ShowPopup(
                    calledNumber,
                    callEvent.CalledName,
                    contact,
                    isIncoming: false,
                    _callerPopupMode,
                    _extension);
            }

            LogManager.Log("Bridge: Outgoing call {0} to {1} (contact={2})",
                callId, LogManager.Mask(calledNumber), contact?.DatevContact?.Name ?? "unknown");
            _notificationManager.NewCall(callData);
        }

        /// <summary>
        /// Handle call connected
        /// </summary>
        private void HandleConnected(string callId, TapiCallEvent callEvent)
        {
            var record = _callTracker.GetCall(callId);

            if (record == null)
            {
                LogManager.Log("Bridge: Creating record for previously unknown call {0}", callId);

                bool isIncoming = callEvent.IsIncoming;
                record = _callTracker.AddCall(callId, isIncoming);

                string remoteNumber = isIncoming ? callEvent.CallerNumber : callEvent.CalledNumber;
                record.RemoteNumber = remoteNumber;
                record.RemoteName = isIncoming ? callEvent.CallerName : callEvent.CalledName;
                record.LocalNumber = isIncoming ? (callEvent.CalledNumber ?? _extension) : (callEvent.CallerNumber ?? _extension);

                var callData = new CallData
                {
                    CallID = CallIdGenerator.Next(),
                    CallState = ENUM_CALLSTATE.eCSOffered,
                    Direction = isIncoming ? ENUM_DIRECTION.eDirIncoming : ENUM_DIRECTION.eDirOutgoing,
                    Begin = record.StartTime,
                    End = record.StartTime
                };

                LookupAndFillContact(record, callData, remoteNumber);

                _notificationManager.NewCall(callData);
            }

            if (!CallStateMachine.TryTransition(record, TapiCallState.Connected))
                return;

            record.ConnectedTime = DateTime.Now;
            record.State = ENUM_CALLSTATE.eCSConnected;

            // Close caller popup on connect
            if (_enableCallerPopup)
            {
                CallerPopupForm.CloseCurrentPopup();
            }

            if (record.CallData != null)
            {
                record.CallData.CallState = ENUM_CALLSTATE.eCSConnected;
                record.CallData.Begin = record.ConnectedTime.Value;
                record.CallData.End = record.ConnectedTime.Value;

                LogManager.Log("Bridge: Call {0}", callId);
                _notificationManager.CallStateChanged(record.CallData);

                // Schedule contact reshow if enabled — skip for DATEV-initiated calls
                // (DATEV already specified the contact in the dial command)
                int reshowDelay = DebugConfigWatcher.GetInt(
                    DebugConfigWatcher.Instance?.ContactReshowDelaySeconds,
                    "ContactReshowDelaySeconds", _contactReshowDelaySeconds);
                if (reshowDelay > 0 && string.IsNullOrEmpty(record.CallData.SyncID))
                {
                    ScheduleContactReshow(record, reshowDelay);
                }
            }
        }

        /// <summary>
        /// Schedule a delayed contact reshow after call connects.
        /// Allows user to change contact assignment mid-call.
        /// Fires CallAdressatChanged if user picks a different contact.
        /// </summary>
        private void ScheduleContactReshow(CallRecord record, int reshowDelaySeconds)
        {
            string remoteNumber = record.RemoteNumber;
            if (string.IsNullOrEmpty(remoteNumber) || remoteNumber.Length < _minCallerIdLength)
                return;

            var contacts = DatevCache.GetContactByNumber(remoteNumber);
            if (contacts.Count <= 1)
                return;

            // Apply last-agent routing so previously-selected contact appears first
            contacts = ContactRoutingCache.ApplyRouting(remoteNumber, contacts);

            string callId = record.TapiCallId;
            int delayMs = reshowDelaySeconds * 1000;

            LogManager.Debug("Bridge: Scheduling contact reshow in {0}s for call {1} ({2} contacts)",
                reshowDelaySeconds, callId, contacts.Count);

            Task.Delay(delayMs).ContinueWith(t =>
            {
                try
                {
                    // Verify call is still connected
                    var currentRecord = _callTracker.GetCall(callId);
                    if (currentRecord == null || currentRecord.TapiState != TapiCallState.Connected)
                    {
                        LogManager.Debug("Contact reshow: Call {0} no longer connected, skipping", callId);
                        return;
                    }

                    // Re-show contact selection (stays open until user picks or cancels)
                    var selectedContact = ContactSelectionForm.SelectContact(
                        remoteNumber, contacts, record.IsIncoming);

                    if (selectedContact != null && currentRecord.CallData != null)
                    {
                        string existingSyncId = currentRecord.CallData.SyncID;
                        string previousId = currentRecord.CallData.AdressatenId;

                        CallDataManager.Fill(currentRecord.CallData, remoteNumber, selectedContact);

                        // Preserve existing SyncID (DATEV assigns during the call)
                        if (!string.IsNullOrEmpty(existingSyncId))
                            currentRecord.CallData.SyncID = existingSyncId;

                        // Only fire notification if contact actually changed
                        if (currentRecord.CallData.AdressatenId != previousId)
                        {
                            LogManager.Log("Contact reshow: Contact changed for call {0} - new={1} (SyncID={2})",
                                callId, currentRecord.CallData.Adressatenname, currentRecord.CallData.SyncID);
                            _notificationManager.CallAdressatChanged(currentRecord.CallData);

                            // Update routing cache with new preference
                            ContactRoutingCache.RecordUsage(remoteNumber, currentRecord.CallData.AdressatenId);
                        }
                        else
                        {
                            LogManager.Debug("Contact reshow: Same contact selected for call {0}", callId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Log("Contact reshow error for call {0}: {1}", callId, ex.Message);
                }
            });
        }

        /// <summary>
        /// Handle call disconnected
        /// </summary>
        private void HandleDisconnected(string callId, TapiCallEvent callEvent)
        {
            var record = _callTracker.GetCall(callId);

            if (record == null)
            {
                LogManager.Log("Bridge: Unknown call {0} (ignoring)", callId);
                return;
            }

            if (!CallStateMachine.TryTransition(record, TapiCallState.Disconnected))
                return;

            _callTracker.RemoveCall(callId);

            // Close any open popups/dialogs for this call
            CallerPopupForm.CloseCurrentPopup();
            ContactSelectionForm.CloseCurrentDialog();

            record.EndTime = DateTime.Now;
            record.State = record.WasConnected
                ? ENUM_CALLSTATE.eCSFinished
                : ENUM_CALLSTATE.eCSAbsence;

            var duration = record.GetDuration();
            string durationStr = duration.HasValue
                ? string.Format("{0:D2}:{1:D2}:{2:D2}",
                    (int)duration.Value.TotalHours,
                    duration.Value.Minutes,
                    duration.Value.Seconds)
                : "N/A";

            if (record.CallData != null)
            {
                record.CallData.CallState = record.State;
                record.CallData.End = record.EndTime.Value;

                LogManager.Log("Bridge: Call {0} (wasConnected={1}, duration={2})",
                    callId, record.WasConnected, durationStr);

                _notificationManager.CallStateChanged(record.CallData);

                // Add to call history for later re-journaling (only DATEV-matched contacts)
                if (!string.IsNullOrEmpty(record.CallData.AdressatenId))
                    _callHistory.AddEntry(CallHistoryEntry.FromCallRecord(record));

                // Only show journal popup for answered calls with a matched DATEV contact
                // Skip outbound calls unless EnableJournalPopupOutbound is set
                if (_enableJournaling && _enableJournalPopup && !_isMuted && record.WasConnected
                    && !string.IsNullOrEmpty(record.CallData.AdressatenId)
                    && (record.IsIncoming || _enableJournalPopupOutbound))
                {
                    // Ensure DataSource is set (defensive - should already be set by Fill)
                    if (string.IsNullOrEmpty(record.CallData.DataSource))
                    {
                        LogManager.Warning("Journal: DataSource empty, defaulting to 3CX");
                        record.CallData.DataSource = DatevDataSource.ThirdParty;
                    }

                    // Show journal popup - only send if user writes a note and clicks send
                    var journalCallData = record.CallData;
                    JournalForm.ShowJournal(
                        journalCallData.Adressatenname,
                        journalCallData.CalledNumber,
                        journalCallData.DataSource,
                        journalCallData.Begin,
                        journalCallData.End,
                        note =>
                        {
                            if (!string.IsNullOrWhiteSpace(note))
                            {
                                // Per DATEV spec: NewJournal must not be sent while a call is active
                                if (_callTracker.Count > 0)
                                {
                                    LogManager.Warning("Journal: Blocked - {0} active call(s)", _callTracker.Count);
                                    return;
                                }

                                // Per DATEV IDL: NewJournal needs a new CallID; SyncID must not be set
                                journalCallData.CallID = CallIdGenerator.Next();
                                journalCallData.SyncID = string.Empty;
                                journalCallData.Note = note;
                                _notificationManager.NewJournal(journalCallData);
                                LogManager.Log("Journal sent for call {0} (NewCallID={1}, {2} chars)",
                                    callId, journalCallData.CallID, note.Length);
                            }
                            else
                            {
                                LogManager.Log("Journal skipped for call {0} - empty note", callId);
                            }
                        });
                }
            }
        }

        #endregion

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
            await DatevCache.StartLoadAsync(null, progressText);
            LogManager.Log("Contacts reloaded: {0} contacts, {1} phone number keys",
                DatevCache.ContactCount, DatevCache.PhoneNumberKeyCount);
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
            _enableJournaling = AppConfig.GetBool(ConfigKeys.EnableJournaling, true);
            _enableJournalPopup = AppConfig.GetBool(ConfigKeys.EnableJournalPopup, true);
            _enableJournalPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableJournalPopupOutbound, false);
            _enableCallerPopup = AppConfig.GetBool(ConfigKeys.EnableCallerPopup, true);
            _enableCallerPopupOutbound = AppConfig.GetBool(ConfigKeys.EnableCallerPopupOutbound, false);
            _callerPopupMode = AppConfig.GetEnum(ConfigKeys.CallerPopupMode, CallerPopupMode.Form);
            _contactReshowDelaySeconds = AppConfig.GetInt(ConfigKeys.ContactReshowDelaySeconds, 3);

            // Update call history store live
            bool histIn = AppConfig.GetBool(ConfigKeys.CallHistoryInbound, true);
            bool histOut = AppConfig.GetBool(ConfigKeys.CallHistoryOutbound, false);
            int histMax = AppConfig.GetInt(ConfigKeys.CallHistoryMaxEntries, 5);
            _callHistory.UpdateConfig(histMax, histIn, histOut);

            // DATEV Active contacts filter
            DatevContactManager.FilterActiveContactsOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            LogManager.Log("Settings applied live: Journaling={0}, JournalPopup={1}, CallerPopup={2}, History={3}/{4}/{5}, ActiveOnly={6}",
                _enableJournaling, _enableJournalPopup, _enableCallerPopup, histIn, histOut, histMax, DatevContactManager.FilterActiveContactsOnly);
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
