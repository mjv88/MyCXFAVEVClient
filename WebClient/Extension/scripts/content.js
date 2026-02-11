(() => {
  // Guard against double injection (manifest content_scripts + chrome.scripting.executeScript
  // share the same isolated world). Re-injection just re-sends provision data.
  if (window.__3cx_datev_content_active) {
    try {
      const raw = localStorage.getItem("wc.provision");
      if (raw) {
        const prov = JSON.parse(raw);
        if (prov?.username) {
          chrome.runtime.sendMessage({
            type: "3CX_PROVISION",
            provision: {
              extension: String(prov.username).trim(),
              domain: prov.domain || "",
              version: (localStorage.getItem("wc.version") || "").replace(/^"|"$/g, ""),
              userName: localStorage.getItem("yourname") || ""
            }
          });
        }
      }
    } catch {}
    return;
  }
  window.__3cx_datev_content_active = true;

  const BRIDGE_CHANNEL = "__3cx_datev_bridge__";
  let debugLogging = false;
  let pageHookInjected = false;

  // Known 3CX PWA hash routes that indicate a WebClient session.
  const WEBCLIENT_HASH_ROUTES = [
    "/people", "/calls", "/webclient", "/chat",
    "/voicemail", "/settings", "/meetingscheduler"
  ];

  const isLikelyWebClientPage = () => {
    const path = window.location.pathname || "";
    const hash = window.location.hash || "";

    // Path-based: /webclient or /webclient/...
    if (path.startsWith("/webclient")) return true;

    // Hash-routed PWA variants: /#/people, /#/calls, /#/webclient, etc.
    if (hash) {
      const normalizedHash = hash.replace(/^#\/?/, "/");
      if (WEBCLIENT_HASH_ROUTES.some((route) => normalizedHash.startsWith(route))) return true;
    }

    return false;
  };

  const logDebug = (...args) => {
    if (!debugLogging) return;
    console.log("[3CX-DATEV][content]", ...args);
  };

  try {
    chrome.storage.local.get(["debugLogging"]).then((cfg) => {
      debugLogging = !!cfg.debugLogging;
    }).catch(() => {});

    chrome.storage.onChanged.addListener((changes, areaName) => {
      if (areaName === "local" && changes.debugLogging) {
        debugLogging = !!changes.debugLogging.newValue;
      }
    });
  } catch {
    // Extension context already invalidated at load time
  }

  function injectPageHook(reason) {
    const script = document.createElement("script");
    script.src = chrome.runtime.getURL("scripts/page-hook.js");
    script.dataset.bridgeChannel = BRIDGE_CHANNEL;
    script.async = false;
    (document.head || document.documentElement).appendChild(script);
    script.remove();
    logDebug("Injected page hook", { reason, href: location.href });
  }

  /**
   * Read 3CX provision data from localStorage (available on the webclient origin).
   * Returns { extension, domain, version, userName } or null.
   */
  function readProvision() {
    try {
      const raw = localStorage.getItem("wc.provision");
      if (!raw) return null;
      const prov = JSON.parse(raw);
      if (!prov || !prov.username) return null;

      const version = localStorage.getItem("wc.version");
      const yourname = localStorage.getItem("yourname");

      return {
        extension: String(prov.username).trim(),
        domain: prov.domain || "",
        version: version ? version.replace(/^"|"$/g, "") : "",
        userName: yourname || ""
      };
    } catch {
      return null;
    }
  }

  let provisionSent = false;

  function sendProvision(reason) {
    if (provisionSent) return;
    const prov = readProvision();
    if (!prov) return;
    provisionSent = true;
    logDebug("Sending provision", { reason, extension: prov.extension, domain: prov.domain });
    safeSendMessage({ type: "3CX_PROVISION", provision: prov });
  }

  function refreshWebClientDetection(reason) {
    if (pageHookInjected) {
      logDebug("WebClient detection skipped; page hook already injected", { reason });
      // Still try to send provision (may have become available after page load)
      sendProvision(reason);
      return;
    }

    if (!isLikelyWebClientPage()) {
      logDebug("WebClient detection negative", { reason, href: location.href });
      return;
    }

    pageHookInjected = true;
    logDebug("WebClient detection positive", { reason, href: location.href });
    injectPageHook(reason);
    sendProvision(reason);
  }

  // Guard against stale content scripts after extension reload/update.
  // Once the context is invalidated, all chrome.runtime calls will throw.
  let contextInvalidated = false;

  function safeSendMessage(msg) {
    if (contextInvalidated) return;
    try {
      chrome.runtime.sendMessage(msg);
    } catch (err) {
      if (String(err).includes("Extension context invalidated")) {
        contextInvalidated = true;
        logDebug("Extension context invalidated — content script is stale, stopping");
      } else {
        console.warn("[3CX-DATEV][content] sendMessage failed", err);
      }
    }
  }

  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data) {
      return;
    }

    const msg = event.data;
    if (msg.channel !== BRIDGE_CHANNEL || msg.source !== "3cx-page-hook") {
      return;
    }

    logDebug("Page hook signal", msg.payload?.kind || "(unknown)");

    safeSendMessage({
      type: "3CX_RAW_SIGNAL",
      payload: msg.payload
    });
  });

  chrome.runtime.onMessage.addListener((message) => {
    if (message?.type === "REFRESH_WEBCLIENT_DETECTION") {
      refreshWebClientDetection("runtime-message");
    }
  });

  window.addEventListener("hashchange", () => {
    refreshWebClientDetection("hashchange");
  });

  window.addEventListener("popstate", () => {
    refreshWebClientDetection("popstate");
  });

  refreshWebClientDetection("initial");
})();
