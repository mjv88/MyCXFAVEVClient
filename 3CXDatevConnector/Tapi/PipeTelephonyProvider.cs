using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Core;
using DatevConnector.Datev.Managers;
using static DatevConnector.Interop.TapiInterop;
// Uses EventHelper for consistent event invocation across all providers

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Named pipe telephony provider for terminal server environments.
    /// Creates \\.\pipe\3CX_tsp_server_{extension} as a SERVER and waits for
    /// the 3CX Softphone to connect as client (replacing dialer.exe).
    ///
    /// The 3CX v20 MSIX app has no real TAPI TSP — its "TAPI integration" is
    /// entirely pipe-based. dialer.exe creates the pipe server, the Softphone
    /// connects as client every 2 seconds. We replace dialer.exe.
    /// </summary>
    public class PipeTelephonyProvider : ITelephonyProvider
    {
        // Sentinel handle to mark the virtual line as "connected"
        // (TapiLineInfo.IsConnected checks Handle != IntPtr.Zero)
        private static readonly IntPtr PipeConnectedHandle = new IntPtr(-2);

        private readonly string _extension;
        private TapiPipeServer _server;
        private bool _disposed;
        private volatile bool _connected;
        private int _nextCallId;

        // Virtual line representing the pipe connection
        private TapiLineInfo _virtualLine;
        private readonly List<TapiLineInfo> _lines = new List<TapiLineInfo>();

        // Active call tracking (keyed by string call ID from pipe, mapped to TapiCallEvent)
        private readonly ConcurrentDictionary<string, TapiCallEvent> _activeCalls =
            new ConcurrentDictionary<string, TapiCallEvent>(StringComparer.OrdinalIgnoreCase);

        // ===== Events =====

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

        /// <summary>
        /// Create a pipe telephony provider for the specified extension
        /// </summary>
        public PipeTelephonyProvider(string extension)
        {
            _extension = extension ?? throw new ArgumentNullException(nameof(extension));
        }

        /// <summary>
        /// Start monitoring via named pipe server (blocks until cancelled)
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await StartAsync(cancellationToken, null);
        }

        /// <summary>
        /// Start the pipe server and wait for the 3CX Softphone to connect.
        /// Creates \\.\pipe\3CX_tsp_server_{extension} — the Softphone polls
        /// for this pipe every 2 seconds and connects automatically.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken, Action<string> progressText)
        {
            LogManager.Log("PipeTelephonyProvider: Erstelle Pipe-Server für Nebenstelle {0}", _extension);
            progressText?.Invoke($"Erstelle 3CX Pipe Server (Nst: {_extension})...");

            // Set up virtual line before server starts
            _virtualLine = new TapiLineInfo
            {
                DeviceId = 0,
                LineName = $"3CX Pipe: {_extension}",
                Extension = _extension,
                Handle = IntPtr.Zero // Not connected yet
            };
            _lines.Clear();
            _lines.Add(_virtualLine);

            if (_server != null)
            {
                _server.ClientConnected -= OnServerClientConnected;
                _server.ClientDisconnected -= OnServerClientDisconnected;
                _server.MessageReceived -= OnPipeMessage;
                _server.Dispose();
            }

            _server = new TapiPipeServer(_extension);

            _server.ClientConnected += OnServerClientConnected;
            _server.ClientDisconnected += OnServerClientDisconnected;
            _server.MessageReceived += OnPipeMessage;

            try
            {
                progressText?.Invoke("Warte auf 3CX Softphone...");

                // RunAsync blocks until cancellation — it accepts connections
                // in a loop, handling disconnect/reconnect automatically
                await _server.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogManager.Warning("PipeTelephonyProvider: Server-Fehler - {0}", ex.Message);
                progressText?.Invoke($"Pipe Server Fehler: {ex.Message}");
                throw new InvalidOperationException("Pipe server failed: " + ex.Message, ex);
            }
            finally
            {
                _connected = false;
                SetLineDisconnected();
                Disconnected?.Invoke();
            }
        }

        private void OnServerClientConnected()
        {
            _connected = true;
            _virtualLine.Handle = PipeConnectedHandle;

            LogManager.Log("PipeTelephonyProvider: 3CX Softphone verbunden");

            LineConnected?.Invoke(_virtualLine);
            Connected?.Invoke();
        }

        private void OnServerClientDisconnected()
        {
            _connected = false;
            _virtualLine.Handle = IntPtr.Zero;
            _activeCalls.Clear();

            LogManager.Log("PipeTelephonyProvider: 3CX Softphone getrennt, warte auf Neuverbindung...");

            LineDisconnected?.Invoke(_virtualLine);
            // Don't fire Disconnected — the server loop keeps running
            // and will accept the next connection automatically
        }

        /// <summary>
        /// Process incoming pipe messages from the 3CX Softphone
        /// and translate to TapiCallEvent for ConnectorService.
        ///
        /// Protocol note: Softphone replies echo the original cmd with additional
        /// __answ# and reply fields. Check IsReply first to avoid treating replies
        /// as new call events.
        /// </summary>
        private void OnPipeMessage(TapiMessage msg)
        {
            // Reply messages (e.g. cmd=SRVHELLO,reply=CLIHELLO,__answ#=1)
            // are responses to our commands — log and skip event processing
            if (msg.IsReply)
            {
                string reply = msg.Reply;
                string correlation = msg.AnswerCorrelation;
                string origCmd = msg.Command;

                if (reply.Equals(TapiCommands.ClientHello, StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Log("PipeTelephonyProvider: Handshake abgeschlossen (CLIHELLO empfangen)");
                }
                else
                {
                    LogManager.Log("PipeTelephonyProvider: Antwort empfangen: cmd={0}, reply={1}, __answ#={2}",
                        origCmd, reply, correlation);
                }
                return;
            }

            string cmd = msg.Command;

            if (string.IsNullOrEmpty(cmd))
            {
                LogManager.Debug("PipeTelephonyProvider: Message without command, ignoring");
                return;
            }

            // Map pipe commands to TAPI call states
            if (cmd.Equals(TapiCommands.Ringing, StringComparison.OrdinalIgnoreCase))
            {
                HandleCallState(msg, LINECALLSTATE_OFFERING);
            }
            else if (cmd.Equals(TapiCommands.Ringback, StringComparison.OrdinalIgnoreCase))
            {
                HandleCallState(msg, LINECALLSTATE_RINGBACK);
            }
            else if (cmd.Equals(TapiCommands.Connected, StringComparison.OrdinalIgnoreCase))
            {
                HandleCallState(msg, LINECALLSTATE_CONNECTED);
            }
            else if (cmd.Equals(TapiCommands.Disconnected, StringComparison.OrdinalIgnoreCase))
            {
                HandleCallState(msg, LINECALLSTATE_DISCONNECTED);
                // Follow with IDLE to match TAPI behavior
                HandleCallState(msg, LINECALLSTATE_IDLE);
            }
            else if (cmd.Equals(TapiCommands.DropCall, StringComparison.OrdinalIgnoreCase))
            {
                // Softphone sends DROP-CALL as hangup notification (user hung up in Softphone)
                HandleCallState(msg, LINECALLSTATE_DISCONNECTED);
                HandleCallState(msg, LINECALLSTATE_IDLE);
            }
            else if (cmd.Equals(TapiCommands.CallInfo, StringComparison.OrdinalIgnoreCase))
            {
                HandleCallInfo(msg);
            }
            else
            {
                LogManager.Debug("PipeTelephonyProvider: Unbekannter Befehl '{0}'", cmd);
            }
        }

        private void HandleCallState(TapiMessage msg, int callState)
        {
            string pipeCallId = msg.CallId;
            if (string.IsNullOrEmpty(pipeCallId))
                pipeCallId = "pipe_" + Interlocked.Increment(ref _nextCallId);

            var callEvent = _activeCalls.GetOrAdd(pipeCallId, _ => new TapiCallEvent
            {
                CallHandle = IntPtr.Zero,
                LineHandle = IntPtr.Zero,
                CallId = Math.Abs(pipeCallId.GetHashCode()),
                Extension = _extension,
                Origin = DetermineOrigin(msg, callState)
            });

            // Update call state
            callEvent.CallState = callState;

            // Update caller/called info from message
            UpdateCallInfo(callEvent, msg);

            LogManager.Log("PipeTelephonyProvider: {0} callId={1} caller={2} called={3}",
                callEvent.CallStateString, pipeCallId,
                callEvent.CallerNumber ?? "-", callEvent.CalledNumber ?? "-");

            EventHelper.SafeInvoke(CallStateChanged, callEvent, "PipeTelephonyProvider.CallStateChanged");

            // Clean up completed calls
            if (callState == LINECALLSTATE_IDLE)
            {
                TapiCallEvent removed;
                _activeCalls.TryRemove(pipeCallId, out removed);
            }
        }

        private void HandleCallInfo(TapiMessage msg)
        {
            string pipeCallId = msg.CallId;
            if (string.IsNullOrEmpty(pipeCallId))
                return;

            TapiCallEvent callEvent;
            if (_activeCalls.TryGetValue(pipeCallId, out callEvent))
            {
                UpdateCallInfo(callEvent, msg);
                LogManager.Debug("PipeTelephonyProvider: Updated call info for {0}", pipeCallId);
            }
        }

        private static void UpdateCallInfo(TapiCallEvent callEvent, TapiMessage msg)
        {
            string callerNum = msg.CallerNumber;
            if (!string.IsNullOrEmpty(callerNum))
                callEvent.CallerNumber = callerNum;

            string callerName = msg.CallerName;
            if (!string.IsNullOrEmpty(callerName))
                callEvent.CallerName = callerName;

            string calledNum = msg.CalledNumber;
            if (!string.IsNullOrEmpty(calledNum))
                callEvent.CalledNumber = calledNum;

            string calledName = msg.CalledName;
            if (!string.IsNullOrEmpty(calledName))
                callEvent.CalledName = calledName;
        }

        private static int DetermineOrigin(TapiMessage msg, int callState)
        {
            // RINGING/OFFERING = incoming, RINGBACK = outgoing
            if (callState == LINECALLSTATE_OFFERING)
                return LINECALLORIGIN_INBOUND;
            if (callState == LINECALLSTATE_RINGBACK)
                return LINECALLORIGIN_OUTBOUND;

            // Check if message has direction hint
            string status = msg[TapiCommands.KeyStatus];
            if (!string.IsNullOrEmpty(status))
            {
                if (status.IndexOf("inbound", StringComparison.OrdinalIgnoreCase) >= 0)
                    return LINECALLORIGIN_INBOUND;
                if (status.IndexOf("outbound", StringComparison.OrdinalIgnoreCase) >= 0)
                    return LINECALLORIGIN_OUTBOUND;
            }

            return LINECALLORIGIN_UNKNOWN;
        }

        // ===== Call Control =====

        /// <summary>
        /// Send MAKE-CALL command to the 3CX Softphone via pipe.
        /// Protocol: __reqId={n},cmd=MAKE-CALL,number={destination}
        /// </summary>
        public int MakeCall(string destination)
        {
            if (!_connected || _server == null)
            {
                LogManager.Log("PipeTelephonyProvider: MakeCall fehlgeschlagen - nicht verbunden (verbunden={0}, Server={1})",
                    _connected, _server != null ? "exists" : "null");
                return -1;
            }

            try
            {
                var msg = TapiMessage.CreateMakeCall(destination);

                LogManager.Log("PipeTelephonyProvider: Sende MAKE-CALL an Pipe: {0}", msg.ToString());
                LogManager.Log("PipeTelephonyProvider: Pipe-Status: clientVerbunden={0}", _server.IsClientConnected);

                if (_server.TrySend(msg))
                {
                    LogManager.Log("PipeTelephonyProvider: MAKE-CALL gesendet OK - {0} (reqId={1})", destination, msg.RequestId);
                    return 1;
                }

                LogManager.Log("PipeTelephonyProvider: MakeCall fehlgeschlagen - TrySend hat false zurückgegeben");
                return -1;
            }
            catch (Exception ex)
            {
                LogManager.Log("PipeTelephonyProvider: MakeCall Ausnahme - {0}", ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Send DROP-CALL command to the 3CX Softphone via pipe
        /// </summary>
        public int DropCall(IntPtr hCall)
        {
            if (!_connected || _server == null)
                return -1;

            // In pipe mode, hCall is not used. Find the active call to drop.
            var lastCall = _activeCalls.Values
                .Where(c => c.CallState != LINECALLSTATE_IDLE && c.CallState != LINECALLSTATE_DISCONNECTED)
                .LastOrDefault();

            if (lastCall == null)
            {
                LogManager.Log("PipeTelephonyProvider: DropCall - kein aktiver Anruf gefunden");
                return -1;
            }

            try
            {
                string pipeCallId = _activeCalls
                    .Where(kvp => kvp.Value == lastCall)
                    .Select(kvp => kvp.Key)
                    .FirstOrDefault();

                if (pipeCallId != null)
                {
                    var msg = TapiMessage.CreateDropCall(pipeCallId);
                    if (_server.TrySend(msg))
                    {
                        LogManager.Log("PipeTelephonyProvider: DropCall gesendet für {0}", pipeCallId);
                        return 1;
                    }
                }

                return -1;
            }
            catch (Exception ex)
            {
                LogManager.Log("PipeTelephonyProvider: DropCall fehlgeschlagen - {0}", ex.Message);
                return -1;
            }
        }

        public TapiCallEvent FindCallById(int callId)
        {
            return _activeCalls.Values.FirstOrDefault(c => c.CallId == callId);
        }

        // ===== Diagnostics =====

        public bool ReconnectLine(string extension, Action<string> progressText = null)
        {
            // Server keeps running and accepts reconnects automatically
            LogManager.Log("PipeTelephonyProvider: ReconnectLine - Server behandelt Neuverbindung automatisch");
            return _connected;
        }

        public void ReconnectAllLines(Action<string> progressText = null)
        {
            LogManager.Log("PipeTelephonyProvider: ReconnectAllLines - Server behandelt Neuverbindung automatisch");
        }

        public bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3)
        {
            bool ok = _connected && _server != null && _server.IsClientConnected;
            progressText?.Invoke(ok
                ? $"3CX Pipe Server ({_extension}): 3CX Softphone verbunden"
                : $"3CX Pipe Server ({_extension}): Warte auf 3CX Softphone");
            return ok;
        }

        // ===== Cleanup =====

        private void SetLineDisconnected()
        {
            if (_virtualLine != null && _virtualLine.IsConnected)
            {
                _virtualLine.Handle = IntPtr.Zero;
                LineDisconnected?.Invoke(_virtualLine);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _connected = false;
            _activeCalls.Clear();

            if (_server != null)
            {
                _server.ClientConnected -= OnServerClientConnected;
                _server.ClientDisconnected -= OnServerClientDisconnected;
                _server.MessageReceived -= OnPipeMessage;
                _server.Dispose();
                _server = null;
            }
        }
    }
}
