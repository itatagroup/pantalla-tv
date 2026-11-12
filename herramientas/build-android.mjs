// Compila, empaqueta y firma el APK sin Gradle: solo el SDK de Android y un JDK.
// Uso: node herramientas/build-android.mjs <carpeta de la app>   (genera <carpeta>/out/<nombre>.apk)
// Lo comparten receptor-android/ y emisor-android/ (cada uno lo llama desde su tools/build.mjs).
// Las dos compilan también android-comun/java (la actualización propia, paquete cl.itatagroup.actualizacion).
// version.json de la app: versionCode, versionName, nombre (del APK y del keystore), minSdk, targetSdk.
//
// Variables opcionales:
//   ANDROID_HOME / ANDROID_SDK_ROOT   SDK de Android (por defecto, el de Android Studio en Windows)
//   JAVA_HOME                         JDK (por defecto, el que viene con Android Studio, o el del PATH)
//   APP_KEYSTORE                      ruta del .jks (por defecto keystore/<nombre>.jks)
//   APP_KEYSTORE_PASS                 clave del .jks (por defecto, el contenido de keystore/password.txt)
//   APP_KEY_ALIAS                     alias de la clave (por defecto "k")
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, copyFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

if (!process.argv[2]) { console.error("Uso: node build-android.mjs <carpeta de la app>"); process.exit(1); }
const root = resolve(process.argv[2]);
const isWin = process.platform === "win32";
const exe = (n) => (isWin ? `${n}.exe` : n);
const out = join(root, "out");
const app = join(root, "app", "src", "main");

function fail(msg) {
  console.error(`ERROR: ${msg}`);
  process.exit(1);
}

function run(cmd, args, env = process.env) {
  try {
    execFileSync(cmd, args, { stdio: ["ignore", "inherit", "inherit"], env });
  } catch {
    fail(`falló: ${cmd} ${args.join(" ")}`);
  }
}

function highest(dir, filter = () => true) {
  if (!existsSync(dir)) return null;
  const names = readdirSync(dir).filter(filter);
  names.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  return names.length ? join(dir, names[names.length - 1]) : null;
}

/** Todos los archivos con la extensión dada, en la carpeta y sus subcarpetas. */
function filesWithExt(dir, ext) {
  const found = [];
  for (const n of readdirSync(dir)) {
    const p = join(dir, n);
    if (statSync(p).isDirectory()) found.push(...filesWithExt(p, ext));
    else if (n.endsWith(ext)) found.push(p);
  }
  return found;
}

// --- SDK ---
const sdk =
  process.env.ANDROID_HOME ||
  process.env.ANDROID_SDK_ROOT ||
  (isWin ? join(process.env.LOCALAPPDATA ?? "", "Android", "Sdk") : "");
if (!sdk || !existsSync(sdk)) fail("no encuentro el SDK de Android: define ANDROID_HOME");
const buildTools = highest(join(sdk, "build-tools"), (n) => /^\d/.test(n));
if (!buildTools) fail("el SDK no tiene build-tools");
const platform = highest(join(sdk, "platforms"), (n) => existsSync(join(sdk, "platforms", n, "android.jar")));
if (!platform) fail("el SDK no tiene ninguna plataforma con android.jar");
const androidJar = join(platform, "android.jar");

// --- JDK ---
function jdkBin(tool) {
  const homes = [
    process.env.JAVA_HOME,
    isWin ? "C:\\Program Files\\Android\\Android Studio\\jbr" : null,
  ].filter(Boolean);
  for (const h of homes) {
    const p = join(h, "bin", exe(tool));
    if (existsSync(p)) return p;
  }
  return tool; // el del PATH
}
const javac = jdkBin("javac");
const java = jdkBin("java");
const jar = jdkBin("jar");

// --- versión ---
// minSdk y targetSdk son opcionales en version.json (por defecto 21 y 29).
const { versionCode, versionName, nombre = "app", minSdk = 21, targetSdk = 29 } = JSON.parse(readFileSync(join(root, "version.json"), "utf8"));

// --- firma ---
const keystore = process.env.APP_KEYSTORE ?? join(root, "keystore", `${nombre}.jks`);
const passFile = join(root, "keystore", "password.txt");
const storePass =
  process.env.APP_KEYSTORE_PASS ?? (existsSync(passFile) ? readFileSync(passFile, "utf8").trim() : null);
const keyAlias = process.env.APP_KEY_ALIAS ?? "k";
if (!existsSync(keystore)) fail(`no existe el keystore ${keystore} (ver «Compilar» en el README: cómo crear tu propia clave con keytool)`);
if (!storePass) fail("falta la clave del keystore (APP_KEYSTORE_PASS o keystore/password.txt)");

console.log(`SDK: ${sdk}\nbuild-tools: ${buildTools}\nplataforma: ${platform}\nversión: ${versionName} (${versionCode})`);

rmSync(out, { recursive: true, force: true });
mkdirSync(join(out, "classes"), { recursive: true });
mkdirSync(join(out, "gen"), { recursive: true });

// 1. recursos
run(join(buildTools, exe("aapt2")), ["compile", "--dir", join(app, "res"), "-o", join(out, "res.zip")]);
run(join(buildTools, exe("aapt2")), [
  "link", "-o", join(out, "base.apk"), "-I", androidJar,
  "--manifest", join(app, "AndroidManifest.xml"),
  "--java", join(out, "gen"),
  "--min-sdk-version", String(minSdk), "--target-sdk-version", String(targetSdk),
  "--version-code", String(versionCode), "--version-name", versionName,
  join(out, "res.zip"),
]);

// 2. Java -> classes
const comun = join(dirname(fileURLToPath(import.meta.url)), "..", "android-comun", "java");
const javaFiles = [
  ...filesWithExt(join(app, "java"), ".java"),
  ...filesWithExt(comun, ".java"),
  ...filesWithExt(join(out, "gen"), ".java"),
];
run(javac, ["--release", "8", "-Xlint:-options", "-classpath", androidJar, "-d", join(out, "classes"), ...javaFiles]);

// 3. classes -> dex
const classFiles = filesWithExt(join(out, "classes"), ".class");
run(java, [
  "-cp", join(buildTools, "lib", "d8.jar"), "com.android.tools.r8.D8",
  "--lib", androidJar, "--min-api", String(minSdk), "--output", out, ...classFiles,
]);

// 4. dex dentro del APK, alinear y firmar
copyFileSync(join(out, "base.apk"), join(out, "unsigned.apk"));
run(jar, ["uf", join(out, "unsigned.apk"), "-C", out, "classes.dex"]);
run(join(buildTools, exe("zipalign")), ["-f", "4", join(out, "unsigned.apk"), join(out, "aligned.apk")]);
run(java, [
  "-jar", join(buildTools, "lib", "apksigner.jar"), "sign",
  "--ks", keystore, "--ks-key-alias", keyAlias,
  // La clave viaja por variable de entorno, no por la línea de comandos (visible en la lista de procesos).
  "--ks-pass", "env:APP_SIGN_PASS", "--key-pass", "env:APP_SIGN_PASS",
  "--out", join(out, `${nombre}.apk`), join(out, "aligned.apk"),
], { ...process.env, APP_SIGN_PASS: storePass });

console.log(`\nListo: ${join(out, `${nombre}.apk`)} (${statSync(join(out, `${nombre}.apk`)).size} bytes)`);
