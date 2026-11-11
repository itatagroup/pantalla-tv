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
- **No se incluye ni se descarga** con Pantalla TV. Desde la 0.7.0 la captura y la codificación son propias y no
  lo necesitan. Si el usuario ya tiene un `ffmpeg.exe` (junto a `PantallaTv.exe` o en el PATH), el programa lo
  usa como **respaldo** cuando la captura propia no funciona en su PC, ejecutándolo como proceso aparte.
- `herramientas/empaquetar.ps1 -ConFfmpeg` permite a quien arma su propio instalador adjuntar un `ffmpeg.exe`
  suyo; en ese caso, quien lo distribuya debe cumplir la licencia de esa compilación.

## Solo para desarrollo y pruebas (no van en el instalador)

| Paquete | Versión | Licencia | Dónde |
|---|---|---|---|
| FFmpeg.AutoGen | 9.0.1.1 | MIT | `PantallaTv.Captura.Prueba` (herramienta de medición) |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT | proyectos de pruebas |
| xunit, xunit.runner.visualstudio | 2.9.3, 3.0.2 | Apache-2.0 | proyectos de pruebas |

`herramientas/` y `emisor-windows/PantallaTv.Captura.Prueba/descargar-ffmpeg-lgpl.ps1` descargan, a pedido y
verificando su huella, una compilación LGPL de ffmpeg solo para esa herramienta de medición; no forma parte del
producto.
