// Compila esta app con el build compartido del repo (herramientas/build-android.mjs).
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const app = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const r = spawnSync(process.execPath, [resolve(app, "..", "herramientas", "build-android.mjs"), app], { stdio: "inherit" });
process.exit(r.status ?? 1);
