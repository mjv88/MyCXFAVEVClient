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
    public class TapiLineInfo
    {
        /// <summary>
        /// Lock for compound check-then-act operations on Handle.
        /// Prevents races between the TAPI message loop thread (HandleLineClose),
        /// OpenSingleLine, and ReconnectLine/ReconnectAllLines.
        /// </summary>
        public readonly object SyncLock = new object();

        public int DeviceId { get; set; }
        public string LineName { get; set; }
        public string Extension { get; set; }
        public IntPtr Handle { get; set; }
        public int ApiVersion { get; set; }
        public bool IsConnected => Handle != IntPtr.Zero;

        // Format: "100 : Name" -> "100"
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

        /// <summary>
        /// Create a copy with selectively updated fields.
        /// Used by TapiLineMonitor to atomically replace entries in the ConcurrentDictionary,
        /// preventing cross-thread mutation of shared TapiCallEvent instances.
        /// </summary>
        public TapiCallEvent WithUpdate(
            int? callState = null,
            int? callId = null,
            int? origin = null,
            int? addressId = null,
            IntPtr? lineHandle = null,
            bool? initialStateLogged = null,
            string callerNumber = null,
            string callerName = null,
            string calledNumber = null,
            string calledName = null,
            string extension = null)
        {
            return new TapiCallEvent
            {
                CallHandle = this.CallHandle,
                LineHandle = lineHandle ?? this.LineHandle,
                CallState = callState ?? this.CallState,
                CallId = callId ?? this.CallId,
                Origin = origin ?? this.Origin,
                AddressId = addressId ?? this.AddressId,
                InitialStateLogged = initialStateLogged ?? this.InitialStateLogged,
                CallerNumber = callerNumber ?? this.CallerNumber,
                CallerName = callerName ?? this.CallerName,
                CalledNumber = calledNumber ?? this.CalledNumber,
                CalledName = calledName ?? this.CalledName,
                Extension = extension ?? this.Extension
            };
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

        public event Action<TapiCallEvent> CallStateChanged;
        public event Action<TapiLineInfo> LineConnected;
        public event Action<TapiLineInfo> LineDisconnected;
        public event Action Connected;
        public event Action Disconnected;

        public bool IsMonitoring => _lines.Values.Any(l => l.IsConnected);
        public int ConnectedLineCount => _lines.Values.Count(l => l.IsConnected);
        public IReadOnlyList<TapiLineInfo> Lines => _lines.Values.ToList().AsReadOnly();
        public string LineName => _lines.Values.FirstOrDefault(l => l.IsConnected)?.LineName;
        public string Extension => _lines.Values.FirstOrDefault(l => l.IsConnected)?.Extension;

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

        private bool SafeInvokeEvent<T>(Action<T> handler, T arg)
        {
            if (_disposing || _disposed)
                return false;
            return EventHelper.SafeInvoke(handler, arg, "TapiLineMonitor");
        }

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

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await StartAsync(cancellationToken, null);
        }

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
                    LogManager.Log("Keine 3CX TAPI Leitung gefunden - warte auf LINE_CREATE...");
                    progressText?.Invoke("Warte auf 3CX Verbindung...");
                    // Fall through to message loop — LINE_CREATE will notify us
                }
            }

            if (_lines.Count > 0)
            {
                _lineManager.OpenAllLines(progressText);

                int connectedCount = _lines.Values.Count(l => l.IsConnected);
                if (connectedCount == 0)
                {
                    LogManager.Log("TAPI Leitungen gefunden aber nicht registriert - warte auf 3CX...");
                    progressText?.Invoke("Warte auf 3CX Desktop App...");
                    // Fall through to message loop — LINE_CREATE will notify us
                }
                else if (connectedCount == 1)
                {
                    var line = _lines.Values.First(l => l.IsConnected);
                    progressText?.Invoke($"3CX TAPI verbunden (Nst: {line.Extension})");
                    SafeInvokeEvent(Connected);
                }
                else
                {
                    var extensions = string.Join(", ", _lines.Values.Where(l => l.IsConnected).Select(l => l.Extension));
                    progressText?.Invoke($"{connectedCount} Leitungen verbunden (Nst: {extensions})");
                    SafeInvokeEvent(Connected);
                }
            }

            await Task.Run(() => MessageLoop(cancellationToken), cancellationToken);
        }

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

                case LINE_CREATE:
                    HandleLineCreate((int)msg.dwParam1.ToInt64());
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

        private void HandleLineCreate(int deviceId)
        {
            LogManager.Log("TAPI: LINE_CREATE empfangen für Gerät {0}", deviceId);

            var lineInfo = _initializer.TryAddDevice(deviceId, _lines);
            if (lineInfo == null)
            {
                LogManager.Debug("TAPI: Gerät {0} passt nicht zum Filter - ignoriert", deviceId);
                return;
            }

            LogManager.Log("TAPI: Neue 3CX Leitung erkannt: {0} (Nst: {1})", lineInfo.LineName, lineInfo.Extension);

            if (_lineManager.OpenSingleLine(lineInfo))
            {
                bool wasDisconnected = !_lines.Values.Any(l => l.IsConnected && l.DeviceId != deviceId);
                if (wasDisconnected)
                {
                    LogManager.Log("TAPI: Erste Leitung verbunden - Connected");
                    SafeInvokeEvent(Connected);
                }
                SafeInvokeEvent(LineConnected, lineInfo);
            }
        }

        private void HandleLineClose(IntPtr hLine)
        {
            TapiLineInfo line;
            if (_linesByHandle.TryGetValue(hLine, out line))
            {
                LogManager.Log("TAPI: LINE_CLOSE empfangen für {0} ({1})", line.Extension, line.LineName);
                lock (line.SyncLock)
                {
                    line.Handle = IntPtr.Zero;
                }
                _linesByHandle.TryRemove(hLine, out _);
                SafeInvokeEvent(LineDisconnected, line);
            }
            else
            {
                LogManager.Log("TAPI: LINE_CLOSE empfangen für unbekanntes Handle 0x{0:X}", hLine.ToInt64());
            }

            if (!_lines.Values.Any(l => l.IsConnected))
            {
                LogManager.Log("TAPI: Alle Leitungen getrennt");
                SafeInvokeEvent(Disconnected);
            }
        }

        private void HandleNewCall(IntPtr hLine, int addressId, IntPtr hCall)
        {
            var callEvent = GetOrCreateCallEvent(hCall);

            TapiLineInfo line;
            string ext = null;
            if (_linesByHandle.TryGetValue(hLine, out line))
            {
                ext = line.Extension;
                LogManager.Debug("TAPI: New call handle 0x{0:X} on line {1} address {2}",
                    hCall.ToInt64(), line.Extension, addressId);
            }
            else
            {
                LogManager.Debug("TAPI: New call handle 0x{0:X} on address {1}", hCall.ToInt64(), addressId);
            }

            var updated = callEvent.WithUpdate(addressId: addressId, lineHandle: hLine, extension: ext);
            _activeCalls[hCall] = updated;
        }

        private void HandleCallState(IntPtr hCall, int callState, IntPtr param2, IntPtr param3)
        {
            var callEvent = GetOrCreateCallEvent(hCall);

            // Read call info from TAPI into a temporary scratch object, then
            // merge everything into a new immutable snapshot via WithUpdate.
            var scratch = new TapiCallEvent { CallHandle = hCall };
            GetCallInfo(hCall, scratch);

            bool markInitial = !callEvent.InitialStateLogged &&
                               (callState == LINECALLSTATE_OFFERING || callState == LINECALLSTATE_DIALING);

            var updated = callEvent.WithUpdate(
                callState: callState,
                callId: scratch.CallId != 0 ? scratch.CallId : (int?)null,
                origin: scratch.Origin != 0 ? scratch.Origin : (int?)null,
                initialStateLogged: markInitial ? true : (bool?)null,
                callerNumber: scratch.CallerNumber,
                callerName: scratch.CallerName,
                calledNumber: scratch.CalledNumber,
                calledName: scratch.CalledName);

            _activeCalls[hCall] = updated;

            string direction = updated.IsIncoming ? "inbound" : "outbound";
            string caller = updated.CallerNumber ?? "?";
            string called = updated.CalledNumber ?? "?";

            if (callState == LINECALLSTATE_IDLE)
            {
                // IDLE is a transient handle setup state - debug only
                LogManager.Debug("TAPI: Call 0x{0:X} state=IDLE direction={1} caller={2} called={3}",
                    hCall.ToInt64(), direction, caller, called);
            }
            else if (markInitial)
            {
                // First real call state - concise summary
                LogManager.Log("TAPI: New call on line {0}", updated.AddressId);
                LogManager.Log("TAPI: Call direction={0} caller={1} called={2}", direction, LogManager.Mask(caller), called);
            }
            else
            {
                LogManager.Log("TAPI: Call state={0} direction={1} caller={2} called={3}",
                    updated.CallStateString, direction, LogManager.Mask(caller), called);
            }

            if (!_disposing && !_disposed)
            {
                EventHelper.SafeInvoke(CallStateChanged, updated, "TapiLineMonitor.CallStateChanged");
            }

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

        private void HandleReply(int requestId, int result)
        {
            if (result != LINEERR_OK)
            {
                LogManager.Log("TAPI: LINE_REPLY request={0} error=0x{1:X8}", requestId, result);
            }
        }

        private TapiCallEvent GetOrCreateCallEvent(IntPtr hCall)
        {
            return _activeCalls.GetOrAdd(hCall, h => new TapiCallEvent { CallHandle = h });
        }

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

                        int usedSize = info.dwUsedSize > 0 ? info.dwUsedSize : bufferSize;
                        callEvent.CallerNumber = ExtractField(pCallInfo, info.dwCallerIDOffset, info.dwCallerIDSize, usedSize);
                        callEvent.CallerName = ExtractField(pCallInfo, info.dwCallerIDNameOffset, info.dwCallerIDNameSize, usedSize);
                        callEvent.CalledNumber = ExtractField(pCallInfo, info.dwCalledIDOffset, info.dwCalledIDSize, usedSize);
                        callEvent.CalledName = ExtractField(pCallInfo, info.dwCalledIDNameOffset, info.dwCalledIDNameSize, usedSize);

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

        private string ExtractField(IntPtr basePtr, int offset, int size, int bufferSize)
        {
            if (offset <= 0 || size <= 0) return null;
            return ReadStringFromBuffer(basePtr, offset, size, bufferSize);
        }

        private string ReadStringFromBuffer(IntPtr basePtr, int offset, int size, int bufferSize)
        {
            if (offset < 0 || size < 0 || offset + size > bufferSize)
                return null;

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

            // Prevent callbacks from firing during teardown
            _disposing = true;
            _disposed = true;
            GC.SuppressFinalize(this);

            _initializer.Shutdown(_lines, _linesByHandle);
        }
    }
}
