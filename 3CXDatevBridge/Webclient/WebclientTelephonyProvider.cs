using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DatevBridge.Core;
using DatevBridge.Core.Config;
using DatevBridge.Datev.Managers;
using DatevBridge.Tapi;
using static DatevBridge.Interop.TapiInterop;

namespace DatevBridge.Webclient
{
    /// <summary>
    /// Telephony provider for 3CX Webclient via browser extension.
    /// Call events arrive from the extension through a Named Pipe (IPC);
    /// commands (DIAL, DROP) are sent back to the extension.
    ///
    /// IPC mechanism: a per-user Named Pipe that the native messaging host
    /// subprocess connects to. This avoids requiring the bridge process itself
    /// to be the native messaging host (which would conflict with WinForms).
    ///
    /// The bridge creates a pipe server "3CX_DATEV_Webclient_{extension}_{sessionId}".
    /// The native messaging host (launched by the browser) connects as client and
    /// relays messages bidirectionally.
    ///
    /// For RDS/multi-session: the pipe name includes the session ID,
    /// preventing cross-session call mixing.
    /// </summary>
    public class WebclientTelephonyProvider : ITelephonyProvider
    {
        // Sentinel handle for the virtual line
        private static readonly IntPtr WebclientConnectedHandle = new IntPtr(-3);

        private string _extension;
        private readonly int _connectTimeoutSec;
        private volatile bool _disposed;
        private volatile bool _connected;

        // Virtual line representing the webclient connection
        private TapiLineInfo _virtualLine;
        private readonly List<TapiLineInfo> _lines = new List<TapiLineInfo>();

        // Active calls (keyed by extension call ID string)
        private readonly ConcurrentDictionary<string, TapiCallEvent> _activeCalls =
            new ConcurrentDictionary<string, TapiCallEvent>(StringComparer.OrdinalIgnoreCase);

        // Debounce: track last state per call to ignore duplicate events
        private readonly ConcurrentDictionary<string, string> _lastCallState =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // IPC
        private NativeMessagingHost _host;
        private NamedPipeServerStream _pipeServer;

        // ===== ITelephonyProvider Events =====
        public event Action<TapiCallEvent> CallStateChanged;
        public event Action<TapiLineInfo> LineConnected;
        public event Action<TapiLineInfo> LineDisconnected;
        public event Action Connected;
        public event Action Disconnected;

        // ===== Properties =====
        public bool IsMonitoring => _connected;
        public int ConnectedLineCount => _connected ? 1 : 0;
        public string LineName => _virtualLine?.LineName;
        public string Extension => _extension;
        public IReadOnlyList<TapiLineInfo> Lines => _lines.AsReadOnly();

        public WebclientTelephonyProvider(string extension)
        {
            _extension = extension ?? throw new ArgumentNullException(nameof(extension));
            _connectTimeoutSec = AppConfig.GetInt(ConfigKeys.WebclientConnectTimeoutSec, 8);
        }

        /// <summary>
        /// Start the Webclient provider. Creates a Named Pipe server and waits for
        /// the browser extension's native messaging host to connect.
        /// Blocks until cancelled or disconnected.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return StartAsync(cancellationToken, null);
        }

        public async Task StartAsync(CancellationToken cancellationToken, Action<string> progressText)
        {
            LogManager.Log("WebclientTelephonyProvider: Starting for extension {0}", _extension);
            progressText?.Invoke("Webclient-Modus: Warte auf Browser-Erweiterung...");

            // Set up virtual line
            _virtualLine = new TapiLineInfo
            {
                DeviceId = 0,
                LineName = "3CX Webclient: " + _extension,
                Extension = _extension,
                Handle = IntPtr.Zero
            };
            _lines.Clear();
            _lines.Add(_virtualLine);

            string pipeName = GetPipeName();
            LogManager.Log("WebclientTelephonyProvider: Pipe name = {0}", pipeName);

            // Accept connections in a loop (reconnect on disconnect)
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    progressText?.Invoke("Webclient: Warte auf Erweiterung...");

                    // Create a new pipe server for each connection cycle
                    _pipeServer = CreatePipeServer(pipeName);

                    // Wait for client connection (browser native messaging host)
                    LogManager.Log("WebclientTelephonyProvider: Waiting for extension to connect on pipe {0}", pipeName);
                    await Task.Factory.FromAsync(
                        _pipeServer.BeginWaitForConnection,
                        _pipeServer.EndWaitForConnection,
                        null);

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    LogManager.Log("WebclientTelephonyProvider: Extension connected on pipe");
                    progressText?.Invoke("Webclient: Erweiterung verbunden, warte auf HELLO...");

                    // Create host for this connection
                    _host = new NativeMessagingHost(_pipeServer, _pipeServer);
                    _host.CallEventReceived += OnExtensionCallEvent;
                    _host.HelloReceived += (ext) =>
                    {
                        _connected = true;
                        _virtualLine.Handle = WebclientConnectedHandle;

                        // Adopt extension from browser if bridge was started without one
                        if (!string.IsNullOrEmpty(ext) && string.IsNullOrEmpty(_extension))
                        {
                            _extension = ext;
                            _virtualLine.Extension = ext;
                            _virtualLine.LineName = "3CX Webclient: " + ext;
                            LogManager.Log("WebclientTelephonyProvider: Extension adopted from browser HELLO: {0}", ext);
                        }

                        // Send HELLO_ACK
                        _host.SendHelloAck("1.0", _extension);

                        LogManager.Log("WebclientTelephonyProvider: Handshake complete (extension={0})", ext);
                        progressText?.Invoke("Webclient: Verbunden (" + (ext ?? _extension) + ")");

                        LineConnected?.Invoke(_virtualLine);
                        Connected?.Invoke();
                    };
                    _host.Disconnected += () =>
                    {
                        _connected = false;
                        _virtualLine.Handle = IntPtr.Zero;
                        _activeCalls.Clear();
                        _lastCallState.Clear();

                        LogManager.Log("WebclientTelephonyProvider: Extension disconnected");
                        progressText?.Invoke("Webclient: Erweiterung getrennt");

                        LineDisconnected?.Invoke(_virtualLine);
                        // Don't fire Disconnected — the loop will accept the next connection
                    };

                    // Run the host read loop (blocks until disconnect)
                    await _host.RunAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogManager.Log("WebclientTelephonyProvider: Error - {0}", ex.Message);
                    progressText?.Invoke("Webclient: Fehler - " + ex.Message);
                }
                finally
                {
                    CleanupConnection();
                }

                // Brief delay before accepting next connection
                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }

            // Final cleanup
            _connected = false;
            if (_virtualLine != null && _virtualLine.IsConnected)
            {
                _virtualLine.Handle = IntPtr.Zero;
                LineDisconnected?.Invoke(_virtualLine);
            }
            Disconnected?.Invoke();
        }

        /// <summary>
        /// Attempt to connect with a timeout (used by auto-detection).
        /// Returns true if extension connects within the timeout.
        /// </summary>
        public async Task<bool> TryConnectAsync(CancellationToken cancellationToken, int timeoutSec)
        {
            string pipeName = GetPipeName();

            // Set up virtual line
            _virtualLine = new TapiLineInfo
            {
                DeviceId = 0,
                LineName = "3CX Webclient: " + _extension,
                Extension = _extension,
                Handle = IntPtr.Zero
            };
            _lines.Clear();
            _lines.Add(_virtualLine);

            try
            {
                _pipeServer = CreatePipeServer(pipeName);
                LogManager.Log("WebclientTelephonyProvider: TryConnect on pipe {0} (timeout={1}s)", pipeName, timeoutSec);

                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                    var connectTask = Task.Factory.FromAsync(
                        _pipeServer.BeginWaitForConnection,
                        _pipeServer.EndWaitForConnection,
                        null);

                    var completedTask = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));

                    if (completedTask != connectTask || timeoutCts.IsCancellationRequested)
                    {
                        // Timeout or cancelled
                        LogManager.Log("WebclientTelephonyProvider: TryConnect timed out");
                        CleanupConnection();
                        return false;
                    }

                    await connectTask; // Get any exceptions

                    // Client connected — wait for HELLO
                    _host = new NativeMessagingHost(_pipeServer, _pipeServer);
                    var helloTcs = new TaskCompletionSource<bool>();

                    _host.HelloReceived += (ext) =>
                    {
                        _connected = true;
                        _virtualLine.Handle = WebclientConnectedHandle;

                        // Adopt extension from browser if bridge was started without one
                        if (!string.IsNullOrEmpty(ext) && string.IsNullOrEmpty(_extension))
                        {
                            _extension = ext;
                            _virtualLine.Extension = ext;
                            _virtualLine.LineName = "3CX Webclient: " + ext;
                            LogManager.Log("WebclientTelephonyProvider: Extension adopted from browser HELLO: {0}", ext);
                        }

                        _host.SendHelloAck("1.0", _extension);
                        helloTcs.TrySetResult(true);
                    };
                    _host.Disconnected += () =>
                    {
                        helloTcs.TrySetResult(false);
                    };

                    // Start reading in background
                    var readTask = _host.RunAsync(timeoutCts.Token);

                    // Wait for HELLO or timeout
                    var helloResult = await Task.WhenAny(helloTcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec), cancellationToken));
                    if (helloResult == helloTcs.Task && helloTcs.Task.Result)
                    {
                        LogManager.Log("WebclientTelephonyProvider: TryConnect succeeded (HELLO received)");
                        // Wire up events for ongoing use
                        _host.CallEventReceived += OnExtensionCallEvent;
                        _host.Disconnected += () =>
                        {
                            _connected = false;
                            _virtualLine.Handle = IntPtr.Zero;
                            _activeCalls.Clear();
                            _lastCallState.Clear();
                            LineDisconnected?.Invoke(_virtualLine);
                        };

                        LineConnected?.Invoke(_virtualLine);
                        Connected?.Invoke();
                        return true;
                    }

                    LogManager.Log("WebclientTelephonyProvider: TryConnect failed (no HELLO)");
                    CleanupConnection();
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                CleanupConnection();
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Log("WebclientTelephonyProvider: TryConnect error - {0}", ex.Message);
                CleanupConnection();
                return false;
            }
        }

        /// <summary>
        /// Continue running after TryConnectAsync succeeded.
        /// Blocks until disconnection.
        /// </summary>
        public async Task RunAfterConnectAsync(CancellationToken cancellationToken)
        {
            if (_host == null || !_connected)
                return;

            // The read loop from TryConnectAsync is still running.
            // Wait for disconnection or cancellation.
            var tcs = new TaskCompletionSource<bool>();
            _host.Disconnected += () => tcs.TrySetResult(true);

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    await tcs.Task;
                }
                catch (TaskCanceledException) { }
            }

            // Reconnect loop
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                CleanupConnection();
                await Task.Delay(1000, cancellationToken);
                await StartAsync(cancellationToken);
            }
        }

        // ===== Call Event Handling =====

        /// <summary>
        /// Map extension CALL_EVENT messages to TapiCallEvent and fire CallStateChanged.
        /// Maps webclient states to TAPI LINECALLSTATE constants so BridgeService
        /// can process them identically to TAPI/Pipe events.
        /// </summary>
        private void OnExtensionCallEvent(ExtensionMessage msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.CallId))
                return;

            string callId = msg.CallId;
            string state = msg.State;

            // Debounce: ignore if same state as last event for this call
            string lastState;
            if (_lastCallState.TryGetValue(callId, out lastState) &&
                string.Equals(lastState, state, StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Debug("WebclientTelephonyProvider: Duplicate state {0} for call {1}, ignoring", state, callId);
                return;
            }
            _lastCallState[callId] = state;

            // Map webclient state to TAPI LINECALLSTATE
            int tapiState;
            bool isInbound = string.Equals(msg.Direction, Protocol.DirectionInbound, StringComparison.OrdinalIgnoreCase);

            if (string.Equals(state, Protocol.StateOffered, StringComparison.OrdinalIgnoreCase))
            {
                tapiState = LINECALLSTATE_OFFERING;
            }
            else if (string.Equals(state, Protocol.StateDialing, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(state, Protocol.StateRinging, StringComparison.OrdinalIgnoreCase))
            {
                // Outbound: dialing/ringing = RINGBACK. Inbound ringing = OFFERING.
                tapiState = isInbound ? LINECALLSTATE_OFFERING : LINECALLSTATE_RINGBACK;
            }
            else if (string.Equals(state, Protocol.StateConnected, StringComparison.OrdinalIgnoreCase))
            {
                tapiState = LINECALLSTATE_CONNECTED;
            }
            else if (string.Equals(state, Protocol.StateEnded, StringComparison.OrdinalIgnoreCase))
            {
                tapiState = LINECALLSTATE_DISCONNECTED;
            }
            else
            {
                LogManager.Log("WebclientTelephonyProvider: Unknown call state '{0}' for call {1}", state, callId);
                return;
            }

            // Get or create TapiCallEvent for this call
            int numericCallId = Math.Abs(callId.GetHashCode());
            var callEvent = _activeCalls.GetOrAdd(callId, _ => new TapiCallEvent
            {
                CallHandle = IntPtr.Zero,
                LineHandle = IntPtr.Zero,
                CallId = numericCallId,
                Extension = _extension,
                Origin = isInbound ? LINECALLORIGIN_INBOUND : LINECALLORIGIN_OUTBOUND
            });

            // Update call data
            callEvent.CallState = tapiState;
            if (!string.IsNullOrEmpty(msg.RemoteNumber))
            {
                if (isInbound)
                {
                    callEvent.CallerNumber = msg.RemoteNumber;
                    callEvent.CallerName = msg.RemoteName;
                    callEvent.CalledNumber = _extension;
                }
                else
                {
                    callEvent.CalledNumber = msg.RemoteNumber;
                    callEvent.CalledName = msg.RemoteName;
                    callEvent.CallerNumber = _extension;
                }
            }

            LogManager.Log("WebclientTelephonyProvider: {0} callId={1} caller={2} called={3} (mapped from '{4}')",
                callEvent.CallStateString, callId,
                callEvent.CallerNumber ?? "-", callEvent.CalledNumber ?? "-", state);

            try
            {
                CallStateChanged?.Invoke(callEvent);
            }
            catch (Exception ex)
            {
                LogManager.Log("WebclientTelephonyProvider: Error in CallStateChanged handler - {0}", ex.Message);
            }

            // Clean up ended calls
            if (tapiState == LINECALLSTATE_DISCONNECTED)
            {
                TapiCallEvent removed;
                _activeCalls.TryRemove(callId, out removed);
                string removedState;
                _lastCallState.TryRemove(callId, out removedState);
            }
        }

        // ===== Call Control =====

        /// <summary>
        /// Send DIAL command to the browser extension.
        /// Returns 1 on success, -1 on failure.
        /// </summary>
        public int MakeCall(string destination)
        {
            if (!_connected || _host == null)
            {
                LogManager.Log("WebclientTelephonyProvider: MakeCall failed - not connected");
                return -1;
            }

            bool sent = _host.SendDial(destination);
            if (sent)
            {
                LogManager.Log("WebclientTelephonyProvider: DIAL sent for {0}", destination);
                return 1;
            }

            LogManager.Log("WebclientTelephonyProvider: MakeCall failed - send error");
            return -1;
        }

        /// <summary>
        /// Send DROP command to the browser extension.
        /// In webclient mode, hCall is not meaningful — drop the most recent active call.
        /// Returns 1 on success, -1 on failure.
        /// </summary>
        public int DropCall(IntPtr hCall)
        {
            if (!_connected || _host == null)
                return -1;

            // Find last active call
            var lastCall = _activeCalls
                .Where(kvp => kvp.Value.CallState != LINECALLSTATE_DISCONNECTED && kvp.Value.CallState != LINECALLSTATE_IDLE)
                .Select(kvp => kvp.Key)
                .LastOrDefault();

            bool sent = _host.SendDrop(lastCall);
            if (sent)
            {
                LogManager.Log("WebclientTelephonyProvider: DROP sent (callId={0})", lastCall ?? "(all)");
                return 1;
            }

            return -1;
        }

        public TapiCallEvent FindCallById(int callId)
        {
            return _activeCalls.Values.FirstOrDefault(c => c.CallId == callId);
        }

        // ===== Diagnostics =====

        public bool ReconnectLine(string extension, Action<string> progressText = null)
        {
            LogManager.Log("WebclientTelephonyProvider: ReconnectLine - pipe server handles reconnect automatically");
            return _connected;
        }

        public void ReconnectAllLines(Action<string> progressText = null)
        {
            LogManager.Log("WebclientTelephonyProvider: ReconnectAllLines - pipe server handles reconnect automatically");
        }

        public bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3)
        {
            bool ok = _connected && _host != null && _host.IsConnected;
            progressText?.Invoke(ok
                ? string.Format("Webclient ({0}): Browser-Erweiterung verbunden", _extension)
                : string.Format("Webclient ({0}): Warte auf Browser-Erweiterung", _extension));
            return ok;
        }

        // ===== Helpers =====

        private string GetPipeName()
        {
            int sessionId = SessionManager.SessionId;
            return string.Format("3CX_DATEV_Webclient_{0}", sessionId);
        }

        private static NamedPipeServerStream CreatePipeServer(string pipeName)
        {
            // Set up DACL: current user = full control, block others
            var pipeSecurity = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            // Allow ALL_APPLICATION_PACKAGES (MSIX/AppContainer access)
            try
            {
                var appPackages = new SecurityIdentifier("S-1-15-2-1");
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    appPackages,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }
            catch
            {
                // Ignore if SID not valid on this OS version
            }

            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1, // maxConnections — one extension at a time per session
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096, // inBufferSize
                4096, // outBufferSize
                pipeSecurity);
        }

        private void CleanupConnection()
        {
            _connected = false;
            _activeCalls.Clear();
            _lastCallState.Clear();

            if (_host != null)
            {
                _host.Dispose();
                _host = null;
            }

            if (_pipeServer != null)
            {
                try { _pipeServer.Dispose(); } catch { }
                _pipeServer = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CleanupConnection();

            if (_virtualLine != null)
                _virtualLine.Handle = IntPtr.Zero;
        }
    }
}
