(() => {
  const channel = document.currentScript?.dataset.bridgeChannel || "__3cx_datev_bridge__";

  const post = (payload) => {
    window.postMessage({
      channel,
      source: "3cx-page-hook",
      payload
    }, "*");
  };

  const toBase64 = (arrayBuffer) => {
    const bytes = new Uint8Array(arrayBuffer);
    let binary = "";
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
      const chunk = bytes.subarray(i, i + chunkSize);
      binary += String.fromCharCode(...chunk);
    }
    return btoa(binary);
  };

  // TEMP: Intercept XHR + fetch to capture SOAP MakeCall format from manual dials
  const NativeXHROpen = XMLHttpRequest.prototype.open;
  const NativeXHRSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(method, url, ...args) {
    this.__3cxUrl = url;
    this.__3cxMethod = method;
    return NativeXHROpen.call(this, method, url, ...args);
  };
  XMLHttpRequest.prototype.send = function(body) {
    if (this.__3cxUrl && String(this.__3cxUrl).includes("MPWebService")) {
      post({ kind: "XHR_SOAP_OUT", method: this.__3cxMethod, url: this.__3cxUrl, body: String(body).substring(0, 2000) });
    }
    return NativeXHRSend.call(this, body);
  };

  const NativeFetch = window.fetch;
  window.fetch = function(input, init) {
    const url = typeof input === "string" ? input : input?.url || "";
    if (url.includes("MPWebService") || url.includes("MyPhone")) {
      const body = init?.body ? String(init.body).substring(0, 2000) : "(no body)";
      const headers = init?.headers || {};
      post({ kind: "FETCH_SOAP_OUT", url, method: init?.method || "GET", headers: JSON.stringify(headers), body });
    }
    return NativeFetch.apply(this, arguments);
  };

  let webclientSocket = null; // Reference to the active 3CX WebSocket

  const NativeWebSocket = window.WebSocket;
  const patchedWebSocket = function patchedWebSocket(url, protocols) {
    const socket = protocols ? new NativeWebSocket(url, protocols) : new NativeWebSocket(url);

    const is3cxWebclientSocket = typeof url === "string" && url.includes("/ws/webclient");
    if (!is3cxWebclientSocket) {
      return socket;
    }

    webclientSocket = socket; // Store reference for DIAL commands
    post({ kind: "WS_OPEN", url });

    socket.addEventListener("message", (evt) => {
      try {
        if (typeof evt.data === "string") {
          let parsed = null;
          try {
            parsed = JSON.parse(evt.data);
          } catch {
            // non-JSON textual payload
          }

          post({
            kind: "WS_TEXT",
            url,
            data: evt.data,
            parsed
          });
          return;
        }

        if (evt.data instanceof ArrayBuffer) {
          post({
            kind: "WS_BINARY",
            url,
            base64: toBase64(evt.data)
          });
          return;
        }

        if (evt.data instanceof Blob) {
          evt.data.arrayBuffer().then((ab) => {
            post({
              kind: "WS_BINARY",
              url,
              base64: toBase64(ab)
            });
          });
        }
      } catch (err) {
        post({ kind: "HOOK_ERROR", where: "ws.message", error: String(err) });
      }
    });

    const nativeSend = socket.send.bind(socket);
    socket.send = function patchedSend(data) {
      try {
        if (typeof data === "string") {
          post({ kind: "WS_OUT_TEXT", url, data });
        } else if (data instanceof ArrayBuffer) {
          post({ kind: "WS_OUT_BINARY", url, base64: toBase64(data) });
        }
      } catch {
        // passive telemetry only
      }
      return nativeSend(data);
    };

    return socket;
  };

  patchedWebSocket.prototype = NativeWebSocket.prototype;

  // Preserve constructor + static members so page checks like
  // `readyState === WebSocket.OPEN` keep working.
  try {
    Object.setPrototypeOf(patchedWebSocket, NativeWebSocket);
  } catch {
    // best effort only
  }

  ["CONNECTING", "OPEN", "CLOSING", "CLOSED"].forEach((name) => {
    try {
      if (Object.prototype.hasOwnProperty.call(NativeWebSocket, name)) {
        Object.defineProperty(patchedWebSocket, name, {
          value: NativeWebSocket[name],
          enumerable: true,
          configurable: true,
          writable: false
        });
      }
    } catch {
      // best effort only
    }
  });

  window.WebSocket = patchedWebSocket;

  // Listen for commands from content script (DIAL, DROP)
  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data) return;
    const msg = event.data;
    if (msg.channel !== channel || msg.source !== "3cx-datev-content") return;

    if (msg.payload?.kind === "DIAL" && msg.payload?.number) {
      const number = msg.payload.number;
      post({ kind: "DIAL_RECEIVED", number });
      triggerDial(number);
    }
  });

  // Auto-dial: use tel: link to show number in webclient, then auto-click Call button
  async function triggerDial(number) {
    // Step 1: Click tel: link (triggers webclient's dial UI with number pre-filled)
    try {
      const link = document.createElement("a");
      link.href = "tel:" + number;
      link.style.display = "none";
      document.body.appendChild(link);
      link.click();
      link.remove();
      post({ kind: "DIAL_TEL_CLICKED", number });
    } catch (err) {
      post({ kind: "DIAL_TEL_ERROR", error: String(err) });
      return;
    }

    // Step 2: Wait for the webclient UI to update, then find and click the Call button
    for (let attempt = 0; attempt < 5; attempt++) {
      await new Promise(r => setTimeout(r, 400));

      // Search overlays (Angular Material CDK dialogs/modals)
      const overlays = document.querySelectorAll(".cdk-overlay-pane, .modal, [role='dialog']");
      for (const overlay of overlays) {
        const btn = findCallButton(overlay);
        if (btn) {
          btn.click();
          post({ kind: "DIAL_AUTO_CLICKED", attempt, text: btn.textContent.trim() });
          return;
        }
      }

      // Search main page content
      const btn = findCallButton(document.body);
      if (btn) {
        btn.click();
        post({ kind: "DIAL_AUTO_CLICKED", attempt, text: btn.textContent.trim() });
        return;
      }
    }

    // Diagnostic: log what buttons exist so we can adjust selectors
    const allBtns = [...document.querySelectorAll("button")].filter(b => b.offsetParent !== null);
    post({
      kind: "DIAL_NO_BUTTON_FOUND",
      visibleButtons: allBtns.slice(0, 20).map(b => ({
        text: b.textContent.trim().substring(0, 40),
        classes: b.className.substring(0, 60),
        ariaLabel: b.getAttribute("aria-label") || ""
      }))
    });
  }

  function findCallButton(container) {
    // Match by aria-label, title, or text content (English + German)
    const buttons = container.querySelectorAll("button, a.btn, [role='button']");
    const callPatterns = /\b(call|dial|anrufen|anruf starten|make call)\b/i;
    for (const btn of buttons) {
      const label = btn.getAttribute("aria-label") || "";
      const title = btn.getAttribute("title") || "";
      const text = btn.textContent.trim();
      if (callPatterns.test(label) || callPatterns.test(title) || callPatterns.test(text)) {
        if (btn.offsetParent !== null && !btn.disabled) {
          return btn;
        }
      }
    }
    // Also try phone icon buttons (SVG with phone path)
    const iconBtns = container.querySelectorAll("button .mat-icon, button svg, button i.icon-phone");
    for (const icon of iconBtns) {
      const btn = icon.closest("button");
      if (btn && btn.offsetParent !== null && !btn.disabled) {
        const text = btn.textContent.trim().toLowerCase();
        // Skip buttons that are clearly NOT call buttons
        if (!text.includes("end") && !text.includes("hang") && !text.includes("auflegen")) {
          return btn;
        }
      }
    }
    return null;
  }

  post({
    kind: "HOOK_READY",
    note: "WebSocket hook active. MessageId=201 protobuf decoding runs in background.js."
  });
})();
