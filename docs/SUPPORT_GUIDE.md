# 3CX - DATEV Connector — Support Guide

## Architecture Overview

The connector is a .NET 9.0 WinForms system tray application (x86) that acts as a proxy between the 3CX Softphone and DATEV Arbeitsplatz.

```
+------------------+     Named Pipe      +-------------------+
|  3CX Softphone   | <=================> |                   |
|  (V20, WinUI 3)  |   UTF-16 LE msgs   |   3CX-DATEV       |
+------------------+                     |   Connector       |
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
| **ConnectorService** | `Core/ConnectorService.cs` | Central orchestrator — wires TAPI, DATEV, and UI; events: `StatusChanged`, `ModeChanged` |
| **TapiLineMonitor** | `Tapi/TapiLineMonitor.cs` | TAPI 2.x call event monitoring |
| **PipeConnectionMethod** | `Tapi/PipeConnectionMethod.cs` | Named pipe server for 3CX commands |
| **TapiPipeServer** | `Tapi/TapiPipeServer.cs` | Low-level pipe I/O |
| **DatevAdapter** | `Datev/COMs/DatevAdapter.cs` | COM adapter registered in ROT |
| **NotificationManager** | `Datev/Managers/NotificationManager.cs` | Connector → DATEV notifications (with circuit breaker) |
| **CallDataManager** | `Datev/Managers/CallDataManager.cs` | Call data handling and SyncID management |
| **DatevContactRepository** | `Datev/DatevContactRepository.cs` | Unified contact repository with phone lookup |
| **WebclientConnectionMethod** | `Webclient/WebclientConnectionMethod.cs` | IConnectionMethod for browser extension |
| **WebSocketBridgeServer** | `Webclient/WebSocketBridgeServer.cs` | WebSocket server for extension (port 19800) |
| **ConnectionMethodSelector** | `Core/ConnectionMethodSelector.cs` | Auto-detection of telephony mode |
| **CallTracker** | `Core/CallTracker.cs` | Active and pending call tracking |
| **LogManager** | `Datev/Managers/LogManager.cs` | Structured logging with rotation |

---

## Startup Sequence

The bridge performs these steps on startup in order:

```
1. Detect environment and select telephony mode
   ├── Log session info (Session ID, IsTerminalSession, SessionName)
   └── ConnectionMethodSelector: Auto, Desktop, TerminalServer, or WebClient
       ├── Auto: Desktop → TerminalServer → WebClient (priority order)
       ├── Explicit mode: use configured provider only
       └── Log selected mode and reason

2. Initialize telephony provider
   ├── TAPI: Connect line monitor
   ├── Pipe: Create Named Pipe server (\\.\pipe\3CX_tsp_server_{extension})
   └── WebClient: Start WebSocket server (port 19800)

3. Initialize DATEV
   ├── Register COM adapter in ROT (AdapterManager)
   ├── Run DATEV connection test
   │   ├── Check DATEV Telefonie process
   │   ├── Check ROT availability (CLSID lookup)
   │   └── Check SDD assembly availability
   ├── Load contacts from SDD (async)
   └── Start config file watcher (hot-reload)

4. Await telephony connection
   ├── TAPI: Line monitor already connected
   ├── Pipe: Await Named Pipe client connection
   └── WebClient: Await browser extension HELLO
```

### DATEV Connection Test Output

```
========================================
DATEV Konnektivitätstest
========================================
DATEV Telefonie (ROT): Verfügbar
DATEV SDD (Kontakte): Verfügbar
DATEV Alle Komponente Verfügbar
```

If DATEV is not available:

```
DATEV Telefonie (ROT): NICHT VERFÜGBAR
DATEV SDD (Kontakte): Verfügbar
DATEV SDD verfügbar - Kontakte erreichbar (CTI noch nicht verfügbar)
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
- **Age-based purge:** Rotated files older than `LogRetentionDays` (default: 7 days, range 1–90) are deleted at startup and after each rotation

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

### Log Prefixes

Messages are prefixed for structured filtering:

| Prefix | Description | Example |
|--------|-------------|---------|
| `DATEV -> Bridge` | Commands from DATEV | `DATEV -> Bridge: Wählen-Befehl empfangen (CallID=... zugewiesen)` |
| `DATEV:` | Notifications to DATEV | `DATEV: NewCall (CallId=..., Direction=eDirIncoming)` |
| `TAPI:` | TAPI call events | `TAPI: LINECALLSTATE_OFFERING on line 161` |
| `Connector:` | Internal connector operations | `Connector: Added call 161-04022026-1430-1234567` |
| `[DATEV] Circuit:` | Circuit breaker state | `[DATEV] Circuit: Closed -> Open (failures=3/3, retry in 30s)` |
| `Terminal Server (TAPI):` | Session management | `Terminal Server (TAPI): Ja` |
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

1. Search log for `DATEV Konnektivitätstest`
2. Expected: `DATEV Alle Komponente Verfügbar`
3. If `NICHT VERFÜGBAR`: DATEV Arbeitsplatz not running or not reachable

**Check Named Pipe (Terminal Server):**

1. Search for `PipeServer` messages
2. Expected: `PipeServer: Starte auf \\.\pipe\3CX_tsp_server_161`
3. Then: `PipeServer: 3CX Softphone verbunden`
4. If only `Pipe erstellt, warte auf 3CX Softphone Verbindung...`: Pipe created but 3CX client hasn't connected

### Issue: No Contacts Loaded

```
[INFO] Kontakte werden von DATEV Stamm Daten Dienst (SDD) geladen...
[ERROR] Kontakte konnten nicht von DATEV SDD geladen werden
```

**Possible causes:**
- DATEV Basis not installed (missing GAC assemblies)
- Insufficient DATEV permissions for the Windows user
- SDD not responding (increase `SddMaxRetries` in `[Connection]`)

**Expected success log:**

```
[INFO] Kontakte werden von DATEV SDD geladen...
[INFO] Kontakte geladen: 19421 Kontakte, 24251 Telefonnummern-Schlüssel
```

### Issue: Calls Detected but No DATEV Notification

1. Search log for `DATEV: NewCall`
2. If present: DATEV notification was sent
3. If missing, search for `Circuit-Breaker`:
   ```
   [INFO] Benachrichtigung 'NewCall' übersprungen - DATEV Circuit-Breaker offen
   ```
4. The circuit breaker opens after 3 consecutive failures. Wait for timeout (default: 30s) or restart the bridge

### Issue: Contact Not Found for Known Number

1. Enable debug logging (`VerboseLogging=true`)
2. Search for `Kontaktsuche`:
   ```
   [INFO] Connector: Kontaktsuche - Eingabe='********4567' Normalisiert='*****45678'
   [INFO] Connector: Kontaktsuche - Keine Übereinstimmung gefunden
   ```
3. Check normalization: Input is stripped to last `MaxCompareLength` digits
4. Dump all contacts: Add `Contacts=true` to `[Debug]` section, then check `contacts.txt`
5. Adjust `MaxCompareLength` if digits don't align (e.g., increase to 12 for longer numbers)

### Issue: SyncID Lost on DATEV-Initiated Calls

1. Search log for `DATEV -> Bridge: Wählen-Befehl`:
   ```
   [INFO] DATEV -> Bridge: Wählen-Befehl empfangen (CallID=... zugewiesen)
   ```
2. Search for `DATEV-initiierter`:
   ```
   [INFO] Connector: DATEV-initiierter ausgehender Anruf ... an ********4567 (SyncID=datev-456, Kontakt=Mueller GmbH)
   ```
3. If SyncID is missing: The pending call wasn't matched by phone number. Check normalization.

### Issue: Journal Popup Not Appearing

1. Verify `EnableJournalPopup=true` in `[Settings]`
2. For outbound calls: `EnableJournalPopupOutbound=true`
3. Journal popup only shows for calls with a resolved DATEV contact (AdressatenId)
4. If `Stummschalten` (silent mode) is active, popups are suppressed

---

## Desktop vs Terminal Server

### Desktop (Single User)

```
Terminal Server (TAPI): Nein
```

- TAPI line monitor connects directly
- Standard pipe name: `\\.\pipe\3CX_tsp_server_{ext}`
- Single ROT registration with base GUID

### Terminal Server / RDS (Multi-User)

```
Terminal Server (TAPI): Ja
```

- Named Pipe server is created first (3CX polls every 2 seconds)
- Each session has isolated ROT (Windows per-session isolation)
- No GUID modification needed — Windows handles isolation
- Each user runs their own bridge instance with their own TAPI driver

**Key difference:** On Terminal Server, the pipe server must be created before TAPI initialization because the 3CX Softphone polls for the pipe at startup.

### Session Diagnostics in Log

The `SessionManager` logs session details at startup:

```
[INFO] Terminal Server (TAPI): Ja
[INFO] ========================================
[INFO] 3CX - DATEV Connector starting (Extension=161)
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
| **Open** | Notifications fail fast without calling DATEV | `Benachrichtigung '...' übersprungen - DATEV Circuit-Breaker offen` |
| **Half-Open** | One test notification allowed through | `[DATEV] Circuit: Open -> HalfOpen` |

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
| `[Connection]` | `TelephonyMode` | Immediate UI update via `ModeChanged` event; provider switch on next reconnect |
| `[Connection]` | All timeout/retry values | Next connection attempt |
| `[Settings]` | `ContactReshowDelaySeconds`, `LastContactRoutingMinutes` | Next call event |

### Value Clamping

All numeric settings are clamped to safe ranges on reload:

| Setting | Range |
|---------|-------|
| `ReconnectIntervalSeconds` | 1–300 |
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
[INFO] Terminal Server (TAPI): Nein
[INFO] ========================================
[INFO] 3CX - DATEV Connector starting (Extension=161)
[INFO] ========================================
[INFO] DATEV Konnektivitätstest
[INFO] ========================================
[INFO] DATEV Telefonie (ROT): Verfügbar
[INFO] DATEV SDD (Kontakte): Verfügbar
[INFO] DATEV Alle Komponente Verfügbar
[INFO] TAPI line monitor connected: 161 : Max Mustermann
[INFO] Extension auto-detected from 3CX TAPI: 161
[INFO] Kontakte werden von DATEV SDD geladen...
[INFO] Kontakte geladen: 19421 Kontakte, 24251 Telefonnummern-Schlüssel
```

### Incoming Call (Full Lifecycle)

```
[INFO] Connector: Eingehender Anruf 161-04022026-1430-0912387 von ********4567 (Kontakt=Mueller GmbH)
[INFO] DATEV: NewCall (CallId=161-04022026-1430-0912387, Direction=eDirIncoming, Number=********4567, Contact=Mueller GmbH, DataSource=DATEV_Adressaten)
[INFO] Connector: Call 161-04022026-1430-0912387
[INFO] DATEV: CallStateChanged (CallId=161-04022026-1430-0912387, State=eCSConnected)
[INFO] Kontaktauswahl: Kontakt geändert für Anruf 161-04022026-1430-0912387 - neu=Mueller Hans (SyncID=datev-123)
[INFO] DATEV: CallAdressatChanged (CallId=161-04022026-1430-0912387, Contact=Mueller Hans, DataSource=DATEV_Adressaten)
[INFO] Connector: Call 161-04022026-1430-0912387 (wasConnected=True, duration=00:05:23)
[INFO] DATEV: CallStateChanged (CallId=161-04022026-1430-0912387, State=eCSFinished)
[INFO] DATEV: NewJournal (CallId=161-04022026-1430-0912387, Duration=00:05:23, Contact=Mueller Hans, DataSource=DATEV_Adressaten, Number=********4567)
```

### Click-to-Dial from DATEV

```
[INFO] DATEV -> Bridge: Wählen-Befehl empfangen (CallID=161-04022026-1435-4829173 zugewiesen)
[INFO] Connector: Ausstehender Anruf hinzugefügt 161-04022026-1435-4829173
[INFO] PipeConnectionMethod: MAKE-CALL gesendet OK - ********4567 (reqId=1)
[INFO] Connector: DATEV-initiierter ausgehender Anruf 161-04022026-1435-4829173 an ********4567 (SyncID=datev-456, Kontakt=Mueller GmbH)
[INFO] DATEV: NewCall (CallId=161-04022026-1435-4829173, Direction=eDirOutgoing, Number=********4567, Contact=Mueller GmbH, DataSource=DATEV_Adressaten)
[INFO] Connector: Call 161-04022026-1435-4829173
[INFO] DATEV: CallStateChanged (CallId=161-04022026-1435-4829173, State=eCSConnected)
[INFO] Connector: Call 161-04022026-1435-4829173 (wasConnected=True, duration=00:03:12)
[INFO] DATEV: CallStateChanged (CallId=161-04022026-1435-4829173, State=eCSFinished)
```

### WebClient Extension Disconnect

```
[INFO] WebClient Connector: Close-Frame empfangen
[INFO] WebClient Connector: Erweiterung getrennt
```

After disconnect, the connector enters a reconnect loop, waiting for the extension to reconnect:

```
[DEBUG] WebClient Connector: Handshake complete (extension=101)
```

All UI forms (StatusForm, SettingsForm, tray icon) update automatically via the `StatusChanged` event. Tray balloon notifications are shown for both connect and disconnect transitions (if notifications are enabled).

### Circuit Breaker Activation

```
[ERROR] DATEV COM call failed: 0x80040154
[INFO] [DATEV] Circuit: Closed -> Open (failures=3/3, retry in 30s)
[INFO] Benachrichtigung 'NewCall' übersprungen - DATEV Circuit-Breaker offen
...
[INFO] [DATEV] Circuit: Open -> HalfOpen
[INFO] [DATEV] Circuit: HalfOpen -> Closed
```
