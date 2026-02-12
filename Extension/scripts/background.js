// ===== Transport: WebSocket to bridge (ws://127.0.0.1:PORT) =====
// Speaks the bridge JSON protocol (HELLO, HELLO_ACK, CALL_EVENT, COMMAND).

const PROTOCOL_VERSION = 1;
const DEFAULT_BRIDGE_PORT = 19800;
const RECONNECT_DELAY_MS = 2_000;
const RECONNECT_MAX_DELAY_MS = 30_000;
const HELLO_RETRY_INTERVAL_MS = 2_000;
const HELLO_MAX_RETRIES = 3;

let ws = null;
let helloSent = false;
let helloAcked = false;
let helloRetryTimer = null;
let helloRetryCount = 0;
let reconnectTimer = null;
let reconnectDelay = RECONNECT_DELAY_MS;
let configuredExtension = "";
let detectedExtension = "";
let detectedDomain = "";
let detectedVersion = "";
let detectedUserName = "";
let debugLogging = false;
let bridgePort = DEFAULT_BRIDGE_PORT;
const HELLO_BOOTSTRAP_TIMER_KEY = "__3cxDatevConnectorHelloTimer";

let webclientTabId = null; // Tab ID of the active 3CX webclient

// Deduplication: field 3 (callId) groups all legs of one logical call
const logicalCallConns = new Map(); // callId(f3) -> Set of active conn.id(f2)
const connIdToCallId = new Map();   // conn.id(f2) -> callId(f3)

function logDebug(...args) {
  if (!debugLogging) return;
  console.log("[3CX-DATEV-C][bg]", ...args);
}

async function loadConfig() {
  const cfg = await chrome.storage.local.get(["extensionNumber", "debugLogging", "bridgePort", "lastProvision"]);
  configuredExtension = (cfg.extensionNumber || "").trim();
  debugLogging = !!cfg.debugLogging;
  bridgePort = parseInt(cfg.bridgePort, 10) || DEFAULT_BRIDGE_PORT;

  // Restore last known provision (survives service worker restart)
  if (!configuredExtension && cfg.lastProvision) {
    detectedExtension = cfg.lastProvision.extension || "";
    detectedDomain = cfg.lastProvision.domain || "";
    detectedVersion = cfg.lastProvision.version || "";
    detectedUserName = cfg.lastProvision.userName || "";
  }
  logDebug("Config loaded", { configuredExtension, detectedExtension, debugLogging, bridgePort });
}

function resolveExtensionNumber() {
  return configuredExtension || detectedExtension || "";
}

// ===== WebSocket transport =====

function connectBridge() {
  if (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN)) {
    return ws;
  }

  const url = `ws://127.0.0.1:${bridgePort}`;
  logDebug("Connecting to bridge", url);

  try {
    ws = new WebSocket(url);
  } catch (err) {
    console.warn("[3CX-DATEV-C][bg] WebSocket create failed", err);
    scheduleReconnect();
    return null;
  }

  ws.onopen = () => {
    logDebug("WebSocket connected");
    reconnectDelay = RECONNECT_DELAY_MS; // reset backoff on success
    helloSent = false;
    helloAcked = false;
    clearHelloRetry();
    ensureHello("ws-open");
  };

  ws.onmessage = (event) => {
    try {
      const msg = JSON.parse(event.data);
      logDebug("Bridge -> extension", msg);

      if (msg && msg.type === "HELLO_ACK") {
        helloAcked = true;
        clearHelloRetry();
        logDebug("HELLO_ACK received", { extension: msg.extension, bridgeVersion: msg.bridgeVersion });
      }

      if (msg && msg.type === "COMMAND") {
        handleBridgeCommand(msg);
      }
    } catch (err) {
      console.warn("[3CX-DATEV-C][bg] Failed to parse bridge message", err);
    }
  };

  ws.onclose = (event) => {
    logDebug("WebSocket closed", { code: event.code, reason: event.reason });
    ws = null;
    helloSent = false;
    helloAcked = false;
    clearHelloRetry();
    scheduleReconnect();
  };

  ws.onerror = (event) => {
    // onerror is always followed by onclose, so reconnect is handled there
    logDebug("WebSocket error", event);
  };

  return ws;
}

function sendBridge(message) {
  try {
    const socket = connectBridge();
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return false;
    }
    socket.send(JSON.stringify(message));
    logDebug("Extension -> bridge", message);
    return true;
  } catch (err) {
    console.error("[3CX-DATEV-C][bg] Bridge send failed", err);
    return false;
  }
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  logDebug("Scheduling reconnect", { delay: reconnectDelay });

  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (ws && ws.readyState === WebSocket.OPEN) return;
    connectBridge();
    // Exponential backoff (capped)
    reconnectDelay = Math.min(reconnectDelay * 1.5, RECONNECT_MAX_DELAY_MS);
  }, reconnectDelay);
}

// ===== HELLO handshake =====

function ensureHello(sourceTabId = "") {
  if (helloAcked) return;
  if (helloSent && ws && ws.readyState === WebSocket.OPEN) return;

  const hello = {
    v: PROTOCOL_VERSION,
    type: "HELLO",
    extension: resolveExtensionNumber(),
    identity: "3CX DATEV Connector Extension MV3"
  };
  if (detectedDomain) hello.domain = detectedDomain;
  if (detectedVersion) hello.webclientVersion = detectedVersion;
  if (detectedUserName) hello.userName = detectedUserName;

  const sent = sendBridge(hello);

  helloSent = sent;
  if (sent) {
    logDebug("HELLO sent", { sourceTabId, extension: resolveExtensionNumber() });
    scheduleHelloRetry();
  }
}

function clearHelloRetry() {
  if (helloRetryTimer) {
    clearTimeout(helloRetryTimer);
    helloRetryTimer = null;
  }
  helloRetryCount = 0;
}

function scheduleHelloRetry() {
  if (helloRetryTimer || helloAcked) return;
  if (helloRetryCount >= HELLO_MAX_RETRIES) return;

  helloRetryTimer = setTimeout(() => {
    helloRetryTimer = null;
    if (helloAcked || !ws || ws.readyState !== WebSocket.OPEN) return;

    helloRetryCount++;
    logDebug("HELLO retry (no ACK)", { attempt: helloRetryCount, max: HELLO_MAX_RETRIES });
    helloSent = false;
    ensureHello("retry");
  }, HELLO_RETRY_INTERVAL_MS);
}

function scheduleHelloBootstrap(delayMs = 250) {
  if (helloAcked) return;
  logDebug("Scheduling HELLO bootstrap", { delayMs });

  const currentTimer = globalThis[HELLO_BOOTSTRAP_TIMER_KEY];
  if (currentTimer) {
    clearTimeout(currentTimer);
  }

  globalThis[HELLO_BOOTSTRAP_TIMER_KEY] = setTimeout(() => {
    globalThis[HELLO_BOOTSTRAP_TIMER_KEY] = null;
    try {
      ensureHello("bootstrap");
    } catch (err) {
      console.warn("[3CX-DATEV-C][bg] HELLO bootstrap failed", err);
    }
  }, Math.max(0, delayMs));
}

// ===== Call event mapping =====

function toCallEvent({ callId, direction, remoteNumber, remoteName, state, reason = "", tabId = "" }) {
  return {
    v: PROTOCOL_VERSION,
    type: "CALL_EVENT",
    ts: Date.now(),
    call: {
      id: String(callId),
      direction,
      remoteNumber: remoteNumber || "",
      remoteName: remoteName || "",
      state,
      reason
    },
    context: {
      extension: resolveExtensionNumber(),
      tabId: tabId === "" ? "" : String(tabId)
    }
  };
}

function emitCallEvent(event, sourceTabId = "") {
  ensureHello(sourceTabId);
  sendBridge(event);
}

// ===== Bridge COMMAND handling (DIAL, DROP) =====

function handleBridgeCommand(msg) {
  if (msg.cmd === "DIAL" && msg.number) {
    logDebug("DIAL command from bridge", { number: msg.number });
    forwardDialToTab(msg.number);
  } else if (msg.cmd === "DROP") {
    logDebug("DROP command from bridge (not yet implemented)");
  } else {
    logDebug("Unknown bridge command", msg);
  }
}

async function forwardDialToTab(number) {
  // Try known webclient tab first
  if (webclientTabId != null) {
    try {
      await chrome.tabs.sendMessage(webclientTabId, { type: "DIAL", number });
      logDebug("DIAL forwarded to known tab", webclientTabId);
      return;
    } catch {
      webclientTabId = null;
    }
  }

  // Fallback: find any 3CX webclient tab
  try {
    const tabs = await chrome.tabs.query({ url: ["https://*/", "https://*/webclient/*"] });
    for (const tab of tabs) {
      try {
        await chrome.tabs.sendMessage(tab.id, { type: "DIAL", number });
        webclientTabId = tab.id;
        logDebug("DIAL forwarded to discovered tab", tab.id);
        return;
      } catch {
        continue;
      }
    }
  } catch (err) {
    logDebug("DIAL failed - no 3CX tab found", err);
  }

  console.warn("[3CX-DATEV-C][bg] DIAL failed: no webclient tab available");
}


function emitFromLocalConnection(conn, actionType, sourceTabId = "") {
  logDebug("RAW LocalConnection:", JSON.stringify({
    "f2": conn.id, "f3": conn.callId, action: actionType,
    state: conn.state, isIncoming: conn.isIncoming,
    callerId: conn.otherPartyCallerId, dn: conn.otherPartyDn
  }));

  // Resolve logical call ID: field 3 groups all legs of one call
  let logicalId;
  if (conn.callId != null && conn.id != null) {
    logicalId = conn.callId;
    connIdToCallId.set(conn.id, logicalId);
  } else if (conn.id != null) {
    logicalId = connIdToCallId.get(conn.id);
  }
  const callId = logicalId ?? conn.id ?? conn.callId;
  if (callId == null) return;

  const direction = conn.isIncoming ? "inbound" : "outbound";
  const remoteNumber = conn.otherPartyCallerId || conn.otherPartyDn || "";
  const remoteName = conn.otherPartyDisplayName || "";

  // ActionType: 1=Inserted, 3=Updated, 4=Deleted
  if (actionType === 4) {
    // Remove this connection from tracking
    if (conn.id != null) {
      connIdToCallId.delete(conn.id);
      const conns = logicalCallConns.get(callId);
      if (conns) {
        conns.delete(conn.id);
        if (conns.size > 0) {
          logDebug("Suppressed ended for conn", conn.id, "- other legs active for logical call", callId);
          return; // other legs still active
        }
        logicalCallConns.delete(callId);
      }
    }

    const evt = toCallEvent({
      callId, direction, remoteNumber, remoteName,
      state: "ended", reason: "unknown", tabId: sourceTabId
    });
    logDebug("Mapped last connection deleted -> ended", evt);
    emitCallEvent(evt, sourceTabId);
    return;
  }

  // For inserts (action=1): register connection, suppress duplicates
  if (actionType === 1 && conn.id != null) {
    let conns = logicalCallConns.get(callId);
    if (!conns) {
      conns = new Set();
      logicalCallConns.set(callId, conns);
    }
    const isFirst = conns.size === 0;
    conns.add(conn.id);
    if (!isFirst) {
      logDebug("Suppressed duplicate leg conn", conn.id, "for logical call", callId);
      return;
    }
  }

  // LocalConnectionState: 0=Unknown/Idle, 1=Ringing, 2=Dialing, 3=Connected
  let state = "";
  if (conn.state === 1 && conn.isIncoming) {
    state = "offered";
  } else if (conn.state === 1 && !conn.isIncoming) {
    state = "ringing";
  } else if (conn.state === 2) {
    state = "dialing";
  } else if (conn.state === 3) {
    state = "connected";
  }

  if (!state) {
    return;
  }

  const evt = toCallEvent({
    callId, direction, remoteNumber, remoteName, state,
    tabId: sourceTabId
  });

  logDebug("Mapped LocalConnection -> CALL_EVENT", evt);
  emitCallEvent(evt, sourceTabId);
}

function tryHandleDecodedMyExtensionInfo(message, sourceTabId = "") {
  if (!message || message.messageId !== 201 || !Array.isArray(message.localConnections)) {
    return false;
  }

  if (!configuredExtension && message.extensionNumber) {
    detectedExtension = String(message.extensionNumber).trim();
  }

  logDebug("Decoded MessageId 201", {
    sourceTabId,
    extension: resolveExtensionNumber(),
    connections: message.localConnections.length
  });

  for (const conn of message.localConnections) {
    const actionType = Number(conn.actionType ?? conn.action ?? conn.containerAction ?? 0);
    emitFromLocalConnection(conn, actionType, sourceTabId);
  }

  return true;
}

// ===== Protobuf parsing =====

function base64ToBytes(base64) {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

class ProtoReader {
  constructor(bytes) {
    this.bytes = bytes;
    this.pos = 0;
    this.len = bytes.length;
  }

  eof() {
    return this.pos >= this.len;
  }

  readByte() {
    if (this.eof()) throw new Error("Unexpected EOF");
    return this.bytes[this.pos++];
  }

  readVarint() {
    let result = 0;
    let shift = 0;
    while (true) {
      const b = this.readByte();
      result |= (b & 0x7f) << shift;
      if ((b & 0x80) === 0) break;
      shift += 7;
      if (shift > 35) throw new Error("Varint too long");
    }
    return result >>> 0;
  }

  readLengthDelimited() {
    const len = this.readVarint();
    const end = this.pos + len;
    if (end > this.len) throw new Error("Length-delimited field exceeds buffer");
    const view = this.bytes.subarray(this.pos, end);
    this.pos = end;
    return view;
  }

  readString() {
    const bytes = this.readLengthDelimited();
    return new TextDecoder("utf-8").decode(bytes);
  }

  skipType(wireType) {
    switch (wireType) {
      case 0:
        this.readVarint();
        return;
      case 1:
        this.pos += 8;
        if (this.pos > this.len) throw new Error("Fixed64 exceeds buffer");
        return;
      case 2: {
        const len = this.readVarint();
        this.pos += len;
        if (this.pos > this.len) throw new Error("Length-delimited skip exceeds buffer");
        return;
      }
      case 5:
        this.pos += 4;
        if (this.pos > this.len) throw new Error("Fixed32 exceeds buffer");
        return;
      default:
        throw new Error(`Unsupported wire type ${wireType}`);
    }
  }
}

function parseLocalConnection(bytes) {
  const reader = new ProtoReader(bytes);
  const out = {};

  while (!reader.eof()) {
    const tag = reader.readVarint();
    const field = tag >>> 3;
    const wire = tag & 0x7;

    switch (field) {
      case 1:
        out.action = reader.readVarint();
        break;
      case 2:
        out.id = reader.readVarint();
        break;
      case 3:
        out.callId = reader.readVarint();
        break;
      case 5:
        out.state = reader.readVarint();
        break;
      case 10:
        out.otherPartyDisplayName = reader.readString();
        break;
      case 11:
        out.otherPartyCallerId = reader.readString();
        break;
      case 12:
        out.isIncoming = !!reader.readVarint();
        break;
      case 22:
        out.otherPartyDn = reader.readString();
        break;
      default:
        reader.skipType(wire);
        break;
    }
  }

  return out;
}

function parseLocalConnections(bytes) {
  const reader = new ProtoReader(bytes);
  const out = { action: 0, items: [] };

  while (!reader.eof()) {
    const tag = reader.readVarint();
    const field = tag >>> 3;
    const wire = tag & 0x7;

    switch (field) {
      case 1:
        out.action = reader.readVarint();
        break;
      case 2:
        out.items.push(parseLocalConnection(reader.readLengthDelimited()));
        break;
      default:
        reader.skipType(wire);
        break;
    }
  }

  return out;
}

function parseMyExtensionInfo(bytes) {
  const reader = new ProtoReader(bytes);
  const localConnections = [];
  let extensionNumber = "";

  while (!reader.eof()) {
    const tag = reader.readVarint();
    const field = tag >>> 3;
    const wire = tag & 0x7;

    if (field === 3 && wire === 2) {
      extensionNumber = reader.readString();
      continue;
    }

    if (field === 18 || field === 20) {
      const group = parseLocalConnections(reader.readLengthDelimited());
      for (const item of group.items) {
        localConnections.push({
          ...item,
          containerAction: group.action
        });
      }
      continue;
    }

    reader.skipType(wire);
  }

  return {
    messageId: 201,
    extensionNumber,
    localConnections
  };
}

function parseGenericMessage(bytes) {
  const reader = new ProtoReader(bytes);
  let messageId = null;
  let messagePayload = null;

  while (!reader.eof()) {
    const tag = reader.readVarint();
    const field = tag >>> 3;
    const wire = tag & 0x7;

    if (field === 1 && wire === 0) {
      messageId = reader.readVarint();
      continue;
    }

    if (wire === 2) {
      const payload = reader.readLengthDelimited();
      if (field === 201) {
        messagePayload = payload;
      } else if (messageId != null && field === messageId) {
        messagePayload = payload;
      }
      continue;
    }

    reader.skipType(wire);
  }

  if (messageId !== 201 || !messagePayload) {
    return null;
  }

  return parseMyExtensionInfo(messagePayload);
}

function parse3cxFrame(payload) {
  if (!payload) return null;

  if (payload.parsed && typeof payload.parsed === "object") {
    return payload.parsed;
  }

  if (payload.kind === "WS_BINARY" && payload.base64) {
    try {
      const bytes = base64ToBytes(payload.base64);
      const parsed = parseGenericMessage(bytes);
      if (parsed) {
        logDebug("Parsed protobuf GenericMessage", {
          messageId: parsed.messageId,
          localConnections: parsed.localConnections.length
        });
      }
      return parsed;
    } catch (err) {
      console.warn("[3CX-DATEV-C][bg] Failed to parse protobuf frame", err);
      return null;
    }
  }

  if (payload.kind === "WS_TEXT" && payload.parsed && typeof payload.parsed === "object") {
    return payload.parsed;
  }

  return null;
}

// ===== Message listeners =====

chrome.runtime.onMessage.addListener((msg, sender) => {
  // Handle provision data from content script (localStorage auto-detect)
  if (msg?.type === "3CX_PROVISION" && msg.provision) {
    const prov = msg.provision;
    const extensionChanged = prov.extension && !configuredExtension && prov.extension !== detectedExtension;

    if (prov.extension && !configuredExtension) {
      detectedExtension = prov.extension;
    }
    if (prov.domain) detectedDomain = prov.domain;
    if (prov.version) detectedVersion = prov.version;
    if (prov.userName) detectedUserName = prov.userName;

    logDebug("Provision received", {
      extension: resolveExtensionNumber(),
      domain: detectedDomain,
      version: detectedVersion,
      userName: detectedUserName
    });

    // Persist so it survives service worker restarts
    chrome.storage.local.set({
      lastProvision: {
        extension: detectedExtension,
        domain: detectedDomain,
        version: detectedVersion,
        userName: detectedUserName
      }
    }).catch(() => {});

    // Re-send HELLO if extension changed or handshake not complete
    if (extensionChanged || !helloAcked) {
      helloSent = false;
      if (extensionChanged) helloAcked = false; // allow re-handshake with correct extension
      ensureHello("provision");
    }
    return;
  }

  if (!msg || msg.type !== "3CX_RAW_SIGNAL") return;

  const sourceTabId = sender?.tab?.id ?? "";
  if (sourceTabId) webclientTabId = sourceTabId; // track active 3CX tab
  logDebug("Raw signal received", { kind: msg.payload?.kind, sourceTabId });

  ensureHello(sourceTabId);

  const decoded = parse3cxFrame(msg.payload);
  if (!decoded) {
    return;
  }

  const handled = tryHandleDecodedMyExtensionInfo(decoded, sourceTabId);
  if (!handled) {
    logDebug("Decoded payload ignored (not MessageId 201 shape)", decoded);
  }
});

// Inject content script into already-open 3CX tabs (content_scripts from
// manifest only run on page navigation, not tabs that are already loaded).
async function injectExistingTabs() {
  try {
    const tabs = await chrome.tabs.query({ url: ["https://*/", "https://*/webclient/*"] });
    for (const tab of tabs) {
      try {
        // Try to reach an existing content script first
        await chrome.tabs.sendMessage(tab.id, { type: "REFRESH_WEBCLIENT_DETECTION" });
        logDebug("Content script already present in tab", tab.id);
      } catch {
        // No content script — inject it
        try {
          await chrome.scripting.executeScript({
            target: { tabId: tab.id },
            files: ["scripts/content.js"]
          });
          logDebug("Injected content script into tab", tab.id);
        } catch (err) {
          logDebug("Could not inject into tab", tab.id, err);
        }
      }
    }
  } catch (err) {
    logDebug("injectExistingTabs failed", err);
  }
}

chrome.runtime.onInstalled.addListener(async () => {
  await loadConfig();
  await injectExistingTabs();
  // Bridge connection is lazy: starts when a 3CX webclient tab sends provision/signal data
});

chrome.runtime.onStartup.addListener(async () => {
  await loadConfig();
  await injectExistingTabs();
  // Bridge connection is lazy: starts when a 3CX webclient tab sends provision/signal data
});

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName !== "local") return;
  if (changes.extensionNumber || changes.debugLogging || changes.bridgePort) {
    const portChanged = !!changes.bridgePort;
    loadConfig().catch((err) => {
      console.warn("[3CX-DATEV-C][bg] Failed to reload config", err);
    });
    if (changes.extensionNumber || portChanged) {
      // Close existing connection so it reconnects with new settings
      if (ws) {
        try { ws.close(); } catch {}
      }
      helloSent = false;
      helloAcked = false;
      clearHelloRetry();
      scheduleHelloBootstrap(50);
    }
  }
});

loadConfig().catch((err) => {
  console.warn("[3CX-DATEV-C][bg] Initial config load failed", err);
});
// No eager WebSocket connect — connection starts lazily when a 3CX webclient tab is detected
