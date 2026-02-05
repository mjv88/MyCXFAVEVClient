using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DatevBridge.Webclient
{
    /// <summary>
    /// Versioned JSON protocol for bridge <-> browser extension communication.
    /// Protocol version 1 — all messages carry "v":1.
    ///
    /// Extension -> Bridge: HELLO, CALL_EVENT
    /// Bridge -> Extension: HELLO_ACK, COMMAND
    /// </summary>
    public static class Protocol
    {
        public const int Version = 1;

        // Message types (Extension -> Bridge)
        public const string TypeHello = "HELLO";
        public const string TypeCallEvent = "CALL_EVENT";

        // Message types (Bridge -> Extension)
        public const string TypeHelloAck = "HELLO_ACK";
        public const string TypeCommand = "COMMAND";

        // Command names (Bridge -> Extension)
        public const string CmdDial = "DIAL";
        public const string CmdDrop = "DROP";

        // Call states (from extension)
        public const string StateOffered = "offered";
        public const string StateDialing = "dialing";
        public const string StateRinging = "ringing";
        public const string StateConnected = "connected";
        public const string StateEnded = "ended";

        // Call directions
        public const string DirectionInbound = "inbound";
        public const string DirectionOutbound = "outbound";

        // End reasons
        public const string ReasonHangup = "hangup";
        public const string ReasonBusy = "busy";
        public const string ReasonFailed = "failed";
        public const string ReasonUnknown = "unknown";
    }

    /// <summary>
    /// Parsed incoming message from the browser extension.
    /// Uses manual JSON parsing (no external dependencies, .NET 4.8 compatible).
    /// </summary>
    public class ExtensionMessage
    {
        public int Version { get; set; }
        public string Type { get; set; }

        // HELLO fields
        public string ExtensionNumber { get; set; }
        public string WebclientIdentity { get; set; }

        // CALL_EVENT fields
        public long Timestamp { get; set; }
        public string CallId { get; set; }
        public string Direction { get; set; }
        public string RemoteNumber { get; set; }
        public string RemoteName { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public string ContextExtension { get; set; }
        public string TabId { get; set; }

        /// <summary>
        /// Parse a JSON string into an ExtensionMessage.
        /// Minimal JSON parser for our known schema — no external library needed.
        /// </summary>
        public static ExtensionMessage Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            var msg = new ExtensionMessage();
            var dict = SimpleJsonParser.Parse(json);
            if (dict == null)
                return null;

            msg.Version = GetInt(dict, "v");
            msg.Type = GetString(dict, "type");

            // HELLO fields
            msg.ExtensionNumber = GetString(dict, "extension");
            msg.WebclientIdentity = GetString(dict, "identity");

            // CALL_EVENT nested "call" object
            msg.CallId = GetString(dict, "call.id");
            msg.Direction = GetString(dict, "call.direction");
            msg.RemoteNumber = GetString(dict, "call.remoteNumber");
            msg.RemoteName = GetString(dict, "call.remoteName");
            msg.State = GetString(dict, "call.state");
            msg.Reason = GetString(dict, "call.reason");
            msg.Timestamp = GetLong(dict, "ts");

            // Context
            msg.ContextExtension = GetString(dict, "context.extension");
            msg.TabId = GetString(dict, "context.tabId");

            return msg;
        }

        private static string GetString(Dictionary<string, string> dict, string key)
        {
            string val;
            return dict.TryGetValue(key, out val) ? val : null;
        }

        private static int GetInt(Dictionary<string, string> dict, string key)
        {
            string val;
            if (dict.TryGetValue(key, out val) && int.TryParse(val, out int result))
                return result;
            return 0;
        }

        private static long GetLong(Dictionary<string, string> dict, string key)
        {
            string val;
            if (dict.TryGetValue(key, out val) && long.TryParse(val, out long result))
                return result;
            return 0;
        }
    }

    /// <summary>
    /// Builds JSON command messages to send to the browser extension.
    /// </summary>
    public static class BridgeMessageBuilder
    {
        /// <summary>
        /// Build HELLO_ACK response after receiving HELLO from extension.
        /// </summary>
        public static string BuildHelloAck(string bridgeVersion, string extension)
        {
            var sb = new StringBuilder();
            sb.Append("{\"v\":").Append(Protocol.Version);
            sb.Append(",\"type\":\"").Append(Protocol.TypeHelloAck).Append("\"");
            sb.Append(",\"bridgeVersion\":\"").Append(EscapeJson(bridgeVersion)).Append("\"");
            sb.Append(",\"extension\":\"").Append(EscapeJson(extension)).Append("\"");
            sb.Append(",\"ready\":true");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Build DIAL command to send to the extension.
        /// </summary>
        public static string BuildDialCommand(string number, string syncId = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"v\":").Append(Protocol.Version);
            sb.Append(",\"type\":\"").Append(Protocol.TypeCommand).Append("\"");
            sb.Append(",\"cmd\":\"").Append(Protocol.CmdDial).Append("\"");
            sb.Append(",\"number\":\"").Append(EscapeJson(number)).Append("\"");
            if (!string.IsNullOrEmpty(syncId))
            {
                sb.Append(",\"context\":{\"syncId\":\"").Append(EscapeJson(syncId)).Append("\"}");
            }
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Build DROP command to send to the extension.
        /// </summary>
        public static string BuildDropCommand(string callId = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"v\":").Append(Protocol.Version);
            sb.Append(",\"type\":\"").Append(Protocol.TypeCommand).Append("\"");
            sb.Append(",\"cmd\":\"").Append(Protocol.CmdDrop).Append("\"");
            if (!string.IsNullOrEmpty(callId))
            {
                sb.Append(",\"callId\":\"").Append(EscapeJson(callId)).Append("\"");
            }
            sb.Append("}");
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                     .Replace("\"", "\\\"")
                     .Replace("\n", "\\n")
                     .Replace("\r", "\\r")
                     .Replace("\t", "\\t");
        }
    }

    /// <summary>
    /// Minimal JSON parser that flattens nested objects using dot notation.
    /// Handles the specific protocol schema without external dependencies.
    /// Example: {"call":{"id":"123"}} -> dict["call.id"] = "123"
    /// </summary>
    internal static class SimpleJsonParser
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json))
                return dict;

            try
            {
                int pos = 0;
                ParseObject(json, ref pos, "", dict);
            }
            catch
            {
                // Return whatever was parsed so far
            }
            return dict;
        }

        private static void ParseObject(string json, ref int pos, string prefix, Dictionary<string, string> dict)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != '{') return;
            pos++; // skip '{'

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}') { pos++; return; }
                if (json[pos] == ',') { pos++; continue; }

                // Read key
                string key = ReadString(json, ref pos);
                if (key == null) return;

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':') return;
                pos++; // skip ':'

                SkipWhitespace(json, ref pos);

                string fullKey = string.IsNullOrEmpty(prefix) ? key : prefix + "." + key;

                if (pos < json.Length && json[pos] == '{')
                {
                    // Nested object — recurse with dot-prefix
                    ParseObject(json, ref pos, fullKey, dict);
                }
                else if (pos < json.Length && json[pos] == '"')
                {
                    string val = ReadString(json, ref pos);
                    if (val != null) dict[fullKey] = val;
                }
                else
                {
                    // Number, bool, null
                    string val = ReadLiteral(json, ref pos);
                    if (val != null && val != "null") dict[fullKey] = val;
                }
            }
        }

        private static string ReadString(string json, ref int pos)
        {
            if (pos >= json.Length || json[pos] != '"') return null;
            pos++; // skip opening quote

            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    char esc = json[pos];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(esc); break;
                    }
                    pos++;
                }
                else if (c == '"')
                {
                    pos++; // skip closing quote
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
            return sb.ToString();
        }

        private static string ReadLiteral(string json, ref int pos)
        {
            int start = pos;
            while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']' && !char.IsWhiteSpace(json[pos]))
                pos++;
            return json.Substring(start, pos - start).Trim();
        }

        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos]))
                pos++;
        }
    }

    /// <summary>
    /// Handles Chrome/Edge Native Messaging framing.
    /// Messages are: [4-byte LE length][UTF-8 JSON payload]
    /// </summary>
    public static class NativeMessagingFraming
    {
        /// <summary>
        /// Read one framed message from stdin.
        /// Returns null on EOF or error.
        /// </summary>
        public static string ReadMessage(Stream input)
        {
            // Read 4-byte length (little-endian)
            byte[] lenBytes = new byte[4];
            int read = ReadExact(input, lenBytes, 0, 4);
            if (read < 4)
                return null;

            int length = BitConverter.ToInt32(lenBytes, 0);
            if (length <= 0 || length > 1024 * 1024) // Max 1MB
                return null;

            byte[] payload = new byte[length];
            read = ReadExact(input, payload, 0, length);
            if (read < length)
                return null;

            return Encoding.UTF8.GetString(payload);
        }

        /// <summary>
        /// Write one framed message to stdout.
        /// </summary>
        public static void WriteMessage(Stream output, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] lenBytes = BitConverter.GetBytes(payload.Length);
            output.Write(lenBytes, 0, 4);
            output.Write(payload, 0, payload.Length);
            output.Flush();
        }

        private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int n = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (n <= 0) break;
                totalRead += n;
            }
            return totalRead;
        }
    }
}
