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
