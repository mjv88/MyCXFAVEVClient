# 3CX - DATEV Connector — User Guide

## Overview

The 3CX - DATEV Connector connects **3CX** (Desktop App or WebClient) with **DATEV Arbeitsplatz**. It runs as a lightweight Windows system tray application and provides:

- Incoming and outgoing call notifications to DATEV
- Click-to-Dial from DATEV applications
- Automatic contact lookup from DATEV central master data (SDD)
- Call journaling with optional note entry
- Caller popup notifications

> **Note:** This application replaces the legacy 3CX MyPhonePlugins integration that was removed in 3CX V20.

---

## Requirements

| Component | Requirement |
|-----------|-------------|
| Operating System | Windows 10 or Windows 11 (x86 or x64) |
| Runtime | .NET Framework 4.8 (included in Windows 10 1903+) |
| 3CX (Desktop mode) | 3CX Windows Softphone App V20 or later + 3CX Multi-Line TAPI driver |
| 3CX (Webclient mode) | Chrome or Edge with 3CX DATEV Connector browser extension |
| DATEV | DATEV Arbeitsplatz with Telefonie component |
| DATEV DLLs | `DATEV.Interop.DatevCtiBuddy.dll`, `Datev.Sdd.Data.ClientInterfaces.dll`, `Datev.Sdd.Data.ClientPlugIn.Base.dll` (installed via GAC by DATEV) |

---

## Installation

### Option A: Portable Mode

1. Copy `3cxDatevConnector.exe` to any folder on the user's machine
2. Run the executable — configuration is created automatically at:
   ```
   %AppData%\3CXDATEVConnector\3CXDATEVConnector.ini
   ```
3. On first run, the application prompts you to launch the **Setup Wizard** for initial configuration

### Option B: MSI Installation (Recommended)

Deploy via GPO, SCCM, or Microsoft Intune as a **per-user installation**:

1. The MSI installs `3cxDatevConnector.exe` to `%LocalAppData%\Programs\3CXDATEVConnector\`
2. Configuration directory is created at `%AppData%\3CXDATEVConnector\`
3. Optionally pre-seed the INI configuration during deployment (see [Pre-Seeding Configuration](#pre-seeding-configuration))
4. Log files are written to `%AppData%\3CXDATEVConnector\3CXDatevConnector.log`

---

## First-Run Setup Wizard

On first launch (when no INI file exists), the application creates a default configuration and asks if you would like to run the Setup Wizard. The wizard has 5 steps:

### Step 1 — Welcome

Overview of the bridge features: TAPI monitoring, DATEV integration, and autostart configuration.

### Step 2 — Telephony Mode

Select the connection mode:

| Mode | Description |
|------|-------------|
| **Auto** (recommended) | Detects best provider automatically (Webclient → Pipe → TAPI) |
| **TAPI** | 3CX Windows Client on Desktop |
| **Pipe** | 3CX Windows Client on Terminal Server |
| **Webclient** | 3CX Webclient in Browser (Chrome/Edge) |

### Step 3 — Provider Configuration

The wizard adapts to the selected telephony mode:

**TAPI mode:**
The wizard detects all available 3CX TAPI lines and displays them in a dropdown.
- Format: `122 - Max Mustermann (Verbunden)`
- The extension number is auto-detected from the TAPI line name

**Pipe mode (Terminal Server):**
The wizard shows the Named Pipe server status:
- Extension number and pipe path (`\\.\pipe\3CX_tsp_server_{ext}`)
- Whether the 3CX Softphone is connected via pipe
- Whether the 3CX Softphone process is running in the current RDP session

**Webclient mode:**
The wizard shows the browser extension connection status:
- Whether the extension is connected via WebSocket (port 19800)
- Installation instructions for Chrome/Edge

### Step 4 — DATEV Connection Test

The wizard tests connectivity to DATEV:

- Checks if DATEV Telefonie is running and accessible via ROT
- Displays contact count on success (if contacts were already loaded)
- Shows troubleshooting hints on failure

### Step 5 — Finish

Summary of detected configuration and an option to enable Windows autostart.

---

## System Tray Operation

After setup, the bridge runs in the system tray (notification area). The tray icon shows the application logo with a colored status ring:

| Ring Color | Meaning |
|------------|---------|
| **Green** | Fully operational — both TAPI and DATEV connected |
| **Orange** | Partial — one component missing (TAPI or DATEV) |
| **Red** | Disconnected — neither TAPI nor DATEV available |

### Tray Menu (Right-Click)

| Menu Item | Description |
|-----------|-------------|
| **Status** | Open status overview |
| **Anrufliste** | Open call history for re-journaling (Strg+H) |
| **Kontakte neu laden** | Reload contacts from DATEV SDD (Strg+R) |
| **Einstellungen** | Open settings dashboard |
| **Hilfe** | Submenu: Troubleshooting, Log file, Setup Wizard |
| **Autostart** | Toggle Windows autostart (HKCU Run) |
| **Stummschalten** | Toggle silent mode (suppress popups) |
| **Neustart** | Reconnect TAPI and reload contacts |
| **Info** | About dialog with version and keyboard shortcuts |
| **Beenden** | Exit the application |

**Double-click** the tray icon to open the **Call History** (default). This can be changed in Settings to open the Status Overview instead.

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Strg+T | Test all connections |
| Strg+R | Reload contacts from DATEV |
| Strg+L | Open log file |
| Strg+H | Open call history |
| Strg+S | Save settings |
| F5 | Refresh current view |
| Esc | Close current window |

---

## Incoming Calls

When an incoming call is detected:

1. The bridge looks up the caller's phone number in the DATEV contact cache
2. If a contact is found, DATEV is notified via `NewCall`
3. A **Caller Popup** appears (if enabled) showing:
   - Call direction (EINGEHENDER ANRUF)
   - Contact name and phone number
   - Contact source (Adressaten / Institution)
4. When the call is **answered**, the popup closes automatically
5. If multiple contacts match the number, a **Contact Selection** dialog appears after the call connects (configurable delay)
6. When the call **ends**, a **Journal Popup** appears (if enabled) where you can enter a note and send it to DATEV

### Caller Popup Modes

| Mode | Behavior |
|------|----------|
| **Beide** (Both) | Shows both a form popup and a Windows balloon notification |
| **Formular** (Form) | Shows only the form popup |
| **Balloon** | Shows only the Windows balloon notification |

Configure via Settings → Pop-Up-Verhalten → Modus.

---

## Outgoing Calls

### Click-to-Dial from DATEV

1. In a DATEV application, click the dial button next to a contact
2. DATEV sends a `Dial` command to the bridge with the contact's phone number
3. The bridge sends a `MAKE-CALL` command to 3CX via named pipe
4. The outgoing call is tracked with the DATEV contact and SyncID preserved
5. All subsequent notifications include the correct DATEV context

### Outgoing Call from 3CX

When you dial manually from 3CX:

1. The bridge detects the outgoing call via TAPI
2. If enabled, a caller popup appears for outgoing calls
3. Contact lookup is performed against DATEV SDD
4. DATEV is notified of the call

> **Note:** Journal popup for outgoing calls is disabled by default. Enable via Settings → Pop-Up-Verhalten → Ausgehende Journal-Popup.

---

## Call Journaling

When a call ends and a DATEV contact was matched:

1. A **Journal Popup** appears showing the contact name and call duration
2. Enter a note in the text area (max 2,000 characters)
3. Click **"Daten weitergeben"** to send the journal entry to DATEV
4. Click **"Abbrechen"** or leave the note empty to skip

The journal entry is sent to DATEV via the `NewJournal` interface with:
- CallID, Direction, Begin/End times, Contact info, Note text

### Call History (Re-Journaling)

The bridge keeps recent DATEV-matched calls in memory for re-journaling:

1. Open via tray menu → **Anrufliste** (or Strg+H)
2. Two lists show recent **Eingehend** (inbound) and **Ausgehend** (outbound) calls
3. Select an entry and click **"Journal senden"** or double-click to open the journal popup
4. Journal status: **✓ Ja** (sent), **Offen** (pending), **—** (unmatched)
5. Previously-journaled entries are dimmed and cannot be re-sent

> **Note:** Only calls with a resolved DATEV contact are stored. Outbound call tracking is disabled by default.

---

## Contact Matching

The bridge matches phone numbers using a normalized suffix comparison:

| Incoming Number | Normalized | Last 10 Digits |
|-----------------|------------|----------------|
| `+49 89 12345678` | `498912345678` | `8912345678` |
| `0049 89 12345678` | `498912345678` | `8912345678` |
| `089/12345678` | `08912345678` | `8912345678` |

All three formats match the same DATEV contact.

### Last-Contact Routing

When a caller has multiple DATEV contacts matching their number, the bridge remembers the last-selected contact:

- If the same number calls again within the configured window (default: 60 minutes), the previously-selected contact is prioritized
- Change the window via Settings → Erweitert → `LastContactRoutingMinutes`
- Set to 0 to disable

---

## Settings

Open Settings via tray menu → **Einstellungen**.

### Pop-Up Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Eingehende Anrufe | Enabled | Show popup for incoming calls |
| Ausgehende Anrufe | Disabled | Show popup for outgoing calls |
| Journal-Popup | Enabled | Show journal note popup after call |
| Ausgehende Journal-Popup | Disabled | Show journal popup for outgoing calls |
| Modus | Formular | Popup type: Beide / Formular / Balloon |
| Kontakt erneut (Sek.) | 3 | Delay before showing contact re-selection after connect |

### Advanced Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Anrufer-ID Mindestlänge | 2 | Minimum digits for contact lookup (auto-adjusts to extension length) |
| Max. Vergleichslänge | 10 | Number of digits compared from end of phone number |
| Anrufliste Eingehend | Enabled | Track inbound calls for re-journaling |
| Anrufliste Ausgehend | Disabled | Track outbound calls for re-journaling |
| Anrufliste Anzahl | 5 | Maximum entries per direction |
| Aktive Kontakte | Disabled | Only load active contacts (Status ≠ 0) from DATEV |

### Telephony Mode

| Setting | Default | Description |
|---------|---------|-------------|
| Modus | Auto | Connection mode: Auto, TAPI, Pipe, or Webclient |
| Aktiver Modus | (read-only) | Shows currently active telephony provider |

> **Note:** Changing the telephony mode requires an application restart.

---

## Configuration File

All settings are stored in:

```
%AppData%\3CXDATEVConnector\3CXDATEVConnector.ini
```

The file is **hot-reloaded** — changes take effect immediately without restarting the application.

### Pre-Seeding Configuration

For enterprise deployment, distribute a prepared INI file to each user's `%AppData%\3CXDATEVConnector\` folder via Intune, SCCM, or GPO logon script.

### Default INI

```ini
; 3CX - DATEV Connector Configuration
; Edit values below. Delete a line to restore its default.

[Settings]
ExtensionNumber=
EnableJournaling=true
EnableJournalPopup=true
EnableJournalPopupOutbound=false
EnableCallerPopup=true
EnableCallerPopupOutbound=false
CallerPopupMode=Form
MinCallerIdLength=2
MaxCompareLength=10
ContactReshowDelaySeconds=3
LastContactRoutingMinutes=60
CallHistoryInbound=true
CallHistoryOutbound=false
CallHistoryMaxEntries=5
ActiveContactsOnly=false

[Connection]
TelephonyMode=Auto
AutoDetectionTimeoutSec=10
WebclientConnectTimeoutSec=8
WebclientEnabled=true
WebclientWebSocketPort=19800
```

### Additional Sections (Optional)

Add these sections manually for advanced tuning:

```ini
; Extra [Connection] settings (not included in default INI)
ReconnectIntervalSeconds=5
ConnectionTimeoutSeconds=30
DatevCircuitBreakerThreshold=3
DatevCircuitBreakerTimeoutSeconds=30
SddMaxRetries=3
SddRetryDelaySeconds=1

[Logging]
LogMaxSizeMB=10
LogMaxFiles=5

[Debug]
VerboseLogging=true
```

---

## Autostart

### Recommended: HKCU Run Key

The application registers itself at:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

Value: `"<path>\3cxDatevConnector.exe" /minimized /silent`

Toggle via tray menu → **Autostart** or during the Setup Wizard.

### Alternative: Task Scheduler

Create a per-user logon task with a 30–60 second delay (useful when DATEV needs time to start):

```
3cxDatevConnector.exe /minimized /silent
```

---

## Command Line Options

```
3cxDatevConnector.exe [Options]

  /minimized    Start without showing status window
  /silent       Start without tray balloon notification
  /config=PATH  Use custom INI configuration file path
  /logdir=PATH  Override log directory location
  /verbose      Enable verbose/debug logging
  /reset        Reset all settings to defaults and exit
  /help         Show help information
```

Options can start with `/`, `-`, or `--`.

---

## Terminal Server / RDS

In multi-user terminal server environments:

- Each user session runs its own bridge instance
- The Windows Running Object Table (ROT) is **per-session** — no GUID conflicts between users
- Each session has its own 3CX TAPI driver instance
- No special configuration is required

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Red tray icon, won't connect | Ensure 3CX Softphone is running and TAPI driver is installed |
| "3CX TAPI INI not found" | Install and configure 3CX Multi-Line TAPI driver |
| No contacts loaded | Check DATEV Arbeitsplatz is running, SDD available |
| Calls not detected | Extension auto-detects from TAPI line name on connect |
| Contact not found | Adjust `MaxCompareLength` in Settings for your number format |
| Popup not showing | Check Settings → Pop-Up-Verhalten: Eingehende Anrufe must be enabled |
| Journal not sent | Ensure contact is matched to DATEV. For outbound, enable Ausgehende Journal-Popup |
| Call history empty | Only DATEV-matched calls are stored. Ensure contacts are loaded |
| Short internal numbers triggering lookup | `MinCallerIdLength` auto-adjusts to extension length |
| Fewer contacts than expected | If "Aktive Kontakte" is enabled, inactive contacts (Status = 0) are excluded |

For detailed diagnostics, enable verbose logging:

```ini
[Debug]
VerboseLogging=true
```

Then open the log file via tray menu → Hilfe → Log-Datei öffnen (Strg+L).
