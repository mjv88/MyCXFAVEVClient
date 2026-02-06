const NATIVE_HOST_NAME = "com.mjv88.datevbridge";
const PROTOCOL_VERSION = 1;
const NATIVE_HOST_RETRY_DELAY_MS = 30_000;
const NATIVE_HOST_MISSING_REGEX = /native( messaging)? host not found/i;

let nativePort = null;
let helloSent = false;
let configuredExtension = "";
let detectedExtension = "";
let debugLogging = false;
let nativeHostUnavailableUntil = 0;
let nativeHostUnavailableReason = "";
let nativeHostMissingLogged = false;
const HELLO_BOOTSTRAP_TIMER_KEY = "__3cxDatevHelloBootstrapTimer";

const calls = new Map();

function logDebug(...args) {
  if (!debugLogging) return;
  console.log("[3CX-DATEV][bg]", ...args);
}

async function loadConfig() {
  const cfg = await chrome.storage.local.get(["extensionNumber", "debugLogging"]);
  configuredExtension = (cfg.extensionNumber || "").trim();
  debugLogging = !!cfg.debugLogging;
  logDebug("Config loaded", { configuredExtension, debugLogging });
}

function resolveExtensionNumber() {
  return configuredExtension || detectedExtension || "";
}

function isNativeHostMissing(message = "") {
  return NATIVE_HOST_MISSING_REGEX.test(String(message));
}

function connectNativeHost() {
  if (nativePort) {
    return nativePort;
  }

  if (nativeHostUnavailableUntil && Date.now() < nativeHostUnavailableUntil) {
    logDebug("Native host connection skipped (backoff)", {
      until: nativeHostUnavailableUntil,
      reason: nativeHostUnavailableReason
    });
    return null;
  }

  logDebug("Connecting native host", NATIVE_HOST_NAME);
  try {
    nativePort = chrome.runtime.connectNative(NATIVE_HOST_NAME);
  } catch (err) {
    nativeHostUnavailableReason = err?.message || String(err);
    nativeHostUnavailableUntil = Date.now() + NATIVE_HOST_RETRY_DELAY_MS;
    console.warn("[3CX-DATEV][bg] Native host connect failed", nativeHostUnavailableReason);
    return null;
  }

  nativePort.onMessage.addListener((msg) => {
    logDebug("Native -> extension", msg);
  });

  nativePort.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError?.message || "";
    if (isNativeHostMissing(err)) {
      if (!nativeHostMissingLogged) {
        console.warn(
          "[3CX-DATEV][bg] Native host missing. Install/register native host:",
          NATIVE_HOST_NAME
        );
        nativeHostMissingLogged = true;
      }
    } else {
      console.warn("[3CX-DATEV][bg] Native host disconnected", err);
    }
    if (err) {
      nativeHostUnavailableReason = err;
      nativeHostUnavailableUntil = Date.now() + NATIVE_HOST_RETRY_DELAY_MS;
    }
    nativePort = null;
    helloSent = false;
  });

  return nativePort;
}

function sendNative(message) {
  try {
    const port = connectNativeHost();
    if (!port) {
      return false;
    }
    port.postMessage(message);
    logDebug("Extension -> native", message);
    return true;
  } catch (err) {
    console.error("[3CX-DATEV][bg] Native host send failed", err);
    return false;
  }
}

function ensureHello(sourceTabId = "") {
  if (helloSent) return;

  const sent = sendNative({
    v: PROTOCOL_VERSION,
    type: "HELLO",
    extension: resolveExtensionNumber(),
    identity: "3CX Webclient Extension MV3"
  });

  helloSent = sent;
  logDebug("HELLO sent", { sent, sourceTabId, extension: resolveExtensionNumber() });
}

function scheduleHelloBootstrap(delayMs = 250) {
  if (helloSent) return;
  logDebug("Scheduling HELLO bootstrap", { delayMs });

  const currentTimer = globalThis[HELLO_BOOTSTRAP_TIMER_KEY] || null;
  if (currentTimer) {
    clearTimeout(currentTimer);
    globalThis[HELLO_BOOTSTRAP_TIMER_KEY] = null;
  }

  const triggerHelloBootstrap = () => {
    globalThis[HELLO_BOOTSTRAP_TIMER_KEY] = null;
    try {
      ensureHello("bootstrap");
    } catch (err) {
      console.warn("[3CX-DATEV][bg] HELLO bootstrap failed", err);
    }
  };

  if (typeof globalThis.setTimeout === "function") {
    globalThis[HELLO_BOOTSTRAP_TIMER_KEY] = globalThis.setTimeout(triggerHelloBootstrap, Math.max(0, delayMs));
    return;
  }

  triggerHelloBootstrap();
}

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
  sendNative(event);
}

function emitFromLocalConnection(conn, actionType, sourceTabId = "") {
  const callId = conn.id ?? conn.callId;
  if (callId == null) return;

  const direction = conn.isIncoming ? "inbound" : "outbound";
  const remoteNumber = conn.otherPartyDn || conn.otherPartyCallerId || "";
  const remoteName = conn.otherPartyDisplayName || "";

  // ActionType: 2=Inserted, 3=Updated, 4=Deleted
  if (actionType === 4) {
    calls.delete(callId);
    const evt = toCallEvent({
      callId,
      direction,
      remoteNumber,
      remoteName,
      state: "ended",
      reason: "unknown",
      tabId: sourceTabId
    });
    logDebug("Mapped deleted connection -> ended", evt);
    emitCallEvent(evt, sourceTabId);
    return;
  }

  // LocalConnectionState: 1=Ringing, 2=Dialing, 3=Connected
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
    logDebug("Ignoring unmapped state", { actionType, state: conn.state, callId, conn });
    return;
  }

  calls.set(callId, {
    direction,
    remoteNumber,
    remoteName,
    state
  });

  const evt = toCallEvent({
    callId,
    direction,
    remoteNumber,
    remoteName,
    state,
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
      // In GenericMessage, payload field index normally matches MessageId.
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

  // Already normalized object (e.g., future page-hook deep integration).
  if (payload.parsed && typeof payload.parsed === "object") {
    return payload.parsed;
  }

  // Expected production path: binary websocket payload (protobuf GenericMessage).
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
      console.warn("[3CX-DATEV][bg] Failed to parse protobuf frame", err);
      return null;
    }
  }

  // Some deployments emit json text diagnostics; keep a tolerant fallback.
  if (payload.kind === "WS_TEXT" && payload.parsed && typeof payload.parsed === "object") {
    return payload.parsed;
  }

  return null;
}

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (!msg || msg.type !== "3CX_RAW_SIGNAL") return;

  const sourceTabId = sender?.tab?.id ?? "";
  logDebug("Raw signal received", { kind: msg.payload?.kind, sourceTabId });

  // Proactively establish native-host handshake even when no call event exists yet.
  // This allows bridge auto-detection to see the extension/PWA while idle.
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

chrome.runtime.onInstalled.addListener(async () => {
  await loadConfig();
  scheduleHelloBootstrap();
});

chrome.runtime.onStartup.addListener(async () => {
  await loadConfig();
  scheduleHelloBootstrap();
});

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName !== "local") return;
  if (changes.extensionNumber || changes.debugLogging) {
    loadConfig().catch((err) => {
      console.warn("[3CX-DATEV][bg] Failed to reload config", err);
    });
    if (changes.extensionNumber) {
      helloSent = false;
      scheduleHelloBootstrap(50);
    }
  }
});

loadConfig().catch((err) => {
  console.warn("[3CX-DATEV][bg] Initial config load failed", err);
});
scheduleHelloBootstrap(500);
