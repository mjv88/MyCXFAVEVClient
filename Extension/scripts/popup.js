const DEFAULT_DIAL_DELAY = 750;

document.addEventListener("DOMContentLoaded", async () => {
  const dialDelayInput = document.getElementById("dialDelay");
  const saveBtn = document.getElementById("saveBtn");
  const reloadBtn = document.getElementById("reloadBtn");
  const statusDot = document.getElementById("statusDot");
  const statusText = document.getElementById("statusText");
  const extLabel = document.getElementById("extLabel");

  // Query connection status from background
  chrome.runtime.sendMessage({ type: "GET_STATUS" }, (resp) => {
    if (!resp) return;
    const { wsState, helloAcked, extension } = resp;

    if (wsState === 1 && helloAcked) {
      statusDot.style.background = "#28A745";
      statusText.textContent = "Connected";
    } else if (wsState === 0 || (wsState === 1 && !helloAcked)) {
      statusDot.style.background = "#FFC107";
      statusText.textContent = "Connecting\u2026";
    } else {
      statusDot.style.background = "#DC3545";
      statusText.textContent = "Disconnected";
    }

    extLabel.textContent = extension || "\u2014";
  });

  // Load dial delay from storage
  const cfg = await chrome.storage.local.get(["dialDelay"]);
  dialDelayInput.value = cfg.dialDelay ?? DEFAULT_DIAL_DELAY;

  // Save button
  saveBtn.addEventListener("click", async () => {
    const dialDelay = parseInt(dialDelayInput.value, 10);
    if (isNaN(dialDelay) || dialDelay < 0 || dialDelay > 5000) return;

    await chrome.storage.local.set({ dialDelay });

    saveBtn.style.background = "#28A745";
    saveBtn.textContent = "Saved!";
    setTimeout(() => {
      saveBtn.style.background = "";
      saveBtn.textContent = "Save";
    }, 1200);
  });

  // Reload button
  reloadBtn.addEventListener("click", () => {
    chrome.runtime.reload();
  });
});
