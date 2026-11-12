#!/usr/bin/env node
// Firma el manifiesto de una app (<app>.json) (ver docs/actualizaciones.md): ECDSA P-256 + SHA-256 sobre los bytes
// EXACTOS del archivo, firma en DER y en base64, en una sola línea, en <manifiesto>.sig.
// Sin dependencias: solo node:crypto.
//
//   node herramientas/firmar-manifiesto.mjs <manifiesto.json> <clave.pem> [--esperada <clave pública SPKI en base64>]
//
// Con --esperada se niega a firmar si la clave privada no corresponde a esa pública (la que llevan las apps): una
// firma con otra clave la rechazarían todas las instalaciones. Nunca imprime la clave privada ni su contenido.
import { readFileSync, writeFileSync } from "node:fs";
import { createPrivateKey, createPublicKey, sign, verify } from "node:crypto";

function fallar(mensaje) {
  console.error(mensaje);
  process.exit(1);
}

const args = process.argv.slice(2);
const iEsperada = args.indexOf("--esperada");
const esperada = iEsperada >= 0 ? args[iEsperada + 1] : null;
if (iEsperada >= 0) args.splice(iEsperada, 2);
const [rutaManifiesto, rutaClave] = args;
if (!rutaManifiesto || !rutaClave || (iEsperada >= 0 && !esperada)) {
  fallar("Uso: node herramientas/firmar-manifiesto.mjs <manifiesto.json> <clave.pem> [--esperada <SPKI base64>]");
}

const datos = readFileSync(rutaManifiesto);
// Se firman los bytes tal cual: un BOM los cambiaría al abrirlo y guardarlo con otro editor, y el contrato dice sin BOM.
if (datos.length >= 3 && datos[0] === 0xef && datos[1] === 0xbb && datos[2] === 0xbf) {
  fallar(`${rutaManifiesto} empieza con BOM: guárdalo como UTF-8 sin BOM.`);
}
try {
  const json = JSON.parse(datos.toString("utf8"));
  if (json.formato !== 1 || typeof json.repo !== "string" || typeof json.apps !== "object") {
    fallar(`${rutaManifiesto} no tiene la forma del contrato (formato 1, repo, apps).`);
  }
} catch {
  fallar(`${rutaManifiesto} no es un JSON válido.`);
}

let privada;
try {
  privada = createPrivateKey(readFileSync(rutaClave));
} catch {
  // Sin el detalle del error: no hace falta y así no se arriesga a mostrar nada de la clave.
  fallar(`No se pudo leer la clave privada de ${rutaClave} (se espera PKCS#8 en PEM).`);
}
if (privada.asymmetricKeyType !== "ec" || privada.asymmetricKeyDetails?.namedCurve !== "prime256v1") {
  fallar("La clave no es ECDSA P-256.");
}

const publica = createPublicKey(privada);
const publicaB64 = publica.export({ type: "spki", format: "der" }).toString("base64");
if (esperada && esperada.trim() !== publicaB64) {
  fallar("La clave privada no corresponde a la clave pública esperada: no se firma.");
}

const firma = sign("sha256", datos, privada); // DER (ieee-p1363 sería el otro formato)
if (!verify("sha256", datos, publica, firma)) fallar("La firma recién hecha no se pudo verificar.");

writeFileSync(`${rutaManifiesto}.sig`, firma.toString("base64"), { encoding: "ascii" });
console.log(`Firmado: ${rutaManifiesto}.sig`);
console.log(`Clave pública (SPKI, base64): ${publicaB64}`);
