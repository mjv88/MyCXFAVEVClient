using System;
using System.Runtime.InteropServices;

namespace DatevConnector.Interop
{
    /// <summary>
    /// Windows TAPI 2.x P/Invoke declarations (tapi32.dll)
    /// </summary>
    public static class TapiInterop
    {
        private const string TAPI_DLL = "tapi32.dll";

        // ===== TAPI Functions =====

        [DllImport(TAPI_DLL, CharSet = CharSet.Auto)]
        public static extern int lineInitializeEx(
            out IntPtr lphLineApp,
            IntPtr hInstance,
            IntPtr lpfnCallback,  // null when using USEEVENT
            string lpszFriendlyAppName,
            out int lpdwNumDevs,
            ref int lpdwAPIVersion,
            ref LINEINITIALIZEEXPARAMS lpLineInitializeExParams);

        [DllImport(TAPI_DLL)]
        public static extern int lineShutdown(IntPtr hLineApp);

        [DllImport(TAPI_DLL)]
        public static extern int lineNegotiateAPIVersion(
            IntPtr hLineApp,
            int dwDeviceID,
            int dwAPILowVersion,
            int dwAPIHighVersion,
            out int lpdwAPIVersion,
            out LINEEXTENSIONID lpExtensionID);

        [DllImport(TAPI_DLL, CharSet = CharSet.Auto)]
        public static extern int lineGetDevCaps(
            IntPtr hLineApp,
            int dwDeviceID,
            int dwAPIVersion,
            int dwExtVersion,
            IntPtr lpLineDevCaps);

        [DllImport(TAPI_DLL)]
        public static extern int lineOpen(
            IntPtr hLineApp,
            int dwDeviceID,
            out IntPtr lphLine,
            int dwAPIVersion,
            int dwExtVersion,
            IntPtr dwCallbackInstance,
            int dwPrivileges,
            int dwMediaModes,
            IntPtr lpCallParams);

        [DllImport(TAPI_DLL)]
        public static extern int lineClose(IntPtr hLine);

        [DllImport(TAPI_DLL, CharSet = CharSet.Auto)]
        public static extern int lineGetCallInfo(
            IntPtr hCall,
            IntPtr lpCallInfo);

        [DllImport(TAPI_DLL, CharSet = CharSet.Auto)]
        public static extern int lineMakeCall(
            IntPtr hLine,
            out IntPtr lphCall,
            string lpszDestAddress,
            int dwCountryCode,
            IntPtr lpCallParams);

        [DllImport(TAPI_DLL)]
        public static extern int lineDrop(
            IntPtr hCall,
            IntPtr lpsUserUserInfo,
            int dwSize);

        [DllImport(TAPI_DLL)]
        public static extern int lineDeallocateCall(IntPtr hCall);

        [DllImport(TAPI_DLL)]
        public static extern int lineGetMessage(
            IntPtr hLineApp,
            ref LINEMESSAGE lpMessage,
            int dwTimeout);

        [DllImport(TAPI_DLL)]
        public static extern int lineSetStatusMessages(
            IntPtr hLine,
            int dwLineStates,
            int dwAddressStates);

        [DllImport(TAPI_DLL)]
        public static extern int lineGetLineDevStatus(
            IntPtr hLine,
            IntPtr lpLineDevStatus);

        // ===== Kernel32 for event handling =====

        [DllImport("kernel32.dll")]
        public static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

        // ===== Constants =====

        // TAPI versions
        public const int TAPI_VERSION_1_0 = 0x00010000;
        public const int TAPI_VERSION_2_2 = 0x00020002;

        // lineInitializeEx options
        public const int LINEINITIALIZEEXOPTION_USEEVENT = 0x00000002;

        // Line privileges
        public const int LINECALLPRIVILEGE_MONITOR = 0x00000002;
        public const int LINECALLPRIVILEGE_OWNER = 0x00000004;

        // Media modes
        public const int LINEMEDIAMODE_UNKNOWN = 0x00000002;
        public const int LINEMEDIAMODE_INTERACTIVEVOICE = 0x00000004;
        public const int LINEMEDIAMODE_AUTOMATEDVOICE = 0x00000008;
        public const int LINEMEDIAMODE_DATAMODEM = 0x00000010;

        // Line call states
        public const int LINECALLSTATE_IDLE = 0x00000001;
        public const int LINECALLSTATE_OFFERING = 0x00000002;
        public const int LINECALLSTATE_ACCEPTED = 0x00000004;
        public const int LINECALLSTATE_DIALTONE = 0x00000008;
        public const int LINECALLSTATE_DIALING = 0x00000010;
        public const int LINECALLSTATE_RINGBACK = 0x00000020;
        public const int LINECALLSTATE_BUSY = 0x00000040;
        public const int LINECALLSTATE_CONNECTED = 0x00000100;
        public const int LINECALLSTATE_PROCEEDING = 0x00000200;
        public const int LINECALLSTATE_DISCONNECTED = 0x00004000;

        // Line messages
        public const int LINE_CALLSTATE = 2;
        public const int LINE_CLOSE = 3;
        public const int LINE_CALLINFO = 1;
        public const int LINE_LINEDEVSTATE = 7;
        public const int LINE_REPLY = 12;
        public const int LINE_CREATE = 18;
        public const int LINE_APPNEWCALL = 23;

        // Line device states
        public const int LINEDEVSTATE_RINGING = 0x00080000;

        // Call origin
        public const int LINECALLORIGIN_OUTBOUND = 0x00000001;
        public const int LINECALLORIGIN_INTERNAL = 0x00000002;
        public const int LINECALLORIGIN_EXTERNAL = 0x00000004;
        public const int LINECALLORIGIN_UNKNOWN = 0x00000010;
        public const int LINECALLORIGIN_UNAVAIL = 0x00000020;
        public const int LINECALLORIGIN_INBOUND = 0x00000080;

        // Wait constants
        public const int WAIT_OBJECT_0 = 0;
        public const int WAIT_TIMEOUT = 258;
        public const int INFINITE = -1;

        // Error codes (from Windows SDK tapi.h)
        public const int LINEERR_OK = 0;
        public const int LINEERR_ALLOCATED = unchecked((int)0x80000001);
        public const int LINEERR_BADDEVICEID = unchecked((int)0x80000002);
        public const int LINEERR_INCOMPATIBLEAPIVERSION = unchecked((int)0x8000000A);
        public const int LINEERR_INCOMPATIBLEEXTVERSION = unchecked((int)0x8000000B);
        public const int LINEERR_INUSE = unchecked((int)0x8000000D);
        public const int LINEERR_INVALAPPHANDLE = unchecked((int)0x80000012);
        public const int LINEERR_INVALCALLPRIVILEGE = unchecked((int)0x80000018);
        public const int LINEERR_INVALLINEHANDLE = unchecked((int)0x80000029);
        public const int LINEERR_INVALLINESTATE = unchecked((int)0x8000002A);
        public const int LINEERR_INVALMEDIAMODE = unchecked((int)0x8000002D);
        public const int LINEERR_INVALPRIVSELECT = unchecked((int)0x80000036);
        public const int LINEERR_NODEVICE = unchecked((int)0x80000042);
        public const int LINEERR_NODRIVER = unchecked((int)0x80000043);
        public const int LINEERR_NOMEM = unchecked((int)0x80000044);
        public const int LINEERR_NOTOWNER = unchecked((int)0x80000046);
        public const int LINEERR_NOTREGISTERED = unchecked((int)0x80000047);
        public const int LINEERR_OPERATIONFAILED = unchecked((int)0x80000048);
        public const int LINEERR_OPERATIONUNAVAIL = unchecked((int)0x80000049);
        public const int LINEERR_RESOURCEUNAVAIL = unchecked((int)0x8000004B);
        public const int LINEERR_STRUCTURETOOSMALL = unchecked((int)0x8000004D);
        public const int LINEERR_UNINITIALIZED = unchecked((int)0x80000050);
        public const int LINEERR_REINIT = unchecked((int)0x80000052);

        /// <summary>
        /// TAPI error categories for handling
        /// </summary>
        public enum TapiErrorCategory
        {
            /// <summary>No error</summary>
            Success,
            /// <summary>Transient error - safe to retry</summary>
            Transient,
            /// <summary>Line handle invalid or closed - needs reconnect</summary>
            LineClosed,
            /// <summary>TAPI shutting down or needs reinitialization</summary>
            Shutdown,
            /// <summary>Permanent error - do not retry</summary>
            Permanent
        }

        /// <summary>
        /// Categorize a TAPI error code for appropriate handling
        /// </summary>
        public static TapiErrorCategory CategorizeError(int errorCode)
        {
            if (errorCode == LINEERR_OK)
                return TapiErrorCategory.Success;

            switch (errorCode)
            {
                // Transient errors - worth retrying
                case LINEERR_RESOURCEUNAVAIL:
                case LINEERR_NOMEM:
                case LINEERR_OPERATIONFAILED:
                    return TapiErrorCategory.Transient;

                // Line closed/invalid - needs reconnect, not retry
                case LINEERR_INVALLINEHANDLE:
                case LINEERR_INVALLINESTATE:
                case LINEERR_BADDEVICEID:
                    return TapiErrorCategory.LineClosed;

                // TAPI shutdown/reinit needed
                case LINEERR_REINIT:
                case LINEERR_UNINITIALIZED:
                    return TapiErrorCategory.Shutdown;

                // Permanent errors - don't retry
                case LINEERR_ALLOCATED:
                case LINEERR_NODEVICE:
                case LINEERR_NODRIVER:
                case LINEERR_NOTREGISTERED:
                case LINEERR_INCOMPATIBLEAPIVERSION:
                case LINEERR_OPERATIONUNAVAIL:
                default:
                    return TapiErrorCategory.Permanent;
            }
        }

        /// <summary>
        /// Get a human-readable description of a TAPI error code
        /// </summary>
        public static string GetErrorDescription(int errorCode)
        {
            switch (errorCode)
            {
                case LINEERR_OK: return "Erfolgreich";
                case LINEERR_ALLOCATED: return "Leitung bereits belegt";
                case LINEERR_BADDEVICEID: return "Ungültige Geräte-ID";
                case LINEERR_INCOMPATIBLEAPIVERSION: return "Inkompatible API-Version";
                case LINEERR_INCOMPATIBLEEXTVERSION: return "Inkompatible Erweiterungsversion";
                case LINEERR_INUSE: return "Leitung wird bereits verwendet";
                case LINEERR_INVALAPPHANDLE: return "Ungültiges Anwendungshandle";
                case LINEERR_INVALCALLPRIVILEGE: return "Ungültiges Anrufprivileg";
                case LINEERR_INVALLINEHANDLE: return "Ungültiges Leitungshandle";
                case LINEERR_INVALLINESTATE: return "Ungültiger Leitungszustand";
                case LINEERR_INVALMEDIAMODE: return "Ungültiger Medien-Modus";
                case LINEERR_INVALPRIVSELECT: return "Ungültige Privilegauswahl";
                case LINEERR_NODEVICE: return "Kein Gerät gefunden";
                case LINEERR_NODRIVER: return "TAPI-Treiber nicht gefunden";
                case LINEERR_NOMEM: return "Nicht genügend Speicher";
                case LINEERR_NOTOWNER: return "Kein Besitzer des Anrufs";
                case LINEERR_NOTREGISTERED: return "TAPI-Treiber nicht registriert (3CX Dialer gestartet?)";
                case LINEERR_OPERATIONFAILED: return "Operation fehlgeschlagen";
                case LINEERR_OPERATIONUNAVAIL: return "Operation nicht verfügbar";
                case LINEERR_REINIT: return "TAPI Neuinitialisierung erforderlich";
                case LINEERR_RESOURCEUNAVAIL: return "Ressource nicht verfügbar";
                case LINEERR_STRUCTURETOOSMALL: return "Puffergröße zu klein";
                case LINEERR_UNINITIALIZED: return "TAPI nicht initialisiert";
                default: return $"Unbekannter Fehler (0x{errorCode:X8})";
            }
        }

        // ===== Structures =====

        [StructLayout(LayoutKind.Sequential)]
        public struct LINEINITIALIZEEXPARAMS
        {
            public int dwTotalSize;
            public int dwNeededSize;
            public int dwUsedSize;
            public int dwOptions;
            public IntPtr hEvent;       // Handles union
            public IntPtr hCompletionPort;
            public int dwCompletionKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LINEEXTENSIONID
        {
            public int dwExtensionID0;
            public int dwExtensionID1;
            public int dwExtensionID2;
            public int dwExtensionID3;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LINEMESSAGE
        {
            public IntPtr hDevice;
            public int dwMessageID;
            public IntPtr dwCallbackInstance;
            public IntPtr dwParam1;
            public IntPtr dwParam2;
            public IntPtr dwParam3;
        }

        // LINEDEVCAPS - variable size structure (header only)
        [StructLayout(LayoutKind.Sequential)]
        public struct LINEDEVCAPS
        {
            public int dwTotalSize;
            public int dwNeededSize;
            public int dwUsedSize;
            public int dwProviderInfoSize;
            public int dwProviderInfoOffset;
            public int dwSwitchInfoSize;
            public int dwSwitchInfoOffset;
            public int dwPermanentLineID;
            public int dwLineNameSize;
            public int dwLineNameOffset;
            public int dwStringFormat;
            public int dwAddressModes;
            public int dwNumAddresses;
            public int dwBearerModes;
            public int dwMaxRate;
            public int dwMediaModes;
            // ... more fields follow but we only need through dwLineNameOffset
        }

        // LINECALLINFO - variable size structure (header)
        [StructLayout(LayoutKind.Sequential)]
        public struct LINECALLINFO
        {
            public int dwTotalSize;
            public int dwNeededSize;
            public int dwUsedSize;
            public int hLine;
            public int dwLineDeviceID;
            public int dwAddressID;
            public int dwBearerMode;
            public int dwRate;
            public int dwMediaMode;
            public int dwAppSpecific;
            public int dwCallID;
            public int dwRelatedCallID;
            public int dwCallParamFlags;
            public int dwCallStates;
            public int dwMonitorDigitModes;
            public int dwMonitorMediaModes;
            public int dwDialParams_1;
            public int dwDialParams_2;
            public int dwDialParams_3;
            public int dwDialParams_4;
            public int dwOrigin;
            public int dwReason;
            public int dwCompletionID;
            public int dwNumOwners;
            public int dwNumMonitors;
            public int dwCountryCode;
            public int dwTrunk;
            // Caller ID
            public int dwCallerIDFlags;
            public int dwCallerIDSize;
            public int dwCallerIDOffset;
            public int dwCallerIDNameSize;
            public int dwCallerIDNameOffset;
            // Called ID
            public int dwCalledIDFlags;
            public int dwCalledIDSize;
            public int dwCalledIDOffset;
            public int dwCalledIDNameSize;
            public int dwCalledIDNameOffset;
            // Connected ID
            public int dwConnectedIDFlags;
            public int dwConnectedIDSize;
            public int dwConnectedIDOffset;
            public int dwConnectedIDNameSize;
            public int dwConnectedIDNameOffset;
            // Redirection ID
            public int dwRedirectionIDFlags;
            public int dwRedirectionIDSize;
            public int dwRedirectionIDOffset;
            public int dwRedirectionIDNameSize;
            public int dwRedirectionIDNameOffset;
            // Redirecting ID
            public int dwRedirectingIDFlags;
            public int dwRedirectingIDSize;
            public int dwRedirectingIDOffset;
            public int dwRedirectingIDNameSize;
            public int dwRedirectingIDNameOffset;
        }

        // LINEDEVSTATUS - variable size structure (header)
        [StructLayout(LayoutKind.Sequential)]
        public struct LINEDEVSTATUS
        {
            public int dwTotalSize;
            public int dwNeededSize;
            public int dwUsedSize;
            public int dwNumOpens;
            public int dwOpenMediaModes;
            public int dwNumActiveCalls;
            public int dwNumOnHoldCalls;
            public int dwNumOnHoldPendCalls;
            public int dwLineFeatures;
            public int dwNumCallCompletions;
            public int dwRingMode;
            public int dwSignalLevel;
            public int dwBatteryLevel;
            public int dwRoamMode;
            public int dwDevStatusFlags;
            public int dwTerminalModesSize;
            public int dwTerminalModesOffset;
            public int dwDevSpecificSize;
            public int dwDevSpecificOffset;
            public int dwAvailableMediaModes;
            public int dwAppInfoSize;
            public int dwAppInfoOffset;
        }

        // LINEDEVSTATUSFLAGS constants
        public const int LINEDEVSTATUSFLAGS_CONNECTED = 0x00000001;
        public const int LINEDEVSTATUSFLAGS_MSGWAIT = 0x00000002;
        public const int LINEDEVSTATUSFLAGS_INSERVICE = 0x00000004;
        public const int LINEDEVSTATUSFLAGS_LOCKED = 0x00000008;
    }
}
