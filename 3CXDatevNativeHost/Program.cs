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
    /// Diagnostic output goes to stderr (Chrome captures but does not block on it).
    /// </summary>
    internal static class Program
    {
        private const string PipePrefix = "3CX_DATEV_Webclient_";
        private const int PipeConnectTimeoutMs = 10_000;
        private const string Version = "1.1.0";

        static int Main()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                int sessionId = Process.GetCurrentProcess().SessionId;
                string pipeName = PipePrefix + sessionId;

                Log("v{0} starting (PID={1}, session={2})", Version, pid, sessionId);
                Log("Connecting to pipe: {0}", pipeName);

                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(PipeConnectTimeoutMs);
                    Log("Pipe connected successfully");

                    var stdin = Console.OpenStandardInput();
                    var stdout = Console.OpenStandardOutput();

                    Log("Starting relay (stdin={0}, stdout={1})",
                        stdin.GetType().Name, stdout.GetType().Name);

                    using (var cts = new CancellationTokenSource())
                    {
                        var stdinToPipe = Task.Run(() => Relay("stdin->pipe", stdin, pipe, cts));
                        var pipeToStdout = Task.Run(() => Relay("pipe->stdout", pipe, stdout, cts));

                        Task.WaitAny(stdinToPipe, pipeToStdout);
                        Log("One relay direction ended, shutting down");
                        cts.Cancel();

                        // Give the other task a moment to finish cleanly
                        try { Task.WaitAll(new[] { stdinToPipe, pipeToStdout }, 2000); }
                        catch { }
                    }
                }

                Log("Exiting normally");
                return 0;
            }
            catch (TimeoutException)
            {
                Log("Bridge pipe not available (timeout after {0}ms)", PipeConnectTimeoutMs);
                return 1;
            }
            catch (Exception ex)
            {
                Log("Fatal error: {0}", ex);
                return 1;
            }
        }

        private static void Relay(string label, Stream input, Stream output, CancellationTokenSource cts)
        {
            long totalBytes = 0;
            int chunkCount = 0;

            try
            {
                var buffer = new byte[4096];

                while (!cts.IsCancellationRequested)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        Log("{0}: EOF after {1} bytes in {2} chunks", label, totalBytes, chunkCount);
                        break;
                    }

                    output.Write(buffer, 0, read);
                    output.Flush();

                    totalBytes += read;
                    chunkCount++;

                    // Log first few chunks for diagnostics
                    if (chunkCount <= 5)
                    {
                        Log("{0}: relayed {1} bytes (chunk #{2}, total={3})",
                            label, read, chunkCount, totalBytes);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Log("{0}: stream disposed (total={1} bytes)", label, totalBytes);
            }
            catch (IOException ex)
            {
                Log("{0}: IO error after {1} bytes - {2}", label, totalBytes, ex.Message);
            }
            catch (Exception ex)
            {
                Log("{0}: unexpected error after {1} bytes - {2}", label, totalBytes, ex.Message);
            }
            finally
            {
                cts.Cancel();
            }
        }

        private static void Log(string format, params object[] args)
        {
            try
            {
                string message = args.Length > 0
                    ? string.Format(format, args)
                    : format;
                Console.Error.WriteLine("[3CXDatevNativeHost] " + message);
                Console.Error.Flush();
            }
            catch { }
        }
    }
}
