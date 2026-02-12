using System;
using System.Collections.Generic;
using System.Text;

namespace DatevConnector.Tapi
{
    /// <summary>
    /// Represents a 3CX pipe protocol message (comma-separated key=value pairs).
    /// Wire format: [2-byte LE payload length][UTF-16LE string]
    /// </summary>
    public class TapiMessage
    {
        private readonly Dictionary<string, string> _content;
        private static int _requestCounter;
        private static int _callIdCounter;

        /// <summary>
        /// Create empty message
        /// </summary>
        public TapiMessage()
        {
            _content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parse message from raw string (comma-separated key=value pairs)
        /// </summary>
        public TapiMessage(string rawMessage) : this()
        {
            if (string.IsNullOrEmpty(rawMessage))
                return;

            foreach (var part in rawMessage.Split(','))
            {
                int eqPos = part.IndexOf('=');
                if (eqPos > 0)
                {
                    string key = part.Substring(0, eqPos).Trim();
                    string value = part.Substring(eqPos + 1).Trim();
                    _content[key] = value;
                }
                else if (!string.IsNullOrWhiteSpace(part))
                {
                    _content[part.Trim()] = string.Empty;
                }
            }
        }

        // ===== Indexer / Key Access =====

        /// <summary>
        /// Get or set a value by key
        /// </summary>
        public string this[string key]
        {
            get => _content.TryGetValue(key, out var v) ? v : string.Empty;
            set => _content[key] = value ?? string.Empty;
        }

        /// <summary>
        /// Check if key exists
        /// </summary>
        public bool ContainsKey(string key) => _content.ContainsKey(key);

        // ===== Protocol Properties =====

        /// <summary>Command type (cmd field)</summary>
        public string Command => this[TapiCommands.KeyCommand];

        /// <summary>Call ID assigned by the Softphone</summary>
        public string CallId => this[TapiCommands.KeyCallId];

        /// <summary>Request ID for command/response correlation</summary>
        public string RequestId => this[TapiCommands.KeyRequestId];

        /// <summary>
        /// Reply field — present when this message is a response to a command.
        /// e.g. reply=CLIHELLO in response to cmd=SRVHELLO
        /// </summary>
        public string Reply => this[TapiCommands.KeyReply];

        /// <summary>
        /// Answer correlation — matches the __reqId of the command this replies to
        /// </summary>
        public string AnswerCorrelation => this[TapiCommands.KeyAnswerCorrelation];

        /// <summary>
        /// True if this message is a reply (has __answ# or reply field)
        /// </summary>
        public bool IsReply
        {
            get
            {
                return !string.IsNullOrEmpty(this[TapiCommands.KeyReply])
                    || !string.IsNullOrEmpty(this[TapiCommands.KeyAnswerCorrelation]);
            }
        }

        /// <summary>Caller/originator number (originator field)</summary>
        public string CallerNumber => this[TapiCommands.KeyOriginator];

        /// <summary>Caller/originator display name (originator_name field)</summary>
        public string CallerName => this[TapiCommands.KeyOriginatorName];

        /// <summary>Called/destination number (called_number field)</summary>
        public string CalledNumber => this[TapiCommands.KeyCalledNumber];

        /// <summary>Called party display name (called_name field)</summary>
        public string CalledName => this[TapiCommands.KeyCalledName];

        // ===== Serialization =====

        /// <summary>
        /// Encode message to string for transmission
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            bool first = true;

            foreach (var kvp in _content)
            {
                if (!first)
                    sb.Append(',');
                first = false;

                if (string.IsNullOrEmpty(kvp.Value))
                    sb.Append(kvp.Key);
                else
                    sb.Append(kvp.Key).Append('=').Append(kvp.Value);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Encode message to bytes for wire transmission.
        /// Format: [2-byte LE length of UTF-16LE payload][UTF-16LE payload]
        /// </summary>
        public byte[] ToBytes()
        {
            string content = ToString();
            byte[] contentBytes = Encoding.Unicode.GetBytes(content);

            int length = contentBytes.Length;
            byte[] result = new byte[2 + length];
            result[0] = (byte)(length & 0xFF);
            result[1] = (byte)((length >> 8) & 0xFF);

            Array.Copy(contentBytes, 0, result, 2, length);
            return result;
        }

        // ===== Factory Methods (Server → Softphone commands) =====

        /// <summary>
        /// Get the next request ID for outgoing commands
        /// </summary>
        private static string NextRequestId()
        {
            return System.Threading.Interlocked.Increment(ref _requestCounter).ToString();
        }

        /// <summary>
        /// Create SRVHELLO handshake message (sent after Softphone connects)
        /// </summary>
        public static TapiMessage CreateServerHello()
        {
            var msg = new TapiMessage();
            msg[TapiCommands.KeyRequestId] = NextRequestId();
            msg[TapiCommands.KeyCommand] = TapiCommands.ServerHello;
            return msg;
        }

        /// <summary>
        /// Generate the next call ID for outgoing calls (emulates TSP call ID assignment)
        /// </summary>
        private static string NextCallId()
        {
            return System.Threading.Interlocked.Increment(ref _callIdCounter).ToString();
        }

        /// <summary>
        /// Create MAKE-CALL command to initiate an outbound call via the Softphone.
        /// Protocol: __reqId={n},cmd=MAKE-CALL,callid={id},number={destination}
        /// Note: Softphone expects "number" for incoming dial commands (TapiCallData.Number),
        /// but uses "called_number" in outgoing notifications (SetPartiesInfo).
        /// callid is required — Softphone uses it to track the call in subsequent notifications.
        /// </summary>
        public static TapiMessage CreateMakeCall(string number)
        {
            var msg = new TapiMessage();
            msg[TapiCommands.KeyRequestId] = NextRequestId();
            msg[TapiCommands.KeyCommand] = TapiCommands.MakeCall;
            msg[TapiCommands.KeyCallId] = NextCallId();
            msg[TapiCommands.KeyNumber] = number;
            return msg;
        }

        /// <summary>
        /// Create DROP-CALL command to hang up a call.
        /// Protocol: __reqId={n},cmd=DROP-CALL,callid={id}
        /// </summary>
        public static TapiMessage CreateDropCall(string callId)
        {
            var msg = new TapiMessage();
            msg[TapiCommands.KeyRequestId] = NextRequestId();
            msg[TapiCommands.KeyCommand] = TapiCommands.DropCall;
            msg[TapiCommands.KeyCallId] = callId;
            return msg;
        }
    }
}
