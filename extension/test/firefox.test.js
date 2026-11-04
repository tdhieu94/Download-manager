import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { toFirefoxManifest, updatesJson, FIREFOX_ID } from "../firefox.js";

const chrome = JSON.parse(readFileSync(new URL("../manifest.json", import.meta.url), "utf8"));

test("firefox manifest uses background scripts and gecko id", () => {
  const ff = toFirefoxManifest(chrome, "1.2.3");
  assert.equal(ff.version, "1.2.3");
  assert.equal(ff.key, undefined);
  assert.deepEqual(ff.background, { scripts: ["background.js"], type: "module" });
  assert.equal(ff.browser_specific_settings.gecko.id, FIREFOX_ID);
  assert.deepEqual(ff.permissions, chrome.permissions);
});

test("updates json points to xpi", () => {
  const json = updatesJson("1.2.3", "https://x/doma.xpi", "ab");
  assert.equal(json.addons[FIREFOX_ID].updates[0].update_link, "https://x/doma.xpi");
  assert.equal(json.addons[FIREFOX_ID].updates[0].update_hash, "sha256:ab");
});
