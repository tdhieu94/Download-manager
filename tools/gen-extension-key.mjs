import { generateKeyPairSync, createHash } from "node:crypto";

const { publicKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
const der = publicKey.export({ type: "spki", format: "der" });
const key = der.toString("base64");
const hex = createHash("sha256").update(der).digest("hex").slice(0, 32);
const id = [...hex].map((c) => String.fromCharCode("a".charCodeAt(0) + parseInt(c, 16))).join("");

console.log(JSON.stringify({ key, id }, null, 2));
