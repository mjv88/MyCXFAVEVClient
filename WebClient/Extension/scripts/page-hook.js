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

  // ===== WebSocket hook: intercept 3CX webclient messages =====

  const NativeWebSocket = window.WebSocket;
  const patchedWebSocket = function patchedWebSocket(url, protocols) {
    const socket = protocols ? new NativeWebSocket(url, protocols) : new NativeWebSocket(url);

    const is3cxWebclientSocket = typeof url === "string" && url.includes("/ws/webclient");
    if (!is3cxWebclientSocket) {
      return socket;
    }

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

    return socket;
  };

  patchedWebSocket.prototype = NativeWebSocket.prototype;

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

  // ===== DIAL command handler =====

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

  // Initiate a call via tel: link + Enter keypress
  async function triggerDial(number) {
    const cleanNumber = number.replace(/\s/g, "");
    post({ kind: "DIAL_STARTING", number: cleanNumber });

    // Create and click a tel: link - the 3CX PWA opens the dialer with the number
    const a = document.createElement("a");
    a.href = `tel:${cleanNumber}`;
    a.style.display = "none";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);

    post({ kind: "DIAL_TEL_LINK_CLICKED", number: cleanNumber });

    // Wait for the PWA to open the dialer / call confirmation dialog
    await new Promise(r => setTimeout(r, 650));

    // Simulate Enter keypress to confirm the call
    const enterEvent = new KeyboardEvent("keydown", {
      key: "Enter", code: "Enter", keyCode: 13, which: 13,
      bubbles: true, cancelable: true
    });
    document.activeElement?.dispatchEvent(enterEvent);
    document.dispatchEvent(enterEvent);

    post({ kind: "DIAL_TEL_ENTER_SENT", number: cleanNumber,
           activeElement: document.activeElement?.tagName || "none",
           activeClasses: (document.activeElement?.className || "").substring(0, 60) });
  }

  post({
    kind: "HOOK_READY",
    note: "WebSocket hook active. MessageId=201 protobuf decoding runs in background.js."
  });
})();
