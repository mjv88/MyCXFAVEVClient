using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Abstraction for telephony providers (TAPI 2.x, Named Pipe, or Webclient).
    /// Desktop sessions use TAPI; terminal server sessions use Named Pipe;
    /// browser-only setups use Webclient (browser extension via WebSocket).
    /// </summary>
    public interface ITelephonyProvider : IDisposable
    {
        // ===== Events =====

        /// <summary>
        /// Fired when a call state changes (ringing, connected, disconnected, etc.)
        /// </summary>
        event Action<TapiCallEvent> CallStateChanged;

        /// <summary>
        /// Fired when a specific line connects
        /// </summary>
        event Action<TapiLineInfo> LineConnected;

        /// <summary>
        /// Fired when a specific line disconnects
        /// </summary>
        event Action<TapiLineInfo> LineDisconnected;

        /// <summary>
        /// Fired when provider is initialized and ready
        /// </summary>
        event Action Connected;

        /// <summary>
        /// Fired when provider loses connection
        /// </summary>
        event Action Disconnected;

        // ===== Properties =====

        /// <summary>
        /// Whether the provider is currently monitoring calls
        /// </summary>
        bool IsMonitoring { get; }

        /// <summary>
        /// Number of connected lines
        /// </summary>
        int ConnectedLineCount { get; }

        /// <summary>
        /// Name of the first connected line
        /// </summary>
        string LineName { get; }

        /// <summary>
        /// Extension of the first connected line
        /// </summary>
        string Extension { get; }

        /// <summary>
        /// All discovered lines
        /// </summary>
        IReadOnlyList<TapiLineInfo> Lines { get; }

        // ===== Connection =====

        /// <summary>
        /// Start monitoring (blocks until cancelled or disconnected)
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Start monitoring with progress callback
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken, Action<string> progressText);

        // ===== Call Control =====

        /// <summary>
        /// Initiate an outbound call.
        /// Returns positive request ID on success, negative on failure.
        /// </summary>
        int MakeCall(string destination);

        /// <summary>
        /// Drop/end a call by handle.
        /// Returns positive request ID on success, negative on failure.
        /// </summary>
        int DropCall(IntPtr hCall);

        /// <summary>
        /// Find an active call by its call ID
        /// </summary>
        TapiCallEvent FindCallById(int callId);

        // ===== Diagnostics =====

        /// <summary>
        /// Reconnect a specific line by extension
        /// </summary>
        bool ReconnectLine(string extension, Action<string> progressText = null);

        /// <summary>
        /// Reconnect all lines
        /// </summary>
        void ReconnectAllLines(Action<string> progressText = null);

        /// <summary>
        /// Test a specific line's health
        /// </summary>
        bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3);
    }
}
