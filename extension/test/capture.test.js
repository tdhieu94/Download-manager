import test from "node:test";
import assert from "node:assert/strict";
import { shouldCapture, PendingTable, toCaptureMessage, resumeStored, decideReply, afterPause, compareVersions, shouldReload, manualId, isManualId, contextUrl, toManualCaptureMessage, decideManualReply } from "../capture.js";

const base = { id: 1, url: "https://example.com/a.zip", state: "in_progress", incognito: false };
const on = { enabled: true };

function fakeTimers() {
  const timers = new Map();
  let next = 1;
  return {
    timers,
    setTimer: (fn, ms) => {
      const handle = next++;
      timers.set(handle, { fn, ms });
      return handle;
    },
    clearTimer: (handle) => timers.delete(handle),
  };
}

test("skips blob/data/file", () => {
  for (const url of ["blob:https://x/1", "data:text/plain,a", "file:///c:/a.txt", "chrome-extension://abc/a", "about:blank"]) {
    assert.equal(shouldCapture({ ...base, url }, on), false, url);
  }
  assert.equal(shouldCapture(base, on), true);
});

test("skips when disabled", () => {
  assert.equal(shouldCapture(base, { enabled: false }), false);
});

test("skips incognito", () => {
  assert.equal(shouldCapture({ ...base, incognito: true }, on), false);
});

test("skips item that completed before pause", () => {
  assert.equal(shouldCapture({ ...base, state: "complete" }, on), false);
});

test("pending expires to onExpire after timeout", () => {
  const t = fakeTimers();
  const expired = [];
  const table = new PendingTable({ ...t, onExpire: (id) => expired.push(id) });
  table.add("7");
  assert.equal([...t.timers.values()][0].ms, 25000);
  [...t.timers.values()][0].fn();
  assert.deepEqual(expired, ["7"]);
  assert.equal(table.take("7"), false);
});

test("markAsking cancels timeout", () => {
  const t = fakeTimers();
  const expired = [];
  const table = new PendingTable({ ...t, onExpire: (id) => expired.push(id) });
  table.add("7");
  table.markAsking("7");
  assert.equal(t.timers.size, 0);
  assert.deepEqual(expired, []);
  assert.equal(table.take("7"), true);
});

test("expireAll resumes every pending", () => {
  const t = fakeTimers();
  const expired = [];
  const table = new PendingTable({ ...t, onExpire: (id) => expired.push(id) });
  table.add("1");
  table.add("2");
  table.markAsking("2");
  table.expireAll();
  assert.deepEqual(expired.sort(), ["1", "2"]);
  assert.equal(t.timers.size, 0);
  assert.equal(table.take("1"), false);
});

test("take returns false for unknown id", () => {
  const t = fakeTimers();
  const table = new PendingTable({ ...t, onExpire: () => {} });
  assert.equal(table.take("nope"), false);
});

test("take cancels the timeout", () => {
  const t = fakeTimers();
  const table = new PendingTable({ ...t, onExpire: () => {} });
  table.add("3");
  assert.equal(table.take("3"), true);
  assert.equal(t.timers.size, 0);
});

test("toCaptureMessage uses basename and nulls zero size", () => {
  const msg = toCaptureMessage(
    { id: 5, url: "https://a/b", finalUrl: "https://a/c", referrer: "https://r/", filename: "C:\\Users\\x\\Downloads\\f.zip", mime: "application/zip", fileSize: 0 },
    "a=1; b=2",
    "UA",
  );
  assert.deepEqual(msg, {
    type: "bat", id: "5", url: "https://a/b", finalUrl: "https://a/c", referrer: "https://r/",
    filename: "f.zip", mime: "application/zip", fileSize: null, cookies: "a=1; b=2", userAgent: "UA",
  });
});

test("toCaptureMessage keeps positive size, posix basename, omits empty finalUrl", () => {
  const msg = toCaptureMessage({ id: 6, url: "https://a/b", finalUrl: "", filename: "/tmp/g.bin", fileSize: 99 }, "", "UA");
  assert.equal(msg.fileSize, 99);
  assert.equal(msg.filename, "g.bin");
  assert.equal(msg.finalUrl, null);
});

function fakeStore(initial = []) {
  const store = { saved: initial, saves: 0 };
  store.save = (ids) => {
    store.saved = [...ids];
    store.saves++;
  };
  store.load = async () => store.saved;
  return store;
}

test("default probe timeout is 25000 ms", () => {
  const t = fakeTimers();
  const table = new PendingTable({ ...t, onExpire: () => {} });
  table.add("1");
  assert.equal([...t.timers.values()][0].ms, 25000);
});

test("pending ids are persisted on add, take, markAsking and expire", () => {
  const t = fakeTimers();
  const store = fakeStore();
  const table = new PendingTable({ ...t, onExpire: () => {}, store });
  table.add("1");
  table.add("2");
  assert.deepEqual(store.saved, ["1", "2"]);
  table.take("1");
  assert.deepEqual(store.saved, ["2"]);
  table.markAsking("2");
  assert.deepEqual(store.saved, ["2"]);
  [...t.timers.values()].forEach((x) => x.fn());
  table.add("3");
  [...t.timers.values()][0].fn();
  assert.deepEqual(store.saved, ["2"]);
  table.expireAll();
  assert.deepEqual(store.saved, []);
});

test("resumeStored resumes stored ids and clears the store", async () => {
  const store = fakeStore(["4", "9"]);
  const resumed = [];
  await resumeStored(store, (id) => resumed.push(id));
  assert.deepEqual(resumed, ["4", "9"]);
  assert.deepEqual(store.saved, []);
});

test("decideReply handles pending and unknown ids", () => {
  assert.equal(decideReply("dang_hoi", true, null), "markAsking");
  assert.equal(decideReply("nhan", true, "in_progress"), "cancel");
  assert.equal(decideReply("huy", true, "in_progress"), "cancel");
  assert.equal(decideReply("tra_lai", true, "in_progress"), "resume");
  assert.equal(decideReply("nhan", false, "in_progress"), "cancel");
  assert.equal(decideReply("nhan", false, "complete"), "none");
  assert.equal(decideReply("nhan", false, null), "none");
  assert.equal(decideReply("huy", false, "in_progress"), "none");
  assert.equal(decideReply("tra_lai", false, "in_progress"), "none");
  assert.equal(decideReply("dang_hoi", false, "in_progress"), "none");
});

test("afterPause skips missing or completed items", () => {
  assert.equal(afterPause(undefined), "skip");
  assert.equal(afterPause({ state: "complete" }), "skip");
  assert.equal(afterPause({ state: "in_progress" }), "proceed");
});

test("compares x.y.z versions", () => {
  assert.equal(compareVersions("1.0.1", "1.0.0"), 1);
  assert.equal(compareVersions("1.2.0", "1.10.0"), -1);
  assert.equal(compareVersions("2.0.0", "2.0.0"), 0);
  assert.equal(compareVersions("abc", "1.0.0"), 0);
});

test("reloads once when DoMa is newer", () => {
  assert.equal(shouldReload("1.1.0", "1.0.0", null), true);
  assert.equal(shouldReload("1.1.0", "1.0.0", "1.1.0"), false);
  assert.equal(shouldReload("1.0.0", "1.0.0", null), false);
  assert.equal(shouldReload("0.9.0", "1.0.0", null), false);
  assert.equal(shouldReload(undefined, "1.0.0", null), false);
});

test("manual ids are distinguishable from browser download ids", () => {
  assert.equal(isManualId(manualId(7)), true);
  assert.equal(isManualId("42"), false);
});

test("context menu prefers link, then media source, http(s) only", () => {
  assert.equal(contextUrl({ linkUrl: "https://a/x.zip", srcUrl: "https://a/i.png" }), "https://a/x.zip");
  assert.equal(contextUrl({ srcUrl: "https://a/v.mp4" }), "https://a/v.mp4");
  assert.equal(contextUrl({ linkUrl: "javascript:void(0)", srcUrl: "blob:https://a/1" }), null);
});

test("manual capture message carries page as referrer", () => {
  const msg = toManualCaptureMessage("m1", "https://a/x.zip", "https://a/page", "k=v", "UA");
  assert.equal(msg.type, "bat");
  assert.equal(msg.referrer, "https://a/page");
  assert.equal(toManualCaptureMessage("m1", "https://a/x", "chrome://newtab", "", "UA").referrer, null);
});

test("manual give-back falls back to browser download", () => {
  assert.equal(decideManualReply("tra_lai"), "fallback");
  assert.equal(decideManualReply("dang_hoi"), "markAsking");
  assert.equal(decideManualReply("nhan"), "done");
  assert.equal(decideManualReply("huy"), "done");
});
