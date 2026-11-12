# Pantalla TV

Ver la pantalla y oír el sonido de un PC con Windows o de un teléfono Android en la tele, por la red local.
Software libre ([GPL-3.0](LICENSE)), en español.

## Qué partes tiene

- `receptor-android/` — la app **«Pantalla»** que recibe (Android 8+): la misma APK sirve en una tele o TV Box (con el
  control remoto) y en un teléfono o tablet (táctil). Muestra y reproduce lo que le llega.
- `emisor-windows/` — el programa del PC (**.NET 10**, `PantallaTv.exe`): ventana principal e ícono junto al reloj,
  con instalador. Captura la pantalla y el sonido, los comprime (H.264) y los manda a la tele. Puede transmitir un
  **monitor virtual** (un segundo monitor que solo existe mientras se transmite; ver `docs/monitor-virtual.md`).
- `emisor-android/` — la app **«Enviar a Pantalla»** del teléfono (Android 10+).
- `herramientas/` — scripts de empaquetado y de medición. `docs/` — protocolo y guías.

La captura es propia (duplicación de escritorio y Media Foundation, 60 fps con poca CPU); **ffmpeg no hace falta**
(si lo tienes instalado, se usa de respaldo). Solo se probó en una gráfica AMD: si tienes NVIDIA o Intel, mira
`docs/prueba-otro-pc.md`. El contenido con DRM (Netflix, etc.) se captura en negro.

## Qué NO hace

- **Sin telemetría, sin cuentas, sin servidores de terceros.** No hay estadísticas de uso ni reporte de errores.
  Lo único que sale a internet es la **búsqueda de actualizaciones**, y **solo cuando pulsas el botón** «Buscar
  actualizaciones»: ninguna app se conecta sola. Es un GET sin datos al manifiesto firmado de esa app en este
  repositorio de GitHub (detalle en «Actualizaciones», más abajo). Nada se instala sin que lo confirmes.
- Lo único que usa la red es esto, y solo dentro de tu red local:
  - **Descubrimiento**: la tele manda cada 2 s un anuncio **UDP** de difusión (broadcast) al puerto **5601**; el
    PC escucha ahí para mostrar las teles que ve (o puedes escribir la IP a mano).
  - **Transmisión**: conexión **TCP** del emisor al puerto **5600** de la tele, con el vídeo, el sonido y los
    mensajes de control. Detalle en `docs/protocolo.md`.
- La opción `--telemetria` del emisor (y «Guardar telemetría (CSV)» en «Avanzado») es un **registro local de
  rendimiento**: escribe un archivo CSV en tu carpeta de datos (fps, Mb/s, uso de CPU y memoria) para que tú lo
  revises. No envía nada a ninguna parte.
- El permiso `INTERNET` de las apps de Android es el que Android exige para abrir cualquier conexión de red,
  también las locales. Las apps solo salen a internet cuando pulsas «Buscar actualización» (ver «Actualizaciones»).
- La conexión **no va cifrada ni exige contraseña** (ver `SECURITY.md`): úsalo en una red de confianza.
- Mientras el PC transmite con sonido queda en silencio y el audio sale por la tele; al detener, vuelve.

## Actualizaciones

**Nada es automático**: ninguna app busca versiones nuevas por su cuenta. Cada una tiene un botón «Buscar
actualizaciones» y solo entonces se conecta a GitHub. Las tres apps se publican por separado (cada una con su
versión y su release). El contrato común (manifiesto, firma, reglas de verificación) y cómo se publica una versión
están en [docs/actualizaciones.md](docs/actualizaciones.md).

Lo mismo en las tres:

- **Qué consulta y a dónde**: con un GET sin cookies ni datos del equipo (User-Agent genérico, `PantallaTv/<versión>`
  y similares), el manifiesto firmado de **esa** app:
  `https://github.com/itatagroup/pantalla-tv/releases/download/actualizaciones/<app>.json` y su firma `<app>.json.sig`,
  con un tope de 64 KB y 10 s cada uno.
- **Qué comprueba**: la firma (ECDSA P-256) con la clave pública que viene dentro de la app, que el manifiesto sea
  de este repositorio, que la versión sea **mayor** que la instalada y que la descarga sea de
  `https://github.com/itatagroup/pantalla-tv/releases/download/`. Si algo no cuadra no ofrece nada y dice «No se
  pudo comprobar» (el detalle queda en el registro local).
- **Instalar**: descarga solo por HTTPS, comprueba el tamaño exacto y el SHA-256 contra el manifiesto firmado (si no
  coinciden, lo borra) y abre el instalador **visible**. Nada se instala en silencio, y nunca mientras transmites.

### Emisor de Windows

- **Dónde**: «Avanzado» › «Buscar actualizaciones» en la ventana, o «Buscar actualizaciones» en el menú del ícono.
- **Si hay versión nueva**: una banda en la ventana, «Hay una versión nueva (x.y.z): notas», con «Actualizar ahora» y
  «Después» (que la esconde hasta la próxima búsqueda). Si estás transmitiendo, espera a que termines.
- **Actualizar ahora**: descarga el instalador (`PantallaTvSetup-x.y.z.exe`) con el avance a la vista en
  `%LOCALAPPDATA%\PantallaTv\actualizaciones\` y abre el **asistente de instalación** con tus opciones actuales
  (accesos directos, inicio con Windows). Al apretar «Actualizar», el asistente cierra Pantalla TV, lo reemplaza y lo
  vuelve a abrir.
- Para probar con otro manifiesto (la firma se exige igual): `PantallaTv.exe --manifiesto-url https://…/emisor-windows.json`.

### Receptor (tele o TV Box) y emisor del teléfono

- **Dónde**: el botón «Buscar actualización» (en el receptor, en la pantalla de espera, con las flechas del control
  y OK; en el emisor del teléfono, bajo «Actualizaciones»). No aparece durante una transmisión.
- **Si hay versión nueva**: aparece «Hay una versión nueva (x.y.z)» y el botón «Actualizar». Al pulsarlo descarga la
  APK con el avance en pantalla y se la entrega a Android, que **siempre pide confirmar** la instalación. La
  primera vez Android pide además permitir «Instalar apps desconocidas» para esa app: la app lo explica y abre ese
  ajuste. Por eso piden el permiso `REQUEST_INSTALL_PACKAGES`.
- Para probar el receptor con otro manifiesto (la firma se exige igual):
  `adb shell am start -n cl.itatagroup.pantalla/.Receptor --es manifiesto_url https://…/receptor-android.json`.

## Requisitos

- **PC**: Windows 10 u 11 de 64 bits. El instalador trae el runtime de .NET dentro; no hace falta instalar nada
  más. Para compilarlo, el SDK de .NET 10.
- **Tele, TV Box, teléfono o tablet que recibe**: Android 8.0 o superior (`minSdk` 26) en la misma red local que el PC. Para el emisor del teléfono,
  Android 10 o superior.
- Para compilar las apps de Android: Node.js, un JDK y el SDK de Android (herramientas de compilación y una plataforma).

### En un teléfono o tablet

«Pantalla» aparece en el lanzador normal. En la espera se puede girar el equipo; al llegar una transmisión se pone
en horizontal o vertical según la imagen, en pantalla completa y sin apagarse. El botón «Orientación»
(automática → horizontal → vertical) la fija antes de conectar o durante la sesión, y se recuerda. Tocar la pantalla
muestra unos segundos una barra con «Búfer», «Orientación» y «Terminar»; Atrás también termina la sesión.
«Terminar» (o Atrás, también en la tele) le avisa al PC o al teléfono que transmite, que se detiene sin
reintentar aunque tenga activado «Reconectar». Si el teléfono que transmite se gira, la imagen se adapta sola.

## Instalar

1. **PC**: descarga `PantallaTvSetup-<versión>.exe` y ábrelo. Si Windows muestra **SmartScreen** («Windows protegió
   su PC»), es porque el instalador **no tiene firma de código**: aprieta **«Más información» › «Ejecutar de todas
   formas»**. Puedes comprobar la descarga antes (ver `docs/verificar.md`). Queda un ícono junto al reloj: clic
   derecho › *Transmitir a …*. La casilla «Instalar el monitor virtual» (marcada por defecto) pide permiso de
   administrador e instala el controlador Virtual-Display-Driver incluido; al desinstalar se pregunta si quitarlo.
   Si algo no funciona: clic derecho › *Diagnóstico para enviar…* (o `PantallaTv.exe --diagnostico`).
2. **Tele**: instala `pantalla-receptor-<versión>.apk` y ábrela («Pantalla»). Hay que permitir instalar apps de
   **orígenes desconocidos** a la app desde la que la abras (Ajustes › Seguridad), o instalarla con
   `adb install`. El botón «Búfer» de la pantalla de espera (o **OK** del control durante una transmisión)
   elige cuánto guarda la tele antes de mostrar (ninguno / 0,5 / 1 / 2 s; 0,5 s por defecto): más búfer, menos
   tirones con un Wi-Fi malo, a cambio de ver la imagen con ese atraso. Vale desde la próxima transmisión. Es el
   único lugar donde se elige; el monitor virtual del PC va siempre sin búfer.
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
