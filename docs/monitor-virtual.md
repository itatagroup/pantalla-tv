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

**Si el monitor ya estaba conectado** (lo conectó la persona o, tras instalar el controlador, Windows, que lo deja en el
primer modo del controlador: 3840×2160), `Conectar` ya no lo daba por bueno sin mirar: desde la 0.12.0 le pone el modo
pedido (sin moverlo de sitio) y, al terminar, le devuelve el que tenía (`MonitorVirtual.AjustarModoDelConectado` y
`RestaurarModoAnterior`). Esa era la causa más probable del 3840×2160 del portátil de César: antes solo se aplicaba el modo
cuando el monitor lo conectaba el propio programa. Además, tras conectarlo, el registro anota el tamaño con que quedó de
verdad si no es el pedido.

Para fijar otro: **Avanzado › Monitor virtual** (la lista muestra los modos que ofrece el controlador; se guarda en
`ModoMonitorVirtual` de los ajustes). Por línea de comandos: `--monitor virtual --modo-virtual 1280x720@60`. Con una
configuración de la 0.10 o anterior la lista solo trae dos modos: «Reinstalar» la actualiza.

## Pendiente

- **Sonido por aplicación** (WASAPI por proceso, Windows 10 2004+): sin hacer.
- **El cursor del ratón** sale en la imagen cuando está sobre el monitor transmitido.
- Probar con un monitor de otro adaptador.
- El contenido con DRM se captura en negro, igual que en el monitor real.

## Portátil con dos gráficas (0.12.0; diagnóstico de César, 13/11/2026)

En un portátil AMD + NVIDIA el monitor virtual falló de tres maneras. Las tres están tratadas en código y con pruebas, **pero
ninguna se pudo probar de punta a punta sin un portátil así** (ver «Qué no se pudo probar»).

1. **«Duplicar» bloquea el monitor virtual** (`ChangeDisplaySettingsEx` daba -1). Antes de conectarlo,
   `Topologia.Actual()` consulta la topología con `QueryDisplayConfig` (`QDC_DATABASE_CURRENT`); si es «Duplicar», pasa a
   «Extender» con `SetDisplayConfig(SDC_TOPOLOGY_EXTEND | SDC_APPLY)`, la misma API de Win+P. Al soltar el monitor vuelve a
   «Duplicar» (solo si sigue en «Extender»: si la persona la cambió entretanto, se respeta). La marca en disco
   (`monitor-virtual-conectado.marca`) lleva una segunda línea `duplicar`, así que si el programa muere a medias, el
   siguiente arranque también vuelve a «Duplicar». La persona ve un aviso (globo y registro): se pasó a «Extender» y, para
   ver en la tele el mismo monitor que en el PC, hay que desmarcar el monitor virtual. Si `SetDisplayConfig` falla, el error
   dice en palabras que hay que hacerlo con Win+P → Extender (no un código suelto) y no se transmite. El diagnóstico anota
   la topología y el aviso.
2. **El virtual quedó en 3840×2160 y ningún codificador lo aceptó.**
   - (a) La resolución: era el caso del monitor ya conectado (arriba, «Resolución del monitor virtual»); ahora se lleva al
     modo de la tele y se devuelve después.
   - (b) El codificador se elige por la **gráfica dueña de la salida** capturada. `PlanCodificador.Ordenar` (función pura,
     con pruebas) da el orden: 1) la gráfica de la salida, con su codificador abierto sobre el mismo dispositivo D3D11; 2) cada
     otra gráfica que tenga codificador de hardware (de más a menos memoria de vídeo, sin las «por software»), **copiando el
     cuadro** a ella; 3) el respaldo con ffmpeg (la tubería nativa lanza `NativaNoDisponibleException`, como siempre). Cada
     intento queda en el registro y en `--diagnostico` con su HRESULT («nativa: intento 2/2 (copiar el cuadro a otra
     gráfica…): no funcionó: … (0x…)»). La copia (`PuenteGpu`) pasa el cuadro ya convertido a NV12 por la memoria del sistema
     (textura de lectura en la gráfica de origen → textura de escritura en la de destino): una textura compartida entre
     adaptadores no es fiable con NV12. Cuesta ~3 MB por cuadro a 1080p en cada sentido (12 MB a 4K). La conversión y el
     cursor siguen en la gráfica que dibuja el monitor.
3. **ffmpeg de respaldo se descarga solo desde el emisor**, con confirmación: ver abajo.

### El monitor virtual en la gráfica de la pantalla física

Con `<friendlyname>default</friendlyname>` en `vdd_settings.xml` Windows elige la gráfica del monitor virtual y en un portátil
AMD + NVIDIA suele elegir la NVIDIA: la captura copia cada cuadro a la AMD (≈0,4 núcleos). Con el nombre de la gráfica que
dibuja la pantalla física (la descripción DXGI, p. ej. `AMD Radeon(TM) Graphics`) el monitor virtual queda en la misma gráfica,
sin copia (medido: 64–65 fps, ≈0,1 núcleos).

- **Al instalar** se escribe la configuración con `Controlador.ElegirGpu`: `default` si hay una sola gráfica de hardware (se
  cuentan por nombre, DXGI puede listar la misma dos veces; las «por software» no cuentan) y, con más de una, la que dibuja la
  pantalla principal. Una configuración nuestra se reconoce con cualquier `friendlyname`, así que reinstalar la actualiza.
- **Si ya estaba instalado** y al conectar el monitor lo dibuja otra gráfica, queda en el registro, en el globo de la
  transmisión, en la nota de «Avanzado» y en `--diagnostico`, y aparece el botón «Mover el monitor virtual a la gráfica de la
  pantalla» (`--reubicar-monitor-virtual`, pide administrador: reescribe la configuración y reinicia el dispositivo). Si la
  configuración la cambió la persona (algo más que `friendlyname`), no se toca: el aviso dice que hay que cambiarlo a mano.
- **Si no se sabe cuál dibuja la principal** (DXGI no responde, o `PantallaTv.exe` tiene una preferencia de gráfica en
  Configuración › Pantalla › Gráficos, que hace que DXGI cuelgue las salidas de la preferida) no se cambia la gráfica que ya
  tenía la configuración; mover el monitor falla con un mensaje que dice por qué.
- Probado en el portátil (15/11/2026): con `default` el aviso salió al conectar; `--reubicar-monitor-virtual` (UAC) escribió
  la AMD, reinició el dispositivo y el virtual quedó en la AMD, sin aviso, transmitiendo a la tele por la misma gráfica.

### ffmpeg: descarga desde el emisor

«Avanzado › Descargar ffmpeg» (aparece si no hay un `ffmpeg.exe` junto al programa, en la carpeta de datos ni en el PATH).
Pide confirmación mostrando de dónde, qué versión, cuánto pesa y la licencia. Baja una versión **fijada** por https,
comprueba que tenga el tamaño y el SHA-256 puestos en el código, extrae **solo** `ffmpeg.exe` a
`%LocalAppData%\PantallaTv\ffmpeg\` (se puede escribir sin administrador, aunque el programa esté en Archivos de programa) y
borra el zip. Con progreso y botón para cancelar; si algo no cuadra no queda nada en disco. Fuente, versión y licencia:
`THIRD-PARTY.md`. Es la segunda conexión a internet del programa y solo ocurre si la persona la pide.

### Qué no se pudo probar

Se probó con lo que hay en un PC con una sola gráfica (AMD): el orden del plan, el paso de «Duplicar» a «Extender» como
lógica (la consulta de la topología es solo lectura; **no se cambió** la topología real), el modo de un monitor ya
conectado como lógica, la copia de un cuadro NV12 entre dos dispositivos D3D11 de la misma gráfica (bytes idénticos), la
tubería nativa de punta a punta con el plan, y la descarga contra un servidor falso. **Falta, en el portátil de dos gráficas:**

- Que «Duplicar» → «Extender» → monitor virtual → transmitir → soltar → «Duplicar» ocurra de verdad (y que la consulta
  `QDC_DATABASE_CURRENT` devuelva «Duplicar» allí; si devolviera otra cosa, no se cambiaría nada y el error de antes seguiría).
- Que Windows no deje el monitor virtual conectado en un modo distinto del pedido.
- Que el codificador de la NVIDIA acepte el cuadro (intento 1) o, si no, que la copia a la otra gráfica funcione (intento 2).
  Ojo: el diseño del NV12 en la memoria de lectura (la croma a `paso × alto` del comienzo) se verificó en AMD; otra
  gráfica con un alto alineado distinto daría una imagen con la croma corrida. Mirar el registro: cada intento y su HRESULT.
- Lo que cuesta la copia por CPU a 1080p60 (esperable: algún punto de CPU y un cuadro más de latencia).

### Probado en un portátil AMD + NVIDIA (15/11/2026)

Ryzen 5 8645HS con Radeon 760M (integrada, dibuja el principal a 144 Hz) y RTX 3050 6 GB Laptop (controlador
610.91); el monitor virtual de MTT queda colgado de la NVIDIA. Hubo que arreglar dos cosas:

1. **La nativa se descartaba con «Arithmetic operation resulted in an overflow»** antes de probar ningún codificador:
   `DedicatedVideoMemory` (un `PointerUSize` de SharpGen) pasaba por una conversión *checked* a `uint`, y 6 GB no caben.
   Ahora se lee con `Gpu.MemoriaDedicada` (también en `--diagnostico`). Cualquier gráfica de más de 4 GB lo provocaba.
2. **En el monitor virtual la tele se quedaba congelada**: DXGI entregaba los cambios con `LastPresentTime = 0` y
   `AccumulatedFrames > 0`, y se tomaban por «solo ratón». Ahora cuenta como cuadro nuevo cualquiera de los dos (como
   Sunshine); con el virtual quieto siguen llegando 0–2 por segundo.

Qué pasa con el plan del codificador en ese portátil (se ve en el registro):

- Intento 1 (la NVIDIA, misma gráfica) e intento 2 (la otra entrada de la NVIDIA en DXGI): la **MFT de NVIDIA no se deja
  crear** (`E_UNEXPECTED` en `ActivateObject`, y también con `CoCreateInstance` directo), y la de AMD no acepta un
  dispositivo de la NVIDIA (`E_FAIL` en `SetD3DManager`). Los errores dicen ahora en qué paso falló cada MFT.
- **Intento 3 (copiar a la AMD) funciona.** 60 s a la tele con la fuente de 60 fps en el virtual: 64–65 fps enviados,
  0 tirados, 0 reinicios, codificador 19,5 ms de media, 0,7–0,9 Mb/s, ~0,4 núcleos (copia por CPU incluida).
- `MFTEnum2` con `MFT_ENUM_ADAPTER_LUID` **no filtra** en ese PC: devuelve las dos MFT para cualquier gráfica.
- La AMD 760M **no retiene** cuadros (la del PC de pruebas sí): el detector lo vio solo.
- **La MFT de NVIDIA solo se crea si Windows asigna el proceso a la NVIDIA** (Configuración → Pantalla → Gráficos →
  «Alto rendimiento»). Con esa preferencia el virtual codifica en la NVIDIA sin copia (≈ 0,1 núcleos, pero 52–55 fps,
  25 ms, retiene un cuadro), la MFT de AMD deja de crearse (`E_OUTOFMEMORY`) y **la duplicación del principal falla**
  (`DXGI_ERROR_UNSUPPORTED`: Desktop Duplication tiene que correr en la gráfica que dibuja el monitor), así que el
  principal cae a `amf-gpu`. Sin preferencia (lo normal) la nativa sirve en los dos monitores: **no hay que pedírsela
  a nadie**. Usar las dos gráficas a la vez necesitaría un proceso aparte por gráfica.

**«Duplicar» → «Extender», probado:** con el virtual conectado se puso «Duplicar» (como Win+P) y se desconectó; Windows
siguió diciendo «Duplicar» (`QDC_DATABASE_CURRENT` lo ve). Al transmitir al virtual el emisor pasó a «Extender», conectó
y transmitió con la nativa (64–65 fps, 0 tirados). **Al terminar no vuelve a «Duplicar»** si solo hay una pantalla física:
al quitar el virtual Windows dice «solo la pantalla del PC» y `RestaurarDuplicar` lo toma como un cambio de la persona.
Queda guardado «Extender» para la pareja pantalla + virtual (la próxima vez no hace falta cambiar nada). Con un monitor
externo duplicado sí volvería (no probado).

## Velocidad y búfer

- **60 fps**: la tubería `amf` de ffmpeg termina el filtro con `format=nv12` (conversión en CPU, ~2 núcleos).
  Antes daba ~37 fps; la causa no era el monitor virtual sino el hilo único que copia el cuadro BGRA. Desde la
  0.7.0 la captura propia («nativa») evita esa copia.
- **El virtual va sin búfer**: el búfer lo elige la tele (0,5 s por defecto), pero cuando el monitor es el virtual
  el emisor marca la sesión como interactiva (bandera del `HELLO`, ver `protocolo.md`) y la tele la muestra sin
  búfer, para que el ratón no se atrase. Desde la 0.9.0 el emisor ya no tiene opción de retardo.
