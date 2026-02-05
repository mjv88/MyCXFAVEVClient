using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DatevBridge.Core;
using DatevBridge.Datev.Managers;
using DatevBridge.Interop;
using static DatevBridge.Interop.TapiInterop;

namespace DatevBridge.Tapi
{
    /// <summary>
    /// Information about a single TAPI line
    /// </summary>
    public class TapiLineInfo
    {
        public int DeviceId { get; set; }
        public string LineName { get; set; }
        public string Extension { get; set; }
        public IntPtr Handle { get; set; }
        public int ApiVersion { get; set; }
        public bool IsConnected => Handle != IntPtr.Zero;

        /// <summary>
        /// Parse extension from line name (format: "161 : Name" -> "161")
        /// </summary>
        public static string ParseExtension(string lineName)
        {
            if (string.IsNullOrEmpty(lineName))
                return "—";

            int colonIndex = lineName.IndexOf(':');
            if (colonIndex > 0)
            {
                return lineName.Substring(0, colonIndex).Trim();
            }
            return lineName.Trim();
        }
    }

    /// <summary>
    /// Call event data from TAPI
    /// </summary>
    public class TapiCallEvent
    {
        public IntPtr CallHandle { get; set; }
        public IntPtr LineHandle { get; set; }
        public int CallState { get; set; }
        public int CallId { get; set; }
        public int Origin { get; set; }
        public int AddressId { get; set; }
        public bool InitialStateLogged { get; set; }
        public string CallerNumber { get; set; }
        public string CallerName { get; set; }
        public string CalledNumber { get; set; }
        public string CalledName { get; set; }
        public string Extension { get; set; }

        public bool IsIncoming => Origin != LINECALLORIGIN_OUTBOUND;

        public string CallStateString
        {
            get
            {
                switch (CallState)
                {
                    case LINECALLSTATE_IDLE: return "IDLE";
                    case LINECALLSTATE_OFFERING: return "OFFERING";
                    case LINECALLSTATE_ACCEPTED: return "ACCEPTED";
                    case LINECALLSTATE_DIALTONE: return "DIALTONE";
                    case LINECALLSTATE_DIALING: return "DIALING";
                    case LINECALLSTATE_RINGBACK: return "RINGBACK";
                    case LINECALLSTATE_BUSY: return "BUSY";
                    case LINECALLSTATE_CONNECTED: return "CONNECTED";
                    case LINECALLSTATE_PROCEEDING: return "PROCEEDING";
                    case LINECALLSTATE_DISCONNECTED: return "DISCONNECTED";
                    default: return "0x" + CallState.ToString("X8");
                }
            }
        }
    }

    /// <summary>
    /// Monitors TAPI lines for call events using the Windows TAPI 2.x API.
    /// Opens all matching 3CX TSP lines in monitor mode to receive call state notifications.
    /// Supports multiple lines within a single TAPI session for efficient resource usage.
    /// </summary>
    public class TapiLineMonitor : ITelephonyProvider
    {
        private IntPtr _hLineApp;
        private IntPtr _hEvent;
        private int _numDevices;
        private bool _disposed;
        private volatile bool _disposing;
        private readonly string _lineNameFilter;
        private readonly string _extensionFilter;
        private readonly ConcurrentDictionary<IntPtr, TapiCallEvent> _activeCalls = new ConcurrentDictionary<IntPtr, TapiCallEvent>();

        // Multi-line support: track all discovered and opened lines (thread-safe for background message loop)
        private readonly ConcurrentDictionary<int, TapiLineInfo> _lines = new ConcurrentDictionary<int, TapiLineInfo>();
        private readonly ConcurrentDictionary<IntPtr, TapiLineInfo> _linesByHandle = new ConcurrentDictionary<IntPtr, TapiLineInfo>();

        /// <summary>
        /// Fired when a call state changes (includes line info via Extension property)
        /// </summary>
        public event Action<TapiCallEvent> CallStateChanged;

        /// <summary>
        /// Fired when a specific line connects
        /// </summary>
        public event Action<TapiLineInfo> LineConnected;

        /// <summary>
        /// Fired when a specific line disconnects
        /// </summary>
        public event Action<TapiLineInfo> LineDisconnected;

        /// <summary>
        /// Fired when TAPI is initialized and at least one line is open (backward compatibility)
        /// </summary>
        public event Action Connected;

        /// <summary>
        /// Fired when all lines are closed or TAPI shuts down (backward compatibility)
        /// </summary>
        public event Action Disconnected;

        /// <summary>
        /// Whether at least one line is currently open and monitoring
        /// </summary>
        public bool IsMonitoring => _lines.Values.Any(l => l.IsConnected);

        /// <summary>
        /// Number of connected lines
        /// </summary>
        public int ConnectedLineCount => _lines.Values.Count(l => l.IsConnected);

        /// <summary>
        /// All discovered lines (connected or not)
        /// </summary>
        public IReadOnlyList<TapiLineInfo> Lines => _lines.Values.ToList().AsReadOnly();

        /// <summary>
        /// The name of the first opened line device (backward compatibility)
        /// </summary>
        public string LineName => _lines.Values.FirstOrDefault(l => l.IsConnected)?.LineName;

        /// <summary>
        /// The extension of the first opened line (backward compatibility)
        /// </summary>
        public string Extension => _lines.Values.FirstOrDefault(l => l.IsConnected)?.Extension;

        /// <summary>
        /// Create a TAPI line monitor
        /// </summary>
        /// <param name="lineNameFilter">Substring to match in line name (e.g. "3CX").
        /// If null/empty, opens the first available voice line.</param>
        /// <param name="extensionFilter">Exact extension number to match (e.g. "150").
        /// If null/empty, opens all lines matching the name filter.</param>
        public TapiLineMonitor(string lineNameFilter = "3CX", string extensionFilter = null)
        {
            _lineNameFilter = lineNameFilter;
            _extensionFilter = extensionFilter;
        }

        /// <summary>
        /// Safely invoke an action, checking for disposal state first.
        /// Returns false if disposing/disposed.
        /// </summary>
        private bool SafeInvokeEvent<T>(Action<T> handler, T arg)
        {
            if (_disposing || _disposed || handler == null)
                return false;
            try
            {
                handler(arg);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Safely invoke a parameterless action, checking for disposal state first.
        /// </summary>
        private bool SafeInvokeEvent(Action handler)
        {
            if (_disposing || _disposed || handler == null)
                return false;
            try
            {
                handler();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Initialize TAPI and start monitoring
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await StartAsync(cancellationToken, null);
        }

        /// <summary>
        /// Initialize TAPI and start monitoring with progress callback
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="progressText">Optional callback for progress text updates</param>
        public async Task StartAsync(CancellationToken cancellationToken, Action<string> progressText)
        {
            Initialize(progressText);

            if (_lines.Count == 0)
            {
                progressText?.Invoke("Keine 3CX TAPI Leitung gefunden");
                throw new InvalidOperationException("No matching TAPI line device found");
            }

            // Open all discovered lines
            OpenAllLines(progressText);

            int connectedCount = _lines.Values.Count(l => l.IsConnected);
            if (connectedCount == 0)
            {
                bool is3CXRunning = SessionManager.Is3CXProcessRunning();
                if (!is3CXRunning)
                {
                    progressText?.Invoke("Warte auf 3CX Desktop App...");
                    throw new InvalidOperationException(
                        "TAPI lines found but not registered - 3CX Desktop App is not running in this session");
                }

                progressText?.Invoke("Keine Leitung konnte geöffnet werden");
                throw new InvalidOperationException("Failed to open any TAPI line");
            }

            if (connectedCount == 1)
            {
                var line = _lines.Values.First(l => l.IsConnected);
                progressText?.Invoke($"3CX TAPI verbunden (Nst: {line.Extension})");
            }
            else
            {
                var extensions = string.Join(", ", _lines.Values.Where(l => l.IsConnected).Select(l => l.Extension));
                progressText?.Invoke($"{connectedCount} Leitungen verbunden (Nst: {extensions})");
            }

            SafeInvokeEvent(Connected);

            // Run message loop
            await Task.Run(() => MessageLoop(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Reconnect a specific line by extension
        /// </summary>
        /// <param name="extension">Extension to reconnect</param>
        /// <param name="progressText">Optional progress callback</param>
        /// <returns>True if reconnected successfully</returns>
        public bool ReconnectLine(string extension, Action<string> progressText = null)
        {
            var line = _lines.Values.FirstOrDefault(l => l.Extension == extension);
            if (line == null)
            {
                LogManager.Log("TAPI: Cannot reconnect - extension {0} not found", extension);
                return false;
            }

            // Close existing handle if open
            if (line.Handle != IntPtr.Zero)
            {
                progressText?.Invoke($"Trenne Leitung {extension}...");
                _linesByHandle.TryRemove(line.Handle, out _);
                lineClose(line.Handle);
                line.Handle = IntPtr.Zero;
                SafeInvokeEvent(LineDisconnected, line);
            }

            // Reopen the line
            progressText?.Invoke($"Öffne Leitung {extension}...");
            return OpenSingleLine(line, progressText);
        }

        /// <summary>
        /// Test a specific line by extension to verify it's actually connected and responsive.
        /// Includes automatic retry for transient errors with exponential backoff.
        /// </summary>
        /// <param name="extension">Extension to test</param>
        /// <param name="progressText">Optional progress callback</param>
        /// <param name="maxRetries">Maximum retry attempts for transient errors (default: 3)</param>
        /// <returns>True if line is connected and responsive</returns>
        public bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3)
        {
            var line = _lines.Values.FirstOrDefault(l => l.Extension == extension);
            if (line == null)
            {
                progressText?.Invoke($"Leitung {extension} nicht gefunden");
                LogManager.Log("TAPI TestLine: Extension {0} not found", extension);
                return false;
            }

            progressText?.Invoke($"Prüfe Leitung {extension}...");

            // First check: is the handle valid?
            if (line.Handle == IntPtr.Zero)
            {
                progressText?.Invoke($"Leitung {extension} nicht verbunden");
                LogManager.Log("TAPI TestLine: Line {0} has no valid handle", extension);
                return false;
            }

            // Execute test with retry logic for transient errors
            int attempt = 0;
            int delayMs = 500; // Start with 500ms delay

            while (attempt <= maxRetries)
            {
                var result = TestLineInternal(line, progressText, attempt > 0);

                switch (result.Category)
                {
                    case TapiErrorCategory.Success:
                        return result.IsConnected;

                    case TapiErrorCategory.Transient:
                        if (attempt < maxRetries)
                        {
                            attempt++;
                            progressText?.Invoke($"Wiederhole ({attempt}/{maxRetries})...");
                            LogManager.Log("TAPI TestLine {0}: Transient error, retry {1}/{2} in {3}ms",
                                extension, attempt, maxRetries, delayMs);
                            Thread.Sleep(delayMs);
                            delayMs *= 2; // Exponential backoff
                            continue;
                        }
                        LogManager.Log("TAPI TestLine {0}: Transient error, max retries reached", extension);
                        progressText?.Invoke($"Fehler nach {maxRetries} Versuchen");
                        return false;

                    case TapiErrorCategory.LineClosed:
                        progressText?.Invoke("Leitung getrennt - Neuverbindung erforderlich");
                        LogManager.Log("TAPI TestLine {0}: Line closed/invalid, reconnect required", extension);
                        // Mark the line as disconnected
                        if (line.Handle != IntPtr.Zero)
                        {
                            _linesByHandle.TryRemove(line.Handle, out _);
                            line.Handle = IntPtr.Zero;
                            SafeInvokeEvent(LineDisconnected, line);
                        }
                        return false;

                    case TapiErrorCategory.Shutdown:
                        progressText?.Invoke("TAPI wird heruntergefahren");
                        LogManager.Log("TAPI TestLine {0}: TAPI shutdown/reinit required", extension);
                        return false;

                    case TapiErrorCategory.Permanent:
                    default:
                        // Don't retry permanent errors
                        return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Internal test result with error category
        /// </summary>
        private struct TestLineResult
        {
            public TapiErrorCategory Category;
            public bool IsConnected;
            public int ErrorCode;
        }

        /// <summary>
        /// Internal method to perform the actual TAPI line test
        /// </summary>
        private TestLineResult TestLineInternal(TapiLineInfo line, Action<string> progressText, bool isRetry)
        {
            if (!isRetry)
                progressText?.Invoke("Abfrage TAPI Status...");

            int bufferSize = 256;
            IntPtr pDevStatus = IntPtr.Zero;

            try
            {
                pDevStatus = Marshal.AllocHGlobal(bufferSize);
                Marshal.WriteInt32(pDevStatus, bufferSize); // dwTotalSize

                int result = lineGetLineDevStatus(line.Handle, pDevStatus);

                if (result != LINEERR_OK)
                {
                    var category = CategorizeError(result);
                    var errorDesc = GetErrorDescription(result);

                    progressText?.Invoke($"TAPI: {errorDesc}");
                    LogManager.Log("TAPI TestLine {0}: lineGetLineDevStatus failed: {1} (0x{2:X8})",
                        line.Extension, errorDesc, result);

                    return new TestLineResult
                    {
                        Category = category,
                        IsConnected = false,
                        ErrorCode = result
                    };
                }

                // Parse the LINEDEVSTATUS structure
                var devStatus = (LINEDEVSTATUS)Marshal.PtrToStructure(pDevStatus, typeof(LINEDEVSTATUS));

                // Check INSERVICE flag
                bool inService = (devStatus.dwDevStatusFlags & LINEDEVSTATUSFLAGS_INSERVICE) != 0;
                bool connected = (devStatus.dwDevStatusFlags & LINEDEVSTATUSFLAGS_CONNECTED) != 0;
                int activeCalls = devStatus.dwNumActiveCalls;
                int numOpens = devStatus.dwNumOpens;

                LogManager.Log("TAPI TestLine {0}: Opens={1}, ActiveCalls={2}, InService={3}, Connected={4}",
                    line.Extension, numOpens, activeCalls, inService, connected);

                // Build status message
                string statusMsg;
                bool isConnected;

                if (inService)
                {
                    if (activeCalls > 0)
                        statusMsg = $"Verbunden ({activeCalls} Anruf{(activeCalls > 1 ? "e" : "")})";
                    else
                        statusMsg = "Verbunden (bereit)";
                    isConnected = true;
                }
                else if (connected)
                {
                    statusMsg = "Verbunden (außer Betrieb)";
                    isConnected = true; // Handle is valid even if not in service
                }
                else
                {
                    statusMsg = "Nicht verbunden";
                    isConnected = false;
                }

                progressText?.Invoke(statusMsg);

                return new TestLineResult
                {
                    Category = TapiErrorCategory.Success,
                    IsConnected = isConnected,
                    ErrorCode = LINEERR_OK
                };
            }
            catch (Exception ex)
            {
                progressText?.Invoke($"Fehler: {ex.Message}");
                LogManager.Log("TAPI TestLine {0}: Exception: {1}", line.Extension, ex.Message);

                return new TestLineResult
                {
                    Category = TapiErrorCategory.Transient, // Treat exceptions as potentially transient
                    IsConnected = false,
                    ErrorCode = -1
                };
            }
            finally
            {
                if (pDevStatus != IntPtr.Zero)
                    Marshal.FreeHGlobal(pDevStatus);
            }
        }

        /// <summary>
        /// Reconnect all lines
        /// </summary>
        /// <param name="progressText">Optional progress callback</param>
        public void ReconnectAllLines(Action<string> progressText = null)
        {
            // Close all open lines
            foreach (var line in _lines.Values.Where(l => l.IsConnected).ToList())
            {
                progressText?.Invoke($"Trenne Leitung {line.Extension}...");
                _linesByHandle.TryRemove(line.Handle, out _);
                lineClose(line.Handle);
                line.Handle = IntPtr.Zero;
                SafeInvokeEvent(LineDisconnected, line);
            }

            // Reopen all lines
            OpenAllLines(progressText);
        }

        /// <summary>
        /// Initialize TAPI subsystem
        /// </summary>
        private void Initialize(Action<string> progressText = null)
        {
            progressText?.Invoke("Initialisiere TAPI...");

            var initParams = new LINEINITIALIZEEXPARAMS();
            initParams.dwTotalSize = Marshal.SizeOf(typeof(LINEINITIALIZEEXPARAMS));
            initParams.dwOptions = LINEINITIALIZEEXOPTION_USEEVENT;

            int apiVersion = TAPI_VERSION_2_2;

            int result = lineInitializeEx(
                out _hLineApp,
                IntPtr.Zero,
                IntPtr.Zero,  // No callback - using events
                "DatevBridge",
                out _numDevices,
                ref apiVersion,
                ref initParams);

            if (result != LINEERR_OK)
            {
                progressText?.Invoke("TAPI Initialisierung fehlgeschlagen");
                throw new InvalidOperationException($"lineInitializeEx failed: 0x{result:X8}");
            }

            _hEvent = initParams.hEvent;
            LogManager.Log("TAPI initialized: {0} line devices", _numDevices);
            progressText?.Invoke($"{_numDevices} TAPI Leitungen gefunden");

            // Find the 3CX line
            FindLine(progressText);
        }

        /// <summary>
        /// Find ALL matching line devices by name filter
        /// </summary>
        private void FindLine(Action<string> progressText = null)
        {
            progressText?.Invoke("Suche 3CX TAPI Leitungen...");
            _lines.Clear();

            for (int i = 0; i < _numDevices; i++)
            {
                LINEEXTENSIONID extensionId;
                int negotiatedVersion;

                int result = lineNegotiateAPIVersion(
                    _hLineApp, i,
                    TAPI_VERSION_1_0, TAPI_VERSION_2_2,
                    out negotiatedVersion, out extensionId);

                if (result != LINEERR_OK)
                    continue;

                string lineName = GetLineName(i, negotiatedVersion);
                if (lineName == null)
                    continue;

                bool matches = string.IsNullOrEmpty(_lineNameFilter) ||
                               lineName.IndexOf(_lineNameFilter, StringComparison.OrdinalIgnoreCase) >= 0;

                string lineExtension = TapiLineInfo.ParseExtension(lineName);

                // Also filter by extension number if specified
                if (matches && !string.IsNullOrEmpty(_extensionFilter))
                {
                    if (lineExtension != _extensionFilter)
                    {
                        LogManager.Log("Skipping TAPI line {0}: \"{1}\" (Ext: {2}) - does not match configured extension {3}",
                            i, lineName, lineExtension, _extensionFilter);
                        continue;
                    }
                }

                if (matches)
                {
                    var lineInfo = new TapiLineInfo
                    {
                        DeviceId = i,
                        LineName = lineName,
                        Extension = lineExtension,
                        ApiVersion = negotiatedVersion,
                        Handle = IntPtr.Zero
                    };

                    _lines[i] = lineInfo;
                    LogManager.Log("Discovered 3CX TAPI line {0}: \"{1}\" (Ext: {2})", i, lineName, lineInfo.Extension);
                    progressText?.Invoke($"3CX Leitung gefunden: {lineInfo.Extension}");
                }
            }

            if (_lines.Count == 0)
            {
                if (!string.IsNullOrEmpty(_extensionFilter))
                    LogManager.Warning("No TAPI line found for extension {0} (filter=\"{1}\", {2} devices scanned)",
                        _extensionFilter, _lineNameFilter ?? "(any)", _numDevices);
                else
                    LogManager.Log("No TAPI line matching \"{0}\" found", _lineNameFilter ?? "(any)");
            }
        }

        /// <summary>
        /// Get the name of a line device
        /// </summary>
        private string GetLineName(int deviceId, int apiVersion)
        {
            // Start with reasonable buffer
            int bufferSize = 1024;
            IntPtr pDevCaps = IntPtr.Zero;

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (pDevCaps != IntPtr.Zero)
                        Marshal.FreeHGlobal(pDevCaps);

                    pDevCaps = Marshal.AllocHGlobal(bufferSize);
                    Marshal.WriteInt32(pDevCaps, bufferSize); // dwTotalSize

                    int result = lineGetDevCaps(_hLineApp, deviceId, apiVersion, 0, pDevCaps);
                    if (result != LINEERR_OK)
                        return null;

                    int neededSize = Marshal.ReadInt32(pDevCaps, 4); // dwNeededSize
                    if (neededSize <= bufferSize)
                    {
                        // Read line name
                        var devCaps = (LINEDEVCAPS)Marshal.PtrToStructure(pDevCaps, typeof(LINEDEVCAPS));
                        if (devCaps.dwLineNameSize > 0 && devCaps.dwLineNameOffset > 0)
                        {
                            IntPtr namePtr = IntPtr.Add(pDevCaps, devCaps.dwLineNameOffset);
                            return Marshal.PtrToStringAuto(namePtr, devCaps.dwLineNameSize / 2).TrimEnd('\0');
                        }
                        return null;
                    }

                    bufferSize = neededSize;
                }
            }
            finally
            {
                if (pDevCaps != IntPtr.Zero)
                    Marshal.FreeHGlobal(pDevCaps);
            }

            return null;
        }

        /// <summary>
        /// Open all discovered lines in monitor mode
        /// </summary>
        private void OpenAllLines(Action<string> progressText = null)
        {
            foreach (var line in _lines.Values)
            {
                progressText?.Invoke($"Öffne Leitung: {line.Extension}...");
                OpenSingleLine(line, progressText);
            }
        }

        /// <summary>
        /// Open a single line in monitor mode
        /// </summary>
        /// <returns>True if opened successfully</returns>
        private bool OpenSingleLine(TapiLineInfo line, Action<string> progressText = null)
        {
            IntPtr hLine;
            int result = lineOpen(
                _hLineApp,
                line.DeviceId,
                out hLine,
                line.ApiVersion,
                0,          // dwExtVersion
                IntPtr.Zero, // dwCallbackInstance
                LINECALLPRIVILEGE_MONITOR,
                LINEMEDIAMODE_INTERACTIVEVOICE | LINEMEDIAMODE_UNKNOWN,
                IntPtr.Zero); // lpCallParams

            if (result != LINEERR_OK)
            {
                var errorDesc = GetErrorDescription(result);
                LogManager.Warning("lineOpen failed for device {0} ({1}): {2} (0x{3:X8})",
                    line.DeviceId, line.Extension, errorDesc, result);
                progressText?.Invoke($"Fehler: Leitung {line.Extension} - {errorDesc}");

                // LINEERR_NOTREGISTERED: 3CX TSP needs the dialer app running
                if (result == LINEERR_NOTREGISTERED)
                {
                    SessionManager.Log3CXTapiDiagnostics(line.Extension);
                    if (!SessionManager.Is3CXProcessRunning())
                        progressText?.Invoke("Warte auf 3CX Desktop App...");
                }

                return false;
            }

            line.Handle = hLine;
            _linesByHandle[hLine] = line;

            // Request line device state notifications (call states come automatically with MONITOR privilege)
            lineSetStatusMessages(hLine, LINEDEVSTATE_RINGING, 0);

            LogManager.Log("TAPI line connected: {0}", line.Extension);
            progressText?.Invoke($"Leitung {line.Extension} verbunden");

            SafeInvokeEvent(LineConnected, line);
            return true;
        }

        /// <summary>
        /// Message loop - waits for TAPI events and processes them
        /// </summary>
        private void MessageLoop(CancellationToken cancellationToken)
        {
            LogManager.Log("3CX - DATEV - Bridge (TAPI) ready");

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for event with 500ms timeout (allows cancellation checks)
                int waitResult = WaitForSingleObject(_hEvent, 500);

                if (waitResult == WAIT_TIMEOUT)
                    continue;

                if (waitResult != WAIT_OBJECT_0)
                    break;

                // Drain all pending messages
                while (!cancellationToken.IsCancellationRequested)
                {
                    var msg = new LINEMESSAGE();
                    int result = lineGetMessage(_hLineApp, ref msg, 0); // Non-blocking

                    if (result != LINEERR_OK)
                        break; // No more messages

                    ProcessMessage(msg);
                }
            }

            LogManager.Log("TAPI message loop ended");
            SafeInvokeEvent(Disconnected);
        }

        /// <summary>
        /// Process a TAPI message
        /// </summary>
        private void ProcessMessage(LINEMESSAGE msg)
        {
            switch (msg.dwMessageID)
            {
                case LINE_CALLSTATE:
                    HandleCallState(msg.hDevice, (int)msg.dwParam1, msg.dwParam2, msg.dwParam3);
                    break;

                case LINE_APPNEWCALL:
                    HandleNewCall(msg.hDevice, (int)msg.dwParam1, msg.dwParam2);
                    break;

                case LINE_CLOSE:
                    HandleLineClose(msg.hDevice);
                    break;

                case LINE_REPLY:
                    HandleReply((int)msg.dwParam1, (int)msg.dwParam2);
                    break;

                default:
                    LogManager.Debug("TAPI msg: ID={0} hDevice=0x{1:X} P1=0x{2:X} P2=0x{3:X}",
                        msg.dwMessageID, msg.hDevice.ToInt64(),
                        msg.dwParam1.ToInt64(), msg.dwParam2.ToInt64());
                    break;
            }
        }

        /// <summary>
        /// Handle LINE_CLOSE - a specific line was closed
        /// </summary>
        private void HandleLineClose(IntPtr hLine)
        {
            TapiLineInfo line;
            if (_linesByHandle.TryGetValue(hLine, out line))
            {
                LogManager.Log("TAPI: LINE_CLOSE received for {0} ({1})", line.Extension, line.LineName);
                line.Handle = IntPtr.Zero;
                _linesByHandle.TryRemove(hLine, out _);
                SafeInvokeEvent(LineDisconnected, line);
            }
            else
            {
                LogManager.Log("TAPI: LINE_CLOSE received for unknown handle 0x{0:X}", hLine.ToInt64());
            }

            // Check if all lines are now disconnected
            if (!_lines.Values.Any(l => l.IsConnected))
            {
                LogManager.Log("TAPI: All lines disconnected");
                SafeInvokeEvent(Disconnected);
            }
        }

        /// <summary>
        /// Handle LINE_APPNEWCALL - new call handle assigned
        /// </summary>
        private void HandleNewCall(IntPtr hLine, int addressId, IntPtr hCall)
        {
            var callEvent = GetOrCreateCallEvent(hCall);
            callEvent.AddressId = addressId;
            callEvent.LineHandle = hLine;

            // Associate call with line extension
            TapiLineInfo line;
            if (_linesByHandle.TryGetValue(hLine, out line))
            {
                callEvent.Extension = line.Extension;
                LogManager.Debug("TAPI: New call handle 0x{0:X} on line {1} address {2}",
                    hCall.ToInt64(), line.Extension, addressId);
            }
            else
            {
                LogManager.Debug("TAPI: New call handle 0x{0:X} on address {1}", hCall.ToInt64(), addressId);
            }
        }

        /// <summary>
        /// Handle LINE_CALLSTATE - call state change
        /// </summary>
        private void HandleCallState(IntPtr hCall, int callState, IntPtr param2, IntPtr param3)
        {
            var callEvent = GetOrCreateCallEvent(hCall);
            callEvent.CallState = callState;

            // Get call info for caller/called details
            GetCallInfo(hCall, callEvent);

            string direction = callEvent.IsIncoming ? "inbound" : "outbound";
            string caller = callEvent.CallerNumber ?? "?";
            string called = callEvent.CalledNumber ?? "?";

            if (callState == LINECALLSTATE_IDLE)
            {
                // IDLE is a transient handle setup state - debug only
                LogManager.Debug("TAPI: Call 0x{0:X} state=IDLE direction={1} caller={2} called={3}",
                    hCall.ToInt64(), direction, caller, called);
            }
            else if (!callEvent.InitialStateLogged &&
                     (callState == LINECALLSTATE_OFFERING || callState == LINECALLSTATE_DIALING))
            {
                // First real call state - concise summary
                callEvent.InitialStateLogged = true;
                LogManager.Log("TAPI: New call on line {0}", callEvent.AddressId);
                LogManager.Log("TAPI: Call direction={0} caller={1} called={2}", direction, LogManager.Mask(caller), called);
            }
            else
            {
                LogManager.Log("TAPI: Call state={0} direction={1} caller={2} called={3}",
                    callEvent.CallStateString, direction, LogManager.Mask(caller), called);
            }

            // Safely invoke the event handler, checking for disposal state
            if (!_disposing && !_disposed)
            {
                try
                {
                    CallStateChanged?.Invoke(callEvent);
                }
                catch (ObjectDisposedException)
                {
                    // Form already disposed, ignore
                }
                catch (InvalidOperationException)
                {
                    // Handle no longer valid, ignore
                }
                catch (Exception ex)
                {
                    LogManager.Log("Error in CallStateChanged handler: {0}", ex.Message);
                }
            }

            // Clean up after disconnect/idle
            if (callState == LINECALLSTATE_IDLE || callState == LINECALLSTATE_DISCONNECTED)
            {
                if (callState == LINECALLSTATE_IDLE)
                {
                    TapiCallEvent removed;
                    if (_activeCalls.TryRemove(hCall, out removed))
                        lineDeallocateCall(hCall);
                }
            }
        }

        /// <summary>
        /// Handle LINE_REPLY - async operation result
        /// </summary>
        private void HandleReply(int requestId, int result)
        {
            if (result != LINEERR_OK)
            {
                LogManager.Log("TAPI: LINE_REPLY request={0} error=0x{1:X8}", requestId, result);
            }
        }

        /// <summary>
        /// Get or create a call event for tracking
        /// </summary>
        private TapiCallEvent GetOrCreateCallEvent(IntPtr hCall)
        {
            return _activeCalls.GetOrAdd(hCall, h => new TapiCallEvent { CallHandle = h });
        }

        /// <summary>
        /// Get call information (caller ID, called ID, etc.)
        /// </summary>
        private void GetCallInfo(IntPtr hCall, TapiCallEvent callEvent)
        {
            int bufferSize = 2048;
            IntPtr pCallInfo = IntPtr.Zero;

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (pCallInfo != IntPtr.Zero)
                        Marshal.FreeHGlobal(pCallInfo);

                    pCallInfo = Marshal.AllocHGlobal(bufferSize);
                    Marshal.WriteInt32(pCallInfo, bufferSize); // dwTotalSize

                    int result = lineGetCallInfo(hCall, pCallInfo);
                    if (result != LINEERR_OK)
                        return;

                    int neededSize = Marshal.ReadInt32(pCallInfo, 4); // dwNeededSize
                    if (neededSize <= bufferSize)
                    {
                        var info = (LINECALLINFO)Marshal.PtrToStructure(pCallInfo, typeof(LINECALLINFO));

                        callEvent.CallId = info.dwCallID;
                        callEvent.Origin = info.dwOrigin;

                        // Extract caller number
                        if (info.dwCallerIDSize > 0 && info.dwCallerIDOffset > 0)
                        {
                            callEvent.CallerNumber = ReadStringFromBuffer(pCallInfo, info.dwCallerIDOffset, info.dwCallerIDSize);
                        }

                        // Extract caller name
                        if (info.dwCallerIDNameSize > 0 && info.dwCallerIDNameOffset > 0)
                        {
                            callEvent.CallerName = ReadStringFromBuffer(pCallInfo, info.dwCallerIDNameOffset, info.dwCallerIDNameSize);
                        }

                        // Extract called number
                        if (info.dwCalledIDSize > 0 && info.dwCalledIDOffset > 0)
                        {
                            callEvent.CalledNumber = ReadStringFromBuffer(pCallInfo, info.dwCalledIDOffset, info.dwCalledIDSize);
                        }

                        // Extract called name
                        if (info.dwCalledIDNameSize > 0 && info.dwCalledIDNameOffset > 0)
                        {
                            callEvent.CalledName = ReadStringFromBuffer(pCallInfo, info.dwCalledIDNameOffset, info.dwCalledIDNameSize);
                        }

                        return;
                    }

                    bufferSize = neededSize;
                }
            }
            finally
            {
                if (pCallInfo != IntPtr.Zero)
                    Marshal.FreeHGlobal(pCallInfo);
            }
        }

        /// <summary>
        /// Read a null-terminated string from a buffer at given offset
        /// </summary>
        private string ReadStringFromBuffer(IntPtr basePtr, int offset, int size)
        {
            if (offset <= 0 || size <= 0)
                return null;

            IntPtr strPtr = IntPtr.Add(basePtr, offset);
            string value = Marshal.PtrToStringAuto(strPtr, size / 2);
            return value?.TrimEnd('\0');
        }

        /// <summary>
        /// Make an outbound call on the first connected line
        /// </summary>
        /// <returns>Positive request ID on success, negative TAPI error code on failure</returns>
        public int MakeCall(string destination)
        {
            return MakeCall(destination, null);
        }

        /// <summary>
        /// Make an outbound call on a specific line
        /// </summary>
        /// <param name="destination">Number to dial</param>
        /// <param name="extension">Extension to use (null = first connected line)</param>
        /// <returns>Positive request ID on success, negative TAPI error code on failure</returns>
        public int MakeCall(string destination, string extension)
        {
            TapiLineInfo line;

            if (extension != null)
            {
                line = _lines.Values.FirstOrDefault(l => l.Extension == extension && l.IsConnected);
                if (line == null)
                {
                    LogManager.Log("TAPI MakeCall: Extension {0} not found or not connected", extension);
                    return -1;
                }
            }
            else
            {
                line = _lines.Values.FirstOrDefault(l => l.IsConnected);
                if (line == null)
                {
                    LogManager.Log("TAPI MakeCall: No line connected");
                    return -1;
                }
            }

            IntPtr hCall;
            int result = lineMakeCall(line.Handle, out hCall, destination, 0, IntPtr.Zero);

            if (result > 0)
            {
                // Positive = async request ID
                LogManager.Log("TAPI MakeCall: Dialing {0} on line {1} (requestId={2})",
                    destination, line.Extension, result);
            }
            else
            {
                LogManager.Log("TAPI MakeCall: Failed for {0} on line {1} (error=0x{2:X8})",
                    destination, line.Extension, result);
            }

            return result;
        }

        /// <summary>
        /// Drop/end a call by handle
        /// </summary>
        public int DropCall(IntPtr hCall)
        {
            if (hCall == IntPtr.Zero)
                return -1;

            int result = lineDrop(hCall, IntPtr.Zero, 0);

            if (result > 0)
            {
                LogManager.Log("TAPI DropCall: 0x{0:X} (requestId={1})", hCall.ToInt64(), result);
            }
            else
            {
                LogManager.Log("TAPI DropCall: Failed for 0x{0:X} (error=0x{1:X8})", hCall.ToInt64(), result);
            }

            return result;
        }

        /// <summary>
        /// Find an active call by its TAPI call ID
        /// </summary>
        public TapiCallEvent FindCallById(int callId)
        {
            foreach (var kvp in _activeCalls)
            {
                if (kvp.Value.CallId == callId)
                    return kvp.Value;
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Set disposing flag first to prevent callbacks from firing
            _disposing = true;
            _disposed = true;
            GC.SuppressFinalize(this);

            // Close all open lines
            foreach (var line in _lines.Values.Where(l => l.IsConnected).ToList())
            {
                LogManager.Log("TAPI: Closing line {0} ({1})", line.DeviceId, line.Extension);
                lineClose(line.Handle);
                line.Handle = IntPtr.Zero;
            }

            _linesByHandle.Clear();

            if (_hLineApp != IntPtr.Zero)
            {
                lineShutdown(_hLineApp);
                _hLineApp = IntPtr.Zero;
            }

            // Note: _hEvent is owned by TAPI, closed by lineShutdown
            _hEvent = IntPtr.Zero;

            LogManager.Log("TAPI line monitor disposed ({0} lines)", _lines.Count);
        }
    }
}
