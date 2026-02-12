const DEFAULT_DIAL_DELAY = 750;

document.addEventListener("DOMContentLoaded", async () => {
  const dialDelayInput = document.getElementById("dialDelay");
  const extensionInput = document.getElementById("extensionNumber");
  const saveBtn = document.getElementById("saveBtn");
  const status = document.getElementById("status");

  // Load current values
  const cfg = await chrome.storage.local.get(["dialDelay", "extensionNumber", "lastProvision"]);
  dialDelayInput.value = cfg.dialDelay ?? DEFAULT_DIAL_DELAY;
  extensionInput.value = cfg.extensionNumber || cfg.lastProvision?.extension || "";

  saveBtn.addEventListener("click", async () => {
    const dialDelay = parseInt(dialDelayInput.value, 10);
    if (isNaN(dialDelay) || dialDelay < 0 || dialDelay > 5000) {
      showStatus("Dial delay must be 0\u20135000 ms", true);
      return;
    }

    await chrome.storage.local.set({ dialDelay });

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
