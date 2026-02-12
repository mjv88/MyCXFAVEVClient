const DEFAULTS = {
  dialDelay: 650,
  extensionNumber: "",
  bridgePort: 19800,
  debugLogging: false
};

document.addEventListener("DOMContentLoaded", async () => {
  const fields = {
    dialDelay: document.getElementById("dialDelay"),
    extensionNumber: document.getElementById("extensionNumber"),
    bridgePort: document.getElementById("bridgePort"),
    debugLogging: document.getElementById("debugLogging")
  };
  const saveBtn = document.getElementById("saveBtn");
  const status = document.getElementById("status");

  // Load current values
  const cfg = await chrome.storage.local.get(Object.keys(DEFAULTS));
  fields.dialDelay.value = cfg.dialDelay ?? DEFAULTS.dialDelay;
  fields.extensionNumber.value = cfg.extensionNumber ?? DEFAULTS.extensionNumber;
  fields.bridgePort.value = cfg.bridgePort ?? DEFAULTS.bridgePort;
  fields.debugLogging.checked = !!cfg.debugLogging;

  saveBtn.addEventListener("click", async () => {
    const dialDelay = parseInt(fields.dialDelay.value, 10);
    if (isNaN(dialDelay) || dialDelay < 0 || dialDelay > 5000) {
      showStatus("Dial delay must be 0\u20135000 ms", true);
      return;
    }

    const bridgePort = parseInt(fields.bridgePort.value, 10);
    if (isNaN(bridgePort) || bridgePort < 1 || bridgePort > 65535) {
      showStatus("Port must be 1\u201365535", true);
      return;
    }

    await chrome.storage.local.set({
      dialDelay,
      extensionNumber: fields.extensionNumber.value.trim(),
      bridgePort,
      debugLogging: fields.debugLogging.checked
    });

    showStatus("Settings saved");
  });

  function showStatus(message, isError) {
    status.textContent = message;
    status.style.color = isError ? "#d93025" : "#34a853";
    if (!isError) {
      setTimeout(() => { status.textContent = ""; }, 2000);
    }
  }
});
