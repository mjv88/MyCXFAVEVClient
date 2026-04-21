# 3CX - DATEV Connector — Integration Test Plan

## Test Environments

### Environment 1: Desktop (TAPI mode)

- Single-user Windows 10/11 desktop
- 3CX Softphone V20 + 3CX Multi-Line TAPI driver installed
- DATEV Arbeitsplatz running with Telefonie component

### Environment 2: Terminal Server (Pipe mode)

- RDS / Terminal Server session (RDP)
- 3CX Softphone V20 connected via Named Pipe
- DATEV Arbeitsplatz in same session

### Environment 3: WebClient (Browser Extension)

- Chrome or Edge with 3CX DATEV Connector extension installed
- 3CX WebClient (PWA) open and logged in
- DATEV Arbeitsplatz running

---

## Log Files to Collect

For each test, collect:

1. **3CX DATEV Connector log** — `%AppData%\3CXDATEVConnector\3CXDatevConnector.log`
2. **DATEV Telefonie log** — location varies by DATEV installation (typically in DATEV log directory)

Enable verbose logging before testing:

```ini
[Debug]
VerboseLogging=true
```

---

## Test Cases

### TC-01: Startup & Mode Detection

**Preconditions:** Application not running. INI configured with `TelephonyMode=Auto`.

**Steps:**

1. Start `3cxDatevConnector.exe`
2. Observe system tray icon and log output

**Expected log output:**

```
3CX Telefonie Modus Initialisierung...
3CX Telefonie Modus: Auto-Detection (configured)
Desktop = True|False
Terminal Server = False
WebClient = Detection..
```

- Environment 1 (Desktop): Mode should resolve to `Tapi`, log shows `TAPI selected - Desktop environment`
- Environment 2 (Terminal Server): Mode should resolve to `Pipe`, log shows `Terminal server session detected`
- Environment 3 (WebClient): Mode should resolve to `WebClient`, log shows `WebClient Connection succeeded via WS`

**Pass criteria:** Correct mode detected, tray icon shows green (or orange if DATEV not yet ready).

---

### TC-02: DATEV Connectivity Test

**Preconditions:** DATEV Arbeitsplatz running with Telefonie component.

**Steps:**

1. Start the connector (or restart via tray menu)
2. Check log for DATEV connection test output

**Expected log output:**

```
=== DATEV Connection Test ===
  DATEV Telefonie (ROT): Available
  DATEV SDD (Kontakte): Available
  DATEV Alle Komponente Verfügbar
=============================
```

**Pass criteria:** All DATEV components detected. Tray icon green ring.

---

### TC-03: Contact Synchronisation

**Preconditions:** DATEV SDD accessible with contacts.

**Steps:**

1. Start the connector or click "Kontakte neu laden" (Strg+R)
2. Check log for contact loading output

**Expected log output:**

```
DATEV Kontaktsynchronisation
Start des 3CX-DATEV-Erkennungsdienst...
Loaded XXXX Adressaten, YYYY einmalige Telefonnummern
Loaded XXXX Institutionen, YYYY einmalige Telefonnummern
Contact lookup dictionary built with ZZZZ unique phone number keys
```

**Verify:** No typos in log messages (`synchronisation` not `syncronisation`, `Adressaten` not `Addressaten`, `einmalige` not `einmaligen`).

**Pass criteria:** Contacts loaded successfully with correct counts and typo-free log messages.

---

### TC-04: Inbound Call — Answered

**Preconditions:** Connector running, DATEV connected, contacts loaded. A known DATEV contact number available.

**Steps:**

1. Call the extension from a known DATEV contact number
2. Observe caller popup
3. Answer the call
4. Wait for contact reshow (if multiple contacts match)
5. Hang up
6. Observe journal popup

**Expected log output:**

```
RINGING: Incoming call {callId} from ********XXXX (contact=Contact Name)
Connector -> DATEV: NewCall (Direction=eDirIncoming, Contact=Contact Name)
CONNECTED: Call {callId}
Connector -> DATEV: CallStateChanged (State=eCSConnected)
DISCONNECTED: Call {callId}
Connector -> DATEV: CallStateChanged (State=eCSFinished)
```

**Verify:** Log uses `Connector -> DATEV:` prefix (not `Bridge -> DATEV:`).

**Pass criteria:** Full call lifecycle logged. DATEV receives all notifications. Journal popup appears after hangup.

---

### TC-05: Inbound Call — Missed

**Preconditions:** Same as TC-04.

**Steps:**

1. Call the extension from a known number
2. Let it ring without answering
3. Caller hangs up

**Expected log output:**

```
RINGING: Incoming call {callId} from ********XXXX (contact=Contact Name)
Connector -> DATEV: NewCall (Direction=eDirIncoming, Contact=Contact Name)
DISCONNECTED: Call {callId}
Connector -> DATEV: CallStateChanged (State=eCSAbsence)
```

**Pass criteria:** Absence state sent to DATEV. No journal popup (call was never connected).

---

### TC-06: Outbound Call — Click-to-Dial from DATEV

**Preconditions:** Same as TC-04.

**Steps:**

1. In DATEV, click the dial button next to a contact
2. Observe the outgoing call in 3CX
3. Remote party answers
4. Hang up

**Expected log output:**

```
DATEV -> Bridge: Dial command received
DATEV Dial: connected=True
DIAL sent for ********XXXX
```

- Environment 3 (WebClient): Additional log `WebClient Connector: RINGBACK callId=...`

**Verify:** Log shows `DATEV Dial: connected=True` (simplified format, not the old verbose format).

**Pass criteria:** Call initiated from DATEV, SyncID preserved through lifecycle.

---

### TC-07: Outbound Call — Manual from 3CX

**Preconditions:** Same as TC-04.

**Steps:**

1. Manually dial a number from the 3CX application
2. Remote party answers
3. Hang up

**Expected log output:**

```
RINGBACK: Outgoing call {callId} to ********XXXX
Connector -> DATEV: NewCall (Direction=eDirOutgoing)
CONNECTED: Call {callId}
Connector -> DATEV: CallStateChanged (State=eCSConnected)
DISCONNECTED: Call {callId}
Connector -> DATEV: CallStateChanged (State=eCSFinished)
```

**Pass criteria:** Outgoing call detected and reported to DATEV.

---

### TC-08: Contact Lookup — Match Found

**Preconditions:** Contacts loaded. Call from a number that matches exactly one DATEV contact.

**Steps:**

1. Receive call from known number
2. Check log for contact resolution

**Expected log output:**

```
Connector: Contact lookup - Input='********XXXX' ... Match found
```

**Pass criteria:** Contact correctly resolved, shown in popup and DATEV notification.

---

### TC-09: Contact Lookup — No Match

**Preconditions:** Contacts loaded. Call from an unknown number.

**Steps:**

1. Receive call from number not in DATEV
2. Check log for lookup result

**Expected log output:**

```
Connector: Contact lookup - Input='********XXXX' ... No match found
```

**Pass criteria:** No crash, DataSource set to `3CX`, popup shows raw caller ID.

---

### TC-10: Contact Selection — Multiple Matches

**Preconditions:** A phone number that matches multiple DATEV contacts.

**Steps:**

1. Receive call from the shared number
2. Answer the call
3. Wait for contact selection dialog (after configurable delay)
4. Select a different contact
5. Verify DATEV receives `CallAdressatChanged`

**Expected log output:**

```
Contact reshow: Contact changed - new=Selected Contact (SyncID=...)
Connector -> DATEV: CallAdressatChanged (Contact=Selected Contact, DataSource=DATEV_Adressaten)
```

**Pass criteria:** Contact selection dialog appears, selection updates DATEV.

---

### TC-11: Journal Popup & Re-Journal

**Preconditions:** `EnableJournalPopup=true`. Call with a matched DATEV contact.

**Steps:**

1. Complete a call with a DATEV-matched contact
2. Enter a note in the journal popup
3. Click "Daten weitergeben"
4. Open Anrufliste (Strg+H)
5. Select the call entry and click "Journal senden"

**Expected log output:**

```
Connector -> DATEV: NewJournal (Duration=HH:MM:SS, Contact=Contact Name)
```

**Pass criteria:** Journal sent to DATEV. Call history shows "Ja" status. Re-journal from history also works.

---

### TC-12: Extension Reconnect (WebClient only)

**Preconditions:** Environment 3 only. Connector running with WebClient mode active.

**Steps:**

1. Close the 3CX WebClient browser tab
2. Wait 5 seconds
3. Reopen the 3CX WebClient
4. Verify extension reconnects

**Expected log output:**

```
WebClient HELLO von extension=XXX...
WebClient Connector: WebClient connected
WebClient Extension Detection: XXX
```

**Verify:** Log shows `WebClient HELLO` (no colon after WebClient), `WebClient connected` (no IP address).

**Pass criteria:** Extension reconnects automatically, HELLO received without IP logging.

---

### TC-12a: Extension Disconnect Updates UI (WebClient only)

**Preconditions:** Environment 3 only. Connector running with WebClient mode active. StatusForm and/or SettingsForm open.

**Steps:**

1. Verify UI shows connected state (green tray icon, "Verbunden" in StatusForm/SettingsForm)
2. Close the browser or disable the browser extension
3. Observe tray icon, StatusForm, and SettingsForm

**Expected log output:**

```
WebClient Connector: Extension disconnected
```

**Expected UI behavior:**
- Tray icon changes to red/orange
- Tray balloon notification shows "Getrennt" (if notifications enabled)
- StatusForm updates to disconnected state
- SettingsForm mode/status labels update

**Pass criteria:** All UI elements update within 1-2 seconds of extension disconnect without user interaction.

---

### TC-12b: Connection Mode Change via Settings

**Preconditions:** Connector running. SettingsForm open.

**Steps:**

1. Note the current connection mode displayed in SettingsForm (e.g., "Desktop (TAPI)" or "Automatisch")
2. Change the connection mode dropdown to a different value (e.g., "Webclient (Browser)")
3. Click Save
4. Observe the mode label in SettingsForm immediately after save
5. Open StatusForm and verify the mode label there

**Expected behavior:**
- Mode label in SettingsForm updates immediately after Save (no restart needed)
- Mode label in StatusForm shows the new mode
- Log shows: `ConnectionMode Konfiguration zur Laufzeit geändert: Auto -> WebClient`
- Connector switches connection methods on next reconnect cycle

**Pass criteria:** Mode label updates instantly in both forms without application restart.

---

### TC-13: Silent Mode Toggle

**Preconditions:** Connector running.

**Steps:**

1. Right-click tray icon → Stummschalten (enable)
2. Receive a call
3. Verify no popup appears
4. Disable silent mode
5. Receive another call
6. Verify popup appears

**Pass criteria:** Silent mode suppresses all popups. Disabling restores them.

---

### TC-14: Extension Popup — Dark Theme & Status (WebClient only)

**Preconditions:** Environment 3 only. Browser extension installed.

**Steps:**

1. Click the extension icon in Chrome/Edge toolbar
2. Verify dark theme popup renders (background `#2D2D30`, white text)
3. Verify status bar shows connection state:
   - Green dot + "Connected" when connector is running and connected
   - Red dot + "Disconnected" when connector is stopped
   - Yellow dot + "Connecting..." during connection attempt
4. Verify extension number shows bold (right-aligned in status bar), or "—" if not detected
5. Change the "DATEV Auto-DIAL" delay value (default: 750 ms)
6. Click "Save" — button should flash green briefly
7. Close and reopen popup — saved value should persist
8. Click "Reload Extension" — extension should reload

**Pass criteria:** Dark theme matches main app. Status dot reflects live WebSocket state. Dial delay persists across popup opens.

---

## WebClient Mode — Terminal Server / RDS

These scenarios cover the multi-user auto-port discovery behaviour on Remote Desktop Services. Each user's connector picks the first free port in the range 19800–19899 and the browser extension discovers it via a session-scoped probe.

### TS-1: Single user, clean install

**Preconditions:** RDS host available. 3CX-DATEV-Connector not yet installed for the test user.

**Steps:**

1. Log into an RDS session. Install the 3CX-DATEV-Connector per-user.
2. Start the tray app. Open `%AppData%\3CXDATEVConnector\logs\*.log`.
3. Open 3CX WebClient in Chrome/Edge with the extension installed.
4. Place a test call and confirm DATEV receives the call event.

**Expected log output:**

```
Bridge lauscht auf Port 19800 (Session-ID <N>)
```

Service-worker console:

```
Bridge verbunden auf Port 19800
```

**Pass criteria:** Bridge binds 19800, extension connects to 19800, DATEV receives the call event.

---

### TS-2: Two users, sequential start

**Preconditions:** RDS host supporting at least two concurrent sessions. Connector installed per-user for both A and B.

**Steps:**

1. Log in as user A. Start the tray app.
2. Log in as user B (without logging A out). Start the tray app for B.
3. Open WebClient as user B.
4. Place a test call as user B.

**Expected log output:**

```
(user A) Bridge lauscht auf Port 19800
(user B) Bridge lauscht auf Port 19801
(user B service-worker) Bridge verbunden auf Port 19801
```

**Pass criteria:** User A stays on 19800, user B lands on 19801. Only user B's DATEV receives user B's test call.

---

### TS-3: Two users, simultaneous start

**Preconditions:** Same as TS-2. A mechanism to start both tray apps within ~1 second (batch file with two `psexec -u` invocations, or two RDP sessions clicked near-simultaneously).

**Steps:**

1. Script-start the tray app as user A and user B within 1 second of each other.
2. Inspect both users' log files.

**Expected log output:**

- Exactly one user logs `Bridge lauscht auf Port 19800`.
- The other user logs `Bridge lauscht auf Port 19801`.
- Neither log contains `Kein freier Port`.

**Pass criteria:** Both bridges start cleanly on distinct ports without collision errors.

---

### TS-4: Cross-session cache hit (negative test)

**Preconditions:** TS-2 setup complete (user A on 19800, user B on 19801).

**Steps:**

1. As user B, in the service-worker console, run `chrome.storage.local.set({bridgePort: 19800})`.
2. Reload the extension.
3. Place a test call as user B.

**Expected log output:**

Service-worker console:

```
Cache-Port 19800 nicht erreichbar, Scan lief, gefunden auf 19801
```

**Pass criteria:** User A's bridge on 19800 silently refuses user B's probe (session check). Extension falls back to scan and locks onto 19801. User A's DATEV does NOT receive user B's call event.

---

### TS-5: Bridge restart port re-discovery

**Preconditions:** User A and user B both logged in with connectors running (A on 19800, B on 19801).

**Steps:**

1. As user A, kill the tray app: `taskkill /f /im 3cxDatevConnector.exe`.
2. As user B, restart the tray app (still should hold 19801; if B had 19800, adjust so B grabs 19800 here).
3. As user A, restart the tray app.
4. Check user A's service-worker console for reconnect.

**Expected log output:**

```
(user A, restart) Bridge lauscht auf Port 19801
```

(or whichever port is free — must not collide with user B)

**Pass criteria:** User A's bridge picks a free port and the extension reconnects to the new port automatically.

---

### TS-6: 25-user load (reduced from full 100)

**Preconditions:** RDS host capable of hosting 25 concurrent sessions. Connector installed per-user for all 25.

**Steps:**

1. Script 25 RDS sessions starting the tray app.
2. Collect the log file from each user's `%AppData%\3CXDATEVConnector\logs\` directory.
3. Extract the `Bridge lauscht auf Port` line from each.
4. Spot-check 3 random users' logs for the HELLO line.

**Expected log output:**

- 25 distinct ports in the range 19800–19824 across the 25 log directories.
- Each spot-checked log shows only that user's own extension number in the `WebClient HELLO von extension=...` line.

**Pass criteria:** No port collisions, no cross-session bleed in HELLO lines.

---

### TS-7: Port exhaustion

**Preconditions:** Tray app not running. PowerShell available.

**Steps:**

1. Externally bind all 100 ports in the range using a PowerShell loop with `[System.Net.Sockets.TcpListener]` instances (keep the listeners alive in that shell).
2. Start the tray app.
3. Check the connector log and tray UI.

**Expected log output:**

```
Kein freier Port in 19800-19899, Bridge nicht gestartet
```

**Pass criteria:** Connector logs the exhaustion message and the tray UI surfaces a user-visible failure state (connection state shows failed / error).

---

### TS-R: Regression — non-RDS single-user

**Preconditions:** Existing Desktop (TAPI) and Terminal Server (Pipe) environments from the top of this document.

**Steps:**

1. Re-run TC-01 through TC-14 on Environment 1 (Desktop) and Environment 2 (Terminal Server, Pipe mode).

**Pass criteria:** All existing TC-XX scenarios remain green. The auto-port change must not touch TAPI or Pipe paths.

---

## Log Format Verification Checklist

After running tests, verify the following in the log file:

| Check | Expected | Old (should NOT appear) |
|-------|----------|------------------------|
| Mode initialization | `3CX Telefonie Modus Initialisierung...` | — |
| Auto mode display | `Auto-Detection (configured)` | `Auto (configured)` |
| Desktop detection | `Desktop = False` or `Desktop = True` | `Desktop = 3CXTAPI.ini nicht gefunden` |
| Terminal Server | `Terminal Server = False` | `Terminal Server = False, SessionName=Console, Session: Id=X` |
| WebClient detection | `WebClient = Detection..` | `WebClient = Auto-Detect` |
| WebClient HELLO | `WebClient HELLO von extension=...` | `WebClient: HELLO von extension=...` |
| Client connected | `WebClient Connector: WebClient connected` | `WebClient Connector: Client connected from 1.2.3.4` |
| DATEV notifications | `Connector -> DATEV:` | `Bridge -> DATEV:` |
| Internal operations | `Connector:` | `Bridge:` |
| DATEV commands | `DATEV -> Bridge:` (unchanged) | — |
| ROT registration | `[DEBUG]` level only | `[INFO]` level |
| TryAccept logs | `[DEBUG]` level only | `[INFO]` level |
| Contact sync | `Kontaktsynchronisation` | `Kontaktsyncronisation` |
| Recipients | `Adressaten` | `Addressaten` |
| Phone numbers | `einmalige Telefonnummern` | `einmaligen Telefonnummern` |
| DIAL flow | `DATEV Dial: connected=True` | `DATEV Dial: Sending to WebclientTelephonyProvider (connected=True)` |
| WebClient call states | `WebClient Connector: RINGBACK callId=...` | `WebclientTelephonyProvider: RINGBACK callId=...` |
| Extension identity | `3CX WebClient` | `3CX DATEV Connector Extension` |
