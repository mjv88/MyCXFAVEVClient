(() => {
  const BRIDGE_CHANNEL = "__3cx_datev_bridge__";
  let debugLogging = false;
  let pageHookInjected = false;

  const isLikelyWebClientPage = () => {
    const path = window.location.pathname || "";
    const hash = window.location.hash || "";

    // Support both path-based (/webclient/*) and hash-routed PWA variants (/#/people, /#/webclient).
    if (path.startsWith("/webclient")) return true;
    if (hash.includes("/webclient")) return true;
    if (path === "/" && hash.startsWith("#/people")) return true;

    return false;
  };

  const logDebug = (...args) => {
    if (!debugLogging) return;
    console.log("[3CX-DATEV][content]", ...args);
  };

  chrome.storage.local.get(["debugLogging"]).then((cfg) => {
    debugLogging = !!cfg.debugLogging;
  }).catch(() => {
    // ignore
  });

  chrome.storage.onChanged.addListener((changes, areaName) => {
    if (areaName === "local" && changes.debugLogging) {
      debugLogging = !!changes.debugLogging.newValue;
    }
  });

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

  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data) {
      return;
    }

    const msg = event.data;
    if (msg.channel !== BRIDGE_CHANNEL || msg.source !== "3cx-page-hook") {
      return;
    }

    logDebug("Page hook signal", msg.payload?.kind || "(unknown)");

    chrome.runtime.sendMessage({
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
