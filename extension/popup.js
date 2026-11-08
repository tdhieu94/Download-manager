import { t } from "./capture.js";

const status = document.getElementById("status");
const toggle = document.getElementById("enabled");
const list = document.getElementById("media");
const empty = document.getElementById("empty");

// Chữ cố định trong popup.html (viết sẵn tiếng Việt).
status.textContent = t("Đang kiểm tra…", "Checking…");
toggle.nextSibling.textContent = " " + t("Bắt link", "Catch links");
document.querySelector("h2").textContent = t("Video và âm thanh trên trang này", "Video and audio on this page");
empty.textContent = t("Chưa thấy video nào. Hãy bấm phát video trên trang rồi mở lại.", "No videos found yet. Play the video on the page, then open this again.");

function showStatus(connected) {
  status.textContent = connected ? t("Đã kết nối với DoMa", "Connected to DoMa") : t("Chưa kết nối với DoMa", "Not connected to DoMa");
  status.className = connected ? "on" : "off";
}

function formatSize(bytes) {
  if (!bytes) return "";
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1).replace(".", ",")} ${units[unit]}`;
}

function describe(entry) {
  const host = (() => {
    try {
      return new URL(entry.url).hostname;
    } catch {
      return "";
    }
  })();
  if (entry.kind === "youtube") return t("YouTube · chọn chất lượng trong DoMa", "YouTube · choose quality in DoMa");
  if (entry.kind === "page") return `${entry.site ?? "Video"} · ${t("chọn chất lượng trong DoMa", "choose quality in DoMa")}`;
  if (entry.kind === "playlist") return t("Playlist YouTube · chọn video trong DoMa", "YouTube playlist · choose videos in DoMa");
  const kind = entry.kind === "hls" ? t("Luồng HLS", "HLS stream") : entry.name.split(".").pop().toUpperCase();
  return [kind, formatSize(entry.size), host].filter(Boolean).join(" · ");
}

function render(tabId, entries) {
  list.replaceChildren();
  empty.hidden = entries.length > 0;
  for (const entry of entries) {
    const item = document.createElement("li");
    const info = document.createElement("div");
    info.className = "info";
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = entry.name;
    name.title = entry.url;
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = describe(entry);
    info.append(name, meta);
    const button = document.createElement("button");
    button.textContent = t("Tải", "Download");
    button.addEventListener("click", async () => {
      button.disabled = true;
      button.textContent = t("Đang gửi…", "Sending…");
      const reply = await chrome.runtime.sendMessage({ type: "media-download", tabId, url: entry.url }).catch(() => null);
      button.textContent = reply?.ok ? t("Đã gửi", "Sent") : t("Lỗi", "Error");
      if (!reply?.ok) {
        meta.textContent = t("Không gửi được sang DoMa. Hãy mở DoMa rồi thử lại.", "Couldn't send to DoMa. Open DoMa and try again.");
        button.disabled = false;
      }
    });
    item.append(info, button);
    list.append(item);
  }
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

chrome.tabs.query({ active: true, currentWindow: true }).then(async ([tab]) => {
  if (tab?.id == null) return render(null, []);
  const entries = await chrome.runtime.sendMessage({ type: "media-list", tabId: tab.id }).catch(() => []);
  render(tab.id, entries ?? []);
});
