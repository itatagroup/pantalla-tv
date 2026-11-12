# Monitor virtual: proyectar sin ocupar la pantalla del PC

Para «proyectar una app y seguir usando el PC»: en vez de capturar una ventana, Windows tiene **un segundo
monitor que no existe físicamente**, se arrastra ahí lo que se quiere ver en la tele (o se trabaja en él) y
Pantalla TV transmite ese monitor. Sirve además como **segundo monitor inalámbrico**.

El monitor lo crea el controlador [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
(«VDD by MTT», licencia MIT, firmado por SignPath Foundation), que va **dentro del instalador**. Ver también
`THIRD-PARTY.md`.

## Estado: funciona a 1080p y 60 fps

Medido con una carta de ajuste en movimiento en el monitor virtual, 45 s, en un PC con gráfica AMD:

| Qué | Resultado |
|---|---|
| Se ve en `--monitores` | Monitor 1, `\\.\DISPLAYn`, «VDD by MTT» (el nombre lo declara su EDID), 1920×1080 |
| Lo que se ve en la TV | El monitor virtual, no el escritorio principal |
| PC | 60,0 fps de media (mín. 59,2), 8,0 Mb/s |
| TV | 60,0 fps de media (mín. 59,1), 0 saltos, retardo ~558 ms (con el búfer de 0,5 s) |
| Caídas | Ninguna |

## Defecto encontrado y corregido: la captura de GPU de AMD no ve el monitor virtual

La tubería de ffmpeg `amf-gpu` (captura `vsrc_amf`) con `monitor_index=1` devolvía una imagen del tamaño del
monitor virtual pero con el contenido del principal, sin fallar ni avisar. La duplicación de escritorio
(`ddagrab`) sí captura el monitor pedido.

Arreglo: **`amf-gpu` solo se usa para el monitor principal**; cualquier otro usa `ddagrab`
(`Tuberias.Candidatas`, con una prueba de regresión).

## Cómo se usa

El monitor virtual **no está conectado al escritorio mientras no se transmite**: abrir el programa no agrega
ningún monitor ni mueve ventanas. Se conecta solo al transmitir y se desconecta al terminar.

1. Abrir «Pantalla» en la tele.
2. En la ventana o en la bandeja, «Qué pantalla se transmite» → **«Monitor virtual (se conecta solo al transmitir)»**
   (aparece si el controlador está instalado; si no, ahí mismo está «Instalar el monitor virtual…») y *Transmitir a …*.
   Tarda unos segundos en conectarlo (1920×1080 a 60 Hz, a la derecha de las demás pantallas). En consola:
   `--monitor virtual`.
3. Arrastrar al monitor virtual la ventana que se quiere ver en la tele.
4. Al detener, salir o tras un cierre brusco queda desconectado: la marca `%AppData%\PantallaTv\monitor-virtual-conectado.marca`
   (con el PID del dueño) se escribe antes de conectar y, si el proceso murió, el siguiente arranque lo
   desconecta. Si el monitor ya estaba conectado a mano, no se toca.
5. Si se eligió «virtual» y el controlador ya no está, se transmite la pantalla principal.
6. Sonido: viaja **todo** el sonido del PC y, por defecto, se silencian los parlantes. Para seguir usando el PC
   con sonido propio, desmarcar **«Silenciar el PC mientras transmite»** o **«Enviar también el sonido»**.
   Separar el sonido de una sola aplicación está pendiente.

Diagnóstico por consola: `--monitores` (indica si el virtual está instalado/conectado) y
`--monitor-virtual estado|conectar|desconectar` (lo conectado así se queda a mano).
La lista de pantallas se pregunta a Windows en cada llamada (`EnumDisplayDevices`), no a `Screen.AllScreens`,
que WinForms guarda en caché. El monitor elegido se guarda por su nombre de Windows; si ya no está, cae al principal.

## El controlador viene en el instalador

El controlador va **dentro de `PantallaTv.exe`** como recurso: `emisor-windows/controlador/` (versión, origen,
huellas y cómo actualizarlo en su `LEEME.md`). Se usa la release oficial 25.7.23, cuyos archivos son idénticos a
la 24.12.24.

- **Asistente**: casilla «Instalar el monitor virtual (para proyectar una ventana y seguir usando el PC)», marcada
  por defecto y oculta si el monitor ya está. Solo eso pide administrador: el programa se relanza elevado con
  `--instalar-controlador`. Si se rechaza el UAC, el resto queda instalado y se avisa. Sin ventanas:
  `--instalar --silencioso --con-monitor-virtual`.
- **Cómo se instala** (`Controlador.cs`, sin `devcon` ni otros binarios): se extraen los archivos a
  `%windir%\Temp`, se crea el nodo raíz `Root\MttVDD` (`ROOT\DISPLAY\000N`) con SetupAPI y se le instala el INF
  con `UpdateDriverForPlugAndPlayDevices`. Se escribe `C:\VirtualDisplayDriver\vdd_settings.xml` (o en la carpeta de
  `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH`) con un monitor y los tamaños comunes de teles,
  tabletas y teléfonos (de 1280×720 a 3840×2160, cada uno a 60, 50 y 30 Hz; lista en `Controlador.TamanosPropios`),
  **solo si no hay uno** o si es el que escribió una versión anterior sin cambios (hasta la 0.10: solo
  1920×1080/1280×720 a 60 Hz). Si el dispositivo ya existía, se deshabilita y habilita (`DIF_PROPERTYCHANGE`) para
  que lea la configuración.
- **Windows conecta y extiende el monitor recién instalado**: el proceso normal (no el elevado) espera a que
  aparezca y lo desconecta, vigilando unos segundos por si Windows lo vuelve a conectar.
- **Marca de propiedad**: `ControladorInstaladoPorNosotros = 1` en la clave de desinstalación, solo si el
  dispositivo lo creó el instalador (si ya existía, es del usuario).
- **Desinstalar**: se desconecta el monitor si quedó conectado y, solo con la marca, se pregunta «¿Quitar también
  el monitor virtual?» (por defecto Sí). Quitar = `--quitar-controlador` elevado: `DiUninstallDevice` de cada nodo
  `Root\MttVDD` y `SetupUninstallOEMInf` de cada `oemNN.inf` del controlador (buscado por contenido: `pnputil`
  responde traducido). `vdd_settings.xml` se borra solo si sigue idéntico al que escribimos. Sin ventanas:
  `--desinstalar --silencioso --quitar-monitor-virtual`.
- **Bandeja**: si no está, «Qué pantalla se transmite» › «Instalar el monitor virtual…».
- **Avanzado › Monitor virtual** (0.11.0+): instalar, **reinstalar** (actualiza la configuración si es la nuestra y
  reinicia el dispositivo; también arregla uno que quedó a medias) o **quitar** en cualquier momento, cada uno con
  su UAC. Si falla, el aviso dice el motivo y queda en el registro (y en el diagnóstico).

### Qué se probó

En un PC con Windows 11 y gráfica AMD, con el instalador:

| Caso | Resultado |
|---|---|
| Quitar un controlador anterior con `--quitar-controlador` (UAC aprobado) | Sin dispositivo ni paquete, sin reinicio; el XML previo, intacto |
| Instalar desde cero con el monitor virtual (`--id Prueba`) | 12 s; dispositivo iniciado, marca puesta, XML propio escrito, **ningún monitor extra** |
| Transmitir el virtual a la TV (`--cli --monitor virtual --seg 20 --telemetria`) | Se conectó solo, 38–60 fps (escritorio quieto), 0 tirados en PC y TV; quedó desconectado |
| Reinstalar encima | Sin UAC, marca conservada, un solo dispositivo |
| Desinstalar con «Sí» | Ni dispositivo ni paquete; XML propio borrado, uno ajeno intacto; programa y registro fuera |
| Desinstalar con «No» | Programa fuera, controlador intacto |
| Rechazar el UAC | Salida 4, «No se dio permiso de administrador», programa instalado, sin marca ni dispositivo |
| Asistente con clics reales | Casilla marcada, UAC, **sin monitor extra al terminar** |
| Bandeja: transmitir y detener | La 1.ª vez tras instalar el monitor **quedó conectado** al detener (Windows lo reconectó); la 2.ª se quitó bien. Arreglo: `Liberar` vigila y vuelve a quitar. |

**Sin verificar:** que tras **reiniciar el PC** el monitor siga desconectado; el aviso de desinstalar interactivo
(los dos mensajes; se probó el mismo código en modo silencioso); «Instalar el monitor virtual…» desde la
bandeja; un PC con otra configuración o una máquina virtual; el UAC «por encima del hombro» (usuario sin
administrador).

## Cómo encuentra el monitor correcto

ffmpeg captura «salida M del adaptador N». `Dxgi.cs` le pregunta a Windows (DXGI) de qué adaptador cuelga cada
pantalla y `Monitores.cs` las une por nombre de dispositivo. Para un monitor de otro adaptador, `ddagrab`
recibe además el adaptador (`-init_hw_device d3d11va=cap:N`); esa rama **no se ha probado con hardware real**,
solo con una prueba unitaria (el monitor virtual probado cuelga de la misma gráfica AMD).

## Resolución del monitor virtual

Desde la 0.11.0 el monitor virtual se conecta con el modo de la tele: el receptor (0.8.0+) informa su pantalla
(`docs/protocolo.md`, «Descubrimiento») y el emisor elige, entre los que ofrece el controlador, el más cercano sin
pasarse: primero el tamaño exacto, después la misma forma (sin franjas), después el más grande, y la frecuencia más
alta que no supere la de la tele. Un teléfono o tableta en vertical se toma en horizontal (el monitor virtual siempre
lo es). En automático se limita a 1080 líneas (una tele 4K recibe 1920×1080), porque 4K a 60 Hz carga la codificación
y sube la latencia; los cuadros por segundo tampoco pasan de la frecuencia del monitor. Si la tele no informa su
pantalla (receptor anterior), 1920×1080 a 60 Hz como antes.

Para fijar otro: **Avanzado › Monitor virtual** (la lista muestra los modos que ofrece el controlador; se guarda en
`ModoMonitorVirtual` de los ajustes). Por línea de comandos: `--monitor virtual --modo-virtual 1280x720@60`. Con una
configuración de la 0.10 o anterior la lista solo trae dos modos: «Reinstalar» la actualiza.

## Pendiente

- **Sonido por aplicación** (WASAPI por proceso, Windows 10 2004+): sin hacer.
- **El cursor del ratón** sale en la imagen cuando está sobre el monitor transmitido.
- Probar con un monitor de otro adaptador.
- El contenido con DRM se captura en negro, igual que en el monitor real.

## Velocidad y búfer

- **60 fps**: la tubería `amf` de ffmpeg termina el filtro con `format=nv12` (conversión en CPU, ~2 núcleos).
  Antes daba ~37 fps; la causa no era el monitor virtual sino el hilo único que copia el cuadro BGRA. Desde la
  0.7.0 la captura propia («nativa») evita esa copia.
- **El virtual va sin búfer**: el búfer lo elige la tele (0,5 s por defecto), pero cuando el monitor es el virtual
  el emisor marca la sesión como interactiva (bandera del `HELLO`, ver `protocolo.md`) y la tele la muestra sin
  búfer, para que el ratón no se atrase. Desde la 0.9.0 el emisor ya no tiene opción de retardo.
