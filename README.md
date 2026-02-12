# 3CX - DATEV Connector

A Windows system tray application that bridges **3CX** (Desktop App or WebClient) with **DATEV**.

## Background

As 3CX v20 evolved it introduced significant architectural changes:
- Migrated from WinForms to WinUI 3
- Changed from .NET Framework to .NET 9.0
- **Removed the legacy plugin system** (MyPhonePlugins)

This broke existing DATEV integrations. This standalone proxy application restores functionality by:

- Monitoring calls via Windows TAPI 2.x API (3CX Multi-Line TAPI driver) — **Desktop mode**
- Monitoring calls via browser extension WebSocket relay — **Webclient mode**
- Sending commands to 3CX via Named Pipes (TAPI) or WebSocket (Webclient)
- Communicating with DATEV via COM/ROT interfaces
- Running as a lightweight system tray application

## Features

| Feature | Description |
|---------|-------------|
| **Incoming Call Notifications** | Detects incoming calls via TAPI and notifies DATEV with caller info |
| **Outgoing Call Notifications** | Tracks outgoing calls initiated from 3CX |
| **3CX-DATEV Click-to-Dial** | Dial numbers directly from DATEV applications (preserves SyncID and contact) |
| **Contact Lookup** | Automatic contact resolution from DATEV SDD (Stammdatendienst) |
| **Call Journaling** | Popup for adding call notes before sending journal (optional) to DATEV |
| **Caller Popup** | Form & balloon notification showing caller info, dismissed on connect |
| **Contact Reselect** | Re-shows contact selection for multiple contacts after call connects (fires CallAdressatChanged) |
| **Last-Contact Routing** | Remembers last-used contact per phone number within configurable window |
| **DATEV-Initiated Calls** | Handles Dial/Drop commands from DATEV with SyncID preservation |
| **Terminal Server Support** | Per-session ROT isolation for multi-user environments |
| **DATEV Startup Test** | Comprehensive startup check of DATEV processes, ROT, and SDD |
| **Extension Auto-Detection** | Auto-detects extension number from TAPI line name on connect |
| **Connection Resilience** | Circuit breaker pattern prevents cascading DATEV failures |
| **Retry Mechanism** | Automatic retry with exponential backoff for SDD operations |
| **Call History** | In-memory circular buffer for re-journaling past calls (DATEV-matched only) |
| **System Tray UI** | Dark themed context menu with emoji prefixes, colored status dot (green/orange/red), autostart toggle, silent mode, restart, Help submenu |
| **Multi-Line TAPI** | Support for multiple TAPI lines with per-line status and test buttons |
| **German Localization** | Complete German UI for all forms, menus, buttons, and labels |
| **Active Contacts Filter** | Optional filter to load only active contacts (Status ≠ 0), disabled by default |
| **Setup Wizard** | First-run wizard for TAPI line selection, DATEV connection test, and optional autostart |
| **Troubleshooting Help** | Built-in help dialog with common problems and solutions |
| **Command Line Options** | Silent mode, custom config paths, verbose logging for deployment |
| **Keyboard Shortcuts** | Quick access to common functions (Ctrl+T, Ctrl+R, Ctrl+H, etc.) |
| **Webclient Mode** | Browser extension captures 3CX WebClient call events via WebSocket (`ws://127.0.0.1:19800`) — no desktop app required |
| **Auto-Detection** | Automatic telephony mode selection (TAPI, Pipe, or Webclient) based on environment |

## Requirements

- Windows 10/11 (x86 or x64)
- .NET Framework 4.8
- DATEV Arbeitsplatz with Telefonie component
- **Desktop mode:** 3CX Windows Softphone App (V20) or later + 3CX Multi-Line TAPI driver
- **Webclient mode:** Chrome or Edge browser with 3CX DATEV Connector extension
- DATEV DLLs found in GAC:
  - `DATEV.Interop.DatevCtiBuddy.dll`
  - `Datev.Sdd.Data.ClientInterfaces.dll`
  - `Datev.Sdd.Data.ClientPlugIn.Base.dll`

## Deployment

### Portable Mode

Copy `3cxDatevConnector.exe` to any folder and run. Configuration is stored at:

```
%AppData%\3CXDATEVConnector\3CXDATEVConnector.ini
```

### MSI Mode (Recommended)

Deploy via GPO / SCCM / Intune as a **per-user installation**.

- Installs `3cxDatevConnector.exe` to `%LocalAppData%\Programs\3CXDATEVConnector\`
- Creates the configuration directory at `%AppData%\3CXDATEVConnector\`
- Optionally pre-seeds `3CXDATEVConnector.ini` during deployment
- Logs written to `%AppData%\3CXDATEVConnector\3CXDatevConnector.log`

### Pre-Seeding Configuration

To deploy with pre-configured settings, place a prepared `3CXDATEVConnector.ini` at:

```
%AppData%\3CXDATEVConnector\3CXDATEVConnector.ini
```

The application reads this file on startup. See the [Configuration](#configuration) section for the full INI structure.

**First-run behavior:** If no INI file exists, the application creates one with default values and prompts the user to launch the Setup Wizard. An existing INI file is never overwritten on startup.

**Enterprise rollout:** To replicate configuration across users, distribute the same `3CXDATEVConnector.ini` via Intune, SCCM, GPO script, or include a template INI in the MSI package that is copied to `%AppData%` on first launch.

### Autostart Options

| Method | Details |
|--------|---------|
| **HKCU Run** (recommended) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `3CXDATEVConnector` = `"<path>\3cxDatevConnector.exe" /minimized /silent` |
| **Task Scheduler** (alternative) | Create a per-user logon task that runs `3cxDatevConnector.exe /minimized /silent` — useful when DATEV needs time to start (configure 30–60s delay) |

## Admin Options (Optional)

```
3cxDatevConnector.exe [Options]

Options:
  /minimized    Start without showing status window
  /silent       Start without tray balloon notification
  /config=PATH  Use custom INI configuration file path
  /logdir=PATH  Override log directory location
  /verbose      Enable verbose/debug logging
  /reset        Reset all settings to defaults and exit
  /help         Show help information

Examples:
  3cxDatevConnector.exe /minimized /silent
  3cxDatevConnector.exe /config="%AppData%\3CXDATEVConnector\3CXDATEVConnector.ini"
  3cxDatevConnector.exe /logdir="D:\Logs\3cxDatevConnector"

Note: Options can start with /, - or --
```

## Configuration

All settings are stored in a single INI file:

```
%AppData%\3CXDATEVConnector\3CXDATEVConnector.ini
```

The file is **hot-reloaded** — changes to `3CXDATEVConnector.ini` take effect immediately without restart.

**Defaults handling:** Missing keys fall back to built-in defaults. Delete a line to restore its default value.

### INI Structure

The default INI file generated on first run contains only the `[Settings]` section. The `[Connection]`, `[Logging]`, and `[Debug]` sections are not included by default — admins and resellers can add them manually when needed.

**Default INI (generated on first run):**

```ini
; 3CX - DATEV Connector Configuration
; Edit values below. Delete a line to restore its default.

[Settings]
; Extension number (auto-detected from TAPI if empty)
ExtensionNumber=

; Journaling
EnableJournaling=true
EnableJournalPopup=true
EnableJournalPopupOutbound=false

; Call Pop-Up
EnableCallerPopup=true
EnableCallerPopupOutbound=false
; CallerPopupMode: Both, Form, Balloon
CallerPopupMode=Form

; Contact matching
MinCallerIdLength=2
MaxCompareLength=10
ContactReshowDelaySeconds=3
LastContactRoutingMinutes=60

; Call History
CallHistoryInbound=true
CallHistoryOutbound=false
CallHistoryMaxEntries=5

; DATEV Contacts
ActiveContactsOnly=false
```

**Additional sections (admin/reseller — add manually when needed):**

```ini
[Connection]
ReconnectIntervalSeconds=5
ConnectionTimeoutSeconds=30
ReadTimeoutSeconds=60
WriteTimeoutSeconds=30
DatevCircuitBreakerThreshold=3
DatevCircuitBreakerTimeoutSeconds=30
SddMaxRetries=3
SddRetryDelaySeconds=1
StaleCallTimeoutMinutes=240
StalePendingTimeoutSeconds=300

[Logging]
; LogLevel: Debug, Info, Warning, Error, Critical
LogLevel=Info
LogMaxSizeMB=10
LogMaxFiles=5
LogAsync=true

[Debug]
; VerboseLogging=true
; TAPIDebug=0|1|2|3
; DATEVDebug=0|1|2|3
; Contacts=true       (dump contacts to contacts.txt)
```

### Configuration Options

| Setting | Default | Section | Description |
|---------|---------|---------|-------------|
| `ExtensionNumber` | (auto) | [Settings] | Auto-detected from TAPI line name on connect |
| `EnableJournaling` | true | [Settings] | Enable call journaling to DATEV |
| `EnableJournalPopup` | true | [Settings] | Show journal note popup after call ends |
| `EnableJournalPopupOutbound` | false | [Settings] | Show journal note popup for outgoing calls |
| `EnableCallerPopup` | true | [Settings] | Show popup notification for incoming calls |
| `EnableCallerPopupOutbound` | false | [Settings] | Show popup notification for outgoing calls |
| `CallerPopupMode` | Form | [Settings] | Notification type: `Form`, `Balloon`, or `Both` |
| `MinCallerIdLength` | 2 | [Settings] | Minimum digits for contact lookup (auto-adjusted to extension length) |
| `MaxCompareLength` | 10 | [Settings] | Number of digits to compare from end of phone number |
| `ContactReshowDelaySeconds` | 3 | [Settings] | Seconds after connect before re-showing contact selection |
| `LastContactRoutingMinutes` | 60 | [Settings] | Minutes to remember last-used contact per number (0 = disabled) |
| `CallHistoryInbound` | true | [Settings] | Track inbound calls for re-journaling |
| `CallHistoryOutbound` | false | [Settings] | Track outbound calls for re-journaling |
| `CallHistoryMaxEntries` | 5 | [Settings] | Maximum entries per direction (circular buffer) |
| `ActiveContactsOnly` | false | [Settings] | Only load contacts with Status ≠ 0 (inactive contacts excluded) |
| `ReconnectIntervalSeconds` | 5 | [Connection] | Seconds between 3CX reconnection attempts |
| `ConnectionTimeoutSeconds` | 30 | [Connection] | Connection timeout for named pipes |
| `ReadTimeoutSeconds` | 60 | [Connection] | Read timeout for pipe operations |
| `WriteTimeoutSeconds` | 30 | [Connection] | Write timeout for pipe operations |
| `DatevCircuitBreakerThreshold` | 3 | [Connection] | Failures before circuit breaker opens |
| `DatevCircuitBreakerTimeoutSeconds` | 30 | [Connection] | Seconds before circuit breaker allows retry |
| `SddMaxRetries` | 3 | [Connection] | Maximum retry attempts for SDD contact loading |
| `SddRetryDelaySeconds` | 1 | [Connection] | Base delay between SDD retries (doubles each attempt) |
| `StaleCallTimeoutMinutes` | 240 | [Connection] | Minutes before active calls are cleaned up |
| `StalePendingTimeoutSeconds` | 300 | [Connection] | Seconds before pending calls are cleaned up |
| `LogLevel` | Info | [Logging] | Log level: Debug, Info, Warning, Error, Critical |
| `LogMaxSizeMB` | 10 | [Logging] | Maximum log file size in MB |
| `LogMaxFiles` | 5 | [Logging] | Number of log files to keep |
| `LogAsync` | true | [Logging] | Enable async logging |
| `LogMaskDigits` | 5 | [Logging] | Number of trailing digits visible in masked phone numbers (0 = disable masking) |
| `DebugLogging` | false | [Logging] | Enable persistent debug-level logging |
| `VerboseLogging` | false | [Debug] | Enable debug-level logging at runtime (hot-reloadable) |
| `TAPIDebug` | 0 | [Debug] | TAPI debug level: 0=off, 1=calls, 2=+states, 3=+raw |
| `DATEVDebug` | 0 | [Debug] | DATEV debug level: 0=off, 1=COM, 2=+data, 3=+raw |
| `Contacts` | false | [Debug] | Dump all cached contacts to contacts.txt |
| `AddressatContacts` | false | [Debug] | Dump recipient contacts to addressat_contacts.txt |
| `InstitutionContacts` | false | [Debug] | Dump institution contacts to institution_contacts.txt |

### DATEV Active Contacts Filter

The `ActiveContactsOnly` setting (in `[Settings]`) controls which contacts are loaded from DATEV SDD:

| Value | Behavior |
|-------|----------|
| `false` (default) | Load all contacts including disabled/inactive ones |
| `true` | Only contacts with Status ≠ 0 are loaded (inactive contacts with Status = 0 are excluded) |

This can also be changed via the Settings dashboard ("Aktive Kontakte" checkbox). When toggled and saved, a contact reload is triggered automatically.

### Phone Number Matching

The `MaxCompareLength` setting controls how phone numbers are matched:

- Incoming: `+49 89 12345678` -> normalized to `498912345678` -> last 10 digits: `8912345678`
- Contact in DATEV: `089/12345678` -> normalized to `08912345678` -> last 10 digits: `8912345678`
- **Match found!**

Adjust this value based on your phone number formats and country codes.

### Extension Auto-Detection

The extension number is automatically detected from the TAPI line name when the connection is established (format: `"122 : Max Mustermann"` -> extension `122`). This replaces any value in `ExtensionNumber`.

### MinCallerIdLength Auto-Detection

The `MinCallerIdLength` is automatically adjusted based on the detected extension length. This prevents internal extension numbers from being mistakenly matched to DATEV contacts:

- Extension `12` (2 digits) -> MinCallerIdLength stays at 2 (matches default)
- Extension `122` (3 digits) -> MinCallerIdLength = 3
- Extension `1220` (4 digits) -> MinCallerIdLength = 4

The configured value is used as a floor; if the extension is longer, the length is increased.

### Configuration Hot-Reload Flow

```
1. User edits 3CXDATEVConnector.ini
   └─> FileSystemWatcher detects change
       └─> DebugConfigWatcher.OnFileChanged()
           └─> Debounce timer (300ms)
               └─> ApplyConfig()
                   ├─> Parse INI sections
                   ├─> Validate values (type, range)
                   │   ├─> Valid: apply new config
                   │   └─> Invalid: log warning, keep last known good config
                   ├─> [Debug] → takes effect immediately
                   ├─> [Connection] → takes effect on next connection attempt
                   └─> [Settings] → takes effect on next lookup/call event
```

## System Tray

### Tray Icon

The tray icon displays the application logo (`bridge_icon.png`) with a dynamic colored status ring:

| Ring Color | Status |
|------------|--------|
| Green | Operational (both TAPI and DATEV connected) |
| Orange | Partial (one component missing: TAPI or DATEV) |
| Red | Disconnected (neither connected) |

The icon is embedded as a resource in the executable. Alternatively, place `bridge_icon.png` (any square PNG) next to the executable for custom branding.

### Context Menu (Right-Click)

The context menu uses a **dark theme** (matching the form/popup color scheme) with a custom `DarkMenuRenderer`. Menu items use emoji prefixes for visual clarity. Status uses a colored dot image (green/orange/red) instead of text symbols.

| Menu Item | Shortcut | Description |
|-----------|----------|-------------|
| **3CX - DATEV Connector** | - | Bold app title (non-interactive) |
| --- | - | Separator |
| 🟢 Status: Betriebsbereit | - | Clickable status with colored dot (green=OK, orange=partial, red=error) |
| 📞 Anrufliste | Strg+H | Open call history for re-journaling |
| 🔄 Kontakte neu laden | Strg+R | Reload contacts from DATEV SDD |
| --- | - | Separator |
| ⚙ Einstellungen | - | Open settings dashboard |
| 📘 Hilfe | > | Submenu with troubleshooting, log, and setup wizard |
| ├─ 🔧 Fehlerbehebung | - | Open troubleshooting help dialog |
| ├─ 📝 Log-Datei öffnen | Strg+L | Open log file in default editor |
| └─ 🔧 Setup-Assistent | - | Re-run the setup wizard |
| --- | - | Separator |
| ✓ Autostart | - | Toggle Windows autostart (HKCU Run) |
| ✓ Stummschalten | - | Toggle silent mode (suppress popups and balloons) |
| --- | - | Separator |
| 🔄 Neustart | - | Reconnect TAPI and reload contacts |
| ℹ Info | - | Show about dialog with version and shortcuts |
| ❌ Beenden | - | Exit the application |

### Double-Click Action

Double-clicking the tray icon opens the **Call History** (Anrufliste) by default. This can be changed in Settings to open the Status Overview instead.

The **Status Overview** (StatusForm) is accessible via the Status menu item and shows:
- Card-based layout with DATEV (green), 3CX TAPI (blue), and Bridge (purple) sections
- Real-time status updates via event subscription
- Per-line TAPI status with individual "Testen" buttons and progress feedback
- Reconnect buttons for TAPI and "Testen" for full reconnect
- Test DATEV button with visual feedback (checkmark/X)
- Quick action buttons (Anrufliste, Einstellungen)

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Strg+T | Test all connections |
| Strg+R | Reload contacts from DATEV |
| Strg+L | Open log file |
| Strg+H | Open call history |
| Strg+S | Save settings |
| F5 | Refresh current view |
| Esc | Close current window |

These shortcuts work in the Status Overview and Settings dialogs.

### Settings Dashboard (Einstellungen)

The Settings dialog (right-click -> Einstellungen) is a single-page dashboard with card-based layout. **All UI is in German**.

| Section | Settings |
|---------|----------|
| **Status Row** | DATEV status + Testen/Laden buttons + sync timestamp, TAPI status + Nebenstelle, Bridge combined status |
| **Pop-Up-Verhalten** | Journaling toggle, Eingehende/Ausgehende Anrufe, Journal-Popup, Ausgehende Journal-Popup, Modus selector (Beide/Formular/Balloon), Kontakt erneut delay |
| **Erweitert** | Anrufer-ID Mindestlänge/Max. Vergleich, Anrufliste (Eingehend/Ausgehend) + Anzahl, DATEV "Aktive Kontakte" filter |

### Caller Popup (Anrufer-Popup)

When enabled, a notification appears for incoming and outgoing calls:

- **Blue accent bar**: Incoming call (EINGEHENDER ANRUF)
- **Blue accent bar**: Outgoing call (AUSGEHENDER ANRUF)
- Shows caller name (from DATEV contact or caller ID)
- Shows phone number and source (Adressaten/Institution)
- Shows contact type (Person/Firma)
- **Stays open until call is answered (Connected)** — no Dismiss button
- Balloon notification includes extension number (e.g., "Nst. 122")
- Balloon notification also shown via system tray (configurable via Modus)

### Contact Selection & Reshow

When multiple DATEV contacts match a phone number:

1. During ringing, the first contact is used (with last-contact routing applied)
2. After the call connects, a contact selection dialog appears (after configurable delay)
3. User can pick a different contact -> fires `CallAdressatChanged` to DATEV
4. Cancel or call ends -> keeps the first contact (no change)

The dialog shows a dropdown of matching contacts with OK/Cancel buttons.

### Last-Contact Routing

When `LastContactRoutingMinutes > 0`, the bridge remembers which contact was last used for each phone number:

- If the same caller calls again within the window, their previously-selected contact is prioritized (moved to first position)
- Applied during both initial contact assignment and contact reshow dialog
- Contact reshow also records new preference for future calls
- The cache is in-memory and resets on application restart
- Useful when a caller has multiple contacts (e.g., multiple employees at the same number)

### Journal Popup

When `EnableJournalPopup` is enabled and a matched DATEV contact (recipient or institution) exists, a journal form appears after the call ends. **Outbound calls are excluded by default** (`EnableJournalPopupOutbound=false`); enable via Settings or INI to show the journal popup for outgoing calls as well.

- Dark theme form showing contact name and call duration
- Text area for entering call notes (max 2000 characters)
- Character count with color coding (orange at 90%, red at 100%)
- "Send to DATEV" button sends the journal entry
- "Cancel" or empty note = no journal sent to DATEV
- Only sends `NewJournal` when user explicitly writes a note and clicks send

### Call History

When enabled, the bridge keeps an in-memory circular buffer of recent DATEV-matched calls for re-journaling:

- **Separate buffers** for inbound and outbound calls (configurable per direction; outbound tracking disabled by default)
- **Only DATEV-matched calls** are stored (entries with a resolved AdressatenId)
- **Configurable capacity** via `CallHistoryMaxEntries` (default: 5 per direction)
- **Terminal server safe** — per-process memory, no file I/O or shared state

Access via tray menu -> Anrufliste:
- Two list views (Eingehend / Ausgehend) showing Zeit, Nummer, Kontakt, Dauer, Journal status
- Clean borderless layout without gridlines
- **Auto-refresh every 5 seconds** for live updates
- Select an entry and click "Journal senden" or double-click to open the journal popup
- Journal status shows "✓ Ja" for sent, "Offen" for pending, "—" for unmatched
- Previously-journaled entries are dimmed and cannot be re-sent
- Journal popup displays connected duration (not total ring-to-end time)
- Buttons: Journal senden, Aktualisieren, Zurück (to StatusForm), Schließen

## Security

### Named Pipe Access Control

The bridge communicates with 3CX via named pipes. Security is enforced using a two-layer model:

#### A) Routing (where to connect)

The **extension number** is used in the pipe name to reach the correct channel:

| Environment | Pipe Name |
|-------------|-----------|
| All | `\\.\pipe\3CX_tsp_server_{extension}` |

The extension number is **not** a security principal — it is guessable, enumerable (e.g., 100–999), and not guaranteed stable across reassignments. It must **never** be treated as a security boundary.

#### B) Authorization (who may connect)

**Access control is enforced via Windows security (DACL)** — the pipe is secured using a Discretionary Access Control List scoped to the **current user's SID**. Only the Windows user who owns the bridge process can read from or write to the pipe.

If the bridge controls pipe server creation (or any IPC server), enforce DACL properly:

```csharp
var ps = new PipeSecurity();
var userSid = WindowsIdentity.GetCurrent().User;

// Allow only current user
ps.AddAccessRule(new PipeAccessRule(userSid,
    PipeAccessRights.FullControl, AccessControlType.Allow));

// Optional: allow LocalSystem
ps.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
    PipeAccessRights.FullControl, AccessControlType.Allow));

// Optional: allow Administrators
ps.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    PipeAccessRights.ReadWrite, AccessControlType.Allow));

var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
```

**Note:** The bridge acts as the pipe **server** in this implementation, so it controls DACL enforcement. The 3CX Softphone connects as client.

#### Hardening Recommendations

- **Extension from TAPI only** — require the extension number to come from TAPI line name auto-detection; do not allow arbitrary override via UI or command line.
- **Validate before connecting** — only connect to a pipe whose name matches the detected extension.
- **No silent call control without DATEV** — optionally block Dial/Drop commands if DATEV is not active.
- **Confirm extension before MAKE-CALL/DROP** — verify the extension matches what TAPI reported before sending pipe commands.

### Terminal Server / RDS Environments

In TS/RDS multi-user environments, isolation is handled naturally by the OS:

- **Windows ROT is per-session** — each terminal server session has its own Running Object Table, so each user session's bridge instance gets its own ROT entry without conflicts. No GUID modification is needed.
- **Each user session** runs its own 3CX TAPI driver instance and bridge instance
- The `SessionManager` detects terminal server sessions via the Windows session ID and `SESSIONNAME` environment variable for diagnostic logging

On console sessions (non-TS), the same base GUIDs and standard pipe names are used.

## Architecture

```
+----------------------------------------------------------------------+
|                       3cxDatevConnector.exe                              |
+----------------------------------------------------------------------+
|  Program.cs                      - Entry point, single instance      |
+----------------------------------------------------------------------+
|  UI/                                                                 |
|    TrayApplication.cs            - System tray, menu, status icons   |
|    UITheme.cs                    - Dark theme, icon generation       |
|    SettingsForm.cs               - Single-page settings dashboard    |
|    CallerPopupForm.cs            - Caller notification popup         |
|    ContactSelectionForm.cs       - Multi-contact selection dropdown  |
|    JournalForm.cs                - Call note entry before DATEV submit|
|    CallHistoryForm.cs            - Re-journal past calls (dual list) |
|    StatusForm.cs                 - Quick status overview (tray dblclick)|
|    SetupWizardForm.cs            - First-run configuration wizard    |
|    TroubleshootingForm.cs        - Built-in help with common solutions|
|    AboutForm.cs                  - About dialog with shortcuts       |
|    Strings/                                                          |
|      UIStrings.cs                - Centralized German UI text        |
|    Theme/                                                            |
|      Layout.cs                   - Layout constants                  |
|      DarkMenuRenderer.cs         - Dark theme for context menus      |
+----------------------------------------------------------------------+
|  Core/                                                               |
|    ConnectorService.cs              - Main orchestrator                 |
|    ConnectorStatus.cs               - Connection status enum            |
|    CallTracker.cs                - Active call management            |
|    CallStateMachine.cs           - State transition validation       |
|    CallRecord.cs                 - Call data record model            |
|    CallIdGenerator.cs            - Unique CallID (Extension-date-random)|
|    CallHistoryEntry.cs           - History entry model for re-journal|
|    CallHistoryStore.cs           - Circular buffer store (in/out)    |
|    ConfigKeys.cs                 - Centralized INI key constants     |
|    ContactRoutingCache.cs        - Last-contact routing memory       |
|    DebugConfigWatcher.cs         - 3CXDATEVConnector.ini hot-reload     |
|    CircuitBreaker.cs             - Circuit breaker pattern           |
|    RetryHelper.cs                - Retry with exponential backoff    |
|    SessionManager.cs             - Terminal server session detection  |
|    ShortcutManager.cs            - Keyboard shortcut definitions     |
|    AutoStartManager.cs           - Windows autostart (HKCU Run)     |
|    CommandLineOptions.cs         - Command line argument parser      |
|    LogPrefixes.cs                - Structured log message prefixes   |
|    Config/                                                           |
|      AppConfig.cs                - Configuration defaults & access   |
|      IniConfig.cs                - INI file reader (Windows API)     |
|    Constants/                                                        |
|      IntegrationConstants.cs     - TAPI/DATEV/timeout constants      |
|    Exceptions/                                                       |
|      TapiException.cs            - TAPI error with category          |
|      DatevException.cs           - DATEV error with category         |
+----------------------------------------------------------------------+
|  Tapi/                                                               |
|    ITelephonyProvider.cs         - Telephony provider interface       |
|    TapiLineMonitor.cs            - TAPI 2.x call event monitoring    |
|    PipeTelephonyProvider.cs      - Named pipe server for 3CX         |
|    TapiPipeServer.cs             - Low-level pipe I/O (server)       |
|    TapiConfigReader.cs           - 3CX TAPI INI auto-detection       |
|    TapiMessage.cs                - Message parsing/encoding          |
|    TapiCallState.cs              - TAPI call state definitions       |
|    TapiCommands.cs               - Pipe protocol command constants   |
+----------------------------------------------------------------------+
|  Webclient/                                                          |
|    Protocol.cs                   - JSON protocol (v1) types & parser |
|    WebSocketBridgeServer.cs      - WebSocket server (port 19800)     |
|    WebclientTelephonyProvider.cs - ITelephonyProvider for Webclient   |
+----------------------------------------------------------------------+
|  Interop/                                                            |
|    Rot.cs                        - Running Object Table interop      |
|    TapiInterop.cs                - TAPI P/Invoke and error handling  |
+----------------------------------------------------------------------+
|  Datev/                                                              |
|    DatevCache.cs                 - Contact cache with lookup         |
|    DatevConnectionChecker.cs     - DATEV availability checks         |
|    COMs/                                                             |
|      CallData.cs                 - IDatevCtiData implementation      |
|      DatevAdapter.cs             - DATEV COM adapter (IDatevCtiControl)|
|    Constants/                                                        |
|      CommonParameters.cs         - Shared configuration values       |
|      DatevDataSource.cs          - Data source identifiers           |
|    Enums/                                                            |
|      DatevEventType.cs           - DATEV event type definitions      |
|    Managers/                                                         |
|      AdapterManager.cs           - COM adapter lifecycle (ROT)       |
|      CallDataManager.cs          - Call data handling                |
|      DatevContactManager.cs      - Contact management (with retry)   |
|      LogManager.cs               - Structured logging                |
|      NotificationManager.cs      - DATEV notifications (w/ breaker)  |
|    DatevData/                                                        |
|      Communication.cs            - Communication record model        |
|      Enums/                                                          |
|        ContactType.cs            - Contact type enum                 |
|        Medium.cs                 - Communication medium enum         |
|      Institutions/                                                   |
|        InstitutionContactDetail.cs - Institution contact details     |
|        InstitutionsContactList.cs                                    |
|      Recipients/                                                     |
|        RecipientContact.cs       - Recipient contact model           |
|        RecipientContactDetail.cs - Recipient contact details         |
|        RecipientsContactList.cs  - Recipient contact list            |
|    PluginData/                                                       |
|      DatevContact.cs             - Plugin contact model              |
|      DatevContactInfo.cs         - Plugin contact info               |
+----------------------------------------------------------------------+
|  Extensions/                                                         |
|    PhoneNumberNormalizer.cs      - Unified phone normalization       |
+----------------------------------------------------------------------+
         |                |                              |
         | TAPI 2.x       | WebSocket                    | COM/ROT
         | (Desktop)       | ws://127.0.0.1:19800         | (per-session)
         |                 | (Webclient)                  |
         | Named Pipe      |                              |
         | (MAKE-CALL)     |                              |
         v                 v                              v
+-----------------+ +-----------------+          +-----------------+
|  3CX Windows    | |  3CX WebClient  |          |     DATEV       |
|  Softphone App  | |  Browser Ext.   |          |   Telefonie     |
|  (V20)          | |  (Chrome/Edge)  |          |   (Arbeitsplatz)|
+-----------------+ +-----------------+          +-----------------+
```

## Data Flow & Events

### Incoming Call Flow

```
1. TAPI Event (LINECALLSTATE_OFFERING)
   └─> TapiLineMonitor detects incoming call
       └─> ConnectorService.OnCallStateChanged()
           ├─> CallTracker.Add() - create CallRecord
           ├─> DatevCache.Lookup(callerNumber) - find contact
           ├─> ContactRoutingCache.ApplyRouting() - apply last-contact preference
           ├─> NotificationManager.NewCall() - notify DATEV
           └─> CallerPopupForm.Show() - display popup

2. TAPI Event (LINECALLSTATE_CONNECTED)
   └─> ConnectorService.OnCallStateChanged()
       ├─> CallStateMachine.Transition(Connected)
       ├─> NotificationManager.CallStateChanged(Connected)
       ├─> CallerPopupForm.Close()
       └─> ContactSelectionForm.Show() (after delay, if multiple contacts)
           └─> User selects contact
               ├─> ContactRoutingCache.RecordUsage()
               └─> NotificationManager.CallAdressatChanged()

3. TAPI Event (LINECALLSTATE_DISCONNECTED)
   └─> ConnectorService.OnCallStateChanged()
       ├─> CallStateMachine.Transition(Disconnected)
       ├─> NotificationManager.CallStateChanged(Finished)
       ├─> CallHistoryStore.Add() (if DATEV-matched)
       └─> JournalForm.Show() (if enabled and contact matched)
           └─> User enters note and clicks Send
               └─> NotificationManager.NewJournal()
```

### Outgoing Call Flow (Click-to-Dial from DATEV)

```
1. DATEV calls DatevAdapter.Dial(callData)
   └─> ConnectorService.OnDatevDial()
       ├─> CallIdGenerator.Next() - generate unique ID
       ├─> Store pending call with SyncID and contact info
       └─> PipeTelephonyProvider.MakeCall(number)

2. TAPI Event (LINECALLSTATE_RINGBACK)
   └─> ConnectorService.OnCallStateChanged()
       ├─> Match pending call by phone number
       ├─> Apply stored SyncID and contact info to CallRecord
       ├─> CallTracker.Add()
       └─> NotificationManager.NewCall() (with preserved DATEV context)

3. TAPI Event (LINECALLSTATE_CONNECTED)
   └─> [Same as incoming call flow]

4. TAPI Event (LINECALLSTATE_DISCONNECTED)
   └─> [Same as incoming call flow]
```

### Contact Loading Flow

```
1. Startup / Manual Reload
   └─> ConnectorService.ReloadContactsAsync()
       └─> RetryHelper.ExecuteWithRetry()
           └─> DatevContactManager.GetContacts()
               ├─> GetRecipients() - fetch from SDD with filter
               │   └─> Filter: @Status NOT EQUAL TO "0" (if ActiveContactsOnly)
               ├─> GetInstitutions() - fetch from SDD
               ├─> FilterPhoneCommunications() - keep only phone numbers
               └─> Return combined list

2. Cache Update
   └─> DatevCache.Initialize(contacts)
       ├─> Build SortedDictionary by normalized phone number
       └─> Create suffix lookup indexes for partial matching
```

## 3CX TAPI Integration

### TAPI Line Monitor

The bridge uses the Windows TAPI 2.x API to monitor call events from the 3CX Multi-Line TAPI driver:

- Monitors the TAPI line matching the configured extension
- Receives call state events: Offering, Connected, Disconnected
- Extracts caller/called party information from TAPI call info
- No polling required — event-driven via TAPI callbacks
- **Thread-safe** line tracking using ConcurrentDictionary
- **Multi-line support** with per-line status indicators in Status Overview

### TAPI Error Handling

TAPI errors are categorized for intelligent retry handling:

| Category | Description | Action |
|----------|-------------|--------|
| `Success` | No error | Continue |
| `Transient` | Temporary issues (reinit, busy, timeout) | Retry with backoff |
| `LineClosed` | Line was closed | Reconnect line |
| `Shutdown` | TAPI shutting down | Full reinitialize |
| `Permanent` | Invalid parameters, operation failed | Log and fail |

Line testing uses automatic retry with exponential backoff (500ms → 1s → 2s → 4s) for transient errors. The Status Overview provides per-line "Testen" buttons with visual progress feedback.

### Extension Auto-Detection from TAPI Line

When the TAPI connection is established, the bridge parses the extension from the line name:
- Line name `"123 : Max Mustermann"` -> extension = `123`
- Updates the internal extension and CallID generator
- Auto-adjusts `MinCallerIdLength` to match extension digit count

### Named Pipe Commands

The bridge creates a named pipe **server** and waits for the 3CX Softphone to connect as client (replacing `dialer.exe`):

- **Pipe name:** `\\.\pipe\3CX_tsp_server_{extension}`
- **Pipe role:** Bridge = Server, 3CX Softphone = Client
- **Pipe direction:** Bidirectional (InOut)
- **Encoding:** Unicode (UTF-16 Little Endian)
- **Pipe security:** Grants access to `ALL APPLICATION PACKAGES` (SID `S-1-15-2-1`) to support MSIX/AppContainer 3CX apps

### Wire Format

```
[Length Lo][Length Hi][Message Body in UTF-16]
```

### Message Format

Messages are comma-separated key=value pairs:
```
__reqId=1,callid=123,cmd=MAKE-CALL,number=+49891234567
```

Reply pattern: Softphone echoes original `cmd` with `__answ#` and `reply` fields.

### Commands

| Direction | Command | Description |
|-----------|---------|-------------|
| Server→Client | `SRVHELLO` | Handshake (softphone replies with `CLIHELLO`) |
| Server→Client | `MAKE-CALL` | Initiate outgoing call (Click-to-Dial) |
| Server→Client | `DROP-CALL` | Hang up active call |
| Client→Server | `RINGING` | Incoming call notification |
| Client→Server | `RINGBACK` | Outgoing call ringing at remote party |
| Client→Server | `CONNECTED` | Call connected |
| Client→Server | `DISCONNECTED` | Call ended |
| Client→Server | `CALL-INFO` | Caller/called party information |
| Client→Server | `DROP-CALL` | Hangup notification from softphone |

### CallID Format

CallIDs are generated in the format: `{Extension}-{ddMMyyyy}-{HHmm}-{Random7}`

Example: `123-23012026-1030-0912387`

This ensures uniqueness across application restarts (no sequential counter reuse).

## Call State Flow

```
                    +-------------+
                    | Initializing|
                    +------+------+
                           |
          +----------------+----------------+
          |                |                |
          v                v                v
    +----------+    +----------+    +------------+
    | Ringing  |    | Ringback |    | Connected  |
    |(incoming)|    |(outgoing)|    |  (direct)  |
    +----+-----+    +----+-----+    +-----+------+
         |               |                |
         +-------+-------+                |
                 |                         |
                 v                         |
          +----------+                     |
          |Connected |<--------------------+
          +----+-----+
               |
               v
        +--------------+
        | Disconnected |
        +--------------+
```

## DATEV Integration

### DataSource Values

| Value | Meaning |
|-------|---------|
| `DATEV_Adressaten` | Contact is a DATEV Recipient (Adressat) |
| `DATEV_Institutionen` | Contact is a DATEV Institution |
| `3CX` | Contact is not in DATEV (third-party / unmatched) |

### Notifications (Bridge -> DATEV)

| Method | When Called |
|--------|-------------|
| `NewCall()` | Call starts ringing/dialing |
| `CallStateChanged()` | Call connected or ended |
| `CallAdressatChanged()` | Contact assignment changed during call (reshow) |
| `NewJournal()` | User submits a journal note via popup |

### Commands (DATEV -> Bridge)

| Method | Action |
|--------|--------|
| `Dial()` | Send MAKE-CALL to 3CX, preserves SyncID and contact info |
| `Drop()` | Send DROP-CALL to 3CX |

### DATEV-Initiated Calls

When DATEV sends a `Dial` command:

1. Contact data (name, ID, DataSource, SyncID) is stored as a "pending call"
2. Bridge sends MAKE-CALL to 3CX via named pipe
3. When the TAPI Ringback event fires, the pending call is matched by normalized phone number
4. The preserved SyncID and contact info are applied to the call record
5. All subsequent notifications (CallStateChanged, NewJournal) include the correct DATEV context

This ensures DATEV's internal call tracking (SyncID) stays consistent through the call lifecycle.

### Terminal Server Support

On terminal server environments, multiple users may run DATEV and the bridge simultaneously. The Windows Running Object Table (ROT) is already per-session on terminal servers, so no GUID modification is needed. The `SessionManager` handles this by:

- Detecting terminal server sessions via the Windows session ID and `SESSIONNAME` environment variable
- Providing session diagnostics (session key, 3CX process detection, pipe availability)
- The `AdapterManager` registers the COM adapter in ROT using the unmodified base GUID — Windows isolates ROT entries per session automatically

On console sessions (non-TS), the same base GUIDs are used.

### Startup Connection Test

On startup, the bridge performs a comprehensive DATEV availability check:

```
=== DATEV Connection Test ===
  DATEV Portal process: Running
  DATEV Telefonie process: Running
  DATEV Telefonie in ROT: Available
  DATEV SDD (contacts): Available
  Result: DATEV fully available - all components detected
=============================
```

Checks performed:
1. **Process detection** — Verifies `Datev.Framework.Portal` and `Datev.Telephony` are running
2. **ROT lookup** — Confirms DATEV Telefonie is registered in the Running Object Table
3. **SDD assembly** — Verifies the Stammdatendienst client library is loadable

### Circuit Breaker Pattern

The bridge implements a circuit breaker to prevent cascading failures when DATEV is unavailable:

```
     +----------------------------------------------------+
     |                    CLOSED                          |
     |  (Normal operation - all calls pass through)       |
     +--------------------+-------------------------------+
                          | Failure count >= threshold
                          v
     +----------------------------------------------------+
     |                     OPEN                           |
     |  (All calls fail fast without attempting DATEV)    |
     +--------------------+-------------------------------+
                          | Timeout elapsed
                          v
     +----------------------------------------------------+
     |                  HALF-OPEN                         |
     |  (One test call allowed through)                   |
     +---------+----------------------------+-------------+
               | Success                    | Failure
               v                            v
            CLOSED                         OPEN
```

Configure via `DatevCircuitBreakerThreshold` and `DatevCircuitBreakerTimeoutSeconds`.

### SDD Retry Mechanism

Contact loading from DATEV SDD uses automatic retry with exponential backoff:

- **Attempt 1**: Immediate
- **Attempt 2**: Wait 1s (configurable via `SddRetryDelaySeconds`)
- **Attempt 3**: Wait 2s
- And so on...

Only transient errors trigger retries (timeouts, COM errors). Permanent failures fail immediately.

### Phone Number Normalization

All phone numbers are normalized consistently using `PhoneNumberNormalizer`:

1. Convert international prefixes (`+49` -> `49`, `0049` -> `49`)
2. Strip all non-digit characters (spaces, dashes, slashes)
3. Compare last N digits (configurable via `MaxCompareLength`)
4. Both Recipients and Institutions use the same normalization

This ensures reliable contact matching across different number formats:

| Input Format | Normalized | Last 10 Digits |
|--------------|------------|----------------|
| `+49 89 12345678` | `498912345678` | `8912345678` |
| `0049 89 12345678` | `498912345678` | `8912345678` |
| `089/12345678` | `08912345678` | `8912345678` |

All three formats match the same contact.

### Memory Optimization

Introduced comprehensive memory optimization across the DATEV contact cache pipeline:

#### Communication Filtering
- Only phone-type communications (`Medium == Phone`) are loaded from DATEV SDD
- Email, fax, website, and other communication types are filtered out
- Reduces Communication objects from ~68,000 to ~17,000 (~4MB savings)
- Applied in `DatevContactManager.FilterPhoneCommunications()`

#### Cache Normalization & Allocation Reduction
- **Cached `EffectiveNormalizedNumber`** — the computed property on `Communication` is now cached on first access, avoiding repeated Regex allocations during cache build (~68K+ string allocations eliminated)
- **Compiled Regex** — `PhoneNumberNormalizer` uses `RegexOptions.Compiled` for the hot-path `NonDigitRegex`, reducing per-call overhead
- **Direct `SortedDictionary` construction** — `BuildLookupDictionary()` builds the lookup dictionary directly via `foreach` instead of LINQ `GroupBy`/`ToDictionary`, eliminating intermediate `Dictionary`, anonymous objects, and the copy step

#### Aggressive GC & Working Set Management
- **Pre-load GC** — before loading new contacts, old cache references are set to `null` and a forced Gen2 GC runs, freeing memory before the new XML deserialization graph (~68K objects) is allocated
- **Post-load double-collect** — after cache build, two forced blocking/compacting Gen2 collections run with `WaitForPendingFinalizers()` in between, ensuring finalizable objects (COM wrappers, XML readers) are fully reclaimed
- **LOH compaction** — `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` eliminates large-object heap fragmentation
- **OS working set trim** — `SetProcessWorkingSetSize(-1, -1)` via P/Invoke returns freed physical pages to the OS so the reduction is visible in Task Manager

#### Resource Disposal
- `UITheme.Cleanup()` is called during application shutdown to dispose all static Font instances
- `TrayApplication._currentMainForm` is properly disposed on shutdown
- Replaced `new T[0]` with `Array.Empty<T>()` throughout to avoid unnecessary allocations

#### Diagnostics
Post-cache GC logs working set before/after and managed heap size (Debug level):
```
[DEBUG] Post-cache GC: working set 280 MB -> 50 MB (freed 230 MB)
```

## Logging

Log file location: `%AppData%\3CXDATEVConnector\3CXDatevConnector.log`

### Log Rotation

- **Size-based rotation**: When a log file reaches `LogMaxSizeMB` (default: 10 MB), it is rotated. Up to `LogMaxFiles` (default: 5) rotated files are kept. Oldest rotated files are deleted when the limit is exceeded.

Configure via `[Logging]` section in `3CXDATEVConnector.ini`:
```ini
[Logging]
LogMaxSizeMB=10
LogMaxFiles=5
```

Enable verbose logging at runtime by setting in `3CXDATEVConnector.ini`:
```ini
[Debug]
VerboseLogging=true
```

### Log Levels

| Level | Description |
|-------|-------------|
| Debug | Detailed diagnostic info (TAPI messages, contact cache, UI events) — enabled via `VerboseLogging=true` |
| Info | Normal operation events (calls, contacts, connections) — default |
| Warning | Potential issues (DATEV unavailable, circuit breaker, unknown INI settings) |
| Error | Failures with stack traces |
| Critical | Fatal errors |

### Log Prefixes

The following prefixes are used for structured logging (defined in `LogPrefixes.cs`):

| Prefix | Description |
|--------|-------------|
| `DATEV -> Bridge` | Messages received from DATEV |
| `Bridge -> DATEV` | Messages sent to DATEV |
| `TAPI` | TAPI events and call state changes |
| `User` | User-initiated actions |
| `System` | System events (startup, shutdown) |
| `Config` | Configuration changes |
| `Settings` | Settings form actions |
| `Cache` | Contact cache operations |
| `CircuitBreaker` | Circuit breaker state changes |
| `Session` | Session management events |
| `Notification` | Popup and notification events |
| `Journal` | Journal entry operations |
| `CallHistory` | Call history operations |
| `ERROR` | Error conditions |

### Key Log Messages

**Startup:**
```
[INFO] IsTerminalSession=False, SessionName=Console, Session: Id=2
[INFO] === DATEV Connection Test ===
[INFO]   DATEV Telefonie (ROT): Available
[INFO]   DATEV SDD (Kontakte): Available
[INFO]   DATEV available - all components detected
[INFO] =============================
[INFO] TAPI line monitor connected: 161 : Marcos Valassas
[INFO] Extension auto-detected from 3CX TAPI: 161
[INFO] Contact cache initialized: 19421 contacts loaded, 24251 unique phone numbers indexed
```

**Incoming Call Flow:**
```
[INFO] RINGING: Incoming call 161-23012026-1030-0912387 from ********4567 (contact=Mueller GmbH)
[INFO] Bridge -> DATEV: NewCall (Direction=eDirIncoming, Contact=Mueller GmbH)
[INFO] CONNECTED: Call 161-23012026-1030-0912387
[INFO] Bridge -> DATEV: CallStateChanged (State=eCSConnected)
[INFO] Contact reshow: Contact changed - new=Mueller Hans (SyncID=datev-123)
[INFO] Bridge -> DATEV: CallAdressatChanged (Contact=Mueller Hans, DataSource=DATEV_Adressaten)
[INFO] DISCONNECTED: Call 161-23012026-1030-0912387
[INFO] Bridge -> DATEV: CallStateChanged (State=eCSFinished)
[INFO] Bridge -> DATEV: NewJournal (Duration=00:05:23, Contact=Mueller Hans)
```

> **Note:** Phone numbers are masked in log output for privacy (default: last 5 digits visible). Configure via `LogMaskDigits` in `[Logging]` section. Set to `0` to disable masking.

**Last-Contact Routing:**
```
[INFO] LastContactRouting: Prioritized AdressatID=12345 for 891234567 (last used 15min ago)
```

**Click-to-Dial (DATEV-initiated):**
```
[INFO] DATEV -> Bridge: Dial command received
[DEBUG]   CalledNumber=********4567, Adressatenname=Mueller GmbH, SyncID=datev-456
[INFO] DATEV Dial: Pending call stored for ********4567 (SyncID=datev-456)
[INFO] DATEV Dial: MAKE-CALL sent for ********4567
[INFO] RINGBACK: Outgoing call 161-23012026-1035-4829173 to ********4567 (DATEV-initiated, SyncID=datev-456)
```

Enable verbose logging (`VerboseLogging=true` in `[Debug]` section) for detailed output including contact cache contents and lookup dictionary.

## Building

1. Open `3CXDatevConnector.sln` in Visual Studio 2019+
2. Ensure DATEV DLLs are available (GAC or local `Lib/` folder)
3. Ensure 3CX Multi-Line TAPI driver is installed
4. Build -> Release
5. Output: `bin\Release\3cxDatevConnector.exe`

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Red icon, won't connect | Ensure 3CX Windows Softphone App (V20) is running and TAPI driver is installed |
| "3CX TAPI INI not found" | Install and configure 3CX Multi-Line TAPI driver |
| No contacts loaded | Check DATEV Arbeitsplatz is running, SDD available |
| Calls not detected | Extension auto-detects from TAPI line name on connect |
| Contact not found | Adjust `MaxCompareLength` in `[Settings]` for your number format |
| Popup not showing | Check `EnableCallerPopup` is true and `CallerPopupMode` includes Form/Balloon in `[Settings]` |
| No balloon notification | Ensure Windows notifications are enabled for the application |
| Journal not sent | Ensure `EnableJournalPopup` is true in `[Settings]` and contact is matched to DATEV. For outbound calls, also enable `EnableJournalPopupOutbound` |
| DATEV notifications failing | Check logs for circuit breaker state, may need to restart |
| SDD keeps timing out | Increase `SddMaxRetries` or `SddRetryDelaySeconds` in `[Connection]` |
| No contact reshow after connect | Set `ContactReshowDelaySeconds` > 0 in `[Settings]` |
| Wrong contact prioritized | Check `LastContactRoutingMinutes` in `[Settings]` (set to 0 to disable routing memory) |
| Call history empty | Only DATEV-matched calls are stored; ensure contacts are loaded |
| TS: "Cannot register DatevAdapter" | Session GUID conflict — check logs for session ID |
| DATEV SyncID lost | Ensure DATEV-initiated calls are using Dial command (not manual dialing) |
| Short internal numbers triggering lookup | MinCallerIdLength auto-adjusts to extension length |
| Fewer contacts than expected | If "Aktive Kontakte" is enabled, only contacts with Status ≠ 0 are loaded (inactive contacts excluded) — check setting |
| Webclient: no calls forwarded | Ensure browser extension is installed, 3CX WebClient is open, and bridge is listening on port 19800 |
| Webclient: empty extension in HELLO | Reload extension; ensure `localStorage.wc.provision` exists in the 3CX PWA origin |
| Webclient: connection refused | Bridge not running or port 19800 blocked; check `Webclient.WebSocketPort` in INI |
| Sync timestamp not updating | Reload contacts via Laden button — timestamp updates on successful load |
| StatusForm shows disconnected after reconnect | Wait for event-based update (up to 6 seconds) or status will auto-refresh |

## License

Proprietary — Internal use only

## Design Decisions

| Area | Decision | Rationale |
|------|----------|-----------|
| **Authorization** | Current user SID (DACL) on named pipe | Extension numbers are guessable and not OS security principals |
| **Routing** | Extension number in pipe name | Identifies the correct channel without serving as a security boundary |
| **Configuration** | Single INI file (`3CXDATEVConnector.ini`) in `%AppData%\3CXDATEVConnector\` | Simpler deployment, hot-reloadable, admin-friendly for enterprise rollout |
| **Deployment** | Per-user MSI via Intune/SCCM/GPO + HKCU Run autostart | Tray app requires per-user context; HKCU Run is simplest reliable autostart |
| **Log Retention** | Size-based rotation (10 MB per file, 5 files max) | Prevents unbounded disk usage while keeping recent diagnostic data |
| **Naming** | `3CXDATEVConnector` (install/log folder), `3CXDatevConnector` (exe, log), `3CXDATEVConnector` (INI) | Consistent naming across all artifacts |
| **Contact Routing** | `LastContactRoutingMinutes` in `[Settings]` | Configurable window for remembering last-used contact per phone number |
| **Config Portability** | Distribute INI file directly (no import/export features) | INI is the single portable artifact; enterprise tools handle distribution |

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.1.9 | 2026-01 | **Memory Optimization**: Filter Communications to phone-only (`Medium == Phone`), reducing objects from ~68K to ~17K. Cached `EffectiveNormalizedNumber` property to eliminate ~68K repeated Regex allocations. Compiled `PhoneNumberNormalizer` Regex. Replaced LINQ `GroupBy`/`ToDictionary` with direct `SortedDictionary` build loop. Pre-load GC releases old cache before XML deserialization. Post-load forced double-collect GC with LOH compaction and `SetProcessWorkingSetSize` P/Invoke to trim OS working set. Added `UITheme.Cleanup()` in shutdown path, `TrayApplication._currentMainForm` disposal, `Array.Empty<T>()` replacements. Post-cache GC diagnostics log (working set before/after, managed heap). |
| 1.1.8 | 2026-01 | **Core Infrastructure**: Added `IniConfig` (typed config access), `IntegrationConstants` (centralized TAPI/DATEV/timeout constants), `TapiException`/`DatevException` (categorized errors for retry decisions) |
| 1.1.8 | 2026-01 | **Setup Wizard** for first-run configuration (TAPI line selection, DATEV connection test, Windows autostart toggle), **Troubleshooting Form** with categorized help for TAPI/DATEV/Contact issues and quick log access, **UIStrings centralization** (all German UI text in single location), **Form refactoring** (consistent theme usage across all forms with Layout constants) |
| 1.1.7 | 2026-01 | **Multi-line TAPI support** with per-line status indicators and test buttons, **TAPI error categorization** (transient, line closed, shutdown, permanent) with intelligent retry, **Thread safety improvements** (ConcurrentDictionary for line tracking, UI thread marshaling via InvokeRequired, volatile modifiers for cross-thread settings access), **ConfigKeys centralization** (all INI keys as compile-time constants), **IDisposable pattern completion** (GC.SuppressFinalize, proper CancellationTokenSource disposal), **ConfigWatcher improvements** (debounce timer instead of Thread.Sleep, consolidated DumpContacts methods), consistent progress label styling, UITheme cleanup (removed unused CreateCard, improved font disposal), YAGNI cleanup (removed unused async retry methods) |
| 1.1.6 | 2026-01 | **German localization** throughout all UI (menus, forms, buttons, labels), StatusForm improvements (TopMost=false, Test button visual feedback with ✓/✗, event-based TAPI status updates with 6s timeout fallback, purple AccentBridge color), CallHistoryForm redesign (larger size, 5-second auto-refresh, clearer journal status: "✓ Ja"/"Offen"/"—"), **Active contacts filter** (`@Status NOT EQUAL TO "0"` for DATEV SDD), sync timestamp display in SettingsForm, improved button layouts |
| 1.1.5 | 2026-01 | Dynamic tray icon with status ring overlay (green/orange/red around app logo), StatusForm quick overview on double-click, embedded icon resource support, call history with separate inbound/outbound buffers for re-journaling (DATEV-matched only, double-click to open journal), extension auto-detected from TAPI line name, simplified Settings UI |
| 1.1.4 | 2026-01 | Single-page settings dashboard, last-contact routing, MinCallerIdLength auto-detection from extension, removed popup countdowns (stay until dismissed/connect), balloon notification threading fix, combined tray status (green/orange/red) |
| 1.1.3 | 2026-01 | TAPI 2.x line monitoring, 3CX TAPI INI auto-config, journal popup, contact reshow on connect, CallAdressatChanged, DATEV-initiated call handling with SyncID, popup close on connect, terminal server session management, DATEV startup connection test, DataSource "3CX" fallback |
| 1.1.2 | 2026-01 | Caller popup notifications, contact selection dialog, circuit breaker pattern, SDD retry mechanism, unified phone normalization, Settings dialog improvements |
| 1.1.1 | 2026-01 | Settings dialog, improved logging, call tracking improvements |
| 1.1.0 | 2026-01 | Initial release |
