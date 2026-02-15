using System;
using System.Collections.Concurrent;
using System.Linq;
using DatevConnector.Core;
using DatevConnector.Datev.Managers;
using static DatevConnector.Interop.TapiInterop;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Manages opening, closing, and reconnecting TAPI lines.
    /// </summary>
    internal class TapiLineManager
    {
        private readonly ConcurrentDictionary<int, TapiLineInfo> _lines;
        private readonly ConcurrentDictionary<IntPtr, TapiLineInfo> _linesByHandle;
        private readonly Func<IntPtr> _getLineAppHandle;
        private readonly Action<TapiLineInfo> _onLineConnected;
        private readonly Action<TapiLineInfo> _onLineDisconnected;

        public TapiLineManager(
            ConcurrentDictionary<int, TapiLineInfo> lines,
            ConcurrentDictionary<IntPtr, TapiLineInfo> linesByHandle,
            Func<IntPtr> getLineAppHandle,
            Action<TapiLineInfo> onLineConnected,
            Action<TapiLineInfo> onLineDisconnected)
        {
            _lines = lines;
            _linesByHandle = linesByHandle;
            _getLineAppHandle = getLineAppHandle;
            _onLineConnected = onLineConnected;
            _onLineDisconnected = onLineDisconnected;
        }

        /// <summary>
        /// Open all discovered lines in monitor mode
        /// </summary>
        public void OpenAllLines(Action<string> progressText = null)
        {
            foreach (var line in _lines.Values)
            {
                progressText?.Invoke($"Öffne Leitung: {line.Extension}...");
                OpenSingleLine(line, progressText);
            }
        }

        /// <summary>
        /// Open a single line in monitor mode
        /// </summary>
        /// <returns>True if opened successfully</returns>
        public bool OpenSingleLine(TapiLineInfo line, Action<string> progressText = null)
        {
            IntPtr hLine;
            int result = lineOpen(
                _getLineAppHandle(),
                line.DeviceId,
                out hLine,
                line.ApiVersion,
                0,          // dwExtVersion
                IntPtr.Zero, // dwCallbackInstance
                LINECALLPRIVILEGE_MONITOR,
                LINEMEDIAMODE_INTERACTIVEVOICE | LINEMEDIAMODE_UNKNOWN,
                IntPtr.Zero); // lpCallParams

            if (result != LINEERR_OK)
            {
                var errorDesc = GetErrorDescription(result);
                LogManager.Warning("lineOpen fehlgeschlagen für Gerät {0} ({1}): {2} (0x{3:X8})",
                    line.DeviceId, line.Extension, errorDesc, result);
                progressText?.Invoke($"Fehler: Leitung {line.Extension} - {errorDesc}");

                // LINEERR_NOTREGISTERED: 3CX TSP needs the dialer app running
                if (result == LINEERR_NOTREGISTERED)
                {
                    SessionManager.Log3CXTapiDiagnostics(line.Extension);
                    if (!SessionManager.Is3CXProcessRunning())
                        progressText?.Invoke("Warte auf 3CX Desktop App...");
                }

                return false;
            }

            line.Handle = hLine;
            _linesByHandle[hLine] = line;

            // Request line device state notifications (call states come automatically with MONITOR privilege)
            lineSetStatusMessages(hLine, LINEDEVSTATE_RINGING, 0);

            LogManager.Log("TAPI Leitung verbunden: {0}", line.Extension);
            progressText?.Invoke($"Leitung {line.Extension} verbunden");

            _onLineConnected?.Invoke(line);
            return true;
        }

        /// <summary>
        /// Reconnect a specific line by extension
        /// </summary>
        public bool ReconnectLine(string extension, Action<string> progressText = null)
        {
            var line = _lines.Values.FirstOrDefault(l => l.Extension == extension);
            if (line == null)
            {
                LogManager.Log("TAPI: Neuverbindung nicht möglich - Nebenstelle {0} nicht gefunden", extension);
                return false;
            }

            // Close existing handle if open
            if (line.Handle != IntPtr.Zero)
            {
                progressText?.Invoke($"Trenne Leitung {extension}...");
                _linesByHandle.TryRemove(line.Handle, out _);
                lineClose(line.Handle);
                line.Handle = IntPtr.Zero;
                _onLineDisconnected?.Invoke(line);
            }

            // Reopen the line
            progressText?.Invoke($"Öffne Leitung {extension}...");
            return OpenSingleLine(line, progressText);
        }

        /// <summary>
        /// Reconnect all lines
        /// </summary>
        public void ReconnectAllLines(Action<string> progressText = null)
        {
            // Close all open lines
            foreach (var line in _lines.Values.Where(l => l.IsConnected).ToList())
            {
                progressText?.Invoke($"Trenne Leitung {line.Extension}...");
                _linesByHandle.TryRemove(line.Handle, out _);
                lineClose(line.Handle);
                line.Handle = IntPtr.Zero;
                _onLineDisconnected?.Invoke(line);
            }

            // Reopen all lines
            OpenAllLines(progressText);
        }
    }
}
