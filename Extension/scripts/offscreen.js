// ===== Offscreen document: owns the WebSocket to the bridge =====
// MV3 service workers are suspended after ~30s of idle activity, which drops
// long-lived WebSockets. Offscreen documents are exempt from that lifetime
// limit, so we host the socket here and talk to the SW over chrome.runtime
// messages.

const PROTOCOL_VERSION = 1;
const DEFAULT_BRIDGE_PORT = 19800;
const BRIDGE_PORT_RANGE_START = 19800;
const BRIDGE_PORT_RANGE_END   = 19899;
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
let hasEverConnected = false;
let configuredExtension = "";
let detectedExtension = "";
let detectedDomain = "";
let detectedVersion = "";
let detectedUserName = "";
let debugLogging = false;
let bridgePort = DEFAULT_BRIDGE_PORT;
const HELLO_BOOTSTRAP_TIMER_KEY = "__3cxDatevConnectorHelloTimer";

let connectingInProgress = false;

function logDebug(...args) {
  if (!debugLogging) return;
  console.log("[3CX-DATEV-C][off]", ...args);
}

function logInfo(...args) {
  console.log("[3CX-DATEV-C][off]", ...args);
}

function resolveExtensionNumber() {
  return configuredExtension || detectedExtension || "";
}

function postToBackground(msg) {
  try {
    chrome.runtime.sendMessage({ ...msg, target: "background" }).catch(() => {});
  } catch {
    // SW not reachable — ignore; the SW will re-sync via INIT when it wakes.
  }
}

function pushBridgeState() {
  postToBackground({
    type: "BRIDGE_STATE",
    wsState: ws ? ws.readyState : 3,
    helloAcked,
    extension: resolveExtensionNumber(),
    port: bridgePort
  });
}

// Deterministic port per extension: BRIDGE_PORT_RANGE_START + extension.
// Extension 1005 on default base 19800 -> 20805. Unique extension -> unique
// port, so we can reach our bridge with one probe instead of scanning the
// range. Returns 0 when the extension is unknown or out of TCP range.
function computePreferredPort() {
  const ext = parseInt(resolveExtensionNumber(), 10);
  if (!Number.isFinite(ext) || ext <= 0) return 0;
  const port = BRIDGE_PORT_RANGE_START + ext;
  if (port < 1024 || port > 65535) return 0;
  return port;
}

// Probe port with fetch before opening a WebSocket. Fetch errors are silently
// catchable and do NOT appear on chrome://extensions, unlike WebSocket
// ERR_CONNECTION_REFUSED which Chrome logs at the browser level.
async function isPortReachable(port) {
  try {
    const response = await fetch(`http://127.0.0.1:${port}/`, {
      signal: AbortSignal.timeout(2000)
    });
    if (!response.ok) return false;
    const body = await response.text();
    return body === "OK";
  } catch {
    return false;
  }
}

// Preferred-first, then cache, then parallel scan.
async function findBridgePort() {
  const preferred = computePreferredPort();
  const cached = Number.isFinite(bridgePort) ? bridgePort : null;

  // 1. Deterministic port from extension — virtually always the bridge.
  if (preferred && await isPortReachable(preferred)) {
    logInfo(`Nebenstelle-Port ${preferred} erreichbar, verbunden`);
    return preferred;
  }

  // 2. Cached port, if different from the (failed) preferred one.
  if (cached && cached !== preferred && await isPortReachable(cached)) {
    logInfo(`Cache-Port ${cached} erreichbar, verbunden`);
    return cached;
  }

  // 3. Parallel probe across the default range.
  const tried = new Set();
  if (preferred) tried.add(preferred);
  if (cached) tried.add(cached);
  const ports = [];
  for (let p = BRIDGE_PORT_RANGE_START; p <= BRIDGE_PORT_RANGE_END; p++) {
    if (!tried.has(p)) ports.push(p);
  }
  const results = await Promise.allSettled(ports.map(isPortReachable));
  const responders = ports.filter((_, i) =>
    results[i].status === "fulfilled" && results[i].value === true);

  if (responders.length === 0) {
    logInfo(`Keine Bridge gefunden (Nebenstelle=${preferred || "?"}, Cache=${cached || "?"}, Range=${BRIDGE_PORT_RANGE_START}-${BRIDGE_PORT_RANGE_END}), Retry geplant`);
    return null;
  }

  const picked = responders[0];
  logInfo(`Scan lief, Bridge gefunden auf ${picked}`);
  return picked;
}

async function connectBridge() {
  if (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN)) {
    return ws;
  }
  if (connectingInProgress) return null;
  connectingInProgress = true;

  const foundPort = await findBridgePort();
  if (foundPort === null) {
    connectingInProgress = false;
    scheduleReconnect();
    return null;
  }

  if (foundPort !== bridgePort) {
    bridgePort = foundPort;
    // Ask the SW to persist the learned port — offscreen doesn't write storage.
    postToBackground({ type: "PORT_LEARNED", port: foundPort });
  }

  const url = `ws://127.0.0.1:${bridgePort}`;
  logDebug("Connecting to bridge", url);

  try {
    ws = new WebSocket(url);
  } catch (err) {
    console.warn("[3CX-DATEV-C][off] WebSocket create failed", err);
    connectingInProgress = false;
    scheduleReconnect();
    return null;
  }

  connectingInProgress = false;

  ws.onopen = () => {
    logDebug("WebSocket connected");
    hasEverConnected = true;
    reconnectDelay = RECONNECT_DELAY_MS; // reset backoff on success
    helloSent = false;
    helloAcked = false;
    clearHelloRetry();
    ensureHello("ws-open");
    pushBridgeState();
  };

  ws.onmessage = (event) => {
    try {
      const msg = JSON.parse(event.data);
      logDebug("Bridge -> extension", msg);

      if (msg && msg.type === "HELLO_ACK") {
        helloAcked = true;
        clearHelloRetry();
        logInfo(`Bridge verbunden auf Port ${bridgePort} (Extension ${msg.extension || "(unbekannt)"})`);
        logDebug("HELLO_ACK received", { extension: msg.extension, bridgeVersion: msg.bridgeVersion, port: msg.port });
        pushBridgeState();
      }

      if (msg && msg.type === "COMMAND") {
        handleBridgeCommand(msg);
      }
    } catch (err) {
      console.warn("[3CX-DATEV-C][off] Failed to parse bridge message", err);
    }
  };

  ws.onclose = (event) => {
    logDebug("WebSocket closed", { code: event.code, reason: event.reason });
    ws = null;
    helloSent = false;
    helloAcked = false;
    clearHelloRetry();
    pushBridgeState();
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
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      connectBridge(); // async — initiates probe + connection for future sends
      return false;
    }
    ws.send(JSON.stringify(message));
    logDebug("Extension -> bridge", message);
    return true;
  } catch (err) {
    console.error("[3CX-DATEV-C][off] Bridge send failed", err);
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

function ensureHello(sourceTabId = "") {
  if (helloAcked) return;
  if (helloSent && ws && ws.readyState === WebSocket.OPEN) return;

  const hello = {
    v: PROTOCOL_VERSION,
    type: "HELLO",
    extension: resolveExtensionNumber(),
    identity: "3CX WebClient"
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
      console.warn("[3CX-DATEV-C][off] HELLO bootstrap failed", err);
    }
  }, Math.max(0, delayMs));
}

function handleBridgeCommand(msg) {
  // Offscreen can't reach tabs — forward to SW for tab routing.
  postToBackground({ type: "BRIDGE_COMMAND", data: msg });
}

function applyInit(msg) {
  const newConfiguredExtension = typeof msg.extensionNumber === "string" ? msg.extensionNumber.trim() : configuredExtension;
  const newDetectedExtension = typeof msg.detectedExtension === "string" ? msg.detectedExtension : detectedExtension;
  const newDetectedDomain = typeof msg.detectedDomain === "string" ? msg.detectedDomain : detectedDomain;
  const newDetectedVersion = typeof msg.detectedVersion === "string" ? msg.detectedVersion : detectedVersion;
  const newDetectedUserName = typeof msg.detectedUserName === "string" ? msg.detectedUserName : detectedUserName;
  const newDebugLogging = typeof msg.debugLogging === "boolean" ? msg.debugLogging : debugLogging;
  const newBridgePort = Number.isFinite(parseInt(msg.bridgePort, 10))
    ? parseInt(msg.bridgePort, 10)
    : bridgePort;

  const prevResolved = resolveExtensionNumber();

  configuredExtension = newConfiguredExtension;
  detectedExtension = newDetectedExtension;
  detectedDomain = newDetectedDomain;
  detectedVersion = newDetectedVersion;
  detectedUserName = newDetectedUserName;
  debugLogging = newDebugLogging;
  bridgePort = newBridgePort || DEFAULT_BRIDGE_PORT;

  const extensionChanged = resolveExtensionNumber() !== prevResolved;

  logDebug("INIT applied", {
    configuredExtension, detectedExtension, debugLogging, bridgePort, extensionChanged
  });

  if (!ws) {
    connectBridge();
  } else if (extensionChanged && ws.readyState === WebSocket.OPEN) {
    // Re-handshake so the bridge sees the new extension number.
    helloSent = false;
    helloAcked = false;
    clearHelloRetry();
    scheduleHelloBootstrap(50);
  }
}

function handleRefresh() {
  logDebug("REFRESH requested — cycling WebSocket");
  if (ws) {
    ws.onopen = null;
    ws.onmessage = null;
    ws.onclose = null;
    ws.onerror = null;
    try { ws.close(); } catch {}
    ws = null;
  }
  helloSent = false;
  helloAcked = false;
  clearHelloRetry();
  reconnectDelay = RECONNECT_DELAY_MS;
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  pushBridgeState();
  connectBridge();
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || msg.target !== "offscreen") return;

  if (msg.type === "INIT") {
    applyInit(msg);
    return;
  }

  if (msg.type === "SEND_TO_BRIDGE") {
    ensureHello("send-to-bridge");
    if (msg.payload) sendBridge(msg.payload);
    return;
  }

  if (msg.type === "REFRESH") {
    handleRefresh();
    return;
  }
});

// Idle until the SW pushes INIT. Do not auto-connect.
logDebug("Offscreen document loaded, waiting for INIT");
