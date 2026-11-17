# Componentes de terceros

Pantalla TV es software libre bajo GPL-3.0 (ver `LICENSE`). Usa o incluye estos componentes de terceros, cada uno
con su propia licencia.

## Incluido en el instalador

### Virtual-Display-Driver (monitor virtual)

- Proyecto: <https://github.com/VirtualDrivers/Virtual-Display-Driver> («VDD by MTT»).
- Licencia: **MIT** (copia en `emisor-windows/controlador/LICENSE`).
- Qué se incluye: `MttVDD.inf`, `MttVDD.dll` y `mttvdd.cat`, **sin cambios**, de la release oficial 25.7.23. Van
  dentro de `PantallaTv.exe` y se instalan solo si el usuario marca «Instalar el monitor virtual». El controlador
  está firmado por SignPath Foundation.
- Origen, huellas SHA-256 y cómo actualizarlo: `emisor-windows/controlador/LEEME.md` y `SHA256SUMS.txt`.

### Paquetes NuGet del emisor de Windows

Se compilan dentro de `PantallaTv.exe` (`emisor-windows/PantallaTv.Emisor/PantallaTv.Emisor.csproj`).

| Paquete | Versión | Licencia |
|---|---|---|
| NAudio.Wasapi | 2.2.1 | MIT |
| NAudio.Core (dependencia) | 2.2.1 | MIT |
| Vortice.Direct3D11 | 3.8.3 | MIT |
| Vortice.D3DCompiler | 3.8.3 | MIT |
| Vortice.MediaFoundation | 3.8.3 | MIT |
| Vortice.DXGI, Vortice.DirectX (dependencias) | 3.8.3 | MIT |
| Vortice.Mathematics (dependencia) | 2.1.0 | MIT |
| SharpGen.Runtime y SharpGen.Runtime.COM (dependencias) | 2.4.2-beta | MIT |

Las apps de Android (`receptor-android/` y `emisor-android/`) **no usan ninguna dependencia**: solo la API de Android.

## No incluido (opcional)

### ffmpeg

- Proyecto: <https://ffmpeg.org>. Licencia LGPL-2.1 o posterior, o GPL, según la compilación.
- **No se incluye en el instalador.** Desde la 0.7.0 la captura y la codificación son propias y no lo necesitan. Si el
  usuario ya tiene un `ffmpeg.exe` (junto a `PantallaTv.exe`, en `%LocalAppData%\PantallaTv\ffmpeg` o en el PATH), el
  programa lo usa como **respaldo** cuando la captura propia no funciona en su PC, ejecutándolo como proceso aparte.
- **Descarga opcional (desde la 0.12.0).** En «Avanzado › Descargar ffmpeg», y solo si el usuario lo confirma, el emisor de
  Windows baja una versión **fijada** en el código (`DescargadorFfmpeg.Fijado`):
  - Qué: ffmpeg **9.0.2**, compilación «essentials» de Gyan Doshi (<https://www.gyan.dev/ffmpeg/builds/>), archivo
    `ffmpeg-9.0.2-essentials_build.zip` (114 768 076 bytes) del release `9.0.2` del repositorio
    <https://github.com/GyanD/codexffmpeg>, por https.
  - Por qué esa: es la compilación para Windows más usada y estable, trae x264 (la tubería por CPU de respaldo lo necesita)
    y publica la huella de cada archivo. Su licencia es **GPL v3** (por x264 y otras bibliotecas), compatible con la GPL-3.0
    de este proyecto; el código fuente y la licencia completa están en las páginas de arriba y dentro del zip.
  - Verificación: SHA-256 `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`, escrito en el código (no se
    lee de un archivo bajado del mismo sitio). Se obtuvo sin bajar el binario, de dos fuentes que coinciden: el campo
    `digest` de la API de releases de GitHub y el `.sha256` publicado por gyan.dev. Si no coincide el tamaño ni la huella,
    se borra todo y no se instala nada.
  - De ese zip solo se extrae `ffmpeg.exe`, a `%LocalAppData%\PantallaTv\ffmpeg` (se puede escribir sin administrador).
    Para cambiar de versión hay que cambiar tamaño, huella y ruta juntos en el código.
- `herramientas/empaquetar.ps1 -ConFfmpeg` permite a quien arma su propio instalador adjuntar un `ffmpeg.exe`
  suyo; en ese caso, quien lo distribuya debe cumplir la licencia de esa compilación.

### VB-CABLE (salida de sonido virtual)

- Proyecto: <https://vb-audio.com/Cable/>, de **VB-Audio Software (Vincent Burel)**. VB-CABLE estándar es
  **donationware**: gratuito para uso personal, con la opción de donar. Su licencia
  (<https://vb-audio.com/Services/licensing.htm>, leída el 16/11/2026) permite instalarlo junto a una aplicación si la
  persona puede ver que es de VB-Audio y tiene la opción de donar; en empresas el que lo distribuye compra licencias. Solo
  el VB-CABLE estándar, nunca A+B ni C+D.
- Para qué: con VB-CABLE, lo que suena en el monitor virtual sale solo por la tele y **el PC sigue sonando** con lo demás
  (sin él, el PC se silencia mientras transmite). Es opcional.
- **No se incluye en el instalador.** Se descarga **solo si la persona lo pide**: casilla (desmarcada) del instalador o
  «Avanzado › Instalar VB-CABLE…» con confirmación. Versión **fijada** en el código (`DescargadorVbCable.Fijado`):
  - Qué: `VBCABLE_Driver_Pack46.zip` (1 324 680 bytes) de <https://download.vb-audio.com/Download_CABLE/>, por https.
  - Verificación: SHA-256 `bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90` y firma Authenticode de
    `VBCABLE_Setup_x64.exe` (2.1.5.8) válida para Windows y con el certificado de «BUREL VINCENT Entrepreneur individuel»
    (huella `A77952D93229D0EC36E2543081EEA7D125732B9C`). Si VB-Audio cambia el paquete con el mismo nombre, la huella
    deja de coincidir y no se instala nada: hay que actualizar la versión fijada.
  - Instalación: su propio instalador con permiso de administrador (`-i -h`); como VB-CABLE se pone solo como salida de
    sonido por defecto, Pantalla TV devuelve la que había. Queda en `%LocalAppData%\PantallaTv\vbcable` con una marca;
    al desinstalar Pantalla TV se pregunta si quitarlo (`-u -h`).
  - Créditos y enlace para donar: en la ventana («Avanzado»), en la confirmación y en el instalador.

## Solo para desarrollo y pruebas (no van en el instalador)

| Paquete | Versión | Licencia | Dónde |
|---|---|---|---|
| FFmpeg.AutoGen | 9.0.1.1 | MIT | `PantallaTv.Captura.Prueba` (herramienta de medición) |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT | proyectos de pruebas |
| xunit, xunit.runner.visualstudio | 2.9.3, 3.0.2 | Apache-2.0 | proyectos de pruebas |

`herramientas/` y `emisor-windows/PantallaTv.Captura.Prueba/descargar-ffmpeg-lgpl.ps1` descargan, a pedido y
verificando su huella, una compilación LGPL de ffmpeg solo para esa herramienta de medición; no forma parte del
producto.
