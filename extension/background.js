import {
  shouldCapture, PendingTable, toCaptureMessage, resumeStored, decideReply, afterPause, shouldReload,
  manualId, isManualId, contextUrl, toManualCaptureMessage, decideManualReply,
  collectPageLinks, prepareBatch, hostsOf, toBatchMessage,
  classifyMedia, totalFromHeaders, mediaFileName, addMedia, pageVideo, youtubePlaylistUrl, isPageMedia, pageEntries,
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
const MENU_ALL_ID = "doma-download-all";
const MENU_YT_ID = "doma-download-youtube";
const MENU_PL_ID = "doma-download-playlist";
// Trang video tải được bằng DoMa (qua yt-dlp).
const VIDEO_SITES = ["*://*.youtube.com/*", "*://*.facebook.com/*", "*://fb.watch/*", "*://*.tiktok.com/*",
  "*://*.instagram.com/*", "*://*.x.com/*", "*://x.com/*", "*://*.twitter.com/*", "*://twitter.com/*"];

function createMenu() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Tải bằng DoMa",
      contexts: ["link", "image", "video", "audio"],
    });
    chrome.contextMenus.create({
      id: MENU_YT_ID,
      title: "Tải video này bằng DoMa",
      contexts: ["page", "video"],
      documentUrlPatterns: VIDEO_SITES,
    });
    chrome.contextMenus.create({
      id: MENU_PL_ID,
      title: "Tải cả playlist/kênh này bằng DoMa…",
      contexts: ["page"],
      documentUrlPatterns: ["*://*.youtube.com/*"],
    });
    chrome.contextMenus.create({
      id: MENU_ALL_ID,
      title: "Tải tất cả link bằng DoMa…",
      contexts: ["page", "selection", "link"],
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
    // Link trang video (YouTube, TikTok…): trình duyệt không tải thay được (chỉ ra trang web), nên không có đường lui.
    manual.set(id, isPageMedia(url) ? null : url);
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

async function captureAll(tab, pageUrl) {
  if (tab?.id == null) return;
  let links;
  try {
    const [frame] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: collectPageLinks });
    links = prepareBatch(frame?.result);
  } catch {
    return; // Trang không cho chạy script (trang hệ thống, cửa hàng tiện ích…).
  }
  let activePort;
  try {
    activePort = await connect();
  } catch {
    return;
  }
  const cookies = {};
  for (const [host, url] of hostsOf(links)) {
    const value = await buildCookies(url);
    if (value) cookies[host] = value;
  }
  const id = manualId(`${Date.now()}-${++manualCounter}`);
  manual.set(id, null);
  try {
    activePort.postMessage(toBatchMessage(id, pageUrl, links, cookies, navigator.userAgent));
  } catch {
    manual.delete(id);
  }
}

// ── Bắt video / âm thanh đang phát ──
const mediaKey = (tabId) => `media:${tabId}`;

async function mediaOf(tabId) {
  return (await chrome.storage.session.get({ [mediaKey(tabId)]: [] }))[mediaKey(tabId)];
}

async function setMedia(tabId, list) {
  if (list.length === 0) await chrome.storage.session.remove(mediaKey(tabId));
  else await chrome.storage.session.set({ [mediaKey(tabId)]: list });
  try {
    await chrome.action.setBadgeBackgroundColor({ tabId, color: "#2563eb" });
    await chrome.action.setBadgeText({ tabId, text: list.length ? String(list.length) : "" });
  } catch {
  }
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0 || details.statusCode >= 400) return;
    const headers = details.responseHeaders ?? [];
    const type = headers.find((h) => h.name.toLowerCase() === "content-type")?.value ?? "";
    const size = totalFromHeaders(headers);
    const media = classifyMedia(details.url, type, size);
    if (!media) return;
    (async () => {
      let title = "";
      let pageUrl = details.initiator ?? details.originUrl ?? null;
      try {
        const tab = await chrome.tabs.get(details.tabId);
        title = tab.title ?? "";
        pageUrl = tab.url ?? pageUrl;
      } catch {
      }
      const entry = {
        url: details.url,
        kind: media.kind,
        mime: type.split(";")[0].trim() || null,
        size: media.kind === "hls" ? null : size,
        name: mediaFileName(title, details.url, media),
        pageUrl,
        at: Date.now(),
      };
      await setMedia(details.tabId, addMedia(await mediaOf(details.tabId), entry));
    })().catch(() => {});
  },
  { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "other"] },
  ["responseHeaders"],
);

// Sang trang khác hoặc đóng tab: xoá danh sách video của tab đó.
// Trang video (YouTube, Facebook, TikTok…) hoặc playlist YouTube: hiện sẵn trong danh sách (DoMa tải bằng yt-dlp).
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (!changeInfo.url && !changeInfo.title) return;
  (async () => {
    const list = changeInfo.url ? [] : await mediaOf(tabId);
    const pages = pageEntries(tab?.url ?? changeInfo.url, tab?.title);
    const rest = list.filter((m) => m.kind !== "page" && m.kind !== "playlist" && m.kind !== "youtube");
    if (pages.length > 0) await setMedia(tabId, [...pages].reverse().reduce((acc, e) => addMedia(acc, e), rest));
    else if (changeInfo.url) await setMedia(tabId, []);
  })().catch(() => {});
});
chrome.tabs.onRemoved.addListener((tabId) => {
  chrome.storage.session.remove(mediaKey(tabId)).catch(() => {});
});

async function captureMedia(entry) {
  capturing++;
  const id = manualId(`${Date.now()}-${++manualCounter}`);
  try {
    let activePort;
    try {
      activePort = await connect();
    } catch {
      return false;
    }
    const cookies = await buildCookies(entry.url);
    manual.set(id, entry.kind === "file" ? entry.url : null);
    pending.add(id);
    activePort.postMessage(toManualCaptureMessage(id, entry.url, entry.pageUrl, cookies, navigator.userAgent, entry.name, entry.mime, entry.size));
    return true;
  } catch {
    pending.take(id);
    manual.delete(id);
    return false;
  } finally {
    capturing--;
    maybeReload();
  }
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_ALL_ID) {
    captureAll(tab, info.pageUrl || tab?.url);
    return;
  }
  if (info.menuItemId === MENU_YT_ID) {
    const url = pageVideo(info.pageUrl || tab?.url)?.url;
    if (url) captureManual(url, url);
    return;
  }
  if (info.menuItemId === MENU_PL_ID) {
    const url = youtubePlaylistUrl(info.pageUrl || tab?.url);
    if (url) captureManual(url, url);
    return;
  }
  if (info.menuItemId !== MENU_ID) return;
  const url = contextUrl(info);
  if (url) captureManual(url, info.pageUrl || tab?.url);
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === "media-list") {
    mediaOf(msg.tabId).then(sendResponse, () => sendResponse([]));
    return true;
  }
  if (msg?.type === "media-download") {
    mediaOf(msg.tabId)
      .then((list) => list.find((m) => m.url === msg.url))
      .then((entry) => (entry ? captureMedia(entry) : false))
      .then((ok) => sendResponse({ ok }), () => sendResponse({ ok: false }));
    return true;
  }
  if (msg?.type !== "status") return false;
  connect().then(
    () => sendResponse({ connected: true }),
    () => sendResponse({ connected: false }),
  );
  return true;
});
