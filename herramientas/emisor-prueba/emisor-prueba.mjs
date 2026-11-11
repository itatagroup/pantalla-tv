// Emisor de prueba: captura la pantalla con ffmpeg (ddagrab + h264_amf) y la manda al receptor
// con el protocolo de docs/protocolo.md. Sirve para probar el M1 antes de que exista el emisor .NET.
//
// Uso: node emisor-prueba.mjs --ip 10.0.0.20 [--fps 60] [--kbps 8000] [--ancho 1920 --alto 1080]
//                             [--tono] [--seg 30]
import { spawn } from "node:child_process";
import net from "node:net";

const arg = (n, d) => {
  const i = process.argv.indexOf(`--${n}`);
  if (i < 0) return d;
  const v = process.argv[i + 1];
  return v === undefined || v.startsWith("--") ? true : v;
};
const ip = arg("ip", null);
if (!ip || ip === true) {
  console.error("Falta --ip");
  process.exit(1);
}
const fps = Number(arg("fps", 60));
const kbps = Number(arg("kbps", 8000));
const ancho = Number(arg("ancho", 1920));
const alto = Number(arg("alto", 1080));
const conTono = arg("tono", false) === true;
const segundos = Number(arg("seg", 0));
const retardo = Number(arg("retardo", 0)); // ms de retardo a propósito que se pide al receptor
const TASA = 48000;
const CANALES = 2;

const T = { HELLO: 1, HELLO_OK: 2, HELLO_ERR: 3, VIDEO_CONFIG: 0x10, VIDEO: 0x11, AUDIO: 0x20, PING: 0x30, PONG: 0x31, PEDIR_CLAVE: 0x40 };
const t0 = process.hrtime.bigint();
const ahoraUs = () => (process.hrtime.bigint() - t0) / 1000n;

const sock = net.connect(5600, ip);
sock.setNoDelay(true);

function mensaje(tipo, ptsUs, carga) {
  const h = Buffer.alloc(13);
  h.writeUInt8(tipo, 0);
  h.writeUInt32BE(carga.length, 1);
  h.writeBigUInt64BE(ptsUs, 5);
  return Buffer.concat([h, carga]);
}

// --- estadísticas ---
let enviados = 0, bytes = 0, tirados = 0, rtt = [];
setInterval(() => {
  const r = rtt.length ? `${Math.round(rtt.reduce((a, b) => a + b) / rtt.length)} ms` : "n/d";
  console.log(`enviado: ${(enviados / 2).toFixed(1)} fps, ${((bytes * 8) / 2 / 1e6).toFixed(1)} Mb/s, tirados ${tirados}, RTT ${r}`);
  enviados = 0; bytes = 0; tirados = 0; rtt = [];
}, 2000);

// --- respuestas del receptor ---
let recibido = Buffer.alloc(0);
let ffmpeg = null;
sock.on("data", (d) => {
  recibido = Buffer.concat([recibido, d]);
  while (recibido.length >= 13) {
    const largo = recibido.readUInt32BE(1);
    if (recibido.length < 13 + largo) break;
    const tipo = recibido.readUInt8(0);
    const pts = recibido.readBigUInt64BE(5);
    const carga = recibido.subarray(13, 13 + largo);
    recibido = recibido.subarray(13 + largo);
    if (tipo === T.HELLO_OK) empezar();
    else if (tipo === T.HELLO_ERR) { console.error(`El receptor rechazó la conexión: ${carga.toString()}`); process.exit(2); }
    else if (tipo === T.PONG) rtt.push(Number(ahoraUs() - pts) / 1000);
    else if (tipo === T.PEDIR_CLAVE) console.log("(el receptor pidió un fotograma clave; llegará en el siguiente GOP)");
  }
});
sock.on("error", (e) => { console.error(`Conexión: ${e.code || e.message}`); process.exit(1); });
sock.on("close", () => { console.log("Conexión cerrada."); if (ffmpeg) ffmpeg.kill(); process.exit(0); });

sock.on("connect", () => {
  const h = Buffer.alloc(17);
  h.writeUInt16BE(retardo, 15);
  h.writeUInt8(1, 0);
  h.writeUInt16BE(ancho, 1);
  h.writeUInt16BE(alto, 3);
  h.writeUInt8(fps, 5);
  h.writeUInt32BE(conTono ? TASA : 0, 6);
  h.writeUInt8(CANALES, 10);
  h.write("0000", 11, "ascii");
  sock.write(mensaje(T.HELLO, 0n, h));
});

// --- vídeo ---
let configEnviada = null;
let pendiente = Buffer.alloc(0);
let acarreo = Buffer.alloc(0);
let esperandoClave = false;

function esAud(b, i) { return b[i] === 0 && b[i + 1] === 0 && b[i + 2] === 1 && (b[i + 3] & 0x1f) === 9; }

/** Posiciones (con su 00 previo si lo hay) donde empieza cada unidad de acceso (marca AUD). */
function inicios(b) {
  const r = [];
  for (let i = 0; i + 3 < b.length; i++) {
    if (esAud(b, i)) r.push(i > 0 && b[i - 1] === 0 ? i - 1 : i);
  }
  return r;
}

function nals(au) {
  const r = [];
  let ini = -1;
  for (let i = 0; i + 2 < au.length; i++) {
    if (au[i] === 0 && au[i + 1] === 0 && au[i + 2] === 1) {
      const p = i > 0 && au[i - 1] === 0 ? i - 1 : i;
      if (ini >= 0) r.push([ini, p]);
      ini = p;
      i += 2;
    }
  }
  if (ini >= 0) r.push([ini, au.length]);
  return r.map(([a, b]) => {
    let p = a;
    while (au[p] === 0) p++;
    return { a, b, tipo: au[p + 1] & 0x1f };
  });
}

function enviarAU(au) {
  const partes = nals(au);
  const clave = partes.some((n) => n.tipo === 5);
  const sps = partes.find((n) => n.tipo === 7);
  const pps = partes.find((n) => n.tipo === 8);
  if (sps && pps) {
    const cfg = Buffer.concat([au.subarray(sps.a, sps.b), au.subarray(pps.a, pps.b)]);
    if (!configEnviada || !configEnviada.equals(cfg)) {
      if (configEnviada) console.log(`(config nueva: ${cfg.length} bytes, antes ${configEnviada.length})`);
      configEnviada = cfg;
      sock.write(mensaje(T.VIDEO_CONFIG, ahoraUs(), cfg));
    }
  }
  if (!configEnviada) return; // todavía no hay SPS/PPS: no se puede decodificar
  // Contrapresión: si la red no da abasto, se tira hasta el siguiente fotograma clave.
  if (sock.writableLength > 1_000_000) esperandoClave = true;
  if (esperandoClave && !clave) { tirados++; return; }
  if (clave) esperandoClave = false;
  const carga = Buffer.concat([Buffer.from([clave ? 1 : 0]), au]);
  sock.write(mensaje(T.VIDEO, ahoraUs(), carga));
  enviados++;
  bytes += carga.length;
}

function empezar() {
  console.log(`Conectado. Capturando ${ancho}x${alto} a ${fps} fps, ${kbps} kb/s${conTono ? ", con tono de prueba" : ""}.`);
  ffmpeg = spawn("ffmpeg", [
    "-hide_banner", "-loglevel", "error",
    "-f", "lavfi", "-i", `ddagrab=0:framerate=${fps},hwdownload,format=bgra`,
    "-fps_mode", "cfr", "-r", String(fps),
    "-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed",
    "-rc", "cbr", "-b:v", `${kbps}k`, "-bf", "0", "-g", String(fps * 2),
    "-bsf:v", "dump_extra=freq=keyframe,h264_metadata=aud=insert",
    "-f", "h264", "pipe:1",
  ], { stdio: ["ignore", "pipe", "inherit"] });
  ffmpeg.stdout.on("data", (d) => {
    pendiente = Buffer.concat([pendiente, d]);
    const ini = inicios(pendiente);
    if (ini.length < 2) return;
    for (let k = 0; k + 1 < ini.length; k++) {
      let au = pendiente.subarray(ini[k], ini[k + 1]);
      if (acarreo.length) { au = Buffer.concat([acarreo, au]); acarreo = Buffer.alloc(0); }
      // Un trozo sin cuadro de vídeo (solo AUD/SPS/PPS) no es una unidad completa: se une al siguiente.
      if (!nals(au).some((n) => n.tipo === 1 || n.tipo === 5)) { acarreo = au; continue; }
      enviarAU(au);
    }
    pendiente = pendiente.subarray(ini[ini.length - 1]);
  });
  ffmpeg.on("exit", (c) => { console.log(`ffmpeg terminó (${c}).`); sock.end(); });

  setInterval(() => sock.write(mensaje(T.PING, ahoraUs(), Buffer.alloc(8))), 2000);
  if (conTono) tono();
  if (segundos > 0) setTimeout(() => { ffmpeg.kill(); }, segundos * 1000);
}

// --- audio de prueba: seno de 440 Hz en paquetes de 10 ms, siguiendo el reloj de pared ---
function tono() {
  let muestras = 0;
  const inicio = process.hrtime.bigint();
  setInterval(() => {
    const debidas = Math.floor((Number(process.hrtime.bigint() - inicio) / 1e9) * TASA);
    while (muestras + 480 <= debidas) {
      const b = Buffer.alloc(480 * CANALES * 2);
      for (let i = 0; i < 480; i++) {
        const v = Math.round(Math.sin((2 * Math.PI * 440 * (muestras + i)) / TASA) * 3000);
        for (let c = 0; c < CANALES; c++) b.writeInt16LE(v, (i * CANALES + c) * 2);
      }
      sock.write(mensaje(T.AUDIO, ahoraUs(), b));
      muestras += 480;
    }
  }, 5);
}
