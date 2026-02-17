using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Named pipe server that replaces dialer.exe on terminal servers.
    /// Creates \\.\pipe\3CX_tsp_server_{extension} and waits for the
    /// Commands from softphone: RINGING, RINGBACK, CONNECTED, DISCONNECTED, CALL-INFO
    /// Commands to softphone: MAKE-CALL, DROP-CALL
    /// </summary>
    public class TapiPipeServer : IDisposable
    {
        private const int MaxMessageLength = 65535;
        private const int LengthPrefixSize = 2;

        private readonly string _pipeName;
        private NamedPipeServerStream _pipe;
        private bool _disposed;
        private volatile bool _clientConnected;

        /// <summary>
        /// Fired when a message is received from the 3CX Softphone
        /// </summary>
        public event Action<TapiMessage> MessageReceived;

        /// <summary>
        /// Fired when the 3CX Softphone connects to our pipe
        /// </summary>
        public event Action ClientConnected;

        /// <summary>
        /// Fired when the 3CX Softphone disconnects
        /// </summary>
        public event Action ClientDisconnected;

        /// <summary>
        /// Whether a client (3CX Softphone) is currently connected
        /// </summary>
        public bool IsClientConnected => _clientConnected;

        /// <summary>
        /// Create a pipe server for the specified extension.
        /// The pipe name follows 3CX convention: 3CX_tsp_server_{extension}
        /// </summary>
        public TapiPipeServer(string extension)
        {
            _pipeName = "3CX_tsp_server_" + extension;
        }

        /// <summary>
        /// Create pipe security that allows MSIX/AppContainer apps to connect.
        /// Without explicit ACLs for ALL APPLICATION PACKAGES (S-1-15-2-1), the Softphone cannot connect to our named pipe.
        /// </summary>
        private static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();

            // Grant full control to the current user (our process)
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                security.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }

            try
            {
                var appPackagesSid = new SecurityIdentifier("S-1-15-2-1");
                security.AddAccessRule(new PipeAccessRule(
                    appPackagesSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                LogManager.Debug("PipeServer: Could not add AppContainer ACL: {0}", ex.Message);
            }

            try
            {
                var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                security.AddAccessRule(new PipeAccessRule(
                    everyoneSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                LogManager.Debug("PipeServer: Could not add Everyone ACL: {0}", ex.Message);
            }

            return security;
        }

        /// <summary>
        /// Run the pipe server loop. Creates the pipe, waits for the 3CX Softphone to connect, reads messages, and reconnects when the client disconnects.
        /// Blocks until cancellation.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            LogManager.Log("PipeServer: Starte auf \\\\.\\pipe\\{0}", _pipeName);

            LogExisting3CXPipes();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {

                    var security = CreatePipeSecurity();
                    _pipe = NamedPipeServerStreamAcl.Create(
                        _pipeName,
                        PipeDirection.InOut,
                        1,                          // maxNumberOfServerInstances
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        4096,                       // inBufferSize
                        4096,                       // outBufferSize
                        security);

                    LogManager.Log("PipeServer: Pipe erstellt, warte auf 3CX Softphone Verbindung...");
                    LogExisting3CXPipes();

                    // Wait for the 3CX Softphone to connect (it polls every 2s)
                    await WaitForConnectionAsync(_pipe, cancellationToken);

                    _clientConnected = true;
                    LogManager.Log("PipeServer: 3CX Softphone verbunden");

                    // Send SRVHELLO handshake (the 3CX Softphone expects this from the server)
                    try
                    {
                        var hello = TapiMessage.CreateServerHello();
                        await SendAsync(hello, cancellationToken);
                        LogManager.Log("PipeServer: SRVHELLO gesendet");
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning("PipeServer: SRVHELLO senden fehlgeschlagen: {0}", ex.Message);
                    }

                    try
                    {
                        ClientConnected?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug("PipeServer: Fehler im ClientConnected-Handler: {0}", ex.Message);
                    }

                    // Read messages until client disconnects or cancellation
                    await ReadLoopAsync(_pipe, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogManager.Warning("PipeServer: Fehler - {0}", ex.Message);
                }
                finally
                {
                    _clientConnected = false;

                    try
                    {
                        ClientDisconnected?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug("PipeServer: Fehler im ClientDisconnected-Handler: {0}", ex.Message);
                    }

                    DisposePipe();
                }

                // Brief pause before recreating the pipe for the next connection
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }

            LogManager.Log("PipeServer: Beendet");
        }

        /// <summary>
        /// Wait for client connection with cancellation support
        /// </summary>
        private async Task WaitForConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
        }

        /// <summary>
        /// Read messages from the connected 3CX Softphone
        /// </summary>
        private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            byte[] lengthBuffer = new byte[LengthPrefixSize];

            try
            {
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    int bytesRead = await ReadExactAsync(pipe, lengthBuffer, LengthPrefixSize, cancellationToken);
                    if (bytesRead < LengthPrefixSize)
                    {
                        LogManager.Log("PipeServer: Client getrennt (unvollständiger Längenblock)");
                        break;
                    }

                    int messageLength = (int)((uint)lengthBuffer[0] + (uint)lengthBuffer[1] * 256U);

                    if (messageLength <= 0 || messageLength > MaxMessageLength)
                    {
                        LogManager.Warning("PipeServer: Ungültige Nachrichtenlänge: {0}", messageLength);
                        continue;
                    }

                    byte[] contentBuffer = new byte[messageLength];
                    bytesRead = await ReadExactAsync(pipe, contentBuffer, messageLength, cancellationToken);
                    if (bytesRead < messageLength)
                    {
                        LogManager.Log("PipeServer: Client getrennt (unvollständige Nachricht)");
                        break;
                    }

                    string content = Encoding.Unicode.GetString(contentBuffer);
                    LogManager.Debug("PipeServer RECV: {0}", content);

                    var message = new TapiMessage(content);
                    try
                    {
                        MessageReceived?.Invoke(message);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Log("PipeServer: Fehler im Nachrichtenhandler: {0}", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (IOException ex)
            {
                LogManager.Log("PipeServer: Client getrennt ({0})", ex.Message);
            }
            catch (Exception ex)
            {
                LogManager.Warning("PipeServer: Lesefehler - {0}", ex.Message);
            }
        }

        /// <summary>
        /// Read exact number of bytes from the pipe
        /// </summary>
        private static async Task<int> ReadExactAsync(
            PipeStream pipe, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await pipe.ReadAsync(buffer, totalRead, count - totalRead, cancellationToken);
                if (read == 0)
                    return totalRead; 
                totalRead += read;
            }
            return totalRead;
        }

        /// <summary>
        /// Send a message to the connected 3CX Softphone
        /// </summary>
        public async Task SendAsync(TapiMessage message, CancellationToken cancellationToken)
        {
            if (_pipe == null || !_pipe.IsConnected)
                throw new InvalidOperationException("No client connected");

            byte[] data = message.ToBytes();
            LogManager.Debug("PipeServer SEND: {0}", message.ToString());

            await _pipe.WriteAsync(data, 0, data.Length, cancellationToken);
            await _pipe.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// Try to send a message, returning false on failure instead of throwing
        /// </summary>
        public bool TrySend(TapiMessage message)
        {
            try
            {
                if (_pipe == null)
                {
                    LogManager.Log("PipeServer: TrySend fehlgeschlagen - Pipe ist null");
                    return false;
                }

                if (!_pipe.IsConnected)
                {
                    LogManager.Log("PipeServer: TrySend fehlgeschlagen - Pipe nicht verbunden");
                    return false;
                }

                byte[] data = message.ToBytes();
                LogManager.Log("PipeServer SEND: {0} ({1} bytes)", message.ToString(), data.Length);

                _pipe.Write(data, 0, data.Length);
                _pipe.Flush();
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Log("PipeServer: TrySend fehlgeschlagen - {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Diagnostic: list all named pipes matching "3CX" on the system
        /// </summary>
        private static void LogExisting3CXPipes()
        {
            try
            {
                var pipeDir = new System.IO.DirectoryInfo(@"\\.\pipe\");
                var pipes = pipeDir.GetFiles("3CX*");
                if (pipes.Length == 0)
                {
                    LogManager.Log("PipeServer: Keine bestehenden 3CX Pipes auf dem System gefunden");
                }
                else
                {
                    LogManager.Log("PipeServer: {0} bestehende 3CX Pipe(s) gefunden:", pipes.Length);
                    foreach (var pipe in pipes)
                    {
                        LogManager.Log("  \\\\.\\.pipe\\{0}", pipe.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug("PipeServer: Could not enumerate pipes: {0}", ex.Message);
            }
        }

        private void DisposePipe()
        {
            if (_pipe != null)
            {
                try { _pipe.Dispose(); }
                catch (Exception ex) { LogManager.Debug("PipeServer: Fehler beim Verwerfen des Pipes - {0}", ex.Message); }
                _pipe = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _clientConnected = false;
            DisposePipe();
        }
    }
}
