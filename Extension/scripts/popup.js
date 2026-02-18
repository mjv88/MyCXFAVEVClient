const DEFAULT_DIAL_DELAY = 750;

function refreshStatus() {
  const statusDot = document.getElementById("statusDot");
  const statusText = document.getElementById("statusText");
  const extLabel = document.getElementById("extLabel");

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
}

document.addEventListener("DOMContentLoaded", async () => {
  const dialDelayInput = document.getElementById("dialDelay");
  const saveBtn = document.getElementById("saveBtn");
  const testBtn = document.getElementById("testBtn");
  const reloadBtn = document.getElementById("reloadBtn");

  refreshStatus();

  const cfg = await chrome.storage.local.get(["dialDelay"]);
  dialDelayInput.value = cfg.dialDelay ?? DEFAULT_DIAL_DELAY;

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

  testBtn.addEventListener("click", () => {
    testBtn.style.background = "#FFC107";
    testBtn.style.color = "#000";
    testBtn.textContent = "Testing\u2026";

    chrome.runtime.sendMessage({ type: "TEST_WEBCLIENT" }, (resp) => {
      if (resp && resp.found) {
        testBtn.style.background = "#28A745";
        testBtn.style.color = "#FFFFFF";
        testBtn.textContent = "Found!";
      } else {
        testBtn.style.background = "#DC3545";
        testBtn.style.color = "#FFFFFF";
        testBtn.textContent = "Not found";
      }

      setTimeout(() => {
        testBtn.style.background = "";
        testBtn.style.color = "";
        testBtn.textContent = "Test";
        refreshStatus();
      }, 1200);
    });
  });

  reloadBtn.addEventListener("click", () => {
    chrome.runtime.sendMessage({ type: "RELOAD_EXTENSION" }, () => {
      reloadBtn.style.background = "#28A745";
      reloadBtn.style.color = "#FFFFFF";
      reloadBtn.textContent = "Reloaded!";

      setTimeout(() => {
        reloadBtn.style.background = "";
        reloadBtn.style.color = "";
        reloadBtn.textContent = "Reload";
        refreshStatus();
      }, 1200);
    });
  });
});
