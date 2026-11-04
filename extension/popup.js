const status = document.getElementById("status");
const toggle = document.getElementById("enabled");

function showStatus(connected) {
  status.textContent = connected ? "Đã kết nối với DoMa" : "Chưa kết nối với DoMa";
  status.className = connected ? "on" : "off";
}

chrome.storage.local.get({ enabled: true }).then(({ enabled }) => {
  toggle.checked = enabled;
});

toggle.addEventListener("change", () => {
  chrome.storage.local.set({ enabled: toggle.checked });
});

chrome.runtime.sendMessage({ type: "status" }).then(
  (reply) => showStatus(reply?.connected === true),
  () => showStatus(false),
);
