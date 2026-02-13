using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Webclient
{
    /// <summary>
    /// Minimal RFC 6455 WebSocket server on localhost for browser extension
    /// communication. The extension connects directly via ws://127.0.0.1:PORT.
    ///
    /// Speaks the bridge JSON protocol (HELLO, HELLO_ACK, CALL_EVENT, COMMAND).
    /// WebSocket handles message boundaries natively — no length-prefix framing needed.
    /// </summary>
    public class WebSocketBridgeServer : IDisposable
    {
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxFrameSize = 1024 * 1024; // 1 MB

        private readonly int _port;
        private TcpListener _listener;
        private readonly object _writeLock = new object();
        private volatile bool _disposed;

        /// <summary>
        /// Groups all per-connection mutable state into a single object.
        /// Simplifies state management, makes transitions explicit, and reduces
        /// the number of fields that must be reset on disconnect from 7 to 1.
        /// </summary>
        private class ClientConnection
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public volatile bool Connected;
            public volatile bool HelloReceived;
            public string ExtensionNumber;
            public string WebclientIdentity;
            public string Domain;
            public string WebclientVersion;

            public bool IsFullyConnected => Connected && HelloReceived;

            public void Reset()
            {
                Connected = false;
                HelloReceived = false;
                ExtensionNumber = null;
                WebclientIdentity = null;
                Domain = null;
                WebclientVersion = null;
                Client = null;
                Stream = null;
            }
        }

        private readonly ClientConnection _conn = new ClientConnection();

        /// <summary>Fired when a CALL_EVENT message is received.</summary>
        public event Action<ExtensionMessage> CallEventReceived;

        /// <summary>Fired when the HELLO handshake completes.</summary>
        public event Action<string> HelloReceived;

        /// <summary>Fired when the client disconnects.</summary>
        public event Action Disconnected;

        public bool IsConnected => _conn.IsFullyConnected && !_disposed;
        public string ExtensionNumber => _conn.ExtensionNumber;
        public string WebclientIdentity => _conn.WebclientIdentity;
        public string Domain => _conn.Domain;
        public string WebclientVersion => _conn.WebclientVersion;

        public WebSocketBridgeServer(int port)
        {
            _port = port;
        }

        // ===== Connection lifecycle =====

        /// <summary>
        /// Listen and accept connections in a loop.
        /// Blocks until cancellation or disposal.
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            StartListener();

            try
            {
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await AcceptAsync(ct);
                        if (client == null) break;

                        if (!await HandshakeAndRunClient(client, ct))
                            continue;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        LogManager.Log("WebClient Connector: Error - {0}", ex.Message);
                    }
                    finally
                    {
                        DisconnectClient(client);
                    }

                    if (!ct.IsCancellationRequested && !_disposed)
                        await Task.Delay(500, ct);
                }
            }
            finally
            {
                StopListener();
            }
        }

        /// <summary>
        /// Accept one connection and wait for HELLO within a timeout.
        /// Used by auto-detection. Leaves the connection open on success.
        /// </summary>
        public async Task<bool> TryAcceptAsync(CancellationToken ct, int timeoutSec)
        {
            StartListener();
            LogManager.Debug("WebClient Connector: TryAccept");

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                // Loop to handle pre-WebSocket HTTP probes from the browser extension.
                // The extension sends a plain GET to check port reachability before opening
                // a real WebSocket — that probe fails the handshake but should not abort detection.
                while (!timeoutCts.IsCancellationRequested)
                {
                    TcpClient client = null;

                    try
                    {
                        client = await AcceptAsync(timeoutCts.Token);
                        if (client == null) return false;

                        if (!await SetupClientAsync(client))
                        {
                            // Handshake failed (HTTP probe) — accept next connection
                            continue;
                        }

                        // Wait for HELLO
                        var helloTcs = new TaskCompletionSource<bool>();
                        Action<string> onHello = _ => helloTcs.TrySetResult(true);
                        HelloReceived += onHello;

                        try
                        {
                            // Start reading in background (uses outer ct so it survives beyond TryAccept return)
                            var readTask = ReadLoopAsync(_conn.Stream, ct);

                            var helloWait = await Task.WhenAny(
                                helloTcs.Task,
                                Task.Delay(Timeout.Infinite, timeoutCts.Token));

                            if (helloWait == helloTcs.Task && helloTcs.Task.Result)
                            {
                                LogManager.Debug("WebClient Connector: TryAccept succeeded");
                                StopListener(); // Release port — client connection stays alive
                                return true;
                            }

                            LogManager.Log("WebClient Connector: TryAccept failed (no HELLO)");
                            DisconnectClient(client);
                            return false;
                        }
                        finally
                        {
                            HelloReceived -= onHello;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (client != null) DisconnectClient(client);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        LogManager.Log("WebClient Connector: TryAccept error - {0}", ex.Message);
                        if (client != null) DisconnectClient(client);
                        // Continue loop to try next connection within timeout
                    }
                }

                LogManager.Log("WebClient Connector: TryAccept timed out ({0}s)", timeoutSec);
                return false;
            }
        }

        // ===== Send methods =====

        public bool SendHelloAck(string bridgeVersion, string extension)
        {
            return SendJson(BridgeMessageBuilder.BuildHelloAck(bridgeVersion, extension));
        }

        public bool SendDial(string number, string syncId = null)
        {
            return SendJson(BridgeMessageBuilder.BuildDialCommand(number, syncId));
        }

        public bool SendDrop(string callId = null)
        {
            return SendJson(BridgeMessageBuilder.BuildDropCommand(callId));
        }

        public bool SendJson(string json)
        {
            if (!_conn.Connected || _conn.Stream == null || _disposed)
                return false;
            try
            {
                lock (_writeLock)
                {
                    WriteTextFrame(_conn.Stream, json);
                }
                LogManager.Debug("WebClient Connector: Sent -> {0}", json);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Log("WebClient Connector: Send failed - {0}", ex.Message);
                return false;
            }
        }

        // ===== WebSocket handshake (RFC 6455 Section 4) =====

        private async Task<bool> PerformHandshakeAsync(NetworkStream stream)
        {
            byte[] buffer = new byte[4096];
            int totalRead = 0;

            // Read until we see end-of-headers
            while (totalRead < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead);
                if (n <= 0) return false;
                totalRead += n;
                if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n"))
                    break;
            }

            string request = Encoding.ASCII.GetString(buffer, 0, totalRead);
            var match = Regex.Match(request, @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                // Respond to plain HTTP requests (extension uses fetch probe to check port
                // reachability before opening a WebSocket, avoiding chrome://extensions errors).
                if (request.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) ||
                    request.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase))
                {
                    const string httpResponse = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK";
                    byte[] httpBytes = Encoding.ASCII.GetBytes(httpResponse);
                    try { await stream.WriteAsync(httpBytes, 0, httpBytes.Length); } catch { }
                }
                return false;
            }

            string acceptKey;
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(
                    Encoding.ASCII.GetBytes(match.Groups[1].Value.Trim() + WsGuid));
                acceptKey = Convert.ToBase64String(hash);
            }

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";

            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
            await stream.FlushAsync();
            return true;
        }

        // ===== WebSocket frame I/O (RFC 6455 Section 5) =====

        private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            LogManager.Debug("WebClient Connector Server: Read loop started");
            try
            {
                while (!ct.IsCancellationRequested && !_disposed && _conn.Connected)
                {
                    var frame = await ReadFrameAsync(stream, ct);
                    if (frame == null) break;

                    switch (frame.Opcode)
                    {
                        case 0x1: // Text
                            ProcessMessage(Encoding.UTF8.GetString(frame.Payload));
                            break;
                        case 0x8: // Close
                            LogManager.Log("WebClient Connector: Close frame received");
                            try { WriteFrame(stream, 0x8, new byte[0]); } catch { }
                            return;
                        case 0x9: // Ping
                            try { WriteFrame(stream, 0xA, frame.Payload ?? new byte[0]); } catch { }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                LogManager.Log("WebClient Connector: IO error - {0}", ex.Message);
            }
            catch (Exception ex)
            {
                LogManager.Log("WebClient Connector: Read error - {0}", ex.Message);
            }
            finally
            {
                bool wasConnected = _conn.Connected;
                _conn.Connected = false;
                LogManager.Log("WebClient Connector Server: Read loop ended");
                if (wasConnected)
                {
                    try { Disconnected?.Invoke(); } catch { }
                }
            }
        }

        private async Task<WsFrame> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] hdr = new byte[2];
            if (!await ReadExactAsync(stream, hdr, 2, ct)) return null;

            byte opcode = (byte)(hdr[0] & 0x0F);
            bool masked = (hdr[1] & 0x80) != 0;
            long len = hdr[1] & 0x7F;

            if (len == 126)
            {
                byte[] ext = new byte[2];
                if (!await ReadExactAsync(stream, ext, 2, ct)) return null;
                len = (ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                byte[] ext = new byte[8];
                if (!await ReadExactAsync(stream, ext, 8, ct)) return null;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
            }

            if (len > MaxFrameSize) return null;

            byte[] maskKey = null;
            if (masked)
            {
                maskKey = new byte[4];
                if (!await ReadExactAsync(stream, maskKey, 4, ct)) return null;
            }

            byte[] payload = new byte[len];
            if (len > 0)
            {
                if (!await ReadExactAsync(stream, payload, (int)len, ct)) return null;
                if (masked)
                    for (int i = 0; i < payload.Length; i++)
                        payload[i] ^= maskKey[i % 4];
            }

            return new WsFrame { Opcode = opcode, Payload = payload };
        }

        private static void WriteTextFrame(NetworkStream stream, string text)
        {
            WriteFrame(stream, 0x1, Encoding.UTF8.GetBytes(text));
        }

        private static void WriteFrame(NetworkStream stream, byte opcode, byte[] payload)
        {
            int hdrLen = 2;
            if (payload.Length >= 126 && payload.Length <= 65535) hdrLen += 2;
            else if (payload.Length > 65535) hdrLen += 8;

            byte[] frame = new byte[hdrLen + payload.Length];
            frame[0] = (byte)(0x80 | opcode); // FIN + opcode

            if (payload.Length < 126)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (payload.Length <= 65535)
            {
                frame[1] = 126;
                frame[2] = (byte)((payload.Length >> 8) & 0xFF);
                frame[3] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                frame[1] = 127;
                long l = payload.Length;
                for (int i = 9; i >= 2; i--) { frame[i] = (byte)(l & 0xFF); l >>= 8; }
            }

            Buffer.BlockCopy(payload, 0, frame, hdrLen, payload.Length);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        private async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buf, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                ct.ThrowIfCancellationRequested();
                int n = await stream.ReadAsync(buf, read, count - read, ct);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        // ===== Protocol handling =====

        private void ProcessMessage(string json)
        {
            LogManager.Debug("WebClient Connector: Received <- {0}", json);

            var msg = ExtensionMessage.Parse(json);
            if (msg == null)
            {
                LogManager.Log("WebClient Connector: Failed to parse message");
                return;
            }

            if (msg.Version != Protocol.Version)
            {
                LogManager.Warning("WebClient Connector: Unsupported protocol version {0} (expected {1})",
                    msg.Version, Protocol.Version);
            }

            if (string.Equals(msg.Type, Protocol.TypeHello, StringComparison.OrdinalIgnoreCase))
            {
                _conn.ExtensionNumber = msg.ExtensionNumber;
                _conn.WebclientIdentity = msg.WebclientIdentity;
                _conn.Domain = msg.Domain;
                _conn.WebclientVersion = msg.WebclientVersion;
                _conn.HelloReceived = true;
                LogManager.Log("WebClient HELLO von extension={0}, identity={1}, FQDN={2}",
                    _conn.ExtensionNumber ?? "(none)", _conn.WebclientIdentity ?? "(none)",
                    _conn.Domain ?? "(none)");
                LogManager.Debug("WebClient Connector: version={0}", _conn.WebclientVersion ?? "(none)");
                HelloReceived?.Invoke(_conn.ExtensionNumber);
            }
            else if (string.Equals(msg.Type, Protocol.TypeCallEvent, StringComparison.OrdinalIgnoreCase))
            {
                if (!_conn.HelloReceived)
                {
                    LogManager.Warning("WebClient Connector: CALL_EVENT before HELLO, ignoring");
                    return;
                }
                LogManager.Log("WebClient Connector: CALL_EVENT callId={0} state={1} direction={2} remote={3}",
                    msg.CallId, msg.State, msg.Direction, LogManager.Mask(msg.RemoteNumber));
                try { CallEventReceived?.Invoke(msg); }
                catch (Exception ex)
                {
                    LogManager.Log("WebClient Connector: Error in handler - {0}", ex.Message);
                }
            }
            else
            {
                LogManager.Log("WebClient Connector: Unknown message type '{0}'", msg.Type);
            }
        }

        // ===== Helpers =====

        private void StartListener()
        {
            if (_listener != null) return;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            LogManager.Log("WebClient Connector: Listening");
        }

        private void StopListener()
        {
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private async Task<TcpClient> AcceptAsync(CancellationToken ct)
        {
            var acceptTask = _listener.AcceptTcpClientAsync();
            var cancelTask = Task.Delay(Timeout.Infinite, ct);
            var done = await Task.WhenAny(acceptTask, cancelTask);
            return done == acceptTask ? await acceptTask : null;
        }

        private async Task<bool> SetupClientAsync(TcpClient client)
        {
            var stream = client.GetStream();
            if (!await PerformHandshakeAsync(stream))
            {
                LogManager.Log("WebClient Connector: Handshake failed");
                client.Close();
                return false;
            }

            _conn.Client = client;
            _conn.Stream = stream;
            _conn.Connected = true;
            return true;
        }

        private async Task<bool> HandshakeAndRunClient(TcpClient client, CancellationToken ct)
        {
            if (!await SetupClientAsync(client)) return false;

            string remote = "(unknown)";
            try { remote = client.Client.RemoteEndPoint?.ToString() ?? remote; } catch { }
            LogManager.Log("WebClient Connector: WebClient connected");

            await ReadLoopAsync(_conn.Stream, ct);
            return true;
        }

        private void DisconnectClient(TcpClient client)
        {
            bool wasConnected = _conn.Connected;
            _conn.Reset();
            if (client != null) try { client.Close(); } catch { }
            if (wasConnected)
            {
                LogManager.Log("WebClient Connector: Client disconnected");
                Disconnected?.Invoke();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _conn.Stream?.Close(); }
            catch (Exception ex) { LogManager.Debug("WebClient Connector: Stream close error during dispose - {0}", ex.Message); }
            try { _conn.Client?.Close(); }
            catch (Exception ex) { LogManager.Debug("WebClient Connector: Client close error during dispose - {0}", ex.Message); }
            _conn.Reset();
            StopListener();
        }

        private class WsFrame
        {
            public byte Opcode;
            public byte[] Payload;
        }
    }
}
