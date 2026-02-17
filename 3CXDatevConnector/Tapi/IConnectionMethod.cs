using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Abstraction for connection methods (TAPI 2.x, Named Pipe, or Webclient).
    /// Desktop sessions use TAPI; terminal server sessions use Named Pipe;
    /// browser-only setups use Webclient (browser extension via WebSocket).
    /// </summary>
    public interface IConnectionMethod : IDisposable
    {
        event Action<TapiCallEvent> CallStateChanged;
        event Action<TapiLineInfo> LineConnected;
        event Action<TapiLineInfo> LineDisconnected;
        event Action Connected;
        event Action Disconnected;

        bool IsMonitoring { get; }
        int ConnectedLineCount { get; }
        string LineName { get; }
        string Extension { get; }
        IReadOnlyList<TapiLineInfo> Lines { get; }

        Task StartAsync(CancellationToken cancellationToken);
        Task StartAsync(CancellationToken cancellationToken, Action<string> progressText);

        /// <summary>
        /// Returns positive request ID on success, negative on failure.
        /// </summary>
        int MakeCall(string destination);

        /// <summary>
        /// Returns positive request ID on success, negative on failure.
        /// </summary>
        int DropCall(IntPtr hCall);

        TapiCallEvent FindCallById(int callId);

        bool ReconnectLine(string extension, Action<string> progressText = null);
        void ReconnectAllLines(Action<string> progressText = null);
        bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3);
    }
}
