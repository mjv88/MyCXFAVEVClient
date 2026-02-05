namespace DatevBridge.Tapi
{
    /// <summary>
    /// 3CX Named Pipe protocol constants.
    /// Protocol confirmed via PipeTest + ProcMon analysis against 3CXSoftphone.dll v20.0.1076.0.
    ///
    /// Architecture:
    ///   Bridge (pipe SERVER) ←→ 3CX Softphone (pipe CLIENT)
    ///   Pipe name: \\.\pipe\3CX_tsp_server_{extension}
    ///
    /// Wire format: [2-byte LE payload length][UTF-16LE string]
    /// Message format: comma-separated key=value pairs
    ///
    /// Reply pattern: Softphone echoes original cmd with __answ# and reply fields.
    ///   e.g. we send:    __reqId=1,cmd=SRVHELLO
    ///        reply is:   __reqId=1,cmd=SRVHELLO,__answ#=1,reply=CLIHELLO
    /// </summary>
    public static class TapiCommands
    {
        // ── Server → Softphone (commands WE send) ──────────────────
        public const string ServerHello = "SRVHELLO";
        public const string MakeCall = "MAKE-CALL";
        public const string DropCall = "DROP-CALL";

        // ── Softphone → Server (notifications we receive) ──────────
        public const string ClientHello = "CLIHELLO";  // reply to SRVHELLO
        public const string Ringing = "RINGING";
        public const string Ringback = "RINGBACK";
        public const string Connected = "CONNECTED";
        public const string Disconnected = "DISCONNECTED";
        public const string CallInfo = "CALL-INFO";
        // DROP-CALL is also sent BY the Softphone as a hangup notification

        // ── Message keys ───────────────────────────────────────────
        public const string KeyCommand = "cmd";
        public const string KeyRequestId = "__reqId";
        public const string KeyReply = "reply";
        public const string KeyAnswerCorrelation = "__answ#";
        public const string KeyCallId = "callid";
        public const string KeyStatus = "status";

        // Field names — outgoing (Server → Softphone: MAKE-CALL uses "number")
        public const string KeyNumber = "number";

        // Field names — incoming (Softphone → Server: SetPartiesInfo uses these)
        public const string KeyOriginator = "originator";
        public const string KeyOriginatorName = "originator_name";
        public const string KeyCalledNumber = "called_number";
        public const string KeyCalledName = "called_name";
    }
}
