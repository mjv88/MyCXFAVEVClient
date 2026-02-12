# 3CX - DATEV Connector — Support Guide

## Architecture Overview

The bridge is a .NET Framework 4.8 WinForms system tray application (x86) that acts as a proxy between the 3CX Softphone and DATEV Arbeitsplatz.

```
+------------------+     Named Pipe      +-------------------+
|  3CX Softphone   | <=================> |                   |
|  (V20, WinUI 3)  |   UTF-16 LE msgs   |   3CX-DATEV       |
+------------------+                     |   Bridge          |
                                         |   (Tray App)      |
+------------------+     TAPI 2.x       |                   |
|  3CX Multi-Line  | -----------------> |                   |
|  TAPI Driver     |   Call events       |                   |
+------------------+                     +--------+----------+
                                                  |
                                         COM/ROT  |  SDD
                                                  v
                                         +-------------------+
                                         |  DATEV            |
                                         |  Arbeitsplatz     |
                                         |  (Telefonie)      |
                                         +-------------------+
```

### Communication Channels

| Channel | Protocol | Direction | Purpose |
|---------|----------|-----------|---------|
| TAPI 2.x | Windows TAPI callbacks | TAPI Driver → Bridge | Call state events (offering, connected, disconnected) |
| Named Pipe | `[2-byte LE length][UTF-16LE body]` | Bidirectional | Commands: MAKE-CALL, DROP-CALL, RINGING, CONNECTED, etc. |
| COM/ROT | Running Object Table | Bidirectional | DATEV notifications (NewCall, CallStateChanged, NewJournal) and commands (Dial, Drop) |
| SDD | .NET Assembly (GAC) | Bridge → DATEV | Contact loading from Stammdatendienst |

### Key Components

| Component | File | Responsibility |
|-----------|------|----------------|
| **ConnectorService** | `Core/ConnectorService.cs` | Central orchestrator — wires TAPI, DATEV, and UI |
| **TapiLineMonitor** | `Tapi/TapiLineMonitor.cs` | TAPI 2.x call event monitoring |
| **PipeTelephonyProvider** | `Tapi/PipeTelephonyProvider.cs` | Named pipe server for 3CX commands |
| **TapiPipeServer** | `Tapi/TapiPipeServer.cs` | Low-level pipe I/O |
| **DatevAdapter** | `Datev/COMs/DatevAdapter.cs` | COM adapter registered in ROT |
| **NotificationManager** | `Datev/Managers/NotificationManager.cs` | Bridge → DATEV notifications (with circuit breaker) |
| **CallDataManager** | `Datev/Managers/CallDataManager.cs` | Call data handling and SyncID management |
| **DatevCache** | `Datev/DatevCache.cs` | Contact cache with phone number lookup |
| **DatevContactManager** | `Datev/Managers/DatevContactManager.cs` | SDD contact loading |
| **CallTracker** | `Core/CallTracker.cs` | Active and pending call tracking |
| **LogManager** | `Datev/Managers/LogManager.cs` | Structured logging with rotation |

---

## Startup Sequence

The bridge performs these steps on startup in order:

```
1. Detect environment (Desktop vs Terminal Server)
   ├── Log session info (Session ID, IsTerminalSession, SessionName)
   └── Determine mode: Named Pipe (TS) or TAPI (Desktop)

2. Create Named Pipe server (if Terminal Server)
   └── Pipe: \\.\pipe\3CX_tsp_server_{extension}

3. Initialize DATEV
   ├── Register COM adapter in ROT (AdapterManager)
   ├── Run DATEV connection test
   │   ├── Check DATEV Telefonie process
   │   ├── Check ROT availability (CLSID lookup)
   │   └── Check SDD assembly availability
   ├── Load contacts from SDD (async)
   └── Start config file watcher (hot-reload)

4. Connect telephony
   ├── Desktop: Connect TAPI line monitor
   └── TS: Await Named Pipe client connection
```

### DATEV Connection Test Output

```
========================================
=== DATEV Connection Test ===
  DATEV Telefonie (ROT): Available
  DATEV SDD (Kontakte): Available
  DATEV available - all components detected
=============================
========================================
```

If DATEV is not available:

```
  DATEV Telefonie (ROT): NOT AVAILABLE
  DATEV not found in ROT (result=0x800401E3)
  DATEV SDD (Kontakte): Available
  DATEV SDD available - contacts accessible (CTI not yet available)
```

---

## Logging

### Log Location

```
%AppData%\3CXDATEVConnector\3CXDatevConnector.log
```

### Log Format

Each line follows this format:

```
[yyyy-MM-dd HH:mm:ss.fff] [LEVEL   ] [T  id] Message
```

| Field | Format | Example |
|-------|--------|---------|
| Timestamp | `yyyy-MM-dd HH:mm:ss.fff` | `2026-02-04 14:30:45.123` |
| Level | 8-char padded uppercase | `INFO    `, `DEBUG   `, `WARNING `, `ERROR   ` |
| Thread ID | 4-char padded managed thread ID | `T   1`, `T  12` |
| Message | Free-form text | `RINGING: Incoming call...` |

### Log Levels

| Level | When to Use | Enabled By |
|-------|-------------|------------|
| `DEBUG` | Detailed diagnostics (TAPI raw messages, contact cache, UI events) | `[Debug] VerboseLogging=true` |
| `INFO` | Normal operations (calls, connections, contacts) | Default |
| `WARNING` | Potential issues (DATEV unavailable, circuit breaker, unknown config keys) | Default |
| `ERROR` | Failures with stack traces | Default |
| `CRITICAL` | Fatal errors preventing operation | Default |

### Log Rotation

- **Trigger:** When log file exceeds `LogMaxSizeMB` (default: 10 MB)
- **Retention:** Keeps `LogMaxFiles` (default: 5) rotated files
- **Naming:** `3CXDatevConnector.log` → `3CXDatevConnector.1.log` → `3CXDatevConnector.2.log` → ...
- **Oldest deleted:** When file count exceeds `LogMaxFiles`

### Enabling Debug Logging

**Method 1 — INI file (persistent, survives restart):**

```ini
[Logging]
DebugLogging=true
```

**Method 2 — INI file (hot-reloadable, takes effect immediately):**

```ini
[Debug]
VerboseLogging=true
```

**Method 3 — Command line (one-time):**

```
3cxDatevConnector.exe /verbose
```

### Log Prefixes

Messages are prefixed for structured filtering:

| Prefix | Description | Example |
|--------|-------------|---------|
| `DATEV -> Bridge` | Commands from DATEV | `DATEV -> Bridge: Dial command received` |
| `Bridge -> DATEV` | Notifications to DATEV | `Bridge -> DATEV: NewCall (Direction=eDirIncoming)` |
| `TAPI` | TAPI call events | `TAPI: LINECALLSTATE_OFFERING on line 161` |
| `Bridge` | Internal bridge operations | `Bridge: Added call 161-04022026-1430-1234567` |
| `Config` | Configuration changes | `Config: VerboseLogging changed to true` |
| `Cache` | Contact cache operations | `Cache: 19421 contacts loaded` |
| `CircuitBreaker` | Circuit breaker state | `CircuitBreaker: OPEN after 3 failures` |
| `Session` | Session management | `Session: Id=2, IsTerminalSession=true` |
| `ERROR` | Error conditions | `ERROR: DATEV COM call failed: 0x80040154` |

---

## Diagnosing Common Issues

### Issue: Red Tray Icon (No Connection)

**Check TAPI:**

1. Open log file (Strg+L)
2. Search for `TAPI` messages at startup
3. Expected: `TAPI line monitor connected: 161 : Max Mustermann`
4. If missing: TAPI driver not installed or no lines available

**Check DATEV:**

1. Search log for `DATEV Connection Test`
2. Expected: `DATEV available - all components detected`
3. If `NOT AVAILABLE`: DATEV Arbeitsplatz not running or not reachable

**Check Named Pipe (Terminal Server):**

1. Search for `PipeServer` messages
2. Expected: `PipeServer: Listening on \\.\pipe\3CX_tsp_server_161`
3. If `3CX Softphone not connected`: Pipe created but 3CX client hasn't connected

### Issue: No Contacts Loaded

```
[WARNING] Loading contacts from DATEV SDD...
[ERROR] SDD load failed: Could not load assembly
```

**Possible causes:**
- DATEV Basis not installed (missing GAC assemblies)
- Insufficient DATEV permissions for the Windows user
- SDD not responding (increase `SddMaxRetries` in `[Connection]`)

**Expected success log:**

```
[INFO] Loading contacts from DATEV SDD...
[INFO] Loaded 19421 contacts from DATEV SDD
[INFO] 3CXDatevConnector: Contact lookup dictionary built with 24251 unique phone number keys
```

### Issue: Calls Detected but No DATEV Notification

1. Search log for `Bridge -> DATEV: NewCall`
2. If present: DATEV notification was sent
3. If missing, search for `CircuitBreaker`:
   ```
   [WARNING] CircuitBreaker: OPEN - skipping DATEV notification
   ```
4. The circuit breaker opens after 3 consecutive failures. Wait for timeout (default: 30s) or restart the bridge

### Issue: Contact Not Found for Known Number

1. Enable debug logging (`VerboseLogging=true`)
2. Search for `Contact lookup`:
   ```
   [INFO] Bridge: Contact lookup - Input='********4567' Normalized='*****45678'
   [INFO] Bridge: Contact lookup - No match found
   ```
3. Check normalization: Input is stripped to last `MaxCompareLength` digits
4. Dump all contacts: Add `Contacts=true` to `[Debug]` section, then check `contacts.txt`
5. Adjust `MaxCompareLength` if digits don't align (e.g., increase to 12 for longer numbers)

### Issue: SyncID Lost on DATEV-Initiated Calls

1. Search log for `DATEV -> Bridge: Dial`:
   ```
   [INFO] DATEV -> Bridge: Dial command received
   [DEBUG]   CalledNumber=********4567, Adressatenname=Mueller GmbH, SyncID=datev-456
   ```
2. Search for `DATEV-initiated`:
   ```
   [INFO] RINGBACK: Outgoing call ... to ********4567 (DATEV-initiated, SyncID=datev-456)
   ```
3. If SyncID is missing in RINGBACK: The pending call wasn't matched by phone number. Check normalization.

### Issue: Journal Popup Not Appearing

1. Verify `EnableJournalPopup=true` in `[Settings]`
2. For outbound calls: `EnableJournalPopupOutbound=true`
3. Journal popup only shows for calls with a resolved DATEV contact (AdressatenId)
4. If `Stummschalten` (silent mode) is active, popups are suppressed

---

## Desktop vs Terminal Server

### Desktop (Single User)

```
IsTerminalSession=False, SessionName=Console, Session: Id=1
```

- TAPI line monitor connects directly
- Standard pipe name: `\\.\pipe\3CX_tsp_server_{ext}`
- Single ROT registration with base GUID

### Terminal Server / RDS (Multi-User)

```
IsTerminalSession=True, SessionName=RDP-Tcp#0, Session: Id=2
```

- Named Pipe server is created first (3CX polls every 2 seconds)
- Each session has isolated ROT (Windows per-session isolation)
- No GUID modification needed — Windows handles isolation
- Each user runs their own bridge instance with their own TAPI driver

**Key difference:** On Terminal Server, the pipe server must be created before TAPI initialization because the 3CX Softphone polls for the pipe at startup.

### Session Diagnostics in Log

The `SessionManager` logs session details at startup:

```
[INFO] IsTerminalSession=True, SessionName=RDP-Tcp#0, Session: Id=2
[INFO] ========================================
[INFO] 3CX - DATEV Connector starting (Extension=161)
[INFO] Mode: Terminal Server (Named Pipe)
```

---

## Circuit Breaker

The bridge implements a circuit breaker to prevent cascading DATEV failures:

```
CLOSED  ──failure──>  OPEN  ──timeout──>  HALF-OPEN
   ^                                          |
   |                                     success/failure
   +──────────── success ────────────────────/     \──> OPEN
```

| State | Behavior | Log Message |
|-------|----------|-------------|
| **Closed** | All DATEV notifications pass through | (normal operation) |
| **Open** | Notifications fail fast without calling DATEV | `CircuitBreaker: OPEN - skipping notification` |
| **Half-Open** | One test notification allowed through | `CircuitBreaker: HALF-OPEN - testing` |

**Configuration:**

| Setting | Default | Description |
|---------|---------|-------------|
| `DatevCircuitBreakerThreshold` | 3 | Failures before opening |
| `DatevCircuitBreakerTimeoutSeconds` | 30 | Seconds before half-open test |

---

## Configuration Hot-Reload

The INI file is monitored by a `FileSystemWatcher`. Changes take effect without restart:

```
File changed → Debounce (300ms) → Parse INI → Validate → Apply
```

### What Reloads Immediately

| Section | Settings | Effect |
|---------|----------|--------|
| `[Debug]` | `VerboseLogging`, `Contacts`, `TAPIDebug`, `DATEVDebug` | Immediate |
| `[Connection]` | All timeout/retry values | Next connection attempt |
| `[Settings]` | `ContactReshowDelaySeconds`, `LastContactRoutingMinutes` | Next call event |

### Value Clamping

All numeric settings are clamped to safe ranges on reload:

| Setting | Range |
|---------|-------|
| `ReconnectIntervalSeconds` | 1–300 |
| `ConnectionTimeoutSeconds` | 5–300 |
| `DatevCircuitBreakerThreshold` | 1–10 |
| `StaleCallTimeoutMinutes` | 30–1440 |
| `ContactReshowDelaySeconds` | 0–30 |
| `LastContactRoutingMinutes` | 0–1440 |

Values outside the range are clamped and a warning is logged.

---

## Contact Dump (Debug)

To dump all cached contacts for inspection:

```ini
[Debug]
Contacts=true
AddressatContacts=true
InstitutionContacts=true
```

Files are written to `%AppData%\3CXDATEVConnector\`:
- `contacts.txt` — All contacts with normalized phone numbers
- `addressat_contacts.txt` — DATEV Recipients only
- `institution_contacts.txt` — DATEV Institutions only

---

## Key Log Sequences

### Healthy Startup

```
[INFO] IsTerminalSession=False, SessionName=Console, Session: Id=1
[INFO] ========================================
[INFO] 3CX - DATEV Connector starting (Extension=161)
[INFO] === DATEV Connection Test ===
[INFO]   DATEV Telefonie (ROT): Available
[INFO]   DATEV SDD (Kontakte): Available
[INFO]   DATEV available - all components detected
[INFO] =============================
[INFO] TAPI line monitor connected: 161 : Max Mustermann
[INFO] Extension auto-detected from 3CX TAPI: 161
[INFO] Loading contacts from DATEV SDD...
[INFO] Loaded 19421 contacts from DATEV SDD
[INFO] 3CXDatevConnector: Contact lookup dictionary built with 24251 unique phone number keys
```

### Incoming Call (Full Lifecycle)

```
[INFO] RINGING: Incoming call 161-04022026-1430-0912387 from ********4567 (contact=Mueller GmbH)
[INFO] Bridge -> DATEV: NewCall (Direction=eDirIncoming, Contact=Mueller GmbH)
[INFO] CONNECTED: Call 161-04022026-1430-0912387
[INFO] Bridge -> DATEV: CallStateChanged (State=eCSConnected)
[INFO] Contact reshow: Contact changed - new=Mueller Hans (SyncID=datev-123)
[INFO] Bridge -> DATEV: CallAdressatChanged (Contact=Mueller Hans, DataSource=DATEV_Adressaten)
[INFO] DISCONNECTED: Call 161-04022026-1430-0912387
[INFO] Bridge -> DATEV: CallStateChanged (State=eCSFinished)
[INFO] Bridge -> DATEV: NewJournal (Duration=00:05:23, Contact=Mueller Hans)
```

### Click-to-Dial from DATEV

```
[INFO] DATEV -> Bridge: Dial command received
[DEBUG]   CalledNumber=********4567, Adressatenname=Mueller GmbH, SyncID=datev-456
[INFO] DATEV Dial: Pending call stored for ********4567 (SyncID=datev-456)
[INFO] DATEV Dial: MAKE-CALL sent for ********4567
[INFO] RINGBACK: Outgoing call 161-04022026-1435-4829173 to ********4567 (DATEV-initiated, SyncID=datev-456)
[INFO] Bridge -> DATEV: NewCall (Direction=eDirOutgoing, SyncID=datev-456)
[INFO] CONNECTED: Call 161-04022026-1435-4829173
[INFO] Bridge -> DATEV: CallStateChanged (State=eCSConnected)
[INFO] DISCONNECTED: Call 161-04022026-1435-4829173
[INFO] Bridge -> DATEV: CallStateChanged (State=eCSFinished)
```

### Circuit Breaker Activation

```
[ERROR] DATEV COM call failed: 0x80040154
[WARNING] CircuitBreaker: Failure 1/3
[ERROR] DATEV COM call failed: 0x80040154
[WARNING] CircuitBreaker: Failure 2/3
[ERROR] DATEV COM call failed: 0x80040154
[WARNING] CircuitBreaker: OPEN after 3 failures (timeout=30s)
[WARNING] CircuitBreaker: OPEN - skipping DATEV notification
...
[INFO] CircuitBreaker: HALF-OPEN - testing
[INFO] CircuitBreaker: CLOSED - test successful
```
