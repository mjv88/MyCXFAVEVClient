# 3CX WebClient Browser Extension (MV3 Skeleton)

This folder contains a **practical starter implementation** for capturing 3CX WebClient call states and forwarding them to the bridge using the repository protocol.

## What it already does

- Injects a page hook to observe the WebClient WebSocket (`/ws/webclient`).
- Forwards raw signals to the extension service worker.
- Opens a Native Messaging channel to `com.mjv88.datevbridge`.
- Sends protocol-compatible `HELLO` and `CALL_EVENT` JSON messages.
- Sends an eager `HELLO` bootstrap so the bridge can detect the extension even before the first call starts.
- Implements state mapping from normalized `LocalConnection` snapshots to bridge states:
  - `Ringing + inbound` -> `offered`
  - `Dialing` -> `dialing`
  - `Ringing + outbound` -> `ringing`
  - `Connected` -> `connected`
  - `Deleted` -> `ended`

## Decoder status (implemented)

`scripts/background.js` already includes protobuf decoding in `parse3cxFrame`:

1. Decodes `payload.base64` from WebSocket binary frames.
2. Parses `GenericMessage` wrapper.
3. Handles `MessageId == 201` as `MyExtensionInfo`.
4. Normalizes `LocalConnection` deltas and emits bridge-compatible `CALL_EVENT` messages.

Normalized shape handled by the mapper:

```js
{
  messageId: 201,
  localConnections: [
    {
      actionType: 2 | 3 | 4,
      id: 123,
      callId: 456,
      state: 1 | 2 | 3,
      isIncoming: true,
      otherPartyDn: "+49891234567",
      otherPartyDisplayName: "Max Mustermann"
    }
  ]
}
```

## Installation (Chrome / Edge)


## Tab vs PWA behavior

- The extension is injected by URL match (`https://*/webclient/*`), so it works for both normal browser tabs and installed 3CX PWA windows.
- In both cases, the service worker receives a `sender.tab.id`; this value is forwarded into `CALL_EVENT.context.tabId` for traceability.
- No manual allocation is required: whichever WebClient page is active and producing websocket frames is processed automatically.

### 1) Register the Native Messaging host

1. Copy `native-host/com.mjv88.datevbridge.json` to a stable local path, e.g.
   `%LocalAppData%\3CXDATEVBridge\com.mjv88.datevbridge.json` (recommended for per-user setup).
2. Edit the manifest:
   - Set `path` to your installed native host executable (for example `3CXDatevNativeHost.exe`).
   - Replace `<YOUR_EXTENSION_ID>` entries in `allowed_origins` after loading the extension once (step 3 below).
3. Register it in user registry (PowerShell):

> Note: The bridge MSI installs per-user under `%LocalAppData%\Programs\3CXDATEVBridge\`.
> So do **not** use `C:\Program Files\...` unless you intentionally deployed there.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\native-host\register-native-host.ps1 -ManifestPath "$env:LOCALAPPDATA\3CXDATEVBridge\com.mjv88.datevbridge.json"
```

This writes both keys:

- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.mjv88.datevbridge`
- `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.mjv88.datevbridge`

### 2) Load the extension unpacked

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `WebClient/Extension`.
4. Open the extension details and copy the extension ID.
5. Put that ID into `allowed_origins` in `com.mjv88.datevbridge.json` and re-run the PowerShell registration script.

### 3) Configure extension number (optional)

`extensionNumber` is auto-detected from WebClient protobuf `MyExtensionInfo.Number` (MessageId 201).

If you want to override it explicitly, set `extensionNumber` in `chrome.storage.local`; this value has priority over auto-detection and is used in `HELLO` and `CALL_EVENT.context.extension`.

You can set it from the extension service worker console:

```js
chrome.storage.local.set({ extensionNumber: "101" })
```

### 4) Verify end-to-end

1. Start `3cxDatevBridge.exe` with `TelephonyMode=Webclient` (or `Auto`).
2. Open 3CX WebClient and place/receive a call.
3. Check bridge logs for:
   - `NativeMessagingHost: HELLO ...`
   - `NativeMessagingHost: CALL_EVENT ...`
   - `WebclientTelephonyProvider: ... (mapped from ...)`

> Note: The extension now sends `HELLO` proactively (startup/raw-signal bootstrap), so Webclient auto-detection can succeed even when no active call is running yet.

If no events arrive, most often `allowed_origins` or native host registry path is wrong.


### 5) Enable diagnostic logging (recommended while validating)

Enable verbose extension logs:

```js
chrome.storage.local.set({ debugLogging: true })
```

Then inspect:

- **Extension service worker console** (`background.js`) for:
  - native host connect/disconnect errors
  - raw signal receipt (`WS_TEXT`, `WS_BINARY`, etc.)
  - call-event mapping decisions (`LocalConnection -> CALL_EVENT`)
- **Tab console** (`content.js`) for page-hook relay traces.
- **Bridge log** (`%AppData%\3CXDATEVBridge\3CXDatevBridge.log`) for:
  - `NativeMessagingHost: HELLO ...`
  - `NativeMessagingHost: CALL_EVENT ...`
  - `WebclientTelephonyProvider: ... (mapped from ...)`

> Decoder status: `parse3cxFrame` now includes a protobuf parser for `GenericMessage` + `MyExtensionInfo` (MessageId `201`) and extracts `LocalConnection` updates (`Action`, `State`, `IsIncoming`, `OtherPartyDn`, `OtherPartyDisplayName`).
> If future 3CX versions change protobuf schema fields, update parser field mappings in `scripts/background.js`.
