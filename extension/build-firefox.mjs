// Dùng: node build-firefox.mjs <thư-mục-đích> [phiên-bản]
// Chép mã tiện ích sang thư mục đích với manifest dành cho Firefox.
import { cpSync, mkdirSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { toFirefoxManifest } from "./firefox.js";

const here = dirname(fileURLToPath(import.meta.url));
const [out, version] = process.argv.slice(2);
if (!out) {
  console.error("Thiếu thư mục đích");
  process.exit(1);
}
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
for (const f of ["background.js", "capture.js", "popup.html", "popup.js", "icons"]) {
  cpSync(join(here, f), join(out, f), { recursive: true });
}
const chrome = JSON.parse(readFileSync(join(here, "manifest.json"), "utf8"));
writeFileSync(join(out, "manifest.json"), JSON.stringify(toFirefoxManifest(chrome, version), null, 2) + "\n");
console.log(`Đã tạo tiện ích Firefox tại ${out}`);
