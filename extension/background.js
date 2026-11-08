import {
  shouldCapture, PendingTable, toCaptureMessage, resumeStored, decideReply, afterPause, shouldReload,
  manualId, isManualId, contextUrl, toManualCaptureMessage, decideManualReply,
} from "./capture.js";

const HOST_NAME = "com.doma.host";
const PONG_TIMEOUT_MS = 6000;

let port = null;
let connecting = null;
let capturing = 0;
let reloadFor = null;

async function checkHostVersion(hostVersion) {
  const { reloadedFor } = await chrome.storage.local.get({ reloadedFor: null });
  if (shouldReload(hostVersion, chrome.runtime.getManifest().version, reloadedFor)) {
    reloadFor = hostVersion;
    maybeReload();
  }
}

// Chỉ nạp lại khi không có lượt tải nào đang chuyển cho DoMa, để không làm rơi lượt tải.
async function maybeReload() {
  if (!reloadFor || capturing > 0 || pending.entries.size > 0) return;
  const version = reloadFor;
  reloadFor = null;
  await chrome.storage.local.set({ reloadedFor: version });
  chrome.runtime.reload();
}

const pendingStore = {
  save: (ids) => chrome.storage.session.set({ pendingIds: ids }),
  load: async () => (await chrome.storage.session.get({ pendingIds: [] })).pendingIds,
};

const pending = new PendingTable({
  store: pendingStore,
  setTimer: (fn, ms) => setTimeout(fn, ms),
  clearTimer: (handle) => clearTimeout(handle),
  onExpire: (id) => (isManualId(id) ? fallbackManual(id) : resumeDownload(Number(id))),
});

// Lượt bắt thủ công đang chờ DoMa trả lời: id → link, để trình duyệt tự tải nếu DoMa không nhận.
const manual = new Map();
// Link trình duyệt tải thay cho DoMa: không bắt lại lần nữa.
const bypass = new Set();
let manualCounter = 0;

async function browserDownload(url) {
  bypass.add(url);
  setTimeout(() => bypass.delete(url), 30000);
  try {
    await chrome.downloads.download({ url });
  } catch {
    bypass.delete(url);
  }
}

function fallbackManual(id) {
  const url = manual.get(id);
  manual.delete(id);
  if (url) browserDownload(url);
}

async function resumeDownload(id) {
  try {
    await chrome.downloads.resume(id);
  } catch {
  }
}

const restored = resumeStored(pendingStore, (id) => (isManualId(id) ? undefined : resumeDownload(Number(id)))).catch(() => {});

async function isEnabled() {
  const { enabled } = await chrome.storage.local.get({ enabled: true });
  return enabled;
}

function connect() {
  if (port) return Promise.resolve(port);
  if (connecting) return connecting;
  connecting = new Promise((resolve, reject) => {
    const candidate = chrome.runtime.connectNative(HOST_NAME);
    let settled = false;
    const finish = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      connecting = null;
      if (error) {
        try {
          candidate.disconnect();
        } catch {
        }
        reject(error);
      } else {
        port = candidate;
        resolve(candidate);
      }
    };
    const timer = setTimeout(() => finish(new Error("pong timeout")), PONG_TIMEOUT_MS);
    candidate.onMessage.addListener((msg) => {
      if (!settled) {
        if (msg?.type === "pong") {
          finish();
          checkHostVersion(msg.version).catch(() => {});
        }
        return;
      }
      handleHostMessage(msg);
    });
    candidate.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      if (!settled) {
        finish(new Error("disconnected"));
        return;
      }
      if (port === candidate) port = null;
      pending.expireAll();
      maybeReload();
    });
    candidate.postMessage({ type: "ping" });
  });
  return connecting;
}

async function handleHostMessage(msg) {
  const id = String(msg?.id ?? "");
  const type = msg?.type;
  if (isManualId(id)) {
    const action = decideManualReply(type);
    if (action === "markAsking") {
      pending.markAsking(id);
      return;
    }
    pending.take(id);
    if (action === "fallback") fallbackManual(id);
    else manual.delete(id);
    maybeReload();
    return;
  }
  if (type === "dang_hoi") {
    pending.markAsking(id);
    return;
  }
  const wasPending = pending.take(id);
  let itemState = null;
  if (!wasPending && type === "nhan") {
    try {
      const [item] = await chrome.downloads.search({ id: Number(id) });
      itemState = item?.state ?? null;
    } catch {
    }
  }
  const action = decideReply(type, wasPending, itemState);
  if (action === "cancel") await cancelAndErase(Number(id));
  else if (action === "resume") await resumeDownload(Number(id));
  maybeReload();
}

async function cancelAndErase(id) {
  try {
    await chrome.downloads.cancel(id);
  } catch {
  }
  try {
    await chrome.downloads.erase({ id });
  } catch {
  }
}

async function buildCookies(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

async function capture(item) {
  capturing++;
  try {
    await captureInner(item);
  } finally {
    capturing--;
    maybeReload();
  }
}

async function captureInner(item) {
  const id = item.id;
  await restored;
  try {
    await chrome.downloads.pause(id);
  } catch {
    return;
  }
  let paused = true;
  try {
    const [current] = await chrome.downloads.search({ id });
    if (afterPause(current) === "skip") {
      paused = false;
      return;
    }
    const live = { ...item, ...current };
    let activePort;
    try {
      activePort = await connect();
    } catch {
      return;
    }
    const cookies = await buildCookies(live.finalUrl || live.url);
    pending.add(String(id));
    try {
      activePort.postMessage(toCaptureMessage(live, cookies, navigator.userAgent));
    } catch (error) {
      pending.take(String(id));
      throw error;
    }
    paused = false;
  } catch {
  } finally {
    if (paused) await resumeDownload(id);
  }
}

chrome.downloads.onCreated.addListener(async (item) => {
  if (bypass.has(item.url) || bypass.has(item.finalUrl)) return;
  const enabled = await isEnabled();
  if (!shouldCapture(item, { enabled })) return;
  await capture(item);
});

// ── Menu chuột phải: "Tải bằng DoMa" cho mọi link, ảnh, video, âm thanh ──
const MENU_ID = "doma-download";

function createMenu() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Tải bằng DoMa",
      contexts: ["link", "image", "video", "audio"],
    });
  });
}

chrome.runtime.onInstalled.addListener(createMenu);
chrome.runtime.onStartup.addListener(createMenu);

async function captureManual(url, pageUrl) {
  capturing++;
  const id = manualId(`${Date.now()}-${++manualCounter}`);
  try {
    let activePort;
    try {
      activePort = await connect();
    } catch {
      // DoMa chưa chạy: để trình duyệt tự tải như bình thường.
      await browserDownload(url);
      return;
    }
    const cookies = await buildCookies(url);
    manual.set(id, url);
    pending.add(id);
    try {
      activePort.postMessage(toManualCaptureMessage(id, url, pageUrl, cookies, navigator.userAgent));
    } catch {
      pending.take(id);
      fallbackManual(id);
    }
  } finally {
    capturing--;
    maybeReload();
  }
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID) return;
  const url = contextUrl(info);
  if (url) captureManual(url, info.pageUrl || tab?.url);
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type !== "status") return false;
  connect().then(
    () => sendResponse({ connected: true }),
    () => sendResponse({ connected: false }),
  );
  return true;
});
