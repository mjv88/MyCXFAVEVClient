(() => {
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

  function refreshWebClientDetection(reason) {
    if (pageHookInjected) {
      logDebug("WebClient detection skipped; page hook already injected", { reason });
      return;
    }

    if (!isLikelyWebClientPage()) {
      logDebug("WebClient detection negative", { reason, href: location.href });
      return;
    }

    pageHookInjected = true;
    logDebug("WebClient detection positive", { reason, href: location.href });
    injectPageHook(reason);
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
