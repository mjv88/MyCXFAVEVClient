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

### TC-14: Settings Change — Dial Delay (WebClient only)

**Preconditions:** Environment 3 only. Browser extension installed.

**Steps:**

1. Click the extension icon in Chrome/Edge toolbar
2. Change the "Wählverzögerung" value (default: 750ms)
3. Click "Speichern"
4. Initiate a Click-to-Dial from DATEV
5. Verify the delay is applied before the dial command is sent

**Pass criteria:** Dial delay matches the configured value. Extension popup shows the saved value on reopen.

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
