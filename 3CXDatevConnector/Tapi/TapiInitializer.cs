using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DatevConnector.Datev.Managers;
using static DatevConnector.Interop.TapiInterop;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Handles TAPI subsystem initialization, line discovery, and shutdown.
    /// </summary>
    internal class TapiInitializer
    {
        private readonly string _lineNameFilter;
        private readonly string _extensionFilter;
        private int _numDevices;

        public IntPtr LineAppHandle { get; private set; }
        public IntPtr EventHandle { get; private set; }

        public TapiInitializer(string lineNameFilter, string extensionFilter)
        {
            _lineNameFilter = lineNameFilter;
            _extensionFilter = extensionFilter;
        }

        /// <summary>
        /// Initialize TAPI subsystem (lineInitializeEx)
        /// </summary>
        public void Initialize(Action<string> progressText = null)
        {
            progressText?.Invoke("Initialisiere TAPI...");

            var initParams = new LINEINITIALIZEEXPARAMS();
            initParams.dwTotalSize = Marshal.SizeOf(typeof(LINEINITIALIZEEXPARAMS));
            initParams.dwOptions = LINEINITIALIZEEXOPTION_USEEVENT;

            int apiVersion = TAPI_VERSION_2_2;
            IntPtr hLineApp;

            int result = lineInitializeEx(
                out hLineApp,
                IntPtr.Zero,
                IntPtr.Zero,  // No callback - using events
                "DatevConnector",
                out _numDevices,
                ref apiVersion,
                ref initParams);

            if (result != LINEERR_OK)
            {
                progressText?.Invoke("TAPI Initialisierung fehlgeschlagen");
                throw new InvalidOperationException($"lineInitializeEx failed: 0x{result:X8}");
            }

            LineAppHandle = hLineApp;
            EventHandle = initParams.hEvent;

            LogManager.Log("TAPI initialized: {0} line devices", _numDevices);
            progressText?.Invoke($"{_numDevices} TAPI Leitungen gefunden");
        }

        /// <summary>
        /// Find ALL matching line devices by name filter.
        /// Must be called after Initialize().
        /// </summary>
        public void FindLines(ConcurrentDictionary<int, TapiLineInfo> lines, Action<string> progressText = null)
        {
            progressText?.Invoke("Suche 3CX TAPI Leitungen...");
            lines.Clear();

            for (int i = 0; i < _numDevices; i++)
            {
                LINEEXTENSIONID extensionId;
                int negotiatedVersion;

                int result = lineNegotiateAPIVersion(
                    LineAppHandle, i,
                    TAPI_VERSION_1_0, TAPI_VERSION_2_2,
                    out negotiatedVersion, out extensionId);

                if (result != LINEERR_OK)
                    continue;

                string lineName = GetLineName(i, negotiatedVersion);
                if (lineName == null)
                    continue;

                bool matches = string.IsNullOrEmpty(_lineNameFilter) ||
                               lineName.IndexOf(_lineNameFilter, StringComparison.OrdinalIgnoreCase) >= 0;

                string lineExtension = TapiLineInfo.ParseExtension(lineName);

                // Also filter by extension number if specified
                if (matches && !string.IsNullOrEmpty(_extensionFilter))
                {
                    if (lineExtension != _extensionFilter)
                    {
                        LogManager.Log("Skipping TAPI line {0}: \"{1}\" (Ext: {2}) - does not match configured extension {3}",
                            i, lineName, lineExtension, _extensionFilter);
                        continue;
                    }
                }

                if (matches)
                {
                    var lineInfo = new TapiLineInfo
                    {
                        DeviceId = i,
                        LineName = lineName,
                        Extension = lineExtension,
                        ApiVersion = negotiatedVersion,
                        Handle = IntPtr.Zero
                    };

                    lines[i] = lineInfo;
                    LogManager.Log("Discovered 3CX TAPI line {0}: \"{1}\" (Ext: {2})", i, lineName, lineInfo.Extension);
                    progressText?.Invoke($"3CX Leitung gefunden: {lineInfo.Extension}");
                }
            }

            if (lines.Count == 0)
            {
                if (!string.IsNullOrEmpty(_extensionFilter))
                    LogManager.Warning("No TAPI line found for extension {0} (filter=\"{1}\", {2} devices scanned)",
                        _extensionFilter, _lineNameFilter ?? "(any)", _numDevices);
                else
                    LogManager.Log("No TAPI line matching \"{0}\" found", _lineNameFilter ?? "(any)");
            }
        }

        /// <summary>
        /// Get the name of a line device
        /// </summary>
        private string GetLineName(int deviceId, int apiVersion)
        {
            int bufferSize = 1024;
            IntPtr pDevCaps = IntPtr.Zero;

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (pDevCaps != IntPtr.Zero)
                        Marshal.FreeHGlobal(pDevCaps);

                    pDevCaps = Marshal.AllocHGlobal(bufferSize);
                    Marshal.WriteInt32(pDevCaps, bufferSize); // dwTotalSize

                    int result = lineGetDevCaps(LineAppHandle, deviceId, apiVersion, 0, pDevCaps);
                    if (result != LINEERR_OK)
                        return null;

                    int neededSize = Marshal.ReadInt32(pDevCaps, 4); // dwNeededSize
                    if (neededSize <= bufferSize)
                    {
                        var devCaps = (LINEDEVCAPS)Marshal.PtrToStructure(pDevCaps, typeof(LINEDEVCAPS));
                        if (devCaps.dwLineNameSize > 0 && devCaps.dwLineNameOffset > 0)
                        {
                            IntPtr namePtr = IntPtr.Add(pDevCaps, devCaps.dwLineNameOffset);
                            return Marshal.PtrToStringAuto(namePtr, devCaps.dwLineNameSize / 2).TrimEnd('\0');
                        }
                        return null;
                    }

                    bufferSize = neededSize;
                }
            }
            finally
            {
                if (pDevCaps != IntPtr.Zero)
                    Marshal.FreeHGlobal(pDevCaps);
            }

            return null;
        }

        /// <summary>
        /// Shutdown TAPI: close all lines and call lineShutdown
        /// </summary>
        public void Shutdown(ConcurrentDictionary<int, TapiLineInfo> lines, ConcurrentDictionary<IntPtr, TapiLineInfo> linesByHandle)
        {
            foreach (var line in lines.Values)
            {
                if (line.IsConnected)
                {
                    LogManager.Log("TAPI: Closing line {0} ({1})", line.DeviceId, line.Extension);
                    lineClose(line.Handle);
                    line.Handle = IntPtr.Zero;
                }
            }

            linesByHandle.Clear();

            if (LineAppHandle != IntPtr.Zero)
            {
                lineShutdown(LineAppHandle);
                LineAppHandle = IntPtr.Zero;
            }

            // _hEvent is owned by TAPI, closed by lineShutdown
            EventHandle = IntPtr.Zero;

            LogManager.Log("TAPI line monitor disposed ({0} lines)", lines.Count);
        }
    }
}
