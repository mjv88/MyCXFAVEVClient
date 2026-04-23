using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Datev.Sdd.Data.ClientInterfaces;
using Datev.Sdd.Data.ClientPlugIn;

namespace DatevConnector.SddProxy
{
    /// <summary>
    /// net48 windowless subprocess that owns all DATEV Stammdaten-Dienst (SDD)
    /// interaction for the .NET 9 tray. Speaks line-delimited JSON over a
    /// named pipe. See the protocol section for the wire contract.
    ///
    /// Why net48: DATEV's SDD client DLLs depend on .NET Framework Remoting
    /// (System.Runtime.Remoting.Messaging.Header, etc.) which no longer exists
    /// in .NET 5+. On Terminal Server sessions that dependency fails at
    /// type-load time, so we keep all SDD calls in this framework-only process
    /// and let the tray talk to us over the pipe.
    /// </summary>
    internal static class Program
    {
        private const string DefaultPipeBaseName = "3cxdatev-sddproxy";
        private const int MaxMessageBytes = 64 * 1024 * 1024; // 64 MB cap for one request/response

        private static string _logPath;
        private static readonly object _logLock = new object();
        private static int _parentPid = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            string pipeName = DefaultPipeBaseName;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--pipe-name=", StringComparison.OrdinalIgnoreCase))
                {
                    pipeName = arg.Substring("--pipe-name=".Length).Trim();
                }
                else if (arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Substring("--parent-pid=".Length).Trim(),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                    {
                        _parentPid = pid;
                    }
                }
            }

            InitLog();
            // Must be registered before any DATEV SDD type is touched.
            DatevAssemblyResolver.Register();
            Log("=== SddProxy starting: PID={0} pipe=\\\\.\\pipe\\{1} parentPid={2} ===",
                Process.GetCurrentProcess().Id, pipeName, _parentPid);

            Thread watchdog = null;
            if (_parentPid > 0)
            {
                watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "SddProxy-Watchdog" };
                watchdog.Start();
            }

            try
            {
                RunPipeLoop(pipeName);
            }
            catch (Exception ex)
            {
                Log("Fatal: {0}", ex);
                return 1;
            }

            Log("=== SddProxy exiting ===");
            return 0;
        }

        // ===== Watchdog =====

        private static void WatchdogLoop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(5000);
                    Process parent = null;
                    try { parent = Process.GetProcessById(_parentPid); }
                    catch (ArgumentException) { parent = null; }

                    if (parent == null || parent.HasExited)
                    {
                        Log("Watchdog: parent PID {0} gone, exiting.", _parentPid);
                        Environment.Exit(0);
                    }
                }
                catch (Exception ex)
                {
                    Log("Watchdog error (continuing): {0}", ex.Message);
                }
            }
        }

        // ===== Pipe server loop =====

        private static void RunPipeLoop(string pipeName)
        {
            while (true)
            {
                using (var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous))
                {
                    Log("Waiting for client on pipe '{0}'...", pipeName);
                    server.WaitForConnection();
                    Log("Client connected.");

                    try
                    {
                        ServeClient(server);
                    }
                    catch (Exception ex)
                    {
                        Log("Client session error: {0}", ex.Message);
                    }

                    if (server.IsConnected)
                    {
                        try { server.Disconnect(); } catch { }
                    }
                    Log("Client disconnected.");
                }
            }
        }

        private static void ServeClient(NamedPipeServerStream server)
        {
            // We intentionally keep StreamReader/StreamWriter with a leaveOpen-style
            // lifetime: they're disposed when this method returns because the server
            // itself is disposed in the outer loop.
            var reader = new StreamReader(server, new UTF8Encoding(false, false), false, 8192, leaveOpen: true);
            var writer = new StreamWriter(server, new UTF8Encoding(false, false), 8192, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            while (server.IsConnected)
            {
                string line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (IOException ex)
                {
                    Log("Read error: {0}", ex.Message);
                    return;
                }

                if (line == null)
                {
                    Log("Client closed the pipe.");
                    return;
                }
                if (line.Length == 0) continue;

                if (line.Length > MaxMessageBytes)
                {
                    WriteError(writer, "request too large");
                    continue;
                }

                string op = ExtractScalar(line, "op") ?? "";
                Log("Request: op={0} ({1} bytes)", op, line.Length);

                string response;
                bool shouldExit = false;
                try
                {
                    response = Dispatch(op, line, out shouldExit);
                }
                catch (Exception ex)
                {
                    Log("Handler error for op '{0}': {1}", op, ex);
                    response = BuildError(ex.Message);
                }

                try
                {
                    writer.WriteLine(response);
                }
                catch (IOException ex)
                {
                    Log("Write error: {0}", ex.Message);
                    return;
                }

                Log("Response: op={0} ok={1} ({2} bytes)",
                    op, response.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0, response.Length);

                if (shouldExit)
                {
                    Log("EXIT requested, shutting down.");
                    try { writer.Flush(); } catch { }
                    Environment.Exit(0);
                }
            }
        }

        private static string Dispatch(string op, string requestJson, out bool shouldExit)
        {
            shouldExit = false;
            switch (op)
            {
                case "PING":
                    return "{\"ok\":true}";

                case "GET_CONTACTS":
                    {
                        bool activeOnly = string.Equals(
                            ExtractScalar(requestJson, "activeOnly") ?? "false",
                            "true", StringComparison.OrdinalIgnoreCase);
                        List<ContactDto> contacts = FetchAllContacts(activeOnly);
                        return BuildContactsResponse(contacts);
                    }

                case "LOOKUP":
                    {
                        // Simple lookup: scan all contacts and return the first match
                        // on any of its normalized phones. The tray is the cache;
                        // this path mostly exists for diagnostics and fallback.
                        string number = ExtractScalar(requestJson, "number") ?? "";
                        List<ContactDto> contacts = FetchAllContacts(activeOnly: false);
                        ContactDto hit = FindByNumber(contacts, number);
                        return BuildLookupResponse(hit);
                    }

                case "EXIT":
                    shouldExit = true;
                    return "{\"ok\":true}";

                default:
                    return BuildError("unknown op: " + op);
            }
        }

        // ===== SDD fetching (ported from DatevContactRepository) =====

        private static List<ContactDto> FetchAllContacts(bool activeOnly)
        {
            var result = new List<ContactDto>();

            RecipientContactDetail[] recipients;
            try
            {
                recipients = GetRecipients(activeOnly);
            }
            catch (Exception ex)
            {
                Log("GetRecipients failed: {0}", ex);
                recipients = new RecipientContactDetail[0];
            }

            foreach (var r in recipients)
            {
                if (r?.Contact == null) continue;
                if (!HasPhone(r.Contact.Communications)) continue;

                var dto = new ContactDto
                {
                    Id = r.Id,
                    DisplayName = r.Contact.Name,
                    FirstName = "",
                    LastName = "",
                    Kind = "Adressat",
                    IsActive = r.Status != 0,
                    IsPrivatePerson = r.Contact.Type == ContactType.Person,
                    Phones = MapPhones(r.Contact.Communications)
                };
                result.Add(dto);
            }

            InstitutionContactDetail[] institutions;
            try
            {
                institutions = GetInstitutions();
            }
            catch (Exception ex)
            {
                Log("GetInstitutions failed: {0}", ex);
                institutions = new InstitutionContactDetail[0];
            }

            foreach (var inst in institutions)
            {
                if (inst == null) continue;
                if (!HasPhone(inst.Communications)) continue;
                var dto = new ContactDto
                {
                    Id = inst.Id,
                    DisplayName = inst.Name,
                    FirstName = "",
                    LastName = "",
                    Kind = "Institution",
                    IsActive = true, // institutions have no Status field; assume active
                    IsPrivatePerson = false,
                    Phones = MapPhones(inst.Communications)
                };
                result.Add(dto);
            }

            int totalPhones = result.Sum(c => c.Phones?.Count ?? 0);
            Log("SDD: fetched {0} contacts ({1} phones), activeOnly={2}",
                result.Count, totalPhones, activeOnly);
            return result;
        }

        private static bool HasPhone(Communication[] comms)
        {
            if (comms == null || comms.Length == 0) return false;
            foreach (var c in comms)
            {
                if (c == null) continue;
                if (c.Medium != Medium.Phone) continue;
                // Accept if either raw or normalized is non-empty — the tray
                // re-normalizes before use, so we don't need to pre-filter for
                // emptiness after normalization here.
                if (!string.IsNullOrWhiteSpace(c.NormalizedNumber) ||
                    !string.IsNullOrWhiteSpace(c.Number))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<PhoneDto> MapPhones(Communication[] comms)
        {
            var list = new List<PhoneDto>();
            if (comms == null) return list;
            foreach (var c in comms)
            {
                if (c == null) continue;
                if (c.Medium != Medium.Phone) continue;
                if (string.IsNullOrWhiteSpace(c.NormalizedNumber) &&
                    string.IsNullOrWhiteSpace(c.Number))
                {
                    continue;
                }
                list.Add(new PhoneDto
                {
                    Number = c.Number ?? "",
                    NormalizedNumber = c.NormalizedNumber ?? ""
                });
            }
            return list;
        }

        private static ContactDto FindByNumber(List<ContactDto> contacts, string number)
        {
            string query = DigitsOnly(number);
            if (query.Length == 0) return null;
            foreach (var c in contacts)
            {
                if (c.Phones == null) continue;
                foreach (var p in c.Phones)
                {
                    string candidate = DigitsOnly(
                        !string.IsNullOrEmpty(p.NormalizedNumber) ? p.NormalizedNumber : p.Number);
                    if (candidate.Length == 0) continue;
                    if (candidate == query ||
                        candidate.EndsWith(query, StringComparison.Ordinal) ||
                        query.EndsWith(candidate, StringComparison.Ordinal))
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        private static string DigitsOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch >= '0' && ch <= '9') sb.Append(ch);
            }
            return sb.ToString();
        }

        // The SDD-calling code below is a direct port of DatevContactRepository.cs
        // in the tray process — same contract identifiers, same element names,
        // same XML deserialization. Only difference: no retry helper (we let the
        // client drive retries) and inline logging to the proxy log file.

        private static RecipientContactDetail[] GetRecipients(bool activeOnly)
        {
            const string contractIdentifier = "Datev.Sdd.Contract.Browse.1.2";
            const string elementName = "KontaktDetail";

            var contactList = GetItemsList<RecipientsContactList>(
                contractIdentifier, elementName, string.Empty);

            if (contactList?.ContactDetails == null)
                return new RecipientContactDetail[0];

            var contacts = contactList.ContactDetails;
            if (activeOnly)
            {
                int before = contacts.Length;
                contacts = contacts.Where(c => c.Status != 0).ToArray();
                Log("SDD: {0} Adressaten (active, filtered from {1})", contacts.Length, before);
            }
            else
            {
                Log("SDD: {0} Adressaten", contacts.Length);
            }
            return contacts;
        }

        private static InstitutionContactDetail[] GetInstitutions()
        {
            const string contractIdentifier = "Datev.Inst.Contract.1.0";
            const string elementName = "TELEFONIE";

            var contactList = GetItemsList<InstitutionsContactList>(
                contractIdentifier, elementName, string.Empty);

            if (contactList?.ContactDetails == null)
                return new InstitutionContactDetail[0];

            Log("SDD: {0} Institutionen", contactList.ContactDetails.Length);
            return contactList.ContactDetails;
        }

        private static T GetItemsList<T>(string contractIdentifier, string elementName, string filterExpression)
            where T : class
        {
            const string dataEnvironment = "Datev.DataEnvironment.Default";

            using (Proxy proxy = Proxy.Instance)
            {
                IRequestHandler requestHandler = proxy.RequestHandler;
                IRequestHelper requestHelper = proxy.RequestHelper;

                Request readRequest = requestHelper.CreateDataObjectCollectionAccessReadRequest(
                    elementName,
                    contractIdentifier,
                    dataEnvironment,
                    filterExpression ?? string.Empty);

                using (Response response = requestHandler.Execute(readRequest))
                {
                    if (!response.HasData)
                    {
                        throw new XmlException($"SDD returned no data ({elementName}, HasData=false)");
                    }
                    try
                    {
                        var xmlSerializer = new XmlSerializer(typeof(T));
                        using (XmlReader xmlReader = response.CreateReader())
                        {
                            return (T)xmlSerializer.Deserialize(xmlReader);
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.InnerException is XmlException xmlEx)
                    {
                        throw new XmlException($"SDD XML invalid ({elementName}): {xmlEx.Message}", xmlEx);
                    }
                }
            }
        }

        // ===== JSON builders (hand-rolled, same style as BridgeMessageBuilder) =====

        private static string BuildError(string reason)
        {
            var sb = new StringBuilder(64 + (reason?.Length ?? 0));
            sb.Append("{\"ok\":false,\"reason\":\"").Append(EscapeJson(reason ?? "")).Append("\"}");
            return sb.ToString();
        }

        private static void WriteError(StreamWriter writer, string reason)
        {
            try { writer.WriteLine(BuildError(reason)); } catch { }
        }

        private static string BuildContactsResponse(List<ContactDto> contacts)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"ok\":true,\"contacts\":[");
            for (int i = 0; i < contacts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendContact(sb, contacts[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildLookupResponse(ContactDto hit)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"ok\":true,\"contact\":");
            if (hit == null) sb.Append("null");
            else AppendContact(sb, hit);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendContact(StringBuilder sb, ContactDto c)
        {
            sb.Append('{');
            sb.Append("\"id\":\"").Append(EscapeJson(c.Id)).Append("\",");
            sb.Append("\"displayName\":\"").Append(EscapeJson(c.DisplayName)).Append("\",");
            sb.Append("\"firstName\":\"").Append(EscapeJson(c.FirstName)).Append("\",");
            sb.Append("\"lastName\":\"").Append(EscapeJson(c.LastName)).Append("\",");
            sb.Append("\"kind\":\"").Append(EscapeJson(c.Kind)).Append("\",");
            sb.Append("\"isActive\":").Append(c.IsActive ? "true" : "false").Append(',');
            sb.Append("\"isPrivatePerson\":").Append(c.IsPrivatePerson ? "true" : "false").Append(',');
            sb.Append("\"phones\":[");
            if (c.Phones != null)
            {
                for (int i = 0; i < c.Phones.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var p = c.Phones[i];
                    sb.Append('{');
                    sb.Append("\"number\":\"").Append(EscapeJson(p.Number)).Append("\",");
                    sb.Append("\"normalizedNumber\":\"").Append(EscapeJson(p.NormalizedNumber)).Append("\"");
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ===== Minimal JSON scalar extractor =====
        //
        // Requests are trivially shallow ({"op":"...","activeOnly":true,"number":"..."})
        // so we don't need a full parser here. We pull one scalar value by key.

        private static string ExtractScalar(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;
            int pos = idx + needle.Length;
            // Skip whitespace and colon
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
            if (pos >= json.Length || json[pos] != ':') return null;
            pos++;
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
            if (pos >= json.Length) return null;

            if (json[pos] == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < json.Length)
                {
                    char c = json[pos];
                    if (c == '\\' && pos + 1 < json.Length)
                    {
                        char esc = json[pos + 1];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (pos + 5 < json.Length &&
                                    int.TryParse(json.Substring(pos + 2, 4),
                                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                {
                                    sb.Append((char)cp);
                                    pos += 4;
                                }
                                break;
                            default: sb.Append(esc); break;
                        }
                        pos += 2;
                    }
                    else if (c == '"')
                    {
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
            else
            {
                int start = pos;
                while (pos < json.Length && json[pos] != ',' && json[pos] != '}' &&
                       json[pos] != ']' && !char.IsWhiteSpace(json[pos]))
                {
                    pos++;
                }
                return json.Substring(start, pos - start).Trim();
            }
        }

        // ===== Log =====

        private static void InitLog()
        {
            try
            {
                string temp = Path.GetTempPath();
                _logPath = Path.Combine(temp, "3CXDATEVConnector-SddProxy.log");
                // Roll on each startup: keep it tiny, as the spec asks.
                try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { }
            }
            catch
            {
                _logPath = null;
            }
        }

        private static void Log(string format, params object[] args)
        {
            if (_logPath == null) return;
            string line;
            try
            {
                string msg = args != null && args.Length > 0
                    ? string.Format(CultureInfo.InvariantCulture, format, args)
                    : format;
                line = string.Format(CultureInfo.InvariantCulture, "[{0:yyyy-MM-dd HH:mm:ss.fff}] [PID {1}] {2}{3}",
                    DateTime.Now, Process.GetCurrentProcess().Id, msg, Environment.NewLine);
            }
            catch
            {
                return;
            }

            lock (_logLock)
            {
                try { File.AppendAllText(_logPath, line, new UTF8Encoding(false)); } catch { }
            }
        }
    }
}
