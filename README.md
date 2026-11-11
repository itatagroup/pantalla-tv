# Pantalla TV

Ver la pantalla y oír el sonido de un PC con Windows o de un teléfono Android en la tele, por la red local.
Software libre ([GPL-3.0](LICENSE)), en español.

## Qué partes tiene

- `receptor-android/` — la app **«Pantalla»** de la tele o TV Box (Android 8+). Muestra y reproduce lo que le llega.
- `emisor-windows/` — el programa del PC (**.NET 10**, `PantallaTv.exe`): ventana principal e ícono junto al reloj,
  con instalador. Captura la pantalla y el sonido, los comprime (H.264) y los manda a la tele. Puede transmitir un
  **monitor virtual** (un segundo monitor que solo existe mientras se transmite; ver `docs/monitor-virtual.md`).
- `emisor-android/` — la app **«Enviar a Pantalla»** del teléfono (Android 10+).
- `herramientas/` — scripts de empaquetado y de medición. `docs/` — protocolo y guías.

La captura es propia (duplicación de escritorio y Media Foundation, 60 fps con poca CPU); **ffmpeg no hace falta**
(si lo tienes instalado, se usa de respaldo). Solo se probó en una gráfica AMD: si tienes NVIDIA o Intel, mira
`docs/prueba-otro-pc.md`. El contenido con DRM (Netflix, etc.) se captura en negro.

## Qué NO hace

- **Sin telemetría, sin cuentas, sin servidores de terceros.** No se conecta a internet: no hay actualizaciones
  automáticas, ni estadísticas de uso, ni reporte de errores. Está planeada una **búsqueda de actualizaciones
  opcional y que se puede apagar**: cuando llegue, esta sección dirá exactamente qué consulta, a dónde, y cómo
  desactivarla.
- Lo único que usa la red es esto, y solo dentro de tu red local:
  - **Descubrimiento**: la tele manda cada 2 s un anuncio **UDP** de difusión (broadcast) al puerto **5601**; el
    PC escucha ahí para mostrar las teles que ve (o puedes escribir la IP a mano).
  - **Transmisión**: conexión **TCP** del emisor al puerto **5600** de la tele, con el vídeo, el sonido y los
    mensajes de control. Detalle en `docs/protocolo.md`.
- La opción `--telemetria` del emisor (y «Guardar telemetría (CSV)» en «Avanzado») es un **registro local de
  rendimiento**: escribe un archivo CSV en tu carpeta de datos (fps, Mb/s, uso de CPU y memoria) para que tú lo
  revises. No envía nada a ninguna parte.
- El permiso `INTERNET` de las apps de Android es el que Android exige para abrir cualquier conexión de red,
  también las locales; no se usa para salir a internet.
- La conexión **no va cifrada ni exige contraseña** (ver `SECURITY.md`): úsalo en una red de confianza.
- Mientras el PC transmite con sonido queda en silencio y el audio sale por la tele; al detener, vuelve.

## Requisitos

- **PC**: Windows 10 u 11 de 64 bits. El instalador trae el runtime de .NET dentro; no hace falta instalar nada
  más. Para compilarlo, el SDK de .NET 10.
- **Tele**: Android 8.0 o superior (`minSdk` 26) en la misma red local que el PC. Para el emisor del teléfono,
  Android 10 o superior.
- Para compilar las apps de Android: Node.js, un JDK y el SDK de Android (herramientas de compilación y una plataforma).

## Instalar

1. **PC**: descarga `PantallaTvSetup-<versión>.exe` y ábrelo. Si Windows muestra **SmartScreen** («Windows protegió
   su PC»), es porque el instalador **no tiene firma de código**: aprieta **«Más información» › «Ejecutar de todas
   formas»**. Puedes comprobar la descarga antes (ver `docs/verificar.md`). Queda un ícono junto al reloj: clic
   derecho › *Transmitir a …*. La casilla «Instalar el monitor virtual» (marcada por defecto) pide permiso de
   administrador e instala el controlador Virtual-Display-Driver incluido; al desinstalar se pregunta si quitarlo.
   Si algo no funciona: clic derecho › *Diagnóstico para enviar…* (o `PantallaTv.exe --diagnostico`).
2. **Tele**: instala `pantalla-receptor-<versión>.apk` y ábrela («Pantalla»). Hay que permitir instalar apps de
   **orígenes desconocidos** a la app desde la que la abras (Ajustes › Seguridad), o instalarla con
   `adb install`. El botón **OK** del control cambia el retardo (sin retardo / 0,5 / 1 / 2 s).
3. **Teléfono**: instala `pantalla-emisor-<versión>.apk`, abre «Enviar a Pantalla» y elige la tele.

## Compilar

Todo lo publicable de una vez (instalador y APK, en `dist/`, que Git ignora):

```powershell
powershell -File herramientas/empaquetar.ps1   # corre las pruebas, publica el emisor y compila los APK
```

Por partes:

```bash
# Emisor de Windows (las advertencias del compilador son error)
cd emisor-windows
dotnet build PantallaTv.sln -c Release
dotnet test  PantallaTv.sln -c Release

# Apps de Android (sin Gradle ni dependencias: solo el SDK de Android y un JDK)
cd receptor-android && node tools/build.mjs      # deja out/receptor.apk
cd ../emisor-android && node tools/build.mjs     # deja out/emisor.apk
```

El build de Android busca el SDK en `ANDROID_HOME` (en Windows, por defecto el de Android Studio) y el JDK en
`JAVA_HOME`. **Los APK se firman con tu propia clave**, que no está en el repositorio. Créala una vez por app
(la carpeta `keystore/` está en `.gitignore`):

```bash
cd receptor-android
mkdir keystore
keytool -genkeypair -keystore keystore/receptor.jks -alias k -keyalg RSA -keysize 2048 -validity 10000
echo "TU_CLAVE" > keystore/password.txt     # la misma clave que le diste al keystore
# lo mismo en emisor-android/ con keystore/emisor.jks
```

(`keystore/<nombre>.jks`, con `nombre` de `version.json`; también sirven las variables `APP_KEYSTORE`,
`APP_KEYSTORE_PASS` y `APP_KEY_ALIAS`.) **Guarda un respaldo de tu clave**: sin ella no podrás publicar
actualizaciones que se instalen encima de la versión anterior. Nunca la subas al repositorio.

## Créditos

- **[Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)** (licencia MIT): el
  controlador del monitor virtual, que se incluye en el instalador **sin cambios**.
- **Proyectos que se estudiaron para el diseño de la captura y la codificación.** Se leyó su código fuente para
  entender qué hacen y por qué; son **ideas de diseño, sin copiar código** (los tres son GPL-3.0):
  - [Sunshine](https://github.com/LizardByte/Sunshine): captura por duplicación de escritorio, conversión de
    BGRA a NV12 en la GPU con sombreadores, ajustes de baja latencia del codificador (sin cuadros B, búfer de
    control de tasa de un cuadro) y recodificar el último cuadro con la pantalla quieta.
  - [Apollo](https://github.com/ClassicOldSong/Apollo) (fork de Sunshine con monitor virtual propio): estudiado
    para comparar el manejo del monitor virtual; usa su propio controlador, distinto del que se incluye aquí.
  - [Moonlight Android](https://github.com/moonlight-stream/moonlight-android): estudiado para comparar cómo
    decodifica en Android con `MediaCodec`.
- **[ffmpeg](https://ffmpeg.org)** (opcional, **no incluido**): si lo tienes instalado, se usa de respaldo.

Las licencias de todo lo que se usa: [THIRD-PARTY.md](THIRD-PARTY.md).

## Contribuir, seguridad y licencia

- Issues y pull requests bienvenidos: [CONTRIBUTING.md](CONTRIBUTING.md). Vulnerabilidades: [SECURITY.md](SECURITY.md).
- Licencia: **GPL-3.0** ([LICENSE](LICENSE)). Componentes de terceros: [THIRD-PARTY.md](THIRD-PARTY.md).
- Verificar descargas: [docs/verificar.md](docs/verificar.md).
