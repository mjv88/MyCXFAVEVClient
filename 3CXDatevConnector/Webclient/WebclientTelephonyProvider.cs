using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core;
using DatevConnector.Core.Config;
using DatevConnector.Datev.Managers;
using DatevConnector.Tapi;
using static DatevConnector.Interop.TapiInterop;

namespace DatevConnector.Webclient
{
    /// <summary>
    /// Telephony provider for 3CX Webclient via browser extension.
    /// Call events arrive from the extension through a WebSocket connection;
    /// commands (DIAL, DROP) are sent back to the extension.
    ///
    /// The bridge listens on ws://127.0.0.1:{port} (default 19800).
    /// The browser extension connects directly — no relay process needed.
    /// </summary>
    public class WebclientTelephonyProvider : ITelephonyProvider
    {
        // Sentinel handle for the virtual line
        private static readonly IntPtr WebclientConnectedHandle = new IntPtr(-3);

        private string _extension;
        private readonly int _connectTimeoutSec;
        private readonly int _wsPort;
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

        // IPC — WebSocket
        private WebSocketBridgeServer _wsServer;

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
            _wsPort = AppConfig.GetInt(ConfigKeys.WebclientWebSocketPort, 19800);
        }

        /// <summary>
        /// Start the Webclient provider. Creates a WebSocket server and waits for
        /// the browser extension to connect.
        /// Blocks until cancelled or disconnected.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return StartAsync(cancellationToken, null);
        }

        public async Task StartAsync(CancellationToken cancellationToken, Action<string> progressText)
        {
            LogManager.Log("WebClient: Starting for extension {0}", _extension);
            progressText?.Invoke("WebClient-Modus: Warte auf Browser-Erweiterung...");

            InitVirtualLine();

            await StartWebSocketAsync(cancellationToken, progressText);

            // Final cleanup
            _connected = false;
            if (_virtualLine != null && _virtualLine.IsConnected)
            {
                _virtualLine.Handle = IntPtr.Zero;
                LineDisconnected?.Invoke(_virtualLine);
            }
            Disconnected?.Invoke();
        }

        private async Task StartWebSocketAsync(CancellationToken cancellationToken, Action<string> progressText)
        {
            // If TryConnectAsync left an active connection, wait for it to end first
            // before entering the accept loop (avoids port conflict)
            if (_wsServer != null && _wsServer.IsConnected)
            {
                LogManager.Debug("WebClient Connector: Continuing existing WebSocket connection");
                var disconnectTcs = new TaskCompletionSource<bool>();
                _wsServer.Disconnected += () => disconnectTcs.TrySetResult(true);

                using (cancellationToken.Register(() => disconnectTcs.TrySetCanceled()))
                {
                    try { await disconnectTcs.Task; }
                    catch (TaskCanceledException) { return; }
                }

                CleanupConnection();

                if (cancellationToken.IsCancellationRequested || _disposed)
                    return;
                await Task.Delay(1000, cancellationToken);
            }
            else
            {
                // Clean up any leftover non-connected server (e.g. failed TryConnect)
                CleanupConnection();
            }

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    progressText?.Invoke("WebClient: Warte auf Erweiterung (WebSocket)...");

                    _wsServer = new WebSocketBridgeServer(_wsPort);
                    WireWebSocketEvents(progressText);

                    // RunAsync blocks: accepts connections in a loop, handles reconnect
                    await _wsServer.RunAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogManager.Log("WebClient Connector: WebSocket error - {0}", ex.Message);
                    progressText?.Invoke("WebClient: Fehler - " + ex.Message);
                }
                finally
                {
                    CleanupConnection();
                }

                if (!cancellationToken.IsCancellationRequested && !_disposed)
                    await Task.Delay(1000, cancellationToken);
            }
        }

        private void WireWebSocketEvents(Action<string> progressText)
        {
            _wsServer.CallEventReceived += OnExtensionCallEvent;
            _wsServer.HelloReceived += (ext) =>
            {
                OnHelloReceived(ext, progressText);
                _wsServer.SendHelloAck("1.0", _extension);
            };
            _wsServer.Disconnected += () =>
            {
                OnTransportDisconnected(progressText);
            };
        }

        private void OnHelloReceived(string ext, Action<string> progressText)
        {
            _connected = true;
            _virtualLine.Handle = WebclientConnectedHandle;

            if (!string.IsNullOrEmpty(ext) && string.IsNullOrEmpty(_extension))
            {
                _extension = ext;
                _virtualLine.Extension = ext;
                _virtualLine.LineName = "3CX WebClient: " + ext;
                LogManager.Log("WebClient Extension Detection: {0}", ext);
            }

            LogManager.Debug("WebClient Connector: Handshake complete (extension={0})", ext);
            progressText?.Invoke("WebClient: Verbunden (" + (ext ?? _extension) + ")");

            LineConnected?.Invoke(_virtualLine);
            Connected?.Invoke();
        }

        private void OnTransportDisconnected(Action<string> progressText)
        {
            _connected = false;
            _virtualLine.Handle = IntPtr.Zero;
            _activeCalls.Clear();
            _lastCallState.Clear();

            LogManager.Log("WebClient Connector: Extension disconnected");
            progressText?.Invoke("WebClient: Erweiterung getrennt");

            LineDisconnected?.Invoke(_virtualLine);
            Disconnected?.Invoke();
        }

        /// <summary>
        /// Attempt to connect with a timeout (used by auto-detection).
        /// Returns true if extension connects within the timeout.
        /// </summary>
        public async Task<bool> TryConnectAsync(CancellationToken cancellationToken, int timeoutSec)
        {
            InitVirtualLine();

            try
            {
                _wsServer = new WebSocketBridgeServer(_wsPort);
                WireWebSocketEvents(null);

                bool ok = await _wsServer.TryAcceptAsync(cancellationToken, timeoutSec);
                if (ok)
                {
                    LogManager.Log("WebClient Connection succeeded via WS");
                    return true;
                }

                LogManager.Log("WebClient Connector: TryConnect failed (no HELLO within timeout)");
                CleanupConnection();
                return false;
            }
            catch (OperationCanceledException)
            {
                CleanupConnection();
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Log("WebClient Connector: TryConnect error - {0}", ex.Message);
                CleanupConnection();
                return false;
            }
        }

        /// <summary>
        /// Continue running after TryConnectAsync succeeded.
        /// Blocks until disconnection, then reconnects.
        /// </summary>
        public async Task RunAfterConnectAsync(CancellationToken cancellationToken)
        {
            if (_wsServer == null || !_connected) return;

            var tcs = new TaskCompletionSource<bool>();
            _wsServer.Disconnected += () => tcs.TrySetResult(true);

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                try { await tcs.Task; } catch (TaskCanceledException) { }
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
        /// Maps webclient states to TAPI LINECALLSTATE constants so ConnectorService
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
                LogManager.Debug("WebClient Connector: Duplicate state {0} for call {1}, ignoring", state, callId);
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
                LogManager.Log("WebClient Connector: Unknown call state '{0}' for call {1}", state, callId);
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

            LogManager.Log("WebClient Connector: {0} callId={1} caller={2} called={3} (mapped from '{4}')",
                callEvent.CallStateString, callId,
                LogManager.Mask(callEvent.CallerNumber) ?? "-", LogManager.Mask(callEvent.CalledNumber) ?? "-", state);

            EventHelper.SafeInvoke(CallStateChanged, callEvent, "WebclientTelephonyProvider.CallStateChanged");

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
            if (!_connected)
            {
                LogManager.Log("WebClient Connector: MakeCall failed - not connected");
                return -1;
            }

            bool sent = _wsServer != null && _wsServer.SendDial(destination);

            if (sent)
            {
                LogManager.Log("DIAL sent for {0}", LogManager.Mask(destination));
                return 1;
            }

            LogManager.Log("WebClient Connector: MakeCall failed - send error");
            return -1;
        }

        /// <summary>
        /// Send DROP command to the browser extension.
        /// In webclient mode, hCall is not meaningful — drop the most recent active call.
        /// Returns 1 on success, -1 on failure.
        /// </summary>
        public int DropCall(IntPtr hCall)
        {
            if (!_connected)
                return -1;

            // Find last active call
            var lastCall = _activeCalls
                .Where(kvp => kvp.Value.CallState != LINECALLSTATE_DISCONNECTED && kvp.Value.CallState != LINECALLSTATE_IDLE)
                .Select(kvp => kvp.Key)
                .LastOrDefault();

            bool sent = _wsServer != null && _wsServer.SendDrop(lastCall);
            if (sent)
            {
                LogManager.Log("WebClient Connector: DROP sent (callId={0})", lastCall ?? "(all)");
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
            LogManager.Log("WebClient Connector: ReconnectLine - WebSocket server handles reconnect automatically");
            return _connected;
        }

        public void ReconnectAllLines(Action<string> progressText = null)
        {
            LogManager.Log("WebClient Connector: ReconnectAllLines - WebSocket server handles reconnect automatically");
        }

        public bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3)
        {
            bool ok = _connected && _wsServer != null && _wsServer.IsConnected;
            progressText?.Invoke(ok
                ? string.Format("WebClient ({0}): Browser-Erweiterung verbunden", _extension)
                : string.Format("WebClient ({0}): Warte auf Browser-Erweiterung", _extension));
            return ok;
        }

        // ===== Helpers =====

        private void InitVirtualLine()
        {
            _virtualLine = new TapiLineInfo
            {
                DeviceId = 0,
                LineName = "3CX WebClient: " + _extension,
                Extension = _extension,
                Handle = IntPtr.Zero
            };
            _lines.Clear();
            _lines.Add(_virtualLine);
        }

        private void CleanupConnection()
        {
            _connected = false;
            _activeCalls.Clear();
            _lastCallState.Clear();

            if (_wsServer != null)
            {
                try { _wsServer.Dispose(); } catch { }
                _wsServer = null;
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
