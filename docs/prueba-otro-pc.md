# Probar Pantalla TV en tu PC (NVIDIA, Intel o AMD)

> Al día con la 0.7.1. La captura propia («nativa») solo se probó en una gráfica AMD; falta saber cómo se
> comporta en NVIDIA e Intel. Esta guía explica qué hacer y qué mandar de vuelta para ayudar a mejorarla.

## Qué necesitas

- El instalador `PantallaTvSetup-<versión>.exe` (de los *releases* del repo o armado con
  `powershell -File herramientas/empaquetar.ps1`).
- La app «Pantalla» (`pantalla-receptor-<versión>.apk`) instalada en una tele o TV Box con Android 8 o superior.
- Opcional: un `ffmpeg.exe` junto al instalador (o instalado en el PATH, por ejemplo con
  `winget install Gyan.FFmpeg`). Sirve de respaldo si la captura propia no funciona en tu gráfica; no hace falta
  para probar la captura propia.

## Pasos (unos 10 minutos)

1. Abre el instalador. El monitor virtual es opcional (pide administrador): márcalo solo si quieres probarlo.
2. Al terminar, el asistente abre Pantalla TV y aparece su **ventana**. **Antes de transmitir**, saca el
   diagnóstico: en la ventana, abre **«Avanzado»** (abajo) → botón **«Diagnóstico para enviar…»** (o clic derecho
   en el ícono junto al reloj → el mismo nombre). Tarda unos segundos (prueba la captura 3 s en cada monitor, sin tele) y abre el
   informe en el Bloc de notas. Queda también en `%LOCALAPPDATA%\PantallaTv\diagnostico-*.txt`.
   Desde una terminal da lo mismo: `"%LOCALAPPDATA%\Programs\PantallaTv\PantallaTv.exe" --diagnostico`.
3. Abre «Pantalla» en la tele y, en la ventana del PC, elígela en **«Transmitir a»** y aprieta **«Transmitir»**,
   con todo por defecto (forma de capturar «Automática»). Pon un vídeo con movimiento 1 minuto. Mientras
   transmite, la ventana muestra cuadros por segundo, Mb/s y la **«Forma de capturar»** en uso (por ejemplo
   «Propia, sin ffmpeg (nativa)» o una de ffmpeg): anótalos, o saca una captura de la ventana.
4. Si puedes, repite con **«Guardar telemetría (CSV)»** marcado (en «Avanzado»); el CSV queda en la carpeta que
   abre **«Abrir carpeta de telemetría»**. Es un registro local de rendimiento: no se envía a ninguna parte. Los
   ajustes se cambian con la transmisión detenida.
5. Si la imagen no llega o se ve mal: detén y en **«Avanzado» → «Forma de capturar»** elige «Propia, sin ffmpeg (nativa)» y prueba de
   nuevo; después «ffmpeg · NVIDIA (nvenc)» o «ffmpeg · Intel (qsv)» si tienes ffmpeg. Anota qué funcionó. Vuelve a «Automática».
6. Saca otra vez el diagnóstico (paso 2): ahora el «Registro» del final dice qué codificador se eligió y por qué
   se cayó al respaldo, si pasó.

## Cómo mandar el resultado

Abre un *issue* en el repositorio del proyecto con: el texto del diagnóstico, tu gráfica y su controlador, lo que
viste en la ventana (fps, Mb/s, forma de capturar) y si la imagen y el sonido llegaron bien a la tele. Revisa el
diagnóstico antes de pegarlo: incluye tu versión de Windows, los nombres de tus gráficas, pantallas y dispositivo de sonido, y las últimas líneas
del registro (que pueden traer la IP de tu tele en tu red local); borra lo que prefieras no publicar.

## Cómo leer el diagnóstico

| Línea | Qué mirar |
|---|---|
| `== Gráficas` | Fabricante (NVIDIA `VEN_10DE`, Intel `VEN_8086`) y, por gráfica, **«codificadores H.264 por hardware»**. «ninguno» = la nativa no puede en esa gráfica y se usará ffmpeg. En un portátil con dos gráficas, la que tiene `salida N: … en el escritorio` es la que manda. |
| `== Prueba de la captura propia` | **FUNCIONA / NO FUNCIONA** por monitor; «primera … (clave con SPS/PPS)» y la media del codificador (en AMD ~19 ms). La última línea dice qué MFT se usó, qué ajustes **rechazó**, si hubo que **descartar** otra antes, si da **clave a pedido** (si no, una cada 4 s) y si **retiene un cuadro** (AMD sí; NVIDIA o Intel quizá no: entonces no se empuja). |
| `== ffmpeg` | Si hay un ffmpeg de respaldo. |
| `== Registro` | `nativa: …` (codificador elegido), `codificador: la MFT pidió renegociar el tipo de salida (STREAM_CHANGE)` (nunca visto en AMD), `tubería nativa descartada: …` (por qué cayó al respaldo), `nativa: se rehace todo tras …`. |

## Qué se espera y qué no se sabe

- **Esperado**: la MFT de NVIDIA («NVIDIA H.264 Encoder MFT») o de Intel («Intel® Quick Sync Video H.264 Encoder
  MFT») acepta la textura NV12 y transmite a 60 fps con poca CPU, como en AMD.
- **Sin probar** (por eso esta guía): si esas MFT aceptan VBR con tope, el búfer de un cuadro y la clave a pedido;
  si piden `STREAM_CHANGE`; si retienen un cuadro; cómo se comporta un portátil con dos gráficas (se prueban
  primero las MFT de la gráfica del monitor, `MFTEnum2` con su LUID).
- **Si la nativa falla**, la transmisión sigue sola con ffmpeg (`nvenc`, `qsv`…) si está instalado, sin cortar:
  eso también sirve, pero `nvenc` y `qsv` **tampoco se han probado nunca**.

## Para provocar el respaldo en este PC (desarrollo)

`PANTALLATV_FALLO_NATIVA=inicio` hace fallar la nativa antes del primer cuadro; `=seguido`, cada 2 s después de
empezar (al 4.º fallo en 60 s se pasa a la siguiente). Solo para probar: no tiene efecto si la variable no existe.
