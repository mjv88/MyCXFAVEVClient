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

  // Auto-dial: open dialer via tel: link, populate the number input, then click Call
  async function triggerDial(number) {
    // Step 1: Click tel: link to open the webclient's dialer UI
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

    // Step 2: Wait for the dialer UI to render
    await new Promise(r => setTimeout(r, 500));

    // Step 3: Find the dialer input and set the correct number
    const inputSet = await setDialerNumber(number);
    if (!inputSet) {
      // Diagnostic: dump what the dialer looks like
      dumpDialerDiagnostics("after tel: link");
      return;
    }

    // Step 4: Wait for Angular to process the input change, then click Call
    await new Promise(r => setTimeout(r, 300));

    for (let attempt = 0; attempt < 5; attempt++) {
      const btn = findCallButton(document.body);
      if (btn) {
        btn.click();
        post({ kind: "DIAL_AUTO_CLICKED", attempt, number, text: btn.textContent.trim() });
        return;
      }
      await new Promise(r => setTimeout(r, 300));
    }

    dumpDialerDiagnostics("after input set, no Call button");
  }

  // Find the dialer's number input and populate it with the phone number
  async function setDialerNumber(number) {
    // Search in overlays first (Angular Material CDK), then full page
    const containers = [
      ...document.querySelectorAll(".cdk-overlay-pane, .modal, [role='dialog']"),
      document.body
    ];

    for (const container of containers) {
      // Strategy 1: Find <input> elements (type=text, type=tel, type=search, or no type)
      const inputs = container.querySelectorAll(
        "input[type='tel'], input[type='text'], input[type='search'], input:not([type]), input[type='number']"
      );
      for (const input of inputs) {
        if (input.offsetParent === null || input.disabled || input.readOnly) continue;
        // Skip tiny/hidden inputs
        if (input.offsetWidth < 30) continue;

        // Set the value using native setter to bypass Angular's getter/setter
        const nativeInputValueSetter = Object.getOwnPropertyDescriptor(
          HTMLInputElement.prototype, "value"
        ).set;
        nativeInputValueSetter.call(input, number);

        // Dispatch events Angular listens for
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
        input.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true }));
        input.focus();

        post({ kind: "DIAL_INPUT_SET", number, inputTag: input.tagName,
               inputType: input.type, classes: input.className.substring(0, 60),
               placeholder: input.placeholder || "" });
        return true;
      }

      // Strategy 2: Find contenteditable elements used as inputs
      const editables = container.querySelectorAll("[contenteditable='true']");
      for (const el of editables) {
        if (el.offsetParent === null) continue;
        el.textContent = number;
        el.dispatchEvent(new Event("input", { bubbles: true }));
        post({ kind: "DIAL_INPUT_SET", number, inputTag: el.tagName,
               classes: el.className.substring(0, 60), note: "contenteditable" });
        return true;
      }
    }

    post({ kind: "DIAL_NO_INPUT_FOUND", number });
    return false;
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
    // Also try phone icon buttons (mat-icon with phone-related content)
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

  // Diagnostic dump of the dialer area - helps us adjust selectors
  function dumpDialerDiagnostics(context) {
    const inputs = [...document.querySelectorAll("input")].map(i => ({
      type: i.type, value: i.value.substring(0, 30),
      placeholder: i.placeholder || "", classes: i.className.substring(0, 60),
      visible: i.offsetParent !== null, disabled: i.disabled,
      readOnly: i.readOnly, width: i.offsetWidth
    }));
    const overlays = [...document.querySelectorAll(".cdk-overlay-pane")].map(o => ({
      classes: o.className.substring(0, 80),
      childCount: o.children.length,
      innerHtml: o.innerHTML.substring(0, 300)
    }));
    const allBtns = [...document.querySelectorAll("button")].filter(b => b.offsetParent !== null);
    post({
      kind: "DIAL_DIAGNOSTICS",
      context,
      inputs: inputs.slice(0, 15),
      overlays: overlays.slice(0, 5),
      visibleButtons: allBtns.slice(0, 20).map(b => ({
        text: b.textContent.trim().substring(0, 40),
        classes: b.className.substring(0, 60),
        ariaLabel: b.getAttribute("aria-label") || ""
      }))
    });
  }

  post({
    kind: "HOOK_READY",
    note: "WebSocket hook active. MessageId=201 protobuf decoding runs in background.js."
  });
})();
