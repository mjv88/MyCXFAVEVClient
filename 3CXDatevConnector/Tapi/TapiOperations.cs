using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DatevConnector.Datev.Managers;
using static DatevConnector.Interop.TapiInterop;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Handles TAPI call operations (make/drop/find) and line health testing.
    /// </summary>
    internal class TapiOperations
    {
        private readonly ConcurrentDictionary<int, TapiLineInfo> _lines;
        private readonly ConcurrentDictionary<IntPtr, TapiCallEvent> _activeCalls;
        private readonly ConcurrentDictionary<IntPtr, TapiLineInfo> _linesByHandle;
        private readonly Action<TapiLineInfo> _onLineDisconnected;

        public TapiOperations(
            ConcurrentDictionary<int, TapiLineInfo> lines,
            ConcurrentDictionary<IntPtr, TapiCallEvent> activeCalls,
            ConcurrentDictionary<IntPtr, TapiLineInfo> linesByHandle,
            Action<TapiLineInfo> onLineDisconnected)
        {
            _lines = lines;
            _activeCalls = activeCalls;
            _linesByHandle = linesByHandle;
            _onLineDisconnected = onLineDisconnected;
        }

        /// <summary>
        /// Make an outbound call on the first connected line
        /// </summary>
        public int MakeCall(string destination)
        {
            return MakeCall(destination, null);
        }

        /// <summary>
        /// Make an outbound call on a specific line
        /// </summary>
        public int MakeCall(string destination, string extension)
        {
            TapiLineInfo line;

            if (extension != null)
            {
                line = _lines.Values.FirstOrDefault(l => l.Extension == extension && l.IsConnected);
                if (line == null)
                {
                    LogManager.Log("TAPI MakeCall: Nebenstelle {0} nicht gefunden oder nicht verbunden", extension);
                    return -1;
                }
            }
            else
            {
                line = _lines.Values.FirstOrDefault(l => l.IsConnected);
                if (line == null)
                {
                    LogManager.Log("TAPI MakeCall: Keine Leitung verbunden");
                    return -1;
                }
            }

            IntPtr hCall;
            int result = lineMakeCall(line.Handle, out hCall, destination, 0, IntPtr.Zero);

            if (result > 0)
            {
                // Positive = async request ID
                LogManager.Log("TAPI MakeCall: Wähle {0} auf Leitung {1} (requestId={2})",
                    destination, line.Extension, result);
            }
            else
            {
                LogManager.Log("TAPI MakeCall: Fehlgeschlagen für {0} auf Leitung {1} (Fehler=0x{2:X8})",
                    destination, line.Extension, result);
            }

            return result;
        }

        /// <summary>
        /// Drop/end a call by handle
        /// </summary>
        public int DropCall(IntPtr hCall)
        {
            if (hCall == IntPtr.Zero)
                return -1;

            int result = lineDrop(hCall, IntPtr.Zero, 0);

            if (result > 0)
            {
                LogManager.Log("TAPI DropCall: 0x{0:X} (requestId={1})", hCall.ToInt64(), result);
            }
            else
            {
                LogManager.Log("TAPI DropCall: Fehlgeschlagen für 0x{0:X} (Fehler=0x{1:X8})", hCall.ToInt64(), result);
            }

            return result;
        }

        /// <summary>
        /// Find an active call by its TAPI call ID
        /// </summary>
        public TapiCallEvent FindCallById(int callId)
        {
            foreach (var kvp in _activeCalls)
            {
                if (kvp.Value.CallId == callId)
                    return kvp.Value;
            }
            return null;
        }

        /// <summary>
        /// Test a specific line by extension to verify it's actually connected and responsive.
        /// Includes automatic retry for transient errors with exponential backoff.
        /// </summary>
        public bool TestLine(string extension, Action<string> progressText = null, int maxRetries = 3)
        {
            var line = _lines.Values.FirstOrDefault(l => l.Extension == extension);
            if (line == null)
            {
                progressText?.Invoke($"Leitung {extension} nicht gefunden");
                LogManager.Log("TAPI TestLine: Nebenstelle {0} nicht gefunden", extension);
                return false;
            }

            progressText?.Invoke($"Prüfe Leitung {extension}...");

            // First check: is the handle valid?
            if (line.Handle == IntPtr.Zero)
            {
                progressText?.Invoke($"Leitung {extension} nicht verbunden");
                LogManager.Log("TAPI TestLine: Leitung {0} hat kein gültiges Handle", extension);
                return false;
            }

            // Execute test with retry logic for transient errors
            int attempt = 0;
            int delayMs = 500; // Start with 500ms delay

            while (attempt <= maxRetries)
            {
                var result = TestLineInternal(line, progressText, attempt > 0);

                switch (result.Category)
                {
                    case TapiErrorCategory.Success:
                        return result.IsConnected;

                    case TapiErrorCategory.Transient:
                        if (attempt < maxRetries)
                        {
                            attempt++;
                            progressText?.Invoke($"Wiederhole ({attempt}/{maxRetries})...");
                            LogManager.Log("TAPI TestLine {0}: Vorübergehender Fehler, Versuch {1}/{2} in {3}ms",
                                extension, attempt, maxRetries, delayMs);
                            Thread.Sleep(delayMs);
                            delayMs *= 2; // Exponential backoff
                            continue;
                        }
                        LogManager.Log("TAPI TestLine {0}: Vorübergehender Fehler, maximale Versuche erreicht", extension);
                        progressText?.Invoke($"Fehler nach {maxRetries} Versuchen");
                        return false;

                    case TapiErrorCategory.LineClosed:
                        progressText?.Invoke("Leitung getrennt - Neuverbindung erforderlich");
                        LogManager.Log("TAPI TestLine {0}: Leitung geschlossen/ungültig, Neuverbindung erforderlich", extension);
                        // Mark the line as disconnected
                        if (line.Handle != IntPtr.Zero)
                        {
                            _linesByHandle.TryRemove(line.Handle, out _);
                            line.Handle = IntPtr.Zero;
                            _onLineDisconnected?.Invoke(line);
                        }
                        return false;

                    case TapiErrorCategory.Shutdown:
                        progressText?.Invoke("TAPI wird heruntergefahren");
                        LogManager.Log("TAPI TestLine {0}: TAPI Neustart erforderlich", extension);
                        return false;

                    case TapiErrorCategory.Permanent:
                    default:
                        // Don't retry permanent errors
                        return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Internal test result with error category
        /// </summary>
        private struct TestLineResult
        {
            public TapiErrorCategory Category;
            public bool IsConnected;
            public int ErrorCode;
        }

        /// <summary>
        /// Internal method to perform the actual TAPI line test
        /// </summary>
        private TestLineResult TestLineInternal(TapiLineInfo line, Action<string> progressText, bool isRetry)
        {
            if (!isRetry)
                progressText?.Invoke("Abfrage TAPI Status...");

            int bufferSize = 256;
            IntPtr pDevStatus = IntPtr.Zero;

            try
            {
                pDevStatus = Marshal.AllocHGlobal(bufferSize);
                Marshal.WriteInt32(pDevStatus, bufferSize); // dwTotalSize

                int result = lineGetLineDevStatus(line.Handle, pDevStatus);

                if (result != LINEERR_OK)
                {
                    var category = CategorizeError(result);
                    var errorDesc = GetErrorDescription(result);

                    progressText?.Invoke($"TAPI: {errorDesc}");
                    LogManager.Log("TAPI TestLine {0}: lineGetLineDevStatus fehlgeschlagen: {1} (0x{2:X8})",
                        line.Extension, errorDesc, result);

                    return new TestLineResult
                    {
                        Category = category,
                        IsConnected = false,
                        ErrorCode = result
                    };
                }

                // Parse the LINEDEVSTATUS structure
                var devStatus = (LINEDEVSTATUS)Marshal.PtrToStructure(pDevStatus, typeof(LINEDEVSTATUS));

                // Check INSERVICE flag
                bool inService = (devStatus.dwDevStatusFlags & LINEDEVSTATUSFLAGS_INSERVICE) != 0;
                bool connected = (devStatus.dwDevStatusFlags & LINEDEVSTATUSFLAGS_CONNECTED) != 0;
                int activeCalls = devStatus.dwNumActiveCalls;
                int numOpens = devStatus.dwNumOpens;

                LogManager.Log("TAPI TestLine {0}: Opens={1}, ActiveCalls={2}, InService={3}, Connected={4}",
                    line.Extension, numOpens, activeCalls, inService, connected);

                // Build status message
                string statusMsg;
                bool isConnected;

                if (inService)
                {
                    if (activeCalls > 0)
                        statusMsg = $"Verbunden ({activeCalls} Anruf{(activeCalls > 1 ? "e" : "")})";
                    else
                        statusMsg = "Verbunden (bereit)";
                    isConnected = true;
                }
                else if (connected)
                {
                    statusMsg = "Verbunden (außer Betrieb)";
                    isConnected = true; // Handle is valid even if not in service
                }
                else
                {
                    statusMsg = "Nicht verbunden";
                    isConnected = false;
                }

                progressText?.Invoke(statusMsg);

                return new TestLineResult
                {
                    Category = TapiErrorCategory.Success,
                    IsConnected = isConnected,
                    ErrorCode = LINEERR_OK
                };
            }
            catch (Exception ex)
            {
                progressText?.Invoke($"Fehler: {ex.Message}");
                LogManager.Log("TAPI TestLine {0}: Ausnahme: {1}", line.Extension, ex.Message);

                return new TestLineResult
                {
                    Category = TapiErrorCategory.Transient, // Treat exceptions as potentially transient
                    IsConnected = false,
                    ErrorCode = -1
                };
            }
            finally
            {
                if (pDevStatus != IntPtr.Zero)
                    Marshal.FreeHGlobal(pDevStatus);
            }
        }
    }
}
