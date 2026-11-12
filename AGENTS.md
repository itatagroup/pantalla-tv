# AGENTS.md — Pantalla TV

Guía para agentes de IA y colaboradores. Lee también `README.md` y `CONTRIBUTING.md`.

## Idioma

Todo en **español**: respuestas, mensajes de commit, documentación y textos de pantalla. El código (nombres de
variables, funciones) sigue la convención de cada parte.

## Qué es

Ver y oír la pantalla de un PC o un teléfono en la tele, por la red local. Tres piezas y herramientas:

- `emisor-windows/` — emisor de Windows, **.NET 10** + WinForms. Solución `PantallaTv.sln`:
  `PantallaTv.Emisor` (el programa y el instalador), `PantallaTv.Emisor.Tests` (xUnit),
  `PantallaTv.Captura.Prueba` (+ `.Tests`: herramienta de medición, no va en el instalador) y `controlador/`
  (Virtual-Display-Driver incluido, ver su `LEEME.md`).
- `receptor-android/` y `emisor-android/` — apps Android en Java, **sin Gradle y sin dependencias**. Lo que
  comparten (la actualización propia) está en `android-comun/java/` y lo compila `herramientas/build-android.mjs`.
- `herramientas/` — `empaquetar.ps1` (arma `dist/`), `publicar.ps1` y `firmar-manifiesto.mjs` (arman un release), `build-android.mjs` (build compartido de las dos apps),
  scripts de medición.
- `docs/` — `protocolo.md` (el contrato entre emisor y receptor), `monitor-virtual.md`, `prueba-otro-pc.md`,
  `verificar.md`, `actualizaciones.md` (el contrato de las actualizaciones y cómo publicar una versión).

Arquitectura del emisor: la ventana principal y el menú de la bandeja no tienen lógica propia; todo pasa por
`Mando.cs` (estado, ajustes y acciones). Un cambio de opciones va ahí. La captura propia está en
`PantallaTv.Emisor/Nativa/` (duplicación de escritorio → NV12 en la GPU → Media Foundation); ffmpeg es un
respaldo opcional (`IFuenteVideo`, en `FuenteVideo.cs`).

## Ramas

`dev` es la rama de trabajo y recibe los PR; `main` es la estable. El mantenedor decide cuándo se mezcla a `main`.

## Comandos

- Emisor de Windows (desde `emisor-windows/`): `dotnet build PantallaTv.sln -c Release` y
  `dotnet test PantallaTv.sln -c Release`. Las advertencias del compilador son error.
- Apps Android: `cd receptor-android && node tools/build.mjs` (y lo mismo en `emisor-android/`). Las dos usan
  `herramientas/build-android.mjs`. Necesitan una clave propia en `keystore/` (ver el README, «Compilar»).
- Todo lo publicable: `powershell -File herramientas/empaquetar.ps1` → `dist/` (ignorado por git). Se niega a
  seguir si falta un archivo del controlador o su huella no coincide con `SHA256SUMS.txt`.
- Un release, **una app por vez** (cada una con su versión): `powershell -File herramientas/publicar.ps1 -App
  emisor-windows|receptor-android|emisor-android -Version x.y.z` → `dist/publicar/<app>-vx.y.z/` (el archivo, su
  manifiesto `<app>.json` firmado con `herramientas/firmar-manifiesto.mjs`, `SHA256SUMS.txt`, `notas.md`). No
  publica nada: imprime los comandos de `gh` para que los corra el mantenedor. Ver `docs/actualizaciones.md`.

## Reglas

1. **Sin telemetría ni conexiones nuevas a internet.** La única salida a internet es la búsqueda de
   actualizaciones, y solo cuando el usuario pulsa «Buscar actualizaciones» (nada automático; contrato en `docs/actualizaciones.md`). Lo demás, solo red local: UDP 5601 (descubrimiento) y TCP 5600
   (transmisión). Nada de servicios de terceros ni cuentas.
2. **Apps de Android sin dependencias** (nada de AndroidX): los APK deben seguir siendo diminutos.
3. **Pruebas en verde** antes de dar algo por terminado, y pruebas para lo que se cambie.
4. **Nunca subir claves**: `keystore/`, `*.jks`, contraseñas. En ejemplos, IP de documentación o privadas
   genéricas (`10.0.0.x`), nunca de una red real.
5. **Latencia primero**: toda decisión de captura, codificación o envío se mide (captura → pantalla).
6. Un cambio en `docs/protocolo.md` se hace a la vez en los dos lados (`Protocolo.cs` del emisor y los
   `Protocolo.java` de las apps).
7. Al contribuir se acepta la licencia GPL-3.0 (ver `LICENSE`).

## Cuidados al probar

- **Instalar o quitar el controlador del monitor virtual cambia el sistema** (pide UAC): no lo hagas sin permiso
  del dueño del PC.
- **El emisor silencia el PC al transmitir con audio.** En pruebas automáticas el audio va apagado por defecto;
  no cambies ese valor por defecto de `OpcionesEmision`. Rescate: `PantallaTv.exe --quitar-silencio`.
- **El instalador escribe en el registro del usuario**: para probarlo usa siempre `--id` y `--dir` de prueba.
- Para abrir una compilación mientras el `PantallaTv.exe` instalado está corriendo: `PANTALLATV_PERFIL=prueba`
  (si no, la instancia única le pide al instalado que muestre su ventana y se cierra) y, para capturas,
  `PANTALLATV_TEMA=claro|oscuro`.
- Para probar el respaldo de captura: `PANTALLATV_FALLO_NATIVA=inicio|seguido`.
- No uses una TV ajena ni una que alguien esté mirando para pruebas (`adb logcat -s Pantalla` muestra si hay una
  sesión en curso).
