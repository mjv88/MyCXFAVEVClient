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

  // TEMP: Intercept XHR to capture SOAP MakeCall format from manual dials
  const NativeXHROpen = XMLHttpRequest.prototype.open;
  const NativeXHRSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(method, url, ...args) {
    this.__3cxUrl = url;
    return NativeXHROpen.call(this, method, url, ...args);
  };
  XMLHttpRequest.prototype.send = function(body) {
    if (this.__3cxUrl && String(this.__3cxUrl).includes("MPWebService")) {
      post({ kind: "XHR_SOAP_OUT", url: this.__3cxUrl, body: String(body).substring(0, 1000) });
    }
    return NativeXHRSend.call(this, body);
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
      dialViaSoap(number);
    }
  });

  // Auto-dial via 3CX SOAP API (MPWebService.asmx)
  async function dialViaSoap(number) {
    const baseUrl = window.location.origin;
    const url = baseUrl + "/MyPhone/MPWebService.asmx";

    const soapBody = `<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
               xmlns:mp="http://www.3cx.com/MPWebService/">
  <soap:Body>
    <mp:MakeCallTo>
      <mp:to>${number}</mp:to>
    </mp:MakeCallTo>
  </soap:Body>
</soap:Envelope>`;

    try {
      const resp = await fetch(url, {
        method: "POST",
        headers: {
          "Content-Type": "text/xml; charset=utf-8",
          "SOAPAction": "http://www.3cx.com/MPWebService/MakeCallTo"
        },
        body: soapBody,
        credentials: "include"
      });

      const text = await resp.text();
      post({
        kind: "DIAL_SOAP_RESULT",
        status: resp.status,
        ok: resp.ok,
        body: text.substring(0, 500)
      });
    } catch (err) {
      post({ kind: "DIAL_SOAP_ERROR", error: String(err) });
    }
  }

  post({
    kind: "HOOK_READY",
    note: "WebSocket hook active. MessageId=201 protobuf decoding runs in background.js."
  });
})();
