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

  post({
    kind: "HOOK_READY",
    note: "WebSocket hook active. Protobuf MessageId=201 decoding is handled in background worker."
  });
})();
