# WebClient RDS Auto-Port Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Let multiple Windows users on the same Terminal Server each run the WebClient bridge without per-user port configuration. Bridge walks a port range and picks a free one; extension discovers it via cache + parallel scan; Windows session identity isolates users.

**Architecture:** Bridge binds the first free port in 19800–19899. An accept-time check rejects any TCP peer not in the bridge's own Windows session. Extension tries its cached port first and falls back to a parallel HTTP probe of the range; on success, writes back the winning port.

**Tech Stack:** C# (net481, WinForms), Win32 P/Invoke (`iphlpapi.dll`, `kernel32.dll`), Chromium extension Manifest V3 (`background.js`, `chrome.storage.local`, `fetch`, `WebSocket`).

**Design doc:** `docs/plans/2026-04-20-webclient-rds-auto-port-design.md`

**Testing stance:** No new unit test project (matches current project culture). All verification is manual integration testing per the scenarios added to `docs/TEST_PLAN.md`. Where applicable, each task's "Verify" step tells you exactly what to click/log-grep.

---

## Task 0: Baseline check before starting

**Files:** none (read-only verification)

**Step 1: Confirm clean working tree**

Run: `git status`
Expected: `nothing to commit, working tree clean` on branch `main`.

**Step 2: Confirm the design doc commit is present**

Run: `git log --oneline -3`
Expected: Most recent commit is `Add design doc for WebClient auto port discovery on RDS` (SHA `6143835` or later).

**Step 3: Confirm the solution builds as-is**

Run from repo root: `msbuild 3CXDatevConnector.sln -nologo -m -v:m` (or open in Visual Studio and `Build → Build Solution`).
Expected: `Build succeeded.` with 0 errors. If warnings already exist on `main`, note the count — later tasks must not increase it.

No commit for this task.

---

## Task 1: Config keys for port range

**Files:**
- Modify: `3CXDatevConnector/Core/ConfigKeys.cs`
- Modify: `3CXDatevConnector/Core/Config/AppConfig.cs` (default-value table + section-map table + INI template writer)
- Modify: `README.md` (config reference table at line 231)

**Step 1: Add the new key constant**

Edit `3CXDatevConnector/Core/ConfigKeys.cs`. Next to `public const string WebclientWebSocketPort = "Webclient.WebSocketPort";` (line 82), add:

```csharp
public const string WebclientWebSocketPortRangeSize = "Webclient.WebSocketPortRangeSize";
```

**Step 2: Wire the default value and section mapping**

In `3CXDatevConnector/Core/Config/AppConfig.cs`:

- In the defaults dictionary (near line 79, where `WebclientWebSocketPort` is set to `"19800"`), add:
  ```csharp
  { ConfigKeys.WebclientWebSocketPortRangeSize, "100" },
  ```
- In the section map (near line 136, where `WebclientWebSocketPort` is mapped to `SectionConnection`), add:
  ```csharp
  { ConfigKeys.WebclientWebSocketPortRangeSize, SectionConnection },
  ```
- In the INI template writer around line 311 (where `DefaultLine(ConfigKeys.ExtensionNumber)` is emitted), find the analogous `DefaultLine` call for `WebclientWebSocketPort` in the same method and add a following line:
  ```csharp
  writer.WriteLine(DefaultLine(ConfigKeys.WebclientWebSocketPortRangeSize));
  ```

Use `AppConfig.GetIntClamped(ConfigKeys.WebclientWebSocketPortRangeSize, 100, 1, 1000)` when reading in later tasks — do not hand-parse.

**Step 3: Document the new key in README**

Edit `README.md`. In the config reference table that contains the existing `WebclientWebSocketPort` row (around line 231), add a new row directly below it:

```
| `WebclientWebSocketPortRangeSize` | 100 | [Connection] | Number of ports to walk starting at `WebclientWebSocketPort` when binding the bridge. Use `1` to restore single-fixed-port behaviour. |
```

Also in the same README section that describes WebClient mode (around lines 46–48), rephrase the mention of `ws://127.0.0.1:19800` to note "default port 19800, bridge walks `19800..19899` on Terminal Server installations so each user gets a free port automatically." Keep it one sentence.

**Step 4: Build**

Run: `msbuild 3CXDatevConnector.sln -nologo -m -v:m`
Expected: Build succeeded, warning count unchanged from Task 0.

**Step 5: Smoke-run the app**

Run `3cxDatevConnector.exe` from `3CXDatevConnector/bin/Debug/` (or your Visual Studio output dir) once. Open `%AppData%/3CXDATEVConnector/config.ini`. Expected: new line `WebclientWebSocketPortRangeSize=100` present under `[Connection]`. Close the app.

If the INI existed from a prior run, manually add the line (existing installs never auto-get new keys — the template writer only fires for fresh INIs).

**Step 6: Commit**

```bash
git add 3CXDatevConnector/Core/ConfigKeys.cs 3CXDatevConnector/Core/Config/AppConfig.cs README.md
git commit -m "Add WebSocketPortRangeSize config key (default 100)"
```

---

## Task 2: LoopbackPeerSession P/Invoke helper

**Files:**
- Create: `3CXDatevConnector/Webclient/LoopbackPeerSession.cs`

**Step 1: Write the helper**

Create `3CXDatevConnector/Webclient/LoopbackPeerSession.cs`:

```csharp
using System;
using System.Net;
using System.Runtime.InteropServices;

namespace DatevConnector.Webclient
{
    // Resolves the Windows session ID of the process that owns a loopback
    // TCP peer. Used to reject cross-session connections on Terminal Server.
    internal static class LoopbackPeerSession
    {
        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int tableClass,
            int reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;   // network byte order, high 16 bits zero
            public uint RemoteAddr;
            public uint RemotePort;  // network byte order, high 16 bits zero
            public uint OwningPid;
        }

        // Returns the session ID of the peer's owning process, or null if
        // the peer cannot be resolved (closed before lookup, PID recycled,
        // table race). Callers should treat null as "reject".
        public static uint? ResolvePeerSessionId(IPEndPoint local, IPEndPoint peer)
        {
            if (local == null || peer == null) return null;

            uint localAddr = (uint)BitConverter.ToInt32(local.Address.GetAddressBytes(), 0);
            uint peerAddr  = (uint)BitConverter.ToInt32(peer.Address.GetAddressBytes(),  0);
            uint localPortNbo = HostPortToTableNbo((ushort)local.Port);
            uint peerPortNbo  = HostPortToTableNbo((ushort)peer.Port);

            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET,
                TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buffer, ref size, false, AF_INET,
                    TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc != 0) return null;

                int rowCount = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));

                for (int i = 0; i < rowCount; i++)
                {
                    var row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(
                        IntPtr.Add(rowPtr, i * rowSize),
                        typeof(MIB_TCPROW_OWNER_PID));

                    // The row whose LOCAL endpoint is our peer and whose
                    // REMOTE endpoint is our local socket — from the peer's
                    // point of view, we are its remote.
                    if (row.LocalAddr  == peerAddr  && row.LocalPort  == peerPortNbo  &&
                        row.RemoteAddr == localAddr && row.RemotePort == localPortNbo)
                    {
                        if (ProcessIdToSessionId(row.OwningPid, out uint sid))
                            return sid;
                        return null;
                    }
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static uint CurrentSessionId()
        {
            return ProcessIdToSessionId(GetCurrentProcessId(), out uint sid) ? sid : 0;
        }

        // Table stores ports as: low 16 bits = network-order bytes of the port,
        // high 16 bits zero. Build the comparison value the same way.
        private static uint HostPortToTableNbo(ushort port)
        {
            ushort nbo = (ushort)((port >> 8) | (port << 8));
            return (uint)nbo;
        }
    }
}
```

**Step 2: Build**

Run: `msbuild 3CXDatevConnector.sln -nologo -m -v:m`
Expected: Build succeeded, warning count unchanged.

**Step 3: Smoke verify in a scratch spot**

Temporarily add at the top of `Program.cs` `Main` method, before any other code:

```csharp
System.Diagnostics.Debug.WriteLine(
    $"LoopbackPeerSession.CurrentSessionId = {LoopbackPeerSession.CurrentSessionId()}");
```

Run from Visual Studio (F5) with a Debug output window open. Expected: a line reading `LoopbackPeerSession.CurrentSessionId = N` where `N` matches the value shown by `query session` at a command prompt (typically `1` for a console session, higher for RDS sessions).

**Remove** the smoke line before committing — it's not meant to ship.

**Step 4: Commit**

```bash
git add 3CXDatevConnector/Webclient/LoopbackPeerSession.cs
git commit -m "Add LoopbackPeerSession P/Invoke helper"
```

---

## Task 3: Bridge port-range binding

**Files:**
- Modify: `3CXDatevConnector/Webclient/WebSocketBridgeServer.cs`
- Modify: `3CXDatevConnector/Webclient/WebclientConnectionMethod.cs`

**Step 1: Expose port range on the server**

In `WebSocketBridgeServer.cs`:

- Replace the field `private readonly int _port;` (near line 26) with:
  ```csharp
  private readonly int _rangeStart;
  private readonly int _rangeEnd;
  public int BoundPort { get; private set; }
  ```
- Update the constructor (it currently takes a single `port` argument from `WebclientConnectionMethod.cs:68`) to accept `(int rangeStart, int rangeEnd)` and set the new fields. Keep a convenience overload `WebSocketBridgeServer(int port) : this(port, port) {}` so the new code compiles before `WebclientConnectionMethod` is updated in Step 2.

**Step 2: Walk the range when binding**

Locate `StartListener()` (search for `_listener = new TcpListener(IPAddress.Loopback, _port);` at line 493). Replace that single binding attempt with a walk:

```csharp
if (_listener != null) return;

System.Net.Sockets.SocketException lastError = null;
for (int port = _rangeStart; port <= _rangeEnd; port++)
{
    try
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        BoundPort = port;
        LogManager.Log("WebClient Connector: Bridge lauscht auf Port {0} (Session-ID {1})",
            port, LoopbackPeerSession.CurrentSessionId());
        return;
    }
    catch (System.Net.Sockets.SocketException ex)
        when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
    {
        lastError = ex;
        // try next port
    }
}

LogManager.Log("WebClient Connector: Kein freier Port in {0}-{1}, Bridge nicht gestartet",
    _rangeStart, _rangeEnd);
throw new System.IO.IOException(
    $"No free port in range {_rangeStart}-{_rangeEnd}", lastError);
```

Remove the now-unused `_port` field references anywhere else in the file (there should be at most one or two).

**Step 3: Pass the range from `WebclientConnectionMethod`**

In `WebclientConnectionMethod.cs`, at the constructor site around line 68, replace:

```csharp
_wsPort = AppConfig.GetIntClamped(ConfigKeys.WebclientWebSocketPort, 19800, 1024, 65535);
```
... and the subsequent `new WebSocketBridgeServer(_wsPort)` construction, with:

```csharp
int rangeStart = AppConfig.GetIntClamped(ConfigKeys.WebclientWebSocketPort, 19800, 1024, 65535);
int rangeSize  = AppConfig.GetIntClamped(ConfigKeys.WebclientWebSocketPortRangeSize, 100, 1, 1000);
int rangeEnd   = Math.Min(65535, rangeStart + rangeSize - 1);
_wsPort = rangeStart; // kept for any existing logs referring to _wsPort
// construct server with range
_server = new WebSocketBridgeServer(rangeStart, rangeEnd);
```

Adjust the exact variable names to match the existing file. If `_wsPort` is used elsewhere (e.g. for diagnostics), leave it; after `_server.StartListener()` runs inside `WebSocketBridgeServer`, read `_server.BoundPort` for any downstream diagnostics.

**Step 4: Build**

Run: `msbuild 3CXDatevConnector.sln -nologo -m -v:m`
Expected: Build succeeded, warning count unchanged.

**Step 5: Verify via simulated collision**

From a second terminal, pre-bind 19800:

```powershell
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 19800); $l.Start(); "holding 19800 — press Ctrl+C to release"
```

Start the tray app. Open the log at `%AppData%/3CXDATEVConnector/logs/` and look for the startup line. Expected: `Bridge lauscht auf Port 19801` (or 19802+ if you also hold 19801).

Stop the app, release the PowerShell listener (Ctrl+C), restart the app. Expected: `Bridge lauscht auf Port 19800`.

**Step 6: Commit**

```bash
git add 3CXDatevConnector/Webclient/WebSocketBridgeServer.cs 3CXDatevConnector/Webclient/WebclientConnectionMethod.cs
git commit -m "Bridge walks port range on bind"
```

---

## Task 4: Bridge session-identity check at accept

**Files:**
- Modify: `3CXDatevConnector/Webclient/WebSocketBridgeServer.cs`

**Step 1: Add the accept wrapper**

In `WebSocketBridgeServer.cs`, add a private helper below `AcceptAsync` (follow the file's existing naming):

```csharp
// Accept a connection and enforce same-Windows-session identity. Returns
// null if the peer isn't in our session (cross-RDS-session call). Closing
// the socket without a WebSocket handshake means cross-session probes see
// a silent TCP close, which the extension already treats as "try another
// port".
private async Task<System.Net.Sockets.TcpClient> AcceptAndVerifyAsync(CancellationToken ct)
{
    var client = await AcceptAsync(ct);
    if (client == null) return null;

    try
    {
        var local = (System.Net.IPEndPoint)client.Client.LocalEndPoint;
        var peer  = (System.Net.IPEndPoint)client.Client.RemoteEndPoint;
        uint? peerSid = LoopbackPeerSession.ResolvePeerSessionId(local, peer);
        uint mySid = LoopbackPeerSession.CurrentSessionId();

        if (peerSid == null || peerSid.Value != mySid)
        {
            LogManager.Debug(
                "WebClient Connector: Peer aus Session {0} abgewiesen, eigene Session {1}",
                peerSid.HasValue ? peerSid.Value.ToString() : "(null)", mySid);
            try { client.Close(); } catch { }
            return null;
        }
    }
    catch (Exception ex)
    {
        LogManager.Debug("WebClient Connector: Session-Pruefung fehlgeschlagen - {0}", ex.Message);
        try { client.Close(); } catch { }
        return null;
    }

    return client;
}
```

**Step 2: Use the wrapper in both accept paths**

In `RunAsync`, find the call to `AcceptAsync` (inside the `while (!ct.IsCancellationRequested)` loop) and replace it with `AcceptAndVerifyAsync`. If the result is `null`, `continue` the loop **without** the 500 ms `Task.Delay(500, ct)` — cross-session rejects should loop back to `Accept` immediately so a legitimate probe isn't delayed.

In `TryAcceptAsync` (around line 146), replace the `AcceptAsync` call with `AcceptAndVerifyAsync`. If `null`, `continue` the loop.

Concretely, the `RunAsync` inner loop becomes roughly:

```csharp
while (!ct.IsCancellationRequested && !_disposed)
{
    TcpClient client = null;
    try
    {
        client = await AcceptAndVerifyAsync(ct);
        if (client == null) continue;                   // NEW: no 500ms delay

        if (!await HandshakeAndRunClient(client, ct))
            continue;
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        LogManager.Log("WebClient Connector: Fehler - {0}", ex.Message);
    }
    finally
    {
        DisconnectClient(client);
    }

    if (!ct.IsCancellationRequested && !_disposed)
        await Task.Delay(500, ct);                      // only for fully-handled clients
}
```

Keep the existing `await Task.Delay(500, ct)` **only** after a fully-handled client (the path where a real same-session browser disconnected).

**Step 3: Build**

Run: `msbuild 3CXDatevConnector.sln -nologo -m -v:m`
Expected: Build succeeded, warning count unchanged.

**Step 4: Smoke check same-session accept still works**

Run the tray app on your dev box (single user, same session). Open 3CX WebClient in Chrome with the extension installed. Expected: normal connect, `HELLO` log appears. No `Peer aus Session ... abgewiesen` lines in the log.

**Step 5: Commit**

```bash
git add 3CXDatevConnector/Webclient/WebSocketBridgeServer.cs
git commit -m "Reject cross-session loopback peers at accept"
```

---

## Task 5: HELLO_ACK carries the bound port

**Files:**
- Modify: `3CXDatevConnector/Webclient/Protocol.cs` (or wherever `BridgeMessageBuilder` lives — search `BuildHelloAck`)
- Modify: `3CXDatevConnector/Webclient/WebSocketBridgeServer.cs` (`SendHelloAck` site + the HELLO handler at line 461)

**Step 1: Find the builder**

Run: `Grep pattern="BuildHelloAck" output_mode="content" -n=true`. You're looking for `public static string BuildHelloAck(...)` somewhere in the Webclient folder.

**Step 2: Add the port parameter**

Add an `int port` parameter to `BuildHelloAck`. Update the emitted JSON to include `"port": <n>`. Example (adapt to the existing style — may be string-builder-based, not serializer-based):

```csharp
public static string BuildHelloAck(string bridgeVersion, string extension, int port)
{
    // ...existing construction...
    // add: "port": port
}
```

**Step 3: Update the single caller**

In `WebSocketBridgeServer.cs`, find `SendHelloAck` (around line 205):

```csharp
public bool SendHelloAck(string bridgeVersion, string extension)
{
    return SendJson(BridgeMessageBuilder.BuildHelloAck(bridgeVersion, extension));
}
```

Change the body to pass `BoundPort`:

```csharp
return SendJson(BridgeMessageBuilder.BuildHelloAck(bridgeVersion, extension, BoundPort));
```

Do **not** change the public signature of `SendHelloAck` — the caller (likely in `WebclientConnectionMethod`) doesn't need to know the port.

**Step 4: Extend the HELLO-received log line**

At line 461 in `WebSocketBridgeServer.cs`, the existing:

```csharp
LogManager.Log("WebClient HELLO von extension={0}, identity={1}, FQDN={2}",
    _conn.ExtensionNumber ?? "(none)", _conn.WebclientIdentity ?? "(none)",
    _conn.Domain ?? "(none)");
```

Append `, Port={3}` and `BoundPort` as the fourth argument:

```csharp
LogManager.Log("WebClient HELLO von extension={0}, identity={1}, FQDN={2}, Port={3}",
    _conn.ExtensionNumber ?? "(none)", _conn.WebclientIdentity ?? "(none)",
    _conn.Domain ?? "(none)", BoundPort);
```

**Step 5: Build**

Run: `msbuild 3CXDatevConnector.sln -nologo -m -v:m`
Expected: Build succeeded, warning count unchanged.

**Step 6: Verify the ACK JSON**

Run the tray app with `DebugLogging=true` in the INI. Connect with the extension. In the log, find the line that starts `WebClient Connector: Sent -> ` following a HELLO. Expected: JSON includes `"port":19800` (or whichever port was bound).

**Step 7: Commit**

```bash
git add 3CXDatevConnector/Webclient/Protocol.cs 3CXDatevConnector/Webclient/WebSocketBridgeServer.cs
git commit -m "Include bound port in HELLO_ACK and startup log"
```

*(If `BuildHelloAck` lives in a different file, adjust the `git add` accordingly.)*

---

## Task 6: Extension — refactor `isPortReachable` to take a port arg

**Files:**
- Modify: `Extension/scripts/background.js`

**Step 1: Change the signature**

Find `async function isPortReachable()` at `Extension/scripts/background.js:62`. Change it to take a `port` parameter and use that instead of the module-level `bridgePort`:

```js
async function isPortReachable(port) {
  try {
    await fetch(`http://127.0.0.1:${port}/`, { signal: AbortSignal.timeout(2000) });
    return true;
  } catch {
    return false;
  }
}
```

**Step 2: Update the single caller**

Around line 85 in `connectBridge`:

```js
if (!(await isPortReachable(bridgePort))) { ... }
```

**Step 3: Load the extension unpacked and confirm no regression**

Open `chrome://extensions/`, enable Developer mode, click "Reload" on the 3CX-DATEV-C extension. Open its service-worker console. Open 3CX WebClient. Expected: the service-worker console shows `Connecting to bridge ws://127.0.0.1:19800` and eventually `HELLO_ACK received`. No new errors.

**Step 4: Commit**

```bash
git add Extension/scripts/background.js
git commit -m "Refactor isPortReachable to accept port argument"
```

---

## Task 7: Extension — parallel scan & cache-first discovery

**Files:**
- Modify: `Extension/scripts/background.js`

**Step 1: Add constants and a `logInfo` helper**

Near the top of `background.js`, next to `DEFAULT_BRIDGE_PORT`, add:

```js
const BRIDGE_PORT_RANGE_START = 19800;
const BRIDGE_PORT_RANGE_END   = 19899;
const HELLO_TIMEOUT_MS        = 1500;

function logInfo(...args) {
  console.log("[3CX-DATEV-C][bg]", ...args);
}
```

Keep `logDebug` as-is (still gated by `debugLogging`).

**Step 2: Add `findBridgePort`**

Add alongside `connectBridge`:

```js
// Cache-first, parallel-scan-on-miss. Returns a port number or null.
async function findBridgePort() {
  // 1. Cached port first.
  const cached = Number.isFinite(bridgePort) ? bridgePort : null;
  if (cached && await isPortReachable(cached)) {
    logInfo(`Cache-Port ${cached} erreichbar, verbunden`);
    return cached;
  }

  // 2. Parallel probe across the range.
  const ports = [];
  for (let p = BRIDGE_PORT_RANGE_START; p <= BRIDGE_PORT_RANGE_END; p++) ports.push(p);
  const results = await Promise.allSettled(ports.map(isPortReachable));
  const responders = ports.filter((_, i) =>
    results[i].status === "fulfilled" && results[i].value === true);

  if (responders.length === 0) {
    logInfo(`Keine Bridge in ${BRIDGE_PORT_RANGE_START}-${BRIDGE_PORT_RANGE_END} gefunden, Retry geplant`);
    return null;
  }

  const picked = responders[0];
  if (cached) {
    logInfo(`Cache-Port ${cached} nicht erreichbar, Scan lief, gefunden auf ${picked}`);
  } else {
    logInfo(`Scan lief, Bridge gefunden auf ${picked}`);
  }
  return picked;
}
```

**Step 3: Replace `connectBridge`'s reachability probe with `findBridgePort`**

Around line 82, in `connectBridge`, replace:

```js
const url = `ws://127.0.0.1:${bridgePort}`;
logDebug("Connecting to bridge", url);

if (!(await isPortReachable(bridgePort))) {
  logDebug("Port probe failed — server not reachable");
  connectingInProgress = false;
  scheduleReconnect();
  return null;
}
```

... with:

```js
const foundPort = await findBridgePort();
if (foundPort === null) {
  connectingInProgress = false;
  scheduleReconnect();
  return null;
}

if (foundPort !== bridgePort) {
  bridgePort = foundPort;
  // Suppress the onChanged-driven reconnect (we're already connecting here).
  suppressNextPortChangeReconnect = true;
  chrome.storage.local.set({ bridgePort: foundPort });
}

const url = `ws://127.0.0.1:${bridgePort}`;
logDebug("Connecting to bridge", url);
```

At module scope near the other `let`-declared state variables, add:

```js
let suppressNextPortChangeReconnect = false;
```

**Step 4: Respect the suppression flag in the storage listener**

Find the `chrome.storage.onChanged` listener around line 824 and wrap the reconnect trigger:

```js
if (changes.bridgePort) {
  if (suppressNextPortChangeReconnect) {
    suppressNextPortChangeReconnect = false;
    bridgePort = parseInt(changes.bridgePort.newValue, 10) || DEFAULT_BRIDGE_PORT;
    // Do not reconnect — we initiated this change mid-connect.
  } else {
    // existing reconnect-on-port-change behaviour
  }
}
```

Preserve the existing behaviour for `changes.extensionNumber` / `changes.debugLogging` in the same listener.

**Step 5: Log the winning port on HELLO_ACK**

In the `ws.onmessage` handler around line 118 where `HELLO_ACK` is handled, add a `logInfo`:

```js
if (msg && msg.type === "HELLO_ACK") {
  helloAcked = true;
  clearHelloRetry();
  clearBridgeRetryAlarm();
  logInfo(`Bridge verbunden auf Port ${bridgePort} (Extension ${msg.extension || "(unbekannt)"})`);
  logDebug("HELLO_ACK received", { extension: msg.extension, bridgeVersion: msg.bridgeVersion, port: msg.port });
}
```

**Step 6: Reload the extension and verify the happy path**

Reload unpacked extension. Open service-worker console. Open 3CX WebClient. Expected log sequence:

```
[3CX-DATEV-C][bg] Cache-Port 19800 erreichbar, verbunden
[3CX-DATEV-C][bg] Bridge verbunden auf Port 19800 (Extension <your-ext>)
```

**Step 7: Verify the cache-miss path**

In the service worker console, run: `chrome.storage.local.set({bridgePort: 19799})` (a port that is definitely not bound). Wait a few seconds. Expected:

```
[3CX-DATEV-C][bg] Cache-Port 19799 nicht erreichbar, Scan lief, gefunden auf 19800
[3CX-DATEV-C][bg] Bridge verbunden auf Port 19800 (Extension <your-ext>)
```

Expected in `chrome.storage.local`: `bridgePort === 19800` again after the reconnect (you can check with `chrome.storage.local.get('bridgePort')`).

**Step 8: Commit**

```bash
git add Extension/scripts/background.js
git commit -m "Extension cache-first, parallel-scan bridge discovery"
```

---

## Task 8: TEST_PLAN integration test scenarios

**Files:**
- Modify: `docs/TEST_PLAN.md`

**Step 1: Append the RDS scenarios**

Add a new top-level section to `docs/TEST_PLAN.md` (if there's an existing `## WebClient Mode` section, nest these under it; otherwise add a new `## WebClient Mode — Terminal Server` section):

```markdown
## WebClient Mode — Terminal Server / RDS

### TS-1: Single user, clean install
1. Log into an RDS session. Install the 3CX-DATEV-Connector per-user.
2. Start the tray app. Open `%AppData%/3CXDATEVConnector/logs/*.log`.
   - Expect: `Bridge lauscht auf Port 19800 (Session-ID <N>)`.
3. Open 3CX WebClient in Chrome/Edge with the extension installed.
   - Expect service-worker console log: `Bridge verbunden auf Port 19800`.
4. Place a test call. Confirm DATEV receives the call event.

### TS-2: Two users, sequential start
1. Log in as user A. Start the tray app. Log: `Bridge lauscht auf Port 19800`.
2. Log in as user B (without logging A out). Start the tray app for B.
   - Expect B's log: `Bridge lauscht auf Port 19801`.
3. Open WebClient as user B. Expect `Bridge verbunden auf Port 19801`.
4. Place a test call as user B. Confirm ONLY user B's DATEV receives the event.

### TS-3: Two users, simultaneous start
1. Script-start the tray app as user A and user B within 1 second of each other
   (easiest via a batch file and two `psexec -u` invocations, or two RDP
   sessions and near-simultaneous clicks).
2. Expect: exactly one user on 19800, the other on 19801. No `Kein freier
   Port` errors in either log.

### TS-4: Cross-session cache hit (negative test)
1. As user B, in the service-worker console, run
   `chrome.storage.local.set({bridgePort: 19800})`.
2. Reload the extension.
3. Expect: `Cache-Port 19800 nicht erreichbar, Scan lief, gefunden auf 19801`
   (user A's 19800 silently refuses user B's probe due to the session check).
4. Place a test call as user B. Confirm user A's DATEV does NOT receive the
   event.

### TS-5: Bridge restart port re-discovery
1. As user A, kill the tray app (`taskkill /f /im 3cxDatevConnector.exe`).
2. As user B, start the tray app (grabs 19800).
3. As user A, restart the tray app. Expect: `Bridge lauscht auf Port 19801`.
4. User A's extension reconnects to 19801 (check service worker console).

### TS-6: 25-user load (reduced from full 100)
1. Script 25 RDS sessions starting the tray app.
2. Expect: 25 distinct ports in 19800–19824 across the 25 log directories.
3. Spot-check 3 random users: each log shows only that user's extension
   number in the HELLO line.

### TS-7: Port exhaustion
1. Externally bind all 100 ports in the range (PowerShell loop with
   `[System.Net.Sockets.TcpListener]` instances).
2. Start the tray app.
3. Expect log: `Kein freier Port in 19800-19899, Bridge nicht gestartet`.
4. Expect user-visible failure in the tray UI (connection state shows
   failed / error).

### Regression — non-RDS single-user
- Existing TS-* scenarios for Desktop / Terminal Server (TAPI) modes must
  remain green. This change does not touch those paths.
```

**Step 2: Commit**

```bash
git add docs/TEST_PLAN.md
git commit -m "Add TEST_PLAN scenarios for WebClient RDS auto-port discovery"
```

---

## Task 9: Developer guide update

**Files:**
- Modify: `docs/DEVELOPER_GUIDE.md`

**Step 1: Update the port reference**

Search for `19800` in `docs/DEVELOPER_GUIDE.md` (multiple mentions: around lines 483, 545, 649, 722). Replace hard-coded mentions of "port 19800" with "port range 19800–19899 (walks to first free on startup; see `LoopbackPeerSession` for session identity enforcement)". Exact wording isn't critical — clarity is.

In the config reference table around line 722, add a row for `WebclientWebSocketPortRangeSize` mirroring the README change from Task 1.

**Step 2: Add a short architecture note**

Near the existing WebClient architecture overview (search for "WebSocket server (port 19800)" around line 483), add a paragraph:

```
On Terminal Server deployments, each user's tray app binds its own port in
19800–19899 (first free). Cross-session connections are rejected at accept
time via `LoopbackPeerSession.ResolvePeerSessionId`, which looks up the
peer's owning PID in `GetExtendedTcpTable` and compares
`ProcessIdToSessionId` results. The browser extension probes its cached
port first, then parallel-probes the range on miss, picking the first
responder. Because cross-session bridges refuse probes silently, in practice
only one bridge per user responds.
```

**Step 3: Commit**

```bash
git add docs/DEVELOPER_GUIDE.md
git commit -m "Document WebClient RDS auto-port discovery in developer guide"
```

---

## Task 10: Final smoke + warning-count guard

**Files:** none (verification only)

**Step 1: Full build**

Run: `msbuild 3CXDatevConnector.sln -nologo -m -v:m -clp:Summary`
Expected: Build succeeded. Compare warning count to the Task 0 baseline. If the count is higher, fix the new warnings before considering the change done.

**Step 2: Full smoke on dev machine (single user)**

Run the tray app. Open WebClient with extension. Place one inbound test call and one outbound. Confirm DATEV receives both events with caller-ID routing working as today.

**Step 3: Log-line inventory**

`grep` the log for:
- `Bridge lauscht auf Port` — must appear exactly once per tray-app start.
- `WebClient HELLO von` — must include the `Port=<n>` suffix.
- `Peer aus Session ... abgewiesen` — must NOT appear on single-user dev machines.

**Step 4: Push the branch**

```bash
git log --oneline main..HEAD
```
Confirm 9 commits (Tasks 1–9). Then:

```bash
git push origin main
```

*(Only push if the branch is `main` and the team's workflow allows direct push. Otherwise push to a feature branch and open a PR.)*

**Step 5: Schedule the RDS integration tests**

The changes are not ship-ready until scenarios TS-1 through TS-7 in `docs/TEST_PLAN.md` pass on a real Terminal Server with at least 2 interactive users. Single-user dev smoke (Step 2) is not a substitute. Capture results in the PR / ticket.

---

## Appendix: What's explicitly NOT in this plan

- **Native Messaging transport.** Considered and rejected; see design doc's Alternatives section. If this ever becomes necessary (e.g. for Firefox support or to drop the port-range mechanism entirely), it is a separate rewrite.
- **HELLO_NACK message type.** Not introduced — the TCP-level session check means cross-session peers never reach HELLO.
- **Popup port display.** Noted as a follow-up in the design doc; intentionally deferred.
- **DATEV DLL bundled-fallback.** Separate, unrelated MSI work tracked elsewhere.
- **Second-simultaneous-browser-connection handling.** The bridge currently accepts a second connection and overwrites the first's `_conn`. Pre-existing behaviour, not introduced or fixed here.
