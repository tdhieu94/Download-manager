import { deflateSync } from "node:zlib";
import { writeFileSync, mkdirSync } from "node:fs";

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function arrowPixel(x, y, size) {
  const u = (x + 0.5) / size;
  const v = (y + 0.5) / size;
  const shaft = u >= 0.4 && u <= 0.6 && v >= 0.18 && v <= 0.55;
  const head = v > 0.5 && v <= 0.8 && Math.abs(u - 0.5) <= (0.8 - v) * 1.1 + 0.0;
  const tray = u >= 0.2 && u <= 0.8 && v >= 0.82 && v <= 0.9;
  return shaft || head || tray;
}

function png(size) {
  const rows = [];
  for (let y = 0; y < size; y++) {
    const row = Buffer.alloc(1 + size * 4);
    for (let x = 0; x < size; x++) {
      const white = arrowPixel(x, y, size);
      row.set(white ? [255, 255, 255, 255] : [25, 118, 210, 255], 1 + x * 4);
    }
    rows.push(row);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(Buffer.concat(rows))),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

mkdirSync("extension/icons", { recursive: true });
for (const size of [16, 32, 48, 128]) writeFileSync(`extension/icons/${size}.png`, png(size));
