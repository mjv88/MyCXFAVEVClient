using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DatevConnector.Datev.Managers;
using static DatevConnector.Interop.TapiInterop;

namespace DatevConnector.Tapi
{
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

        public bool Initialize(Action<string> progressText = null)
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
                LogManager.Log("lineInitializeEx fehlgeschlagen: 0x{0:X8}", result);
                return false;
            }

            LineAppHandle = hLineApp;
            EventHandle = initParams.hEvent;

            LogManager.Log(_numDevices == 0
                ? "TAPI initialisiert: Keine Leitung gefunden"
                : string.Format("TAPI initialisiert: {0} Leitung(en)", _numDevices));
            progressText?.Invoke($"{_numDevices} TAPI Leitungen gefunden");
            return true;
        }

        /// <summary>
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

                if (matches && !string.IsNullOrEmpty(_extensionFilter))
                {
                    if (lineExtension != _extensionFilter)
                    {
                        LogManager.Log("Überspringe TAPI Leitung {0}: \"{1}\" (Nst: {2}) - stimmt nicht mit konfigurierter Nebenstelle {3} überein",
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
                    LogManager.Log("3CX TAPI Leitung entdeckt {0}: \"{1}\" (Nst: {2})", i, lineName, lineInfo.Extension);
                    progressText?.Invoke($"3CX Leitung gefunden: {lineInfo.Extension}");
                }
            }

            if (lines.Count == 0)
            {
                if (!string.IsNullOrEmpty(_extensionFilter))
                    LogManager.Warning("Keine TAPI Leitung gefunden für Nebenstelle {0} (Filter=\"{1}\", {2} Geräte gescannt)",
                        _extensionFilter, _lineNameFilter ?? "(any)", _numDevices);
                else
                    LogManager.Log("Keine 3CX TAPI Leitung gefunden");
            }
        }

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

        public void Shutdown(ConcurrentDictionary<int, TapiLineInfo> lines, ConcurrentDictionary<IntPtr, TapiLineInfo> linesByHandle)
        {
            foreach (var line in lines.Values)
            {
                if (line.IsConnected)
                {
                    LogManager.Log("TAPI: Schließe Leitung {0} ({1})", line.DeviceId, line.Extension);
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

            LogManager.Log("TAPI Leitungsmonitor beendet ({0} Leitungen)", lines.Count);
        }
    }
}
