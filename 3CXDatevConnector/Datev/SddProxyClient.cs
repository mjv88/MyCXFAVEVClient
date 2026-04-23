using DatevConnector.Datev.DatevData;
using DatevConnector.Datev.DatevData.Enums;
using DatevConnector.Datev.Managers;
using DatevConnector.Datev.PluginData;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DatevConnector.Datev
{
    /// <summary>
    /// Tray-side client for the net48 SDD proxy subprocess. Extracts the embedded
    /// proxy exe to a versioned temp path, launches it, and speaks line-delimited
    /// JSON over a named pipe. See 3CXDatevConnector.SddProxy/Program.cs for the
    /// server side.
    ///
    /// Threading: one request at a time. Writes are serialized by a lock, and the
    /// response read happens inside the same critical section so the read-end
    /// never gets the tail of a previous response.
    /// </summary>
    internal sealed class SddProxyClient : IDisposable
    {
        private const string EmbeddedProxyFileName = "3CXDatevConnector.SddProxy.exe";
        private const int StartupWaitMs = 5000;
        private const int PipeConnectTimeoutMs = 5000;
        private const int DisposeGraceMs = 2000;

        private readonly object _lock = new object();
        private readonly string _pipeName;
        private readonly int _ownPid;

        private Process _proxyProcess;
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;

        private int _relaunchAttempt; // 0 = first launch, 1+ = retries
        private DateTime _lastRelaunchAttempt = DateTime.MinValue;
        private bool _disposed;

        public SddProxyClient()
        {
            _ownPid = Process.GetCurrentProcess().Id;
            _pipeName = "3cxdatev-sddproxy-" + _ownPid.ToString(CultureInfo.InvariantCulture);
        }

        public bool IsConnected
        {
            get { lock (_lock) return _pipe != null && _pipe.IsConnected; }
        }

        /// <summary>
        /// Launches the proxy (extracting the embedded exe first) and connects
        /// the pipe. Safe to call multiple times — no-op when already connected.
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                EnsureStartedLocked();
            }
        }

        // ===== Request methods =====

        public bool Ping()
        {
            string resp = Request("{\"op\":\"PING\"}");
            return resp != null && resp.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
        }

        public List<DatevContact> GetContacts(bool activeOnly)
        {
            string req = "{\"op\":\"GET_CONTACTS\",\"activeOnly\":" + (activeOnly ? "true" : "false") + "}";
            string resp = Request(req);
            if (resp == null) return new List<DatevContact>();
            return ParseContactsResponse(resp);
        }

        public DatevContact Lookup(string number)
        {
            string req = "{\"op\":\"LOOKUP\",\"number\":\"" + EscapeJson(number ?? "") + "\"}";
            string resp = Request(req);
            if (resp == null) return null;
            return ParseLookupResponse(resp);
        }

        /// <summary>
        /// Sends one request / reads one response. On broken pipe, tries to
        /// relaunch the proxy once (with backoff) and retry the call.
        /// Returns null on unrecoverable failure.
        /// </summary>
        private string Request(string json)
        {
            lock (_lock)
            {
                if (_disposed) return null;

                try
                {
                    EnsureStartedLocked();
                    return ExchangeLocked(json);
                }
                catch (IOException ex)
                {
                    LogManager.Warning("SddProxy: Pipe fehler: {0} - versuche Neustart", ex.Message);
                }
                catch (ObjectDisposedException ex)
                {
                    LogManager.Warning("SddProxy: Pipe disposed: {0} - versuche Neustart", ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    LogManager.Warning("SddProxy: Pipe invalid state: {0} - versuche Neustart", ex.Message);
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "SddProxy: Request fehlgeschlagen");
                    return null;
                }

                // One relaunch attempt with backoff.
                TryRelaunchLocked();
                try
                {
                    EnsureStartedLocked();
                    return ExchangeLocked(json);
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "SddProxy: Request nach Neustart fehlgeschlagen");
                    return null;
                }
            }
        }

        private string ExchangeLocked(string request)
        {
            _writer.WriteLine(request);
            string line = _reader.ReadLine();
            if (line == null) throw new IOException("Pipe closed by proxy");
            return line;
        }

        // ===== Lifecycle =====

        private void EnsureStartedLocked()
        {
            if (_pipe != null && _pipe.IsConnected && _proxyProcess != null && !_proxyProcess.HasExited)
                return;

            CleanupLocked();

            string proxyExe = EnsureProxyExtracted();
            LaunchProxyLocked(proxyExe);
            ConnectPipeLocked();
        }

        /// <summary>
        /// Extracts the embedded proxy exe to a versioned path under LocalAppData,
        /// so upgrades don't race against an older process that's still exiting.
        /// Returns the extracted exe path.
        /// </summary>
        private static string EnsureProxyExtracted()
        {
            // Source 1 (preferred): bundled as a Content file. After self-extract,
            // it lives in AppContext.BaseDirectory/SddProxy/.
            string bundled = Path.Combine(
                AppContext.BaseDirectory, "SddProxy", EmbeddedProxyFileName);

            string version = Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "0.0.0.0";

            string destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "3CXDATEVConnector");
            Directory.CreateDirectory(destDir);

            string destExe = Path.Combine(destDir,
                "SddProxy-" + version + ".exe");

            if (File.Exists(bundled))
            {
                bool needCopy = true;
                try
                {
                    if (File.Exists(destExe) &&
                        new FileInfo(destExe).Length == new FileInfo(bundled).Length &&
                        File.GetLastWriteTimeUtc(destExe) >= File.GetLastWriteTimeUtc(bundled))
                    {
                        needCopy = false;
                    }
                }
                catch { /* fall through and copy */ }

                if (needCopy)
                {
                    try
                    {
                        File.Copy(bundled, destExe, overwrite: true);
                        LogManager.Log("SddProxy: extrahiert nach '{0}'", destExe);
                    }
                    catch (IOException ex)
                    {
                        // Another process may hold the destination — use it as-is
                        // if it exists, otherwise rethrow.
                        LogManager.Warning("SddProxy: Kopie fehlgeschlagen ({0}); nutze vorhandene Datei", ex.Message);
                        if (!File.Exists(destExe)) throw;
                    }
                }
            }
            else if (!File.Exists(destExe))
            {
                throw new FileNotFoundException(
                    "SDD proxy exe not found at " + bundled +
                    " and no prior extraction at " + destExe);
            }

            return destExe;
        }

        private void LaunchProxyLocked(string proxyExe)
        {
            var psi = new ProcessStartInfo
            {
                FileName = proxyExe,
                Arguments =
                    "--pipe-name=" + _pipeName +
                    " --parent-pid=" + _ownPid.ToString(CultureInfo.InvariantCulture),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(proxyExe)
            };

            _proxyProcess = Process.Start(psi);
            if (_proxyProcess == null)
                throw new InvalidOperationException("Proxy process failed to start.");

            LogManager.Log("SddProxy: gestartet PID={0} Pfad='{1}' Pipe='{2}'",
                _proxyProcess.Id, proxyExe, _pipeName);
        }

        private void ConnectPipeLocked()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(StartupWaitMs);
            Exception lastError = null;

            while (DateTime.UtcNow < deadline)
            {
                if (_proxyProcess != null && _proxyProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        "Proxy exited with code " + _proxyProcess.ExitCode +
                        " before accepting a pipe connection.");
                }

                var pipe = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.InOut, PipeOptions.None);
                try
                {
                    pipe.Connect(PipeConnectTimeoutMs);
                    _pipe = pipe;
                    var utf8 = new UTF8Encoding(false, false);
                    _reader = new StreamReader(_pipe, utf8);
                    _writer = new StreamWriter(_pipe, utf8) { NewLine = "\n", AutoFlush = true };
                    LogManager.Log("SddProxy: Pipe verbunden ({0})", _pipeName);
                    return;
                }
                catch (TimeoutException ex) { lastError = ex; pipe.Dispose(); }
                catch (IOException ex) { lastError = ex; pipe.Dispose(); Thread.Sleep(100); }
            }

            throw new TimeoutException(
                "Unable to connect to SDD proxy pipe within " +
                StartupWaitMs + "ms. Last error: " + lastError?.Message);
        }

        private void TryRelaunchLocked()
        {
            CleanupLocked();

            // Exponential backoff: 2s, 4s, 8s, capped at 30s.
            int[] delaysSec = { 2, 4, 8, 16, 30, 30, 30 };
            int idx = Math.Min(_relaunchAttempt, delaysSec.Length - 1);
            int delaySec = delaysSec[idx];

            // Honor the wall-clock gap too — avoid restart storms if the pipe
            // breaks rapidly.
            TimeSpan sinceLast = DateTime.UtcNow - _lastRelaunchAttempt;
            if (sinceLast.TotalSeconds < delaySec)
            {
                int waitMs = (int)Math.Ceiling((delaySec - sinceLast.TotalSeconds) * 1000);
                if (waitMs > 0) Thread.Sleep(waitMs);
            }

            _lastRelaunchAttempt = DateTime.UtcNow;
            _relaunchAttempt++;
            LogManager.Log("SddProxy: Neustart Versuch #{0} (nach {1}s Backoff)",
                _relaunchAttempt, delaySec);
        }

        private void CleanupLocked()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _reader = null;
            _pipe = null;

            if (_proxyProcess != null)
            {
                try
                {
                    if (!_proxyProcess.HasExited) _proxyProcess.Kill();
                }
                catch { }
                try { _proxyProcess.Dispose(); } catch { }
                _proxyProcess = null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                // Best-effort: send EXIT, wait for clean exit, then kill if still alive.
                try
                {
                    if (_writer != null && _pipe != null && _pipe.IsConnected)
                    {
                        _writer.WriteLine("{\"op\":\"EXIT\"}");
                        try { _reader?.ReadLine(); } catch { }
                    }
                }
                catch { }

                try
                {
                    if (_proxyProcess != null && !_proxyProcess.HasExited)
                    {
                        _proxyProcess.WaitForExit(DisposeGraceMs);
                    }
                }
                catch { }

                CleanupLocked();
                LogManager.Log("SddProxy: beendet");
            }
        }

        // ===== JSON helpers =====

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

        private static List<DatevContact> ParseContactsResponse(string json)
        {
            var result = new List<DatevContact>();
            var parser = new ProxyJsonParser(json);
            if (!parser.IsOk())
            {
                string reason = parser.GetErrorReason();
                LogManager.Warning("SddProxy: Antwort ok=false: {0}", reason ?? "(unbekannt)");
                return result;
            }

            foreach (string contactJson in parser.GetArray("contacts"))
            {
                DatevContact c = ParseContact(contactJson);
                if (c != null) result.Add(c);
            }
            return result;
        }

        private static DatevContact ParseLookupResponse(string json)
        {
            var parser = new ProxyJsonParser(json);
            if (!parser.IsOk())
            {
                string reason = parser.GetErrorReason();
                LogManager.Warning("SddProxy: Antwort ok=false: {0}", reason ?? "(unbekannt)");
                return null;
            }
            string inner = parser.GetObject("contact");
            if (inner == null || inner == "null" || inner.Length == 0) return null;
            return ParseContact(inner);
        }

        private static DatevContact ParseContact(string objJson)
        {
            if (string.IsNullOrEmpty(objJson)) return null;
            var p = new ProxyJsonParser(objJson);

            var dc = new DatevContact
            {
                Id = p.GetString("id"),
                Name = p.GetString("displayName"),
                IsPrivatePerson = p.GetBool("isPrivatePerson"),
                IsRecipient = string.Equals(p.GetString("kind"), "Adressat",
                                StringComparison.OrdinalIgnoreCase)
            };

            var phones = new List<Communication>();
            foreach (string phoneJson in p.GetArray("phones"))
            {
                var pp = new ProxyJsonParser(phoneJson);
                phones.Add(new Communication
                {
                    Medium = Medium.Phone,
                    Number = pp.GetString("number"),
                    NormalizedNumber = pp.GetString("normalizedNumber")
                });
            }
            dc.Communications = phones.ToArray();
            return dc;
        }
    }

    /// <summary>
    /// Minimal JSON reader for the proxy response format. Supports string/bool/
    /// number scalars by key, flat object extraction, and array-of-objects
    /// extraction. Not a general-purpose parser — only handles the specific
    /// shapes the proxy emits.
    /// </summary>
    internal sealed class ProxyJsonParser
    {
        private readonly string _json;

        public ProxyJsonParser(string json)
        {
            _json = json ?? "";
        }

        public bool IsOk()
        {
            string v = GetScalarRaw("ok");
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        public string GetErrorReason() => GetString("reason");

        public string GetString(string key)
        {
            int pos = FindKey(key);
            if (pos < 0) return null;
            pos = SkipToValue(pos);
            if (pos < 0 || pos >= _json.Length) return null;
            if (_json[pos] != '"') return null;
            return ReadString(pos, out _);
        }

        public bool GetBool(string key)
        {
            string raw = GetScalarRaw(key);
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the raw JSON substring of the object value for this key
        /// (including braces), or the literal token if it's a scalar/null.
        /// </summary>
        public string GetObject(string key)
        {
            int pos = FindKey(key);
            if (pos < 0) return null;
            pos = SkipToValue(pos);
            if (pos < 0 || pos >= _json.Length) return null;

            if (_json[pos] == '{')
            {
                int end = FindMatching(pos, '{', '}');
                if (end < 0) return null;
                return _json.Substring(pos, end - pos + 1);
            }
            if (_json[pos] == '"')
            {
                string s = ReadString(pos, out _);
                return s;
            }
            // Scalar (null, true, false, number)
            int start = pos;
            while (pos < _json.Length &&
                   _json[pos] != ',' && _json[pos] != '}' && _json[pos] != ']' &&
                   !char.IsWhiteSpace(_json[pos]))
            {
                pos++;
            }
            return _json.Substring(start, pos - start).Trim();
        }

        /// <summary>
        /// Yields the JSON substring of each element in the named array.
        /// </summary>
        public IEnumerable<string> GetArray(string key)
        {
            int pos = FindKey(key);
            if (pos < 0) yield break;
            pos = SkipToValue(pos);
            if (pos < 0 || pos >= _json.Length || _json[pos] != '[') yield break;
            pos++; // skip '['

            while (pos < _json.Length)
            {
                while (pos < _json.Length && char.IsWhiteSpace(_json[pos])) pos++;
                if (pos >= _json.Length) yield break;
                if (_json[pos] == ']') yield break;
                if (_json[pos] == ',') { pos++; continue; }

                if (_json[pos] == '{')
                {
                    int end = FindMatching(pos, '{', '}');
                    if (end < 0) yield break;
                    yield return _json.Substring(pos, end - pos + 1);
                    pos = end + 1;
                }
                else if (_json[pos] == '[')
                {
                    int end = FindMatching(pos, '[', ']');
                    if (end < 0) yield break;
                    yield return _json.Substring(pos, end - pos + 1);
                    pos = end + 1;
                }
                else if (_json[pos] == '"')
                {
                    string s = ReadString(pos, out int newPos);
                    if (s == null) yield break;
                    yield return s;
                    pos = newPos;
                }
                else
                {
                    int start = pos;
                    while (pos < _json.Length &&
                           _json[pos] != ',' && _json[pos] != ']')
                    {
                        pos++;
                    }
                    yield return _json.Substring(start, pos - start).Trim();
                }
            }
        }

        // ===== helpers =====

        private string GetScalarRaw(string key)
        {
            int pos = FindKey(key);
            if (pos < 0) return null;
            pos = SkipToValue(pos);
            if (pos < 0 || pos >= _json.Length) return null;
            if (_json[pos] == '"')
            {
                return ReadString(pos, out _);
            }
            int start = pos;
            while (pos < _json.Length &&
                   _json[pos] != ',' && _json[pos] != '}' && _json[pos] != ']' &&
                   !char.IsWhiteSpace(_json[pos]))
            {
                pos++;
            }
            return _json.Substring(start, pos - start).Trim();
        }

        /// <summary>Return index of first character after the key's closing quote, or -1.</summary>
        private int FindKey(string key)
        {
            string needle = "\"" + key + "\"";
            int searchStart = 0;

            // Look at top-level only: skip over any nested objects/arrays/strings
            // between the start and where we currently are.
            int pos = 0;
            // Skip opening '{'
            while (pos < _json.Length && char.IsWhiteSpace(_json[pos])) pos++;
            if (pos < _json.Length && _json[pos] == '{') pos++;

            int depthObj = 0, depthArr = 0;
            while (pos < _json.Length)
            {
                char c = _json[pos];
                if (c == '"')
                {
                    // Key or string value; check only when we're at top level.
                    int startQ = pos;
                    ReadString(pos, out int newPos);
                    if (depthObj == 0 && depthArr == 0 &&
                        pos + needle.Length <= _json.Length &&
                        string.CompareOrdinal(_json, pos, needle, 0, needle.Length) == 0)
                    {
                        // Make sure the char after the needle is ':' (modulo ws).
                        int after = pos + needle.Length;
                        while (after < _json.Length && char.IsWhiteSpace(_json[after])) after++;
                        if (after < _json.Length && _json[after] == ':')
                        {
                            return pos + needle.Length;
                        }
                    }
                    pos = newPos;
                    searchStart = pos;
                    continue;
                }
                if (c == '{') depthObj++;
                else if (c == '}') { if (depthObj > 0) depthObj--; }
                else if (c == '[') depthArr++;
                else if (c == ']') { if (depthArr > 0) depthArr--; }
                pos++;
            }
            return -1;
        }

        private int SkipToValue(int pos)
        {
            while (pos < _json.Length && char.IsWhiteSpace(_json[pos])) pos++;
            if (pos >= _json.Length || _json[pos] != ':') return -1;
            pos++;
            while (pos < _json.Length && char.IsWhiteSpace(_json[pos])) pos++;
            return pos;
        }

        private string ReadString(int pos, out int newPos)
        {
            newPos = pos;
            if (pos >= _json.Length || _json[pos] != '"') { newPos = pos; return null; }
            pos++;
            var sb = new StringBuilder();
            while (pos < _json.Length)
            {
                char c = _json[pos];
                if (c == '\\' && pos + 1 < _json.Length)
                {
                    char esc = _json[pos + 1];
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
                            if (pos + 5 < _json.Length &&
                                int.TryParse(_json.Substring(pos + 2, 4),
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
                    newPos = pos + 1;
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
            newPos = pos;
            return sb.ToString();
        }

        private int FindMatching(int openPos, char open, char close)
        {
            int depth = 0;
            int pos = openPos;
            while (pos < _json.Length)
            {
                char c = _json[pos];
                if (c == '"')
                {
                    ReadString(pos, out int newPos);
                    pos = newPos;
                    continue;
                }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return pos;
                }
                pos++;
            }
            return -1;
        }
    }
}
