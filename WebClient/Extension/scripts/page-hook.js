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

  // TEMP: Intercept XHR to capture MakeCall protobuf format (binary as base64)
  let lastMakeCallBody = null; // Store last MakeCall binary for replay
  let lastMakeCallContentType = null;

  const NativeXHROpen = XMLHttpRequest.prototype.open;
  const NativeXHRSend = XMLHttpRequest.prototype.send;
  const NativeSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;

  XMLHttpRequest.prototype.open = function(method, url, ...args) {
    this.__3cxUrl = url;
    this.__3cxMethod = method;
    return NativeXHROpen.call(this, method, url, ...args);
  };
  XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
    if (this.__3cxUrl && String(this.__3cxUrl).includes("MPWebService")) {
      if (name.toLowerCase() === "content-type") this.__3cxCT = value;
    }
    return NativeSetRequestHeader.call(this, name, value);
  };
  XMLHttpRequest.prototype.send = function(body) {
    if (this.__3cxUrl && String(this.__3cxUrl).includes("MPWebService")) {
      const captureBody = (ab) => {
        const b64 = toBase64(ab);
        post({ kind: "XHR_MAKECALL_OUT", method: this.__3cxMethod,
               url: this.__3cxUrl, base64: b64, size: ab.byteLength,
               contentType: this.__3cxCT || "" });
        lastMakeCallBody = new Uint8Array(ab);
        lastMakeCallContentType = this.__3cxCT || "";
      };
      if (body instanceof ArrayBuffer) {
        captureBody(body);
      } else if (body instanceof Uint8Array) {
        captureBody(body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength));
      } else if (body instanceof Blob) {
        body.arrayBuffer().then(captureBody);
      } else if (body != null) {
        // Text body (shouldn't happen for protobuf, but capture anyway)
        post({ kind: "XHR_MAKECALL_OUT_TEXT", url: this.__3cxUrl,
               body: String(body).substring(0, 500) });
      }
      // Capture response
      this.addEventListener("load", function() {
        try {
          const resp = this.response;
          if (resp instanceof ArrayBuffer) {
            post({ kind: "XHR_MAKECALL_IN", url: this.__3cxUrl,
                   status: this.status, base64: toBase64(resp), size: resp.byteLength });
          }
        } catch {}
      });
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
      triggerDial(number);
    }
  });

  // Auto-dial: try multiple approaches to initiate a call
  // NOTE: Do NOT use tel: link - it immediately calls the WRONG number (last dialed)
  async function triggerDial(number) {
    post({ kind: "DIAL_STARTING", number });

    // Get extension from localStorage
    let extension = "";
    try {
      const prov = JSON.parse(localStorage.getItem("wc.provision"));
      extension = prov?.username || "";
    } catch {}

    // Approach 1: Build protobuf MakeCall and POST to MPWebService.asmx
    // This is the same mechanism the webclient uses for manual dials
    const protobufOk = await tryProtobufMakeCall(extension, number);
    if (protobufOk) return;

    // Approach 2: DOM-based - find dialer input, set number, click Call
    let inputSet = await setDialerNumber(number);
    if (!inputSet) {
      const opened = await openDialerPanel();
      if (opened) {
        await new Promise(r => setTimeout(r, 500));
        inputSet = await setDialerNumber(number);
      }
    }
    if (inputSet) {
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
    }

    // All approaches failed - dump diagnostics
    dumpDialerDiagnostics(number);
  }

  // Construct and send a MakeCall protobuf to MPWebService.asmx
  // Based on captured traffic: POST with binary protobuf body containing extension + number
  async function tryProtobufMakeCall(extension, number) {
    // Build the protobuf binary
    // From traffic analysis: the request uses a nested message structure
    // We try the captured template first, then construct our own
    const dialString = number.startsWith("+") ? number : "+" + number;

    if (lastMakeCallBody) {
      // Replay approach: replace the number in the captured template
      const modified = replaceNumberInProtobuf(lastMakeCallBody, dialString);
      if (modified) {
        post({ kind: "DIAL_PROTOBUF_REPLAY", number: dialString, size: modified.byteLength });
        try {
          const resp = await NativeFetch.call(window, "/MyPhone/MPWebService.asmx", {
            method: "POST",
            body: modified,
            credentials: "include",
            headers: lastMakeCallContentType
              ? { "Content-Type": lastMakeCallContentType }
              : {}
          });
          if (resp.ok) {
            post({ kind: "DIAL_PROTOBUF_OK", status: resp.status, approach: "replay" });
            return true;
          }
          post({ kind: "DIAL_PROTOBUF_FAIL", status: resp.status, approach: "replay" });
        } catch (err) {
          post({ kind: "DIAL_PROTOBUF_ERROR", error: String(err), approach: "replay" });
        }
      }
    }

    // Construct approach: build protobuf from scratch
    // Encoding: field 13 (LEN) wrapping inner message with extension + dial string
    const innerFields = [
      encodeProtobufString(1, extension),   // field 1: extension "000"
      encodeProtobufVarint(2, 1),           // field 2: call type (1 = outgoing?)
      encodeProtobufString(5, dialString),  // field 5: destination number
    ];
    const inner = concatBytes(innerFields);
    const outer = encodeProtobufBytes(13, inner); // field 13: wrapper

    post({ kind: "DIAL_PROTOBUF_CONSTRUCT", number: dialString,
           size: outer.byteLength, hex: bytesToHex(outer) });

    try {
      const resp = await NativeFetch.call(window, "/MyPhone/MPWebService.asmx", {
        method: "POST",
        body: outer,
        credentials: "include",
        headers: { "Content-Type": "application/octet-stream" }
      });
      const respBody = await resp.arrayBuffer();
      post({ kind: "DIAL_PROTOBUF_RESULT", status: resp.status, ok: resp.ok,
             responseBase64: toBase64(respBody), responseSize: respBody.byteLength });
      if (resp.ok) return true;
    } catch (err) {
      post({ kind: "DIAL_PROTOBUF_ERROR", error: String(err), approach: "construct" });
    }

    return false;
  }

  // Protobuf encoding helpers
  function encodeVarint(value) {
    const bytes = [];
    while (value > 0x7F) {
      bytes.push((value & 0x7F) | 0x80);
      value >>>= 7;
    }
    bytes.push(value & 0x7F);
    return new Uint8Array(bytes);
  }

  function encodeProtobufString(fieldNum, str) {
    const tag = encodeVarint((fieldNum << 3) | 2); // wire type 2 = LEN
    const data = new TextEncoder().encode(str);
    const len = encodeVarint(data.length);
    return concatBytes([tag, len, data]);
  }

  function encodeProtobufVarint(fieldNum, value) {
    const tag = encodeVarint((fieldNum << 3) | 0); // wire type 0 = VARINT
    const val = encodeVarint(value);
    return concatBytes([tag, val]);
  }

  function encodeProtobufBytes(fieldNum, data) {
    const tag = encodeVarint((fieldNum << 3) | 2); // wire type 2 = LEN
    const len = encodeVarint(data.length);
    return concatBytes([tag, len, data]);
  }

  function concatBytes(arrays) {
    const total = arrays.reduce((sum, a) => sum + a.byteLength, 0);
    const result = new Uint8Array(total);
    let offset = 0;
    for (const a of arrays) {
      result.set(new Uint8Array(a.buffer || a, a.byteOffset || 0, a.byteLength), offset);
      offset += a.byteLength;
    }
    return result;
  }

  function bytesToHex(bytes) {
    return [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, "0")).join("");
  }

  // Replace a phone number in captured protobuf binary
  // Searches for known number patterns and substitutes
  function replaceNumberInProtobuf(originalBody, newNumber) {
    // Find phone number patterns in the binary (UTF-8 encoded)
    // Look for strings starting with + or * followed by digits
    const bytes = new Uint8Array(originalBody);
    const str = new TextDecoder().decode(bytes);

    // Find phone numbers: +DIGITS or *+DIGITS or *DIGITS (at least 5 digits)
    const phonePattern = /[\*\+]?\+?\d{5,}/g;
    const matches = [...str.matchAll(phonePattern)];

    if (matches.length === 0) {
      post({ kind: "DIAL_REPLAY_NO_NUMBER", bodyHex: bytesToHex(bytes) });
      return null;
    }

    // Find the longest match (most likely the dial number)
    const best = matches.reduce((a, b) => a[0].length >= b[0].length ? a : b);
    const oldNumber = best[0];
    const oldBytes = new TextEncoder().encode(oldNumber);
    const newBytes = new TextEncoder().encode(newNumber);

    post({ kind: "DIAL_REPLAY_REPLACING", oldNumber, newNumber,
           sameLength: oldBytes.length === newBytes.length });

    if (oldBytes.length === newBytes.length) {
      // Same length: simple byte replacement (no length field changes needed)
      const result = new Uint8Array(bytes);
      const idx = bytes.indexOf(oldBytes[0]);
      // Find exact match position
      for (let i = 0; i <= bytes.length - oldBytes.length; i++) {
        let match = true;
        for (let j = 0; j < oldBytes.length; j++) {
          if (bytes[i + j] !== oldBytes[j]) { match = false; break; }
        }
        if (match) {
          result.set(newBytes, i);
          // Don't break - replace all occurrences
        }
      }
      return result;
    }

    // Different length: too complex for simple replay, skip
    post({ kind: "DIAL_REPLAY_LENGTH_MISMATCH" });
    return null;
  }

  // Try to open the dialer/keypad panel in the webclient
  async function openDialerPanel() {
    // Strategy 1: Find a navigation link/button that opens the dialer
    // 3CX webclient v20 uses Angular Material with mat-icons
    const dialpadSelectors = [
      // Icon-based buttons
      'mat-icon[fonticon="dialpad"]',
      '.mat-icon[fonticon="dialpad"]',
      'button mat-icon[fonticon="dialpad"]',
      'a mat-icon[fonticon="dialpad"]',
      // Text/aria based
      '[aria-label*="dialpad" i]',
      '[aria-label*="keypad" i]',
      '[aria-label*="Wähltastatur" i]',
      '[aria-label*="Tastatur" i]',
      '[aria-label*="Ziffernblock" i]',
      '[title*="dialpad" i]',
      '[title*="keypad" i]',
      // Common class patterns
      '.dialpad-btn',
      '.keypad-btn',
      '[routerlink*="dial"]',
      'a[href*="dial"]',
      'a[href*="keypad"]'
    ];

    for (const sel of dialpadSelectors) {
      const el = document.querySelector(sel);
      if (el) {
        const clickTarget = el.closest("button") || el.closest("a") || el;
        clickTarget.click();
        post({ kind: "DIAL_PANEL_OPENED", selector: sel });
        return true;
      }
    }

    // Strategy 2: Look for mat-icon elements containing "dialpad" text
    const matIcons = document.querySelectorAll("mat-icon, .mat-icon");
    for (const icon of matIcons) {
      const text = (icon.textContent || "").trim().toLowerCase();
      const fontIcon = icon.getAttribute("fonticon") || "";
      if (text === "dialpad" || fontIcon === "dialpad" ||
          text === "call" || text === "phone") {
        const clickTarget = icon.closest("button") || icon.closest("a") || icon;
        if (clickTarget.offsetParent !== null) {
          clickTarget.click();
          post({ kind: "DIAL_PANEL_OPENED", via: "mat-icon:" + (text || fontIcon) });
          return true;
        }
      }
    }

    // Strategy 3: Navigate via hash route (some webclient versions use hash routing)
    // Don't navigate blindly - just report that we couldn't find the panel
    post({ kind: "DIAL_PANEL_NOT_FOUND" });
    return false;
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
        if (!text.includes("end") && !text.includes("hang") && !text.includes("auflegen")) {
          return btn;
        }
      }
    }
    return null;
  }

  // Comprehensive diagnostic dump - helps us see the webclient's actual DOM structure
  function dumpDialerDiagnostics(number) {
    // All inputs on the page
    const inputs = [...document.querySelectorAll("input, textarea")].map(i => ({
      tag: i.tagName, type: i.type, value: i.value.substring(0, 30),
      placeholder: i.placeholder || "", classes: i.className.substring(0, 80),
      visible: i.offsetParent !== null, disabled: i.disabled,
      readOnly: i.readOnly, width: i.offsetWidth, id: i.id || ""
    }));

    // CDK overlay panes
    const overlays = [...document.querySelectorAll(".cdk-overlay-pane")].map(o => ({
      classes: o.className.substring(0, 80),
      childCount: o.children.length,
      innerHTML: o.innerHTML.substring(0, 500)
    }));

    // All visible buttons
    const allBtns = [...document.querySelectorAll("button, [role='button']")]
      .filter(b => b.offsetParent !== null);
    const buttons = allBtns.slice(0, 30).map(b => ({
      text: b.textContent.trim().substring(0, 50),
      classes: b.className.substring(0, 80),
      ariaLabel: b.getAttribute("aria-label") || "",
      title: b.getAttribute("title") || "",
      id: b.id || ""
    }));

    // All mat-icon elements (Angular Material icons)
    const matIcons = [...document.querySelectorAll("mat-icon, .mat-icon")].map(i => ({
      text: i.textContent.trim().substring(0, 30),
      fontIcon: i.getAttribute("fonticon") || "",
      classes: i.className.substring(0, 60),
      visible: i.offsetParent !== null,
      parentTag: i.parentElement?.tagName || "",
      parentAriaLabel: i.parentElement?.getAttribute("aria-label") || ""
    }));

    // Navigation links
    const navLinks = [...document.querySelectorAll("a[href], a[routerlink], [routerlink]")].map(a => ({
      href: (a.getAttribute("href") || "").substring(0, 60),
      routerLink: a.getAttribute("routerlink") || "",
      text: a.textContent.trim().substring(0, 30),
      ariaLabel: a.getAttribute("aria-label") || "",
      visible: a.offsetParent !== null
    }));

    // Angular internals check
    const angular = {
      hasAppRoot: !!document.querySelector("app-root"),
      hasNgVersion: !!document.querySelector("[ng-version]"),
      ngVersion: document.querySelector("[ng-version]")?.getAttribute("ng-version") || "",
      hasNgDebug: typeof window.ng !== "undefined",
    };

    // Check for phone/SIP globals
    const phoneGlobals = Object.keys(window).filter(k =>
      /phone|sip|call|oCall|oLine|oSession|oUA/i.test(k) && k.length < 30
    ).slice(0, 10);

    post({
      kind: "DIAL_DIAGNOSTICS",
      number,
      inputs: inputs.slice(0, 15),
      overlays: overlays.slice(0, 5),
      buttons,
      matIcons: matIcons.slice(0, 20),
      navLinks: navLinks.filter(l => l.visible).slice(0, 15),
      angular,
      phoneGlobals,
      url: window.location.href.substring(0, 100),
      hash: window.location.hash
    });
  }

  post({
    kind: "HOOK_READY",
    note: "WebSocket hook active. MessageId=201 protobuf decoding runs in background.js."
  });
})();
