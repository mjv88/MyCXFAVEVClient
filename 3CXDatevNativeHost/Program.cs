using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DatevBridge.NativeHost
{
    /// <summary>
    /// Minimal relay between Chrome/Edge Native Messaging (stdin/stdout)
    /// and the 3CX-DATEV Bridge named pipe.
    ///
    /// The browser launches this process when the extension calls
    /// chrome.runtime.connectNative(). Messages are framed identically
    /// on both sides (4-byte LE length + UTF-8 JSON), so we relay raw
    /// bytes without parsing.
    ///
    /// Pipe name: 3CX_DATEV_Webclient_{sessionId}
    /// </summary>
    internal static class Program
    {
        private const string PipePrefix = "3CX_DATEV_Webclient_";
        private const int PipeConnectTimeoutMs = 10_000;

        static int Main()
        {
            try
            {
                int sessionId = Process.GetCurrentProcess().SessionId;
                string pipeName = PipePrefix + sessionId;

                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(PipeConnectTimeoutMs);

                    var stdin = Console.OpenStandardInput();
                    var stdout = Console.OpenStandardOutput();

                    using (var cts = new CancellationTokenSource())
                    {
                        var stdinToPipe = Task.Run(() => Relay(stdin, pipe, cts));
                        var pipeToStdout = Task.Run(() => Relay(pipe, stdout, cts));

                        Task.WaitAny(stdinToPipe, pipeToStdout);
                        cts.Cancel();
                    }
                }

                return 0;
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("3CXDatevNativeHost: Bridge pipe not available (timeout)");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("3CXDatevNativeHost: " + ex.Message);
                return 1;
            }
        }

        private static void Relay(Stream input, Stream output, CancellationTokenSource cts)
        {
            try
            {
                var buffer = new byte[4096];
                while (!cts.IsCancellationRequested)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    output.Write(buffer, 0, read);
                    output.Flush();
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            finally
            {
                cts.Cancel();
            }
        }
    }
}
