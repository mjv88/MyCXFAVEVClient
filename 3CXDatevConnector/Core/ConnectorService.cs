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
        private volatile string _extension;
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
        private volatile Task _connectRetryTask;
        private Task _datevAutoDetectTask;

        // Settings
        private int _minCallerIdLength;
        private bool _isMuted;

        // Call history
        private readonly CallHistoryStore _callHistory;

        // Connection mode selection
        private volatile ConnectionMode _selectedMode = ConnectionMode.Auto;
        private ConnectionMode _configuredConnectionMode = ConnectionMode.Auto;
        private string _detectionDiagnostics;

        public event Action<ConnectorStatus> StatusChanged;

        public event Action<ConnectionMode> ModeChanged;

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

        public string Extension => _extension;

        public IReadOnlyList<TapiLineInfo> TapiLines => _tapiMonitor?.Lines ?? EmptyLineList;

        public int ConnectedLineCount => _tapiMonitor?.ConnectedLineCount ?? 0;

        private volatile bool _datevAvailable;
        public bool DatevAvailable
        {
            get => _datevAvailable;
            private set => _datevAvailable = value;
        }

        public void SetDatevAvailable(bool available)
        {
            DatevAvailable = available;
        }

        public bool TapiConnected => Status == ConnectorStatus.Connected;

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

        public CallHistoryStore CallHistory => _callHistory;

        public ConnectionMode SelectedConnectionMode => _selectedMode;

        public string DetectionDiagnostics => _detectionDiagnostics;

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
            int histRetention = AppConfig.GetIntClamped(ConfigKeys.CallHistoryRetentionDays, 7, 1, 90);
            _callHistory = new CallHistoryStore(histMax, histInbound, histOutbound, histRetention);

            // Call event processing (handles all TAPI state transitions + DATEV notifications + UI popups)
            _callEventProcessor = new CallEventProcessor(
                _callTracker, _notificationManager, _callHistory, _extension, _minCallerIdLength);

            // Contact filter (must be set before LoadContactsAsync)
            DatevContactRepository.FilterActiveContactsOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            LogManager.Debug("Configuration: MinCallerIdLength={0}, ActiveContactsOnly={1}",
                _minCallerIdLength, DatevContactRepository.FilterActiveContactsOnly);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // ── Step 1: Mode header first, then environment info ─────────
            _configuredConnectionMode = AppConfig.GetConnectionMode();

            LogManager.Log("3CX Telefonie Modus Initialisierung...");
            LogManager.Log("3CX Telefonie Modus: {0} (konfiguriert)",
                _configuredConnectionMode == ConnectionMode.Auto ? "Auto-Detection" : _configuredConnectionMode.ToString());

            // ── Step 2: Provider selection / auto-detection ──────────────

            var selectionResult = await ConnectionMethodSelector.SelectProviderAsync(
                _extension, _cts.Token);

            _selectedMode = selectionResult.SelectedMode;
            _detectionDiagnostics = selectionResult.DiagnosticSummary;

            LogManager.Log("Telefonie Modus: {0}", _selectedMode);
            LogManager.Debug("Telefonie Modus Grund: {0}", selectionResult.Reason);

            // For Terminal Server mode, start the pipe FIRST before DATEV init
            // so the 3CX Softphone can find it while we load contacts
            bool isPipeMode = _selectedMode == ConnectionMode.TerminalServer;
            if (isPipeMode && selectionResult.Success)
            {
                LogManager.Log("========================================");
                LogManager.Log("  3CX Terminal Server (early start)");
                LogManager.Log("========================================");
                _pipeServerTask = ConnectWithRetryAsync(_cts.Token, selectionResult.Provider);
                _connectRetryTask = _pipeServerTask;
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
                // No provider detected — log diagnostics line by line
                LogManager.Warning("========================================");
                LogManager.Warning("  Kein Connector erkannt!");
                LogManager.Warning("========================================");
                var summary = selectionResult.DiagnosticSummary ?? "Keine Diagnoseinformationen verfügbar";
                foreach (var line in summary.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    LogManager.Warning(line);
                Status = ConnectorStatus.Disconnected;
                // Fall through to retry loop — provider may become available later
            }

            if (isPipeMode && selectionResult.Success)
            {
                // Pipe server already running — await it
                await _pipeServerTask;
            }
            else
            {
                if (selectionResult.Success)
                {
                    LogManager.Log("========================================");
                    LogManager.Log("  3CX Telefonie Modus ({0})", _selectedMode);
                    LogManager.Log("========================================");
                }
                // Start retry loop (with initial provider if available, or auto-detect from scratch)
                _connectRetryTask = ConnectWithRetryAsync(_cts.Token, selectionResult.Provider);
                await _connectRetryTask;
            }
        }

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

        private void ShowDatevUnavailableNotification()
        {
            DatevUnavailableNotified?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler DatevUnavailableNotified;

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

            _datevAutoDetectTask = Task.Run(async () =>
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

        private IConnectionMethod CreateConnectionMethod(string lineFilter)
        {
            switch (_selectedMode)
            {
                case ConnectionMode.WebClient:
                    LogManager.Log("WebClient-Modus ausgewählt");
                    return new WebclientConnectionMethod(_extension);

                case ConnectionMode.TerminalServer:
                    LogManager.Log("Terminal Server (TAPI)-Modus ausgewählt");
                    return new PipeConnectionMethod(_extension);

                case ConnectionMode.Desktop:
                default:
                    LogManager.Log("Desktop (TAPI)-Modus ausgewählt");
                    return new TapiLineMonitor(lineFilter, _extension);
            }
        }

        private void AdoptExtensionFromProvider(string source)
        {
            var firstLine = System.Linq.Enumerable.FirstOrDefault(_tapiMonitor.Lines, l => l.IsConnected);
            if (firstLine != null && firstLine.Extension != _extension)
            {
                LogManager.Debug("Nebenstelle erkannt von {0}: {1}", source, firstLine.Extension);
                _extension = firstLine.Extension;
                _callEventProcessor.UpdateExtension(_extension);
                CallIdGenerator.Initialize(_extension);

                // Save detected extension to config
                AppConfig.Set(ConfigKeys.ExtensionNumber, _extension);
                LogManager.Log("Nebenstelle gespeichert: {0}", _extension);

                if (_extension.Length > _minCallerIdLength)
                {
                    LogManager.Log("Minimumlänge: {0} -> {1} -stellig (Aufgrund der Nebenstellenlänge)",
                        _minCallerIdLength, _extension.Length);
                    _minCallerIdLength = _extension.Length;
                    _callEventProcessor.UpdateMinCallerIdLength(_minCallerIdLength);
                    AppConfig.SetInt(ConfigKeys.MinCallerIdLength, _extension.Length);
                }
            }
        }

        private void OnProviderCallStateChanged(TapiCallEvent evt)
        {
            _callEventProcessor.OnTapiCallStateChanged(evt);
        }

        private void OnProviderLineDisconnected(TapiLineInfo line)
        {
            LogManager.Log("TAPI Leitung getrennt: {0}", line.Extension);
            StatusChanged?.Invoke(Status);
        }

        private void OnProviderConnected()
        {
            AdoptExtensionFromProvider("connected line");
            Status = ConnectorStatus.Connected;
        }

        private void OnProviderDisconnected()
        {
            Status = ConnectorStatus.Disconnected;
            LogManager.Log("Connector getrennt (alle Leitungen)");
        }

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
                        var configuredModeNow = AppConfig.GetConnectionMode();
                        if (configuredModeNow != _configuredConnectionMode)
                        {
                            LogManager.Log("ConnectionMode Konfiguration zur Laufzeit geändert: {0} -> {1}",
                                _configuredConnectionMode, configuredModeNow);
                            _configuredConnectionMode = configuredModeNow;
                        }

                        if (_configuredConnectionMode == ConnectionMode.Auto)
                        {
                            bool tapiInstalled = TapiConfigReader.IsTapiInstalled();

                            if (tapiInstalled && !SessionManager.IsTerminalSession)
                            {
                                // Desktop: TAPI listens passively (LINE_CREATE if no lines yet)
                                providerToUse = new TapiLineMonitor(lineFilter, _extension);
                                _selectedMode = ConnectionMode.Desktop;
                                LogManager.Log("Auto-Erkennung: Desktop (TAPI) - passiver Listener");
                            }
                            else if (tapiInstalled && SessionManager.IsTerminalSession)
                            {
                                // Terminal Server: Pipe waits for softphone
                                providerToUse = new PipeConnectionMethod(_extension);
                                _selectedMode = ConnectionMode.TerminalServer;
                                LogManager.Log("Auto-Erkennung: Terminal Server - passiver Listener");
                            }
                            else if (AppConfig.GetBool(ConfigKeys.WebclientEnabled, true))
                            {
                                // No TAPI: WebClient waits for browser extension
                                providerToUse = new WebclientConnectionMethod(_extension);
                                _selectedMode = ConnectionMode.WebClient;
                                LogManager.Log("Auto-Erkennung: WebClient - passiver Listener");
                            }

                            if (providerToUse == null)
                            {
                                LogManager.Log("Auto-Erkennung: Kein Connector verfügbar");
                                Status = ConnectorStatus.Disconnected;
                                await Task.Delay(TimeSpan.FromSeconds(reconnectInterval), cancellationToken);
                                continue;
                            }

                            _detectionDiagnostics = string.Format("Auto-Erkennung: {0} (passiv)", _selectedMode);
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

                    if (_tapiMonitor != null)
                    {
                        _tapiMonitor.CallStateChanged -= OnProviderCallStateChanged;
                        _tapiMonitor.LineDisconnected -= OnProviderLineDisconnected;
                        _tapiMonitor.Connected -= OnProviderConnected;
                        _tapiMonitor.Disconnected -= OnProviderDisconnected;
                    }
                    _tapiMonitor?.Dispose();
                    _tapiMonitor = providerToUse;
                    _tapiMonitor.CallStateChanged += OnProviderCallStateChanged;
                    _tapiMonitor.LineDisconnected += OnProviderLineDisconnected;
                    _tapiMonitor.Connected += OnProviderConnected;
                    _tapiMonitor.Disconnected += OnProviderDisconnected;

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

        public async Task ReloadContactsAsync()
        {
            await ReloadContactsAsync(null);
        }

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

        public Task ReconnectTapiAsync()
        {
            return ReconnectTapiAsync(null);
        }

        public Task ReconnectTapiAsync(Action<string> progressText)
        {
            LogManager.Log("Manuelle 3CX Neuverbindung angefordert");

            if (_tapiMonitor != null)
            {
                string ext = Extension ?? "\u2014";
                progressText?.Invoke($"Trenne 3CX (Nst: {ext})...");
                _tapiMonitor.Dispose();
                _tapiMonitor = null;
                progressText?.Invoke("3CX getrennt");
            }

            // Immediately update status so UI reflects disconnected state
            Status = ConnectorStatus.Disconnected;

            progressText?.Invoke("Warte auf Neuverbindung...");

            // Start retry loop if not already running (e.g. initial detection failed)
            var cts = _cts;
            try
            {
                if (cts != null && !cts.Token.IsCancellationRequested &&
                    (_connectRetryTask == null || _connectRetryTask.IsCompleted))
                {
                    LogManager.Log("3CX Verbindungsschleife wird gestartet");
                    _connectRetryTask = ConnectWithRetryAsync(cts.Token);
                }
            }
            catch (ObjectDisposedException) { }

            return Task.CompletedTask;
        }

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

        public void ApplySettings()
        {
            // Delegate popup/journal settings to CallEventProcessor
            _callEventProcessor.ApplySettings();

            // Update call history store live
            bool histIn = AppConfig.GetBool(ConfigKeys.CallHistoryInbound, true);
            bool histOut = AppConfig.GetBool(ConfigKeys.CallHistoryOutbound, false);
            int histMax = AppConfig.GetIntClamped(ConfigKeys.CallHistoryMaxEntries, 5, 1, 100);
            int histRetention = AppConfig.GetIntClamped(ConfigKeys.CallHistoryRetentionDays, 7, 1, 90);
            _callHistory.UpdateConfig(histMax, histIn, histOut, histRetention);

            // DATEV Active contacts filter
            DatevContactRepository.FilterActiveContactsOnly = AppConfig.GetBool(ConfigKeys.ActiveContactsOnly, false);

            // Telephony mode — update immediately for UI feedback
            var newMode = AppConfig.GetConnectionMode();
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

            // Flush any pending call history writes and dispose timer
            _callHistory?.Dispose();

            _tapiMonitor?.Dispose();
            _callTracker?.Dispose();
            DebugConfigWatcher.Stop();
        }
    }
}
