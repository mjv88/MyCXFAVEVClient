# 3CX DATEV Connector — Browser Extension (MV3)

This folder contains the browser extension that captures 3CX WebClient call states and forwards them to the connector via WebSocket.

## What it does

- Injects a page hook to observe the WebClient WebSocket (`/ws/webclient`).
- Forwards raw signals to the extension service worker.
- Connects to the connector via WebSocket (`ws://127.0.0.1:19800`).
- Sends protocol-compatible `HELLO` and `CALL_EVENT` JSON messages.
- Sends an eager `HELLO` bootstrap so the connector can detect the extension even before the first call starts.
- Auto-detects extension number from `localStorage.wc.provision` (set by the 3CX PWA).
- Persists provision data to `chrome.storage.local` so it survives service worker restarts.
- Injects content script into already-open 3CX tabs on install/startup.
- Implements state mapping from normalized `LocalConnection` snapshots to connector states:
  - `Ringing + inbound` -> `offered`
  - `Dialing` -> `dialing`
  - `Ringing + outbound` -> `ringing`
  - `Connected` -> `connected`
  - `Deleted` -> `ended`

## Decoder status (implemented)

`scripts/background.js` includes protobuf decoding in `parse3cxFrame`:

1. Decodes `payload.base64` from WebSocket binary frames.
2. Parses `GenericMessage` wrapper.
3. Handles `MessageId == 201` as `MyExtensionInfo`.
4. Normalizes `LocalConnection` deltas and emits connector-compatible `CALL_EVENT` messages.

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

- The extension is injected by URL match for both `https://*/webclient/*` and root `https://*/` pages, then gated by runtime checks (`/webclient` path/hash, `/#/people` hash route, or `localStorage.wc.provision` presence). This keeps PWA/hash-routed WebClient variants working.
- At `document_start`, `localStorage.wc.provision` is checked to detect 3CX PWAs served at root URLs before Angular bootstraps — ensuring the page hook injects before the WebSocket connection is created.
- In both cases, the service worker receives a `sender.tab.id`; this value is forwarded into `CALL_EVENT.context.tabId` for traceability.
- No manual allocation is required: whichever WebClient page is active and producing websocket frames is processed automatically.

### 1) Load the extension unpacked

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `Extension`.

### 2) Configure extension number (optional)

`extensionNumber` is auto-detected from the 3CX PWA's `localStorage.wc.provision` and from WebClient protobuf `MyExtensionInfo.Number` (MessageId 201).

If you want to override it explicitly, set `extensionNumber` in `chrome.storage.local`; this value has priority over auto-detection and is used in `HELLO` and `CALL_EVENT.context.extension`.

You can set it from the extension service worker console:

```js
chrome.storage.local.set({ extensionNumber: "101" })
```

### 3) Verify end-to-end

1. Start `3cxDatevConnector.exe` with `TelephonyMode=WebClient` (or `Auto`).
2. Open 3CX WebClient and place/receive a call.
3. Check connector logs for:
   - `WebClient Connector: HELLO ...`
   - `WebClient Connector: CALL_EVENT ...`
   - `WebClient Connector: ... (mapped from ...)`

> Note: The extension sends `HELLO` proactively (startup/raw-signal bootstrap), so WebClient auto-detection can succeed even when no active call is running yet.

If no events arrive, verify that the connector is running and listening on port 19800 (default).

### 4) Enable diagnostic logging (recommended while validating)

Enable verbose extension logs:

```js
chrome.storage.local.set({ debugLogging: true })
```

Then inspect:

- **Extension service worker console** (`background.js`) for:
  - WebSocket connect/disconnect errors
  - raw signal receipt (`WS_TEXT`, `WS_BINARY`, etc.)
  - call-event mapping decisions (`LocalConnection -> CALL_EVENT`)
- **Tab console** (`content.js`) for page-hook relay traces.
- **Connector log** (`%AppData%\3CXDATEVConnector\3CXDatevConnector.log`) for:
  - `WebClient Connector: HELLO ...`
  - `WebClient Connector: CALL_EVENT ...`
  - `WebClient Connector: ... (mapped from ...)`

> Decoder status: `parse3cxFrame` includes a protobuf parser for `GenericMessage` + `MyExtensionInfo` (MessageId `201`) and extracts `LocalConnection` updates (`Action`, `State`, `IsIncoming`, `OtherPartyDn`, `OtherPartyDisplayName`).
> If future 3CX versions change protobuf schema fields, update parser field mappings in `scripts/background.js`.
