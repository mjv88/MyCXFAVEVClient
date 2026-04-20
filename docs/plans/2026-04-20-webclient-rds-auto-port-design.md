# WebClient Mode on Terminal Server — Automated Per-User Port Discovery

**Status:** Design approved, pending implementation plan
**Date:** 2026-04-20
**Scope:** `3CXDatevConnector/Webclient/*`, `Extension/scripts/background.js`

## Problem

The WebClient mode bridge binds a single loopback TCP port (`127.0.0.1:19800`
by default). On a Windows RDS / Terminal Server with multiple interactive
users, loopback TCP is machine-global: only the first process to bind the port
wins, and every other user's bridge fails to start. Today's workaround is to
manually configure a different `Webclient.WebSocketPort` per user in each
user's INI, and to manually configure the matching `bridgePort` in each user's
browser profile. This does not scale beyond a handful of users and is brittle
under roaming profiles.

Goal: **zero per-user port configuration**, correct isolation between RDS
sessions, and no cross-session event leakage. Support up to ~100 concurrent
users on a single server.

## Non-goals

- Changing the WebClient transport from WebSocket to Native Messaging
  (considered and rejected as too large for this retrofit — see
  Alternatives).
- Changing Desktop or Terminal Server (TAPI) mode behaviour.
- Defending against in-session adversaries. Any tool running as the user is
  trusted, same as today's single-user behaviour.

## Architecture

**Reserved loopback port range:** `19800–19899` (100 ports). Configurable via
`Webclient.WebSocketPort` (range start, default `19800`) and a new
`Webclient.WebSocketPortRangeSize` (default `100`).

**Bridge (`3cxDatevConnector.exe`, one instance per RDS user session):** walks
the range on startup and binds the first free port. Logs the chosen port.

**Session identity boundary:** the bridge verifies every accepted TCP
connection originates from a process in the bridge's own Windows session, via
`GetExtendedTcpTable` + `ProcessIdToSessionId`. Cross-session connections are
closed silently before any WebSocket handshake bytes are exchanged. HELLO
fields (`userName`, `extension`, `domain`) become **informational / logging
only**, not trust tokens — the browser is sandboxed and cannot read the
Windows username, so those fields are forgeable and must not be relied on for
security.

**Extension (Chromium-based):** on connect, tries the cached `bridgePort`
first. On cache miss (probe fails, HELLO times out, or TCP is closed early by
a cross-session peer), parallel-probes the whole range via HTTP `fetch` and
picks the first responder. Because cross-session bridges close probes
silently, in practice the parallel probe yields exactly zero or one responder
per scan.

## Protocol changes

`HELLO` — unchanged (`extension`, `domain`, `webclientVersion`, `userName`).

`HELLO_ACK` — gains one optional field `port`:
```json
{ "v": 1, "type": "HELLO_ACK", "extension": "1001",
  "bridgeVersion": "x.y.z", "port": 19803 }
```
The extension writes this to `chrome.storage.local.bridgePort` after a
successful handshake so future connects are a cache hit.

`HELLO_NACK` — **not introduced**. An earlier revision proposed it, but with
TCP-level session checks, cross-session peers never reach the HELLO stage —
the socket closes silently, the extension's probe/HELLO times out, and the
existing reconnect path handles it. A dedicated NACK message would add a
protocol type used by no code path.

Protocol version stays at `1`. The new optional `port` field is additive and
older bridges / extensions ignore unknowns (`Protocol.cs:91` uses permissive
`GetString` lookups).

## Bridge implementation

### New file: `Webclient/LoopbackPeerSession.cs`

Small P/Invoke helper.

```csharp
internal static class LoopbackPeerSession
{
    // Returns the Windows session ID of the process that owns the remote
    // endpoint of an accepted loopback TCP connection, or null if the peer
    // can't be resolved (connection closed between accept and lookup, etc.).
    public static uint? ResolvePeerSessionId(IPEndPoint local, IPEndPoint peer);

    public static uint CurrentSessionId();
}
```

Internals: `GetExtendedTcpTable(AF_INET, TCP_TABLE_OWNER_PID_ALL)`, scan for
the row matching the `(local, peer)` tuple, call
`ProcessIdToSessionId(row.OwningPid)`. All standard Win32; no admin rights.
IPv4 only — the existing listener uses `IPAddress.Loopback` (v4).

The P/Invoke surface is behind an `ISessionResolver` seam so unit tests can
inject a fake that returns a chosen session ID without needing a second
Windows session.

### `WebSocketBridgeServer` changes

1. **Port-binding walk.** New constructor argument `(rangeStart, rangeEnd)`.
   In `StartListener`, loop `port = start..end`, `new TcpListener(loopback,
   port).Start()`, catch `SocketException` with
   `SocketError.AddressAlreadyInUse` and continue. On success, expose the
   bound port as `public int BoundPort`. On range exhaustion, throw
   `IOException("no free port in 19800-19899")`.

2. **Session check at accept.** Introduce `AcceptAndVerifyAsync(ct)` that
   wraps `AcceptAsync`, runs `LoopbackPeerSession.ResolvePeerSessionId`,
   compares to `CurrentSessionId()`, closes and returns `null` on mismatch.
   Both accept paths — `RunAsync` and `TryAcceptAsync` — use this wrapper.
   The check runs **before** `PerformHandshakeAsync` so no WebSocket /
   HTTP-200-probe bytes are sent to cross-session peers.

3. **HELLO_ACK carries the port.** `BridgeMessageBuilder.BuildHelloAck` gains
   a `port` parameter; `SendHelloAck` passes `BoundPort`.

4. **Reject fast-path.** The existing 500 ms inter-client backoff
   (`WebSocketBridgeServer.cs:115`) is skipped when the rejection reason is
   session-mismatch, so legitimate same-session probes aren't delayed behind
   a queue of neighbours' rejected probes.

### Config compatibility

- `Webclient.WebSocketPort` is reinterpreted as **range start**. Installs
  with a non-default single port (e.g. `19850`) now get `19850..19949`.
- New key `Webclient.WebSocketPortRangeSize` defaults to `100` (clamp
  `1..1000`). Setting `1` restores the single-fixed-port behaviour for
  operators who explicitly need it.

## Extension implementation

### New constants in `background.js`

```js
const BRIDGE_PORT_RANGE_START = 19800;
const BRIDGE_PORT_RANGE_END   = 19899;
const PROBE_TIMEOUT_MS        = 500;
const HELLO_TIMEOUT_MS        = 1500;
```

### Connect flow

1. If `chrome.storage.local.bridgePort` is set, probe it (`PROBE_TIMEOUT_MS`).
   - Reachable → WebSocket, HELLO, wait for `HELLO_ACK` (`HELLO_TIMEOUT_MS`).
     ACK → done (cache unchanged). Timeout / close → fall through to (2).
   - Not reachable → fall through to (2).
2. Parallel-probe `RANGE_START..RANGE_END` via
   `Promise.allSettled(ports.map(isPortReachable))`. Collect responders.
3. If responders is non-empty, connect to the lowest-numbered responder,
   HELLO, await ACK. ACK → persist port to `chrome.storage.local.bridgePort`.
4. If responders is empty → schedule reconnect via the existing backoff.
   One scan per reconnect attempt — no scanning storm.

### Refactor

`isPortReachable(port)` — refactored to accept a port argument. Existing
function already uses `fetch` to avoid `chrome://extensions` error noise.

`findBridgePort()` — new helper that returns a port number or `null`, doing
(1) → (2) → (3).

`connectBridge()` — calls `findBridgePort`, then does the existing WebSocket
dance with the result.

`chrome.storage.local.onChanged` listener (`background.js:824`) — suppress
self-triggered reconnect when the new value equals the currently live port.

### No manifest changes

Existing permissions (`storage`, localhost host permissions) cover this. No
`nativeMessaging` permission, no new native host.

### Popup

No mandatory UI change. A nice-to-have follow-up: surface the currently bound
port in the popup for support visibility ("user is on 19804").

## Logging

**Bridge (always on, info level):**

- On startup, after the port walk succeeds:
  `WebClient Connector: Bridge lauscht auf Port 19803 (Session-ID 3)`
- On HELLO received (extend existing line at `WebSocketBridgeServer.cs:461`):
  `WebClient HELLO von extension=1001, identity=..., FQDN=..., Port=19803`
- On range exhaustion:
  `WebClient Connector: Kein freier Port in 19800-19899, Bridge nicht gestartet`

**Bridge (debug level — noisy on RDS, not useful for support):**

- On cross-session reject:
  `WebClient Connector: Peer aus Session 5 abgewiesen, eigene Session 3`

**Extension (always on, new `logInfo` helper alongside existing
debug-gated `logDebug`):**

- Cache hit: `[3CX-DATEV-C] Cache-Port 19803 erreichbar, verbunden`
- Cache miss + scan hit:
  `[3CX-DATEV-C] Cache-Port 19803 nicht erreichbar, Scan läuft ... gefunden auf 19807`
- Scan empty: `[3CX-DATEV-C] Keine Bridge in 19800-19899 gefunden, Retry in 3s`
- After ACK: `[3CX-DATEV-C] Bridge verbunden auf Port 19807 (Extension 1001)`

**Support angle:** one line per user per side tells you the full mapping on
a 25-user RDS. `grep "Bridge lauscht auf Port"` across user log directories
produces the user-to-port table for the server.

## Error handling

### Bridge

| Situation | Behaviour |
|---|---|
| All 100 ports bound | `StartListener` throws; `WebclientConnectionMethod` catches, surfaces as connection failure, existing reconnect timer retries. |
| `GetExtendedTcpTable` row missing (peer closed) | Treat as reject. Debug log. |
| `ProcessIdToSessionId` fails (PID recycled) | Treat as reject. |
| Same-session HELLO never arrives | Existing behaviour preserved: timeout, disconnect, re-accept. |
| Same-session non-browser peer (curl, diagnostic tool) | Allowed. In-session trust is the boundary. |
| Bridge restart mid-session, different port | Extension's cache probe fails, scan finds new port — no special handling. |

### Extension

| Situation | Behaviour |
|---|---|
| Cached port probe fails | Transparent fallback to parallel scan. One info log. |
| Scan finds 0 bridges | Schedule reconnect via existing backoff. |
| Scan finds >1 bridges (defensive) | Try ascending port order; first ACK wins. Shouldn't occur with session check. |
| `HELLO_ACK` never arrives | Close, invalidate cache, trigger scan. |
| User quits tray app | Cache miss → scan → nothing → scheduled retry. Normal reconnect story. |

### Compatibility

- Old installs with `WebSocketPort=19800`: still work; becomes range start.
- Old installs with non-default single port (e.g. `19850`): still work;
  becomes `19850..19949`. Admin can set `WebSocketPortRangeSize=1` to
  restore fixed-port semantics.
- Old extension vs new bridge: works if bridge lands on 19800. If 19800 is
  taken, upgrade the extension too — a mixed-version RDS fleet is out of
  scope.
- New extension vs old bridge: the scan finds the one bridge on its
  configured port in the first probe.

## Security boundary

1. Listener bound to `IPAddress.Loopback` only — no remote network access.
2. TCP peer must be in the bridge's Windows session — closes
   cross-RDS-session exposure.
3. Nothing authenticates peers *within* a session. Same-session tools are
   trusted, same as today's single-user behaviour.
4. HELLO's `extension` / `userName` / `domain` are informational, not trust.
   Code comments and this doc are the only places this is stated
   explicitly — the boundary is enforced at the TCP layer, not the HELLO
   layer.

## Testing

### Unit tests

*`LoopbackPeerSessionTests.cs` (new):*
- `ResolvePeerSessionId_SelfLoopback_ReturnsCurrentSession`
- `ResolvePeerSessionId_UnknownEndpoint_ReturnsNull`
- `CurrentSessionId_MatchesWtsApi` (sanity check on P/Invoke shape)

*`WebSocketBridgeServerTests.cs` (extend):*
- `Start_WalksRange_PicksFirstFree` — pre-bind 19800, assert `BoundPort ==
  19801`.
- `Start_RangeExhausted_Throws`
- `Accept_SameSession_Succeeds` — in-process (same session) reaches HELLO.
- `Accept_CrossSession_RejectsSilently` — injects a fake `ISessionResolver`
  returning a different session, asserts the server closes without writing.

### Integration tests (manual, on real RDS — add to `TEST_PLAN.md`)

1. **Single user, clean install.** Log shows `Bridge lauscht auf Port 19800`;
   extension shows matching port; call events flow.
2. **Two users, sequential start.** A gets 19800, B gets 19801. Each
   extension connects to its own bridge.
3. **Two users, simultaneous start** (within 1 s). Exactly one 19800, one
   19801. No errors.
4. **Cross-session cache hit.** Manually force user B's cached `bridgePort =
   19800` (user A's bridge). Expect log:
   `Cache-Port 19800 nicht erreichbar, Scan läuft ... gefunden auf 19801`.
   Assert no call events flow into user A's session.
5. **Bridge restart.** Kill user A's tray app, restart. Port may differ if
   another user grabbed 19800. Extension scans, reconnects, logs new port.
6. **25-user load.** Script 25 RDS sessions starting the app. Assert 25
   distinct ports in 19800–19824; no port collisions; cross-check each
   user's log for *only* their own extension number.
7. **Port exhaustion.** Externally block 100 ports, start app, assert
   startup error path and user-visible tray message.

### Non-RDS smoke (regression guard)

- Single-user desktop: boots on 19800 as today. Cache-hit path active from
  first successful connect. Desktop (TAPI) and Terminal Server (TAPI) modes
  are untouched by this change.

### Out of scope for testing

- Adversarial in-session tools impersonating the browser.
- Firefox (extension is Chromium-only).

## Alternatives considered

**A. Deterministic port from username hash.** Bridge binds `19800 +
CRC16(username) % 100`; extension computes the same. Zero scan — but
collisions still require fallback on both sides, which means the extension
still has to scan on collision. Adds complexity with no steady-state benefit
over plain cache-first.

**B. Random port within range.** Avoids thundering-herd on simultaneous
logon, but makes logs noisier and offers no real benefit at 100 users.

**C. Native Messaging.** Bridge registers as a Chrome/Edge native-messaging
host; the extension talks to it via `chrome.runtime.connectNative`. Windows
session isolation is automatic because Chrome spawns the host as the
current user. Zero port coordination, zero session check, cleanest
architecture — but requires a separate host executable (or a new mode on
the tray app), HKCU registry entries per browser per user at install,
`"nativeMessaging"` manifest permission, and the `chrome.runtime.connectNative`
transport rewrite. Rejected as too large for a retrofit; keep as a future
direction if the WebClient transport is ever rewritten.

**D. Pair by 3CX extension number from INI.** Bridge accepts HELLO only if
the incoming `extension` matches its configured `ExtensionNumber`. Caveat:
pure WebClient-on-RDS has no TAPI auto-detection, so each user must
manually configure their extension — not the zero-config goal. Also
weaker as a boundary: the WebClient user could log in with any extension
they have access to. Rejected in favour of session-level isolation.

## Open follow-ups (not in this change)

- Expose the currently bound port in the extension popup for support
  visibility.
- Consider surfacing `BoundPort` in the tray about/settings UI so support
  can read it without opening log files.
- The bridge currently accepts a second simultaneous client and overwrites
  the first connection's state (single `_conn` field). Out of scope here,
  but worth addressing: reject a second client with a clear log line.
