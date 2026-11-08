const SKIPPED_SCHEMES = ["blob:", "data:", "file:", "chrome-extension:", "about:"];

export function shouldCapture(item, { enabled }) {
  if (!enabled) return false;
  if (item.incognito) return false;
  if (item.state === "complete") return false;
  const url = String(item.url ?? "").toLowerCase();
  return !SKIPPED_SCHEMES.some((scheme) => url.startsWith(scheme));
}

export class PendingTable {
  constructor({ setTimer, clearTimer, onExpire, probeTimeoutMs = 25000, store }) {
    this.setTimer = setTimer;
    this.clearTimer = clearTimer;
    this.onExpire = onExpire;
    this.probeTimeoutMs = probeTimeoutMs;
    this.store = store;
    this.entries = new Map();
  }

  add(id) {
    this.cancelTimer(id);
    const handle = this.setTimer(() => {
      if (this.entries.delete(id)) {
        this.persist();
        this.onExpire(id);
      }
    }, this.probeTimeoutMs);
    this.entries.set(id, handle);
    this.persist();
  }

  markAsking(id) {
    if (!this.entries.has(id)) return;
    this.cancelTimer(id);
    this.entries.set(id, null);
  }

  take(id) {
    if (!this.entries.has(id)) return false;
    this.cancelTimer(id);
    this.entries.delete(id);
    this.persist();
    return true;
  }

  expireAll() {
    const ids = [...this.entries.keys()];
    for (const id of ids) {
      this.cancelTimer(id);
      this.entries.delete(id);
    }
    this.persist();
    for (const id of ids) this.onExpire(id);
  }

  persist() {
    this.store?.save([...this.entries.keys()]);
  }

  cancelTimer(id) {
    const handle = this.entries.get(id);
    if (handle != null) this.clearTimer(handle);
  }
}

export async function resumeStored(store, resume) {
  const ids = await store.load();
  if (ids.length === 0) return;
  await store.save([]);
  for (const id of ids) await resume(id);
}

export function decideReply(type, wasPending, itemState) {
  if (type === "dang_hoi") return wasPending ? "markAsking" : "none";
  if (type === "tra_lai") return wasPending ? "resume" : "none";
  if (type === "huy") return wasPending ? "cancel" : "none";
  if (type === "nhan") return wasPending || itemState === "in_progress" ? "cancel" : "none";
  return "none";
}

function parseVersion(text) {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(String(text ?? "").trim());
  return match ? match.slice(1).map(Number) : null;
}

export function compareVersions(a, b) {
  const x = parseVersion(a);
  const y = parseVersion(b);
  if (!x || !y) return 0;
  for (let i = 0; i < 3; i++) {
    if (x[i] !== y[i]) return x[i] > y[i] ? 1 : -1;
  }
  return 0;
}

// DoMa vừa được cập nhật (bộ cài đã ghi đè thư mục tiện ích) nhưng trình duyệt vẫn chạy bản cũ:
// nạp lại một lần cho mỗi phiên bản để tránh vòng lặp khi tiện ích được nạp từ nơi khác.
export function shouldReload(hostVersion, extensionVersion, lastReloadFor) {
  return compareVersions(hostVersion, extensionVersion) > 0 && lastReloadFor !== hostVersion;
}

export function afterPause(item) {
  return !item || item.state === "complete" ? "skip" : "proceed";
}

function basename(path) {
  const text = String(path ?? "");
  return text.slice(Math.max(text.lastIndexOf("\\"), text.lastIndexOf("/")) + 1);
}

// ── Bắt link bằng menu chuột phải (link, ảnh, video, âm thanh) ──
// Lượt bắt này không có lượt tải nào của trình duyệt đi kèm, nên dùng id dạng "m<số>" để phân biệt.
const MANUAL_PREFIX = "m";

export function manualId(counter) {
  return `${MANUAL_PREFIX}${counter}`;
}

export function isManualId(id) {
  return String(id ?? "").startsWith(MANUAL_PREFIX);
}

/** Link mà menu chuột phải được bấm vào: ưu tiên link, rồi tới nguồn ảnh/video/âm thanh. */
export function contextUrl(info) {
  for (const candidate of [info?.linkUrl, info?.srcUrl]) {
    const url = String(candidate ?? "");
    if (/^https?:\/\//i.test(url)) return url;
  }
  return null;
}

export function toManualCaptureMessage(id, url, pageUrl, cookies, userAgent) {
  return {
    type: "bat",
    id,
    url,
    finalUrl: null,
    referrer: /^https?:\/\//i.test(String(pageUrl ?? "")) ? pageUrl : null,
    filename: "",
    mime: null,
    fileSize: null,
    cookies,
    userAgent,
  };
}

/** DoMa trả lời một lượt bắt thủ công: "fallback" = để trình duyệt tự tải link đó. */
export function decideManualReply(type) {
  if (type === "dang_hoi") return "markAsking";
  if (type === "tra_lai") return "fallback";
  return "done";
}

// ── "Tải tất cả link bằng DoMa" ──
export const MAX_BATCH_LINKS = 1000;

/**
 * Chạy trong trang (chrome.scripting.executeScript): lấy mọi link và nguồn video/âm thanh.
 * Nếu đang bôi đen một đoạn thì chỉ lấy link nằm trong đoạn đó. Hàm phải tự đủ, không dùng biến ngoài.
 */
export function collectPageLinks() {
  const selection = window.getSelection();
  const onlySelected = selection && !selection.isCollapsed && selection.rangeCount > 0;
  const links = [];
  for (const a of document.querySelectorAll("a[href]")) {
    if (onlySelected && !selection.containsNode(a, true)) continue;
    links.push({ url: a.href, text: (a.textContent || a.title || "").trim().replace(/\s+/g, " ").slice(0, 120) });
  }
  if (!onlySelected) {
    for (const media of document.querySelectorAll("video[src], audio[src], source[src]")) {
      links.push({ url: media.src, text: "" });
    }
  }
  return links.slice(0, 3000);
}

/** Giữ link http(s), bỏ trùng (không tính phần #), tối đa MAX_BATCH_LINKS. */
export function prepareBatch(links) {
  const seen = new Set();
  const result = [];
  for (const link of links ?? []) {
    const raw = String(link?.url ?? "");
    if (!/^https?:\/\//i.test(raw)) continue;
    const url = raw.split("#")[0];
    if (seen.has(url)) continue;
    seen.add(url);
    result.push({ url, text: String(link?.text ?? "") });
    if (result.length >= MAX_BATCH_LINKS) break;
  }
  return result;
}

/** Mỗi tên miền một link đại diện, để lấy cookie một lần cho cả tên miền (tối đa 50 tên miền). */
export function hostsOf(links, limit = 50) {
  const hosts = new Map();
  for (const { url } of links) {
    try {
      const host = new URL(url).host;
      if (!hosts.has(host)) hosts.set(host, url);
    } catch {
    }
    if (hosts.size >= limit) break;
  }
  return hosts;
}

export function toBatchMessage(id, pageUrl, links, cookiesByHost, userAgent) {
  return {
    type: "bat_nhieu",
    id,
    referrer: /^https?:\/\//i.test(String(pageUrl ?? "")) ? pageUrl : null,
    links,
    cookies: cookiesByHost,
    userAgent,
  };
}

export function toCaptureMessage(item, cookies, userAgent) {
  return {
    type: "bat",
    id: String(item.id),
    url: item.url,
    finalUrl: item.finalUrl || null,
    referrer: item.referrer,
    filename: basename(item.filename),
    mime: item.mime,
    fileSize: item.fileSize > 0 ? item.fileSize : null,
    cookies,
    userAgent,
  };
}
