using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DatevBridge.Datev.Managers;

namespace DatevBridge.Webclient
{
    /// <summary>
    /// Chrome/Edge Native Messaging host.
    /// Reads JSON messages from stdin (framed: 4-byte LE length + UTF-8 payload)
    /// and writes framed JSON responses to stdout.
    ///
    /// The browser extension launches this process; the browser manages the lifecycle.
    /// For the bridge use case, this runs as a background listener thread within
    /// the existing bridge process, reading from redirected stdin/stdout pipes
    /// (or in standalone mode for testing, from actual console stdin/stdout).
    ///
    /// In-process mode: uses provided input/output streams (from the extension
    /// connecting via a launched native host subprocess or via a local IPC pipe).
    /// </summary>
    public class NativeMessagingHost : IDisposable
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly object _writeLock = new object();
        private volatile bool _disposed;
        private volatile bool _helloReceived;
        private string _extensionNumber;
        private string _webclientIdentity;

        /// <summary>
        /// Fired when a CALL_EVENT message is received from the extension.
        /// </summary>
        public event Action<ExtensionMessage> CallEventReceived;

        /// <summary>
        /// Fired when HELLO handshake completes successfully.
        /// </summary>
        public event Action<string> HelloReceived;

        /// <summary>
        /// Fired when the extension disconnects (stream closed / EOF).
        /// </summary>
        public event Action Disconnected;

        /// <summary>
        /// Whether the HELLO handshake has been completed.
        /// </summary>
        public bool IsConnected => _helloReceived && !_disposed;

        /// <summary>
        /// Extension number reported by the browser extension.
        /// </summary>
        public string ExtensionNumber => _extensionNumber;

        /// <summary>
        /// Webclient identity string from the HELLO message.
        /// </summary>
        public string WebclientIdentity => _webclientIdentity;

        /// <summary>
        /// Create a Native Messaging host using the given streams.
        /// For testing, use Console.OpenStandardInput() / Console.OpenStandardOutput().
        /// For in-process IPC, use pipe streams.
        /// </summary>
        public NativeMessagingHost(Stream input, Stream output)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Run the message read loop. Blocks until the stream closes or cancellation is requested.
        /// Call this on a background thread/task.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            LogManager.Log("NativeMessagingHost: Read loop started");

            try
            {
                while (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    // Read one framed message (blocking)
                    string json = await Task.Run(() => NativeMessagingFraming.ReadMessage(_input), cancellationToken);

                    if (json == null)
                    {
                        // EOF — extension closed the connection
                        LogManager.Log("NativeMessagingHost: Stream closed (EOF)");
                        break;
                    }

                    ProcessMessage(json);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (IOException ex)
            {
                LogManager.Log("NativeMessagingHost: IO error - {0}", ex.Message);
            }
            catch (Exception ex)
            {
                LogManager.Log("NativeMessagingHost: Unexpected error - {0}", ex.Message);
            }
            finally
            {
                _helloReceived = false;
                LogManager.Log("NativeMessagingHost: Read loop ended");
                Disconnected?.Invoke();
            }
        }

        /// <summary>
        /// Send a JSON message to the extension (framed).
        /// Thread-safe.
        /// </summary>
        public bool TrySend(string json)
        {
            if (_disposed || _output == null)
                return false;

            try
            {
                lock (_writeLock)
                {
                    NativeMessagingFraming.WriteMessage(_output, json);
                }
                LogManager.Debug("NativeMessagingHost: Sent -> {0}", json);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Log("NativeMessagingHost: Send failed - {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Send HELLO_ACK to the extension.
        /// </summary>
        public bool SendHelloAck(string bridgeVersion, string extension)
        {
            string json = BridgeMessageBuilder.BuildHelloAck(bridgeVersion, extension);
            return TrySend(json);
        }

        /// <summary>
        /// Send DIAL command to the extension.
        /// </summary>
        public bool SendDial(string number, string syncId = null)
        {
            string json = BridgeMessageBuilder.BuildDialCommand(number, syncId);
            return TrySend(json);
        }

        /// <summary>
        /// Send DROP command to the extension.
        /// </summary>
        public bool SendDrop(string callId = null)
        {
            string json = BridgeMessageBuilder.BuildDropCommand(callId);
            return TrySend(json);
        }

        private void ProcessMessage(string json)
        {
            LogManager.Debug("NativeMessagingHost: Received <- {0}", json);

            var msg = ExtensionMessage.Parse(json);
            if (msg == null)
            {
                LogManager.Log("NativeMessagingHost: Failed to parse message");
                return;
            }

            if (msg.Version != Protocol.Version)
            {
                LogManager.Warning("NativeMessagingHost: Unsupported protocol version {0} (expected {1})",
                    msg.Version, Protocol.Version);
            }

            if (string.Equals(msg.Type, Protocol.TypeHello, StringComparison.OrdinalIgnoreCase))
            {
                HandleHello(msg);
            }
            else if (string.Equals(msg.Type, Protocol.TypeCallEvent, StringComparison.OrdinalIgnoreCase))
            {
                HandleCallEvent(msg);
            }
            else
            {
                LogManager.Log("NativeMessagingHost: Unknown message type '{0}'", msg.Type);
            }
        }

        private void HandleHello(ExtensionMessage msg)
        {
            _extensionNumber = msg.ExtensionNumber;
            _webclientIdentity = msg.WebclientIdentity;
            _helloReceived = true;

            LogManager.Log("NativeMessagingHost: HELLO from extension={0}, identity={1}, domain={2}, version={3}",
                _extensionNumber ?? "(none)", _webclientIdentity ?? "(none)",
                msg.Domain ?? "(none)", msg.WebclientVersion ?? "(none)");

            HelloReceived?.Invoke(_extensionNumber);
        }

        private void HandleCallEvent(ExtensionMessage msg)
        {
            if (!_helloReceived)
            {
                LogManager.Warning("NativeMessagingHost: CALL_EVENT before HELLO, ignoring");
                return;
            }

            LogManager.Log("NativeMessagingHost: CALL_EVENT callId={0} state={1} direction={2} remote={3}",
                msg.CallId, msg.State, msg.Direction, LogManager.Mask(msg.RemoteNumber));

            try
            {
                CallEventReceived?.Invoke(msg);
            }
            catch (Exception ex)
            {
                LogManager.Log("NativeMessagingHost: Error in CallEventReceived handler - {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _helloReceived = false;

            try { _input?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
        }
    }
}
