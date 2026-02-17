using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core;
using DatevConnector.Datev.Managers;
using static DatevConnector.Interop.TapiInterop;

namespace DatevConnector.Tapi
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
        /// Parse extension from line name (format: "100 : Name" -> "100")
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
    /// Thin facade that delegates to TapiInitializer, TapiLineManager, and TapiOperations.
    /// </summary>
    public class TapiLineMonitor : IConnectionMethod
    {
        private bool _disposed;
        private volatile bool _disposing;
        private readonly ConcurrentDictionary<IntPtr, TapiCallEvent> _activeCalls = new ConcurrentDictionary<IntPtr, TapiCallEvent>();

        // Multi-line support: track all discovered and opened lines (thread-safe for background message loop)
        private readonly ConcurrentDictionary<int, TapiLineInfo> _lines = new ConcurrentDictionary<int, TapiLineInfo>();
        private readonly ConcurrentDictionary<IntPtr, TapiLineInfo> _linesByHandle = new ConcurrentDictionary<IntPtr, TapiLineInfo>();

        // Extracted helpers
        private readonly TapiInitializer _initializer;
        private readonly TapiLineManager _lineManager;
        private readonly TapiOperations _operations;

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
        /// <param name="extensionFilter">Exact extension number to match (e.g. "100").
        /// If null/empty, opens all lines matching the name filter.</param>
        public TapiLineMonitor(string lineNameFilter = "3CX", string extensionFilter = null)
        {
            _initializer = new TapiInitializer(lineNameFilter, extensionFilter);
            _lineManager = new TapiLineManager(
                _lines, _linesByHandle,
                () => _initializer.LineAppHandle,
                line => SafeInvokeEvent(LineConnected, line),
                line => SafeInvokeEvent(LineDisconnected, line));
            _operations = new TapiOperations(
                _lines, _activeCalls, _linesByHandle,
                line => SafeInvokeEvent(LineDisconnected, line));
        }

        /// <summary>
        /// Safely invoke an action, checking for disposal state first.
        /// </summary>
        private bool SafeInvokeEvent<T>(Action<T> handler, T arg)
        {
            if (_disposing || _disposed)
                return false;
            return EventHelper.SafeInvoke(handler, arg, "TapiLineMonitor");
        }

        /// <summary>
        /// Safely invoke a parameterless action, checking for disposal state first.
        /// </summary>
        private bool SafeInvokeEvent(Action handler)
        {
            if (_disposing || _disposed)
                return false;
            return EventHelper.SafeInvoke(handler, "TapiLineMonitor");
        }

        /// <summary>
        /// Probe whether TAPI lines are available without starting the message loop.
        /// Used by auto-detection to decide if TAPI should be selected.
        /// If this returns true, the monitor is initialized and ready for StartAsync.
        /// If false, the caller should Dispose and try another provider.
        /// </summary>
        public bool ProbeLines()
        {
            if (!_initializer.Initialize())
                return false;

            _initializer.FindLines(_lines);
            return _lines.Count > 0;
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
        public async Task StartAsync(CancellationToken cancellationToken, Action<string> progressText)
        {
            // Skip init/find if ProbeLines() already succeeded
            if (_lines.Count == 0)
            {
                if (!_initializer.Initialize(progressText))
                {
                    progressText?.Invoke("TAPI nicht verfügbar");
                    return;
                }

                _initializer.FindLines(_lines, progressText);

                if (_lines.Count == 0)
                {
                    progressText?.Invoke("Keine 3CX TAPI Leitung gefunden");
                    LogManager.Log("Keine passende TAPI Leitung gefunden");
                    return;
                }
            }

            // Open all discovered lines
            _lineManager.OpenAllLines(progressText);

            int connectedCount = _lines.Values.Count(l => l.IsConnected);
            if (connectedCount == 0)
            {
                bool is3CXRunning = SessionManager.Is3CXProcessRunning();
                if (!is3CXRunning)
                {
                    progressText?.Invoke("Warte auf 3CX Desktop App...");
                    LogManager.Log("TAPI Leitungen gefunden aber nicht registriert - 3CX Desktop App läuft nicht");
                }
                else
                {
                    progressText?.Invoke("Keine Leitung konnte geöffnet werden");
                    LogManager.Log("Keine TAPI Leitung konnte geöffnet werden");
                }
                return;
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

        // ===== IConnectionMethod delegations =====

        public bool ReconnectLine(string extension, Action<string> progressText = null)
        {
            return _lineManager.ReconnectLine(extension, progressText);
        }

        public void ReconnectAllLines(Action<string> progressText = null)
        {
            _lineManager.ReconnectAllLines(progressText);
        }

        public bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3)
        {
            return _operations.TestLine(extension, progressText, maxRetries);
        }

        public int MakeCall(string destination)
        {
            return _operations.MakeCall(destination);
        }

        /// <summary>
        /// Make an outbound call on a specific line
        /// </summary>
        public int MakeCall(string destination, string extension)
        {
            return _operations.MakeCall(destination, extension);
        }

        public int DropCall(IntPtr hCall)
        {
            return _operations.DropCall(hCall);
        }

        public TapiCallEvent FindCallById(int callId)
        {
            return _operations.FindCallById(callId);
        }

        // ===== Message loop and event processing =====

        /// <summary>
        /// Message loop - waits for TAPI events and processes them
        /// </summary>
        private void MessageLoop(CancellationToken cancellationToken)
        {
            LogManager.Log("3CX - DATEV Connector (TAPI) ready");

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for event with 500ms timeout (allows cancellation checks)
                int waitResult = WaitForSingleObject(_initializer.EventHandle, 500);

                if (waitResult == WAIT_TIMEOUT)
                    continue;

                if (waitResult != WAIT_OBJECT_0)
                    break;

                // Drain all pending messages
                while (!cancellationToken.IsCancellationRequested)
                {
                    var msg = new LINEMESSAGE();
                    int result = lineGetMessage(_initializer.LineAppHandle, ref msg, 0); // Non-blocking

                    if (result != LINEERR_OK)
                        break; // No more messages

                    ProcessMessage(msg);
                }
            }

            LogManager.Log("TAPI Nachrichtenschleife beendet");
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
                LogManager.Log("TAPI: LINE_CLOSE empfangen für {0} ({1})", line.Extension, line.LineName);
                line.Handle = IntPtr.Zero;
                _linesByHandle.TryRemove(hLine, out _);
                SafeInvokeEvent(LineDisconnected, line);
            }
            else
            {
                LogManager.Log("TAPI: LINE_CLOSE empfangen für unbekanntes Handle 0x{0:X}", hLine.ToInt64());
            }

            // Check if all lines are now disconnected
            if (!_lines.Values.Any(l => l.IsConnected))
            {
                LogManager.Log("TAPI: Alle Leitungen getrennt");
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
                EventHelper.SafeInvoke(CallStateChanged, callEvent, "TapiLineMonitor.CallStateChanged");
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

                        // Extract caller/called info using shared helper
                        callEvent.CallerNumber = ExtractField(pCallInfo, info.dwCallerIDOffset, info.dwCallerIDSize);
                        callEvent.CallerName = ExtractField(pCallInfo, info.dwCallerIDNameOffset, info.dwCallerIDNameSize);
                        callEvent.CalledNumber = ExtractField(pCallInfo, info.dwCalledIDOffset, info.dwCalledIDSize);
                        callEvent.CalledName = ExtractField(pCallInfo, info.dwCalledIDNameOffset, info.dwCalledIDNameSize);

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
        /// Extract a string field from a TAPI buffer if offset and size are valid.
        /// </summary>
        private string ExtractField(IntPtr basePtr, int offset, int size)
        {
            if (offset <= 0 || size <= 0) return null;
            return ReadStringFromBuffer(basePtr, offset, size);
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

        public void Dispose()
        {
            if (_disposed)
                return;

            // Set disposing flag first to prevent callbacks from firing
            _disposing = true;
            _disposed = true;
            GC.SuppressFinalize(this);

            _initializer.Shutdown(_lines, _linesByHandle);
        }
    }
}
