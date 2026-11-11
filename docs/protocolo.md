# Protocolo de pantalla-tv (versión 1)

Entre el **emisor** (PC) y el **receptor** (TV Box), solo en red local. Cerrado en el M0 y ajustado
en M1–M3; cada ajuste se anota aquí.

## Descubrimiento

- El receptor, mientras está abierto, manda cada 2 s un datagrama **UDP broadcast** al puerto
  **5601**: `PANTALLA-TV 1 <nombre> <puerto>` (texto UTF-8, `<puerto>` = 5600).
- El emisor escucha en 5601 y muestra los receptores que ve. Si no aparece ninguno, permite escribir
  la IP a mano.

## Conexión

**TCP** al puerto **5600** del receptor, con `TCP_NODELAY` en ambos lados. Un solo emisor a la vez:
una segunda conexión se rechaza con `HELLO_ERR`.

## Mensajes

Todos con la misma cabecera de 13 bytes (enteros en *big-endian*):

| Desplazamiento | Tamaño | Campo |
|---|---|---|
| 0 | 1 | `tipo` |
| 1 | 4 | `largo` de la carga (u32) |
| 5 | 8 | `pts`: microsegundos desde el inicio de la sesión, reloj del emisor (u64) |
| 13 | `largo` | carga |

| Tipo | Nombre | Sentido | Carga |
|---|---|---|---|
| `0x01` | `HELLO` | emisor → receptor | versión (u8) · ancho (u16) · alto (u16) · fps (u8) · tasa de audio (u32, 0 = sin audio) · canales (u8) · PIN (4 bytes ASCII, `0000` si no hay) · **opcional:** retardo pedido en ms (u16) |
| `0x02` | `HELLO_OK` | receptor → emisor | vacía |
| `0x03` | `HELLO_ERR` | receptor → emisor | motivo en UTF-8 (`ocupado`, `pin`, `version`) |
| `0x10` | `VIDEO_CONFIG` | emisor → receptor | SPS y PPS en formato *Annex B* (`00 00 00 01` + NAL) |
| `0x11` | `VIDEO` | emisor → receptor | 1 byte de banderas (bit 0 = fotograma clave) + unidad de acceso H.264 en *Annex B* |
| `0x20` | `AUDIO` | emisor → receptor | PCM 16 bit con signo, little-endian, intercalado; tasa y canales (1 o 2) los del `HELLO`. El emisor manda paquetes de 20 ms; en silencio puede no mandar nada |
| `0x30` | `PING` | ambos | 8 bytes cualesquiera (se devuelven iguales) |
| `0x31` | `PONG` | ambos | los mismos 8 bytes del `PING` |
| `0x40` | `PEDIR_CLAVE` | receptor → emisor | vacía: «mándame un fotograma clave ya» |

## Retardo a propósito (añadido en el M1)

El receptor puede retener la imagen y el sonido un tiempo fijo (0,5 / 1 / 2 s) para absorber tirones
de la red al ver vídeos. El emisor pide **500 ms por defecto**; el tope es 5 s. El retardo efectivo es el **mayor** entre el que elige el usuario en la TV y el
que pide el emisor en el `HELLO` (campo opcional, 2 bytes; si falta, 0). El receptor guarda los
cuadros comprimidos y los entrega al decodificador a la hora `pts + desfase de relojes + retardo`;
audio y vídeo comparten esa hora. Con retardo, las reglas 3 y 4 de abajo se aplican sobre una cola
más grande (el retardo en cuadros más un margen).

## Reglas de latencia

1. El emisor codifica **sin cuadros B** y manda un fotograma clave al conectar. Con ffmpeg, además, **cada 4 s**
   (no puede dar uno a pedido). Con la tubería `nativa` (desde el 0.7.0-beta), **solo al conectar y cuando el
   receptor manda `PEDIR_CLAVE`** (o la cola de envío del emisor se llena): lo da en ~25–35 ms. `--clave N` agrega
   una clave de seguridad cada N s.
2. El receptor **descarta vídeo hasta el primer fotograma clave**.
3. El receptor nunca acumula retraso: un cuadro con más de 90 ms de atraso respecto de su hora de salida se
   decodifica pero **no se dibuja**; con más de 250 ms (o con la cola llena) **tira todo hasta el siguiente
   fotograma clave** y manda `PEDIR_CLAVE` (la tubería `nativa` lo atiende al momento; con ffmpeg llega el
   siguiente periódico).
4. Audio: va por una cola propia del emisor, que sale antes que el vídeo. Sin retardo, el receptor no deja
   esperar más de ~140 ms: si se acumula, salta a lo más nuevo.
5. Vídeo y audio comparten el `pts`; el receptor lo usa para alinearlos, no para esperar. El `pts` de un cuadro
   es la hora en que sale del codificador (con ffmpeg y con la `nativa`): el atraso de las reglas 3 y 4 se mide
   desde ahí.
6. `PING` cada 2 s; sin `PONG` en 6 s, se considera caída la conexión y se cierra. El receptor aplica el mismo plazo: si en 6 s no le llega nada (ni `PING`, ni vídeo, ni audio) da la sesión por perdida y vuelve a la pantalla de espera (probado suspendiendo el emisor sin cerrar el socket: ~6 s).
7. **Ritmo variable (desde el 0.7.0-beta, tubería `nativa`).** El emisor manda cuadros **solo cuando la pantalla
   cambia**, hasta los fps del `HELLO`; tras un cambio repite el último unas veces durante 0,5 s y después no manda
   vídeo hasta el siguiente cambio (los `PING` mantienen viva la conexión). No cambia nada en el cable: el
   receptor 0.4.0 ya lo toma bien (cada cuadro se juzga por su `pts`, la pantalla conserva el último dibujado mientras la sesión vive (al terminar o perderse, el receptor 0.4.1 lo tapa con fondo negro y el texto de espera, y no lo destapa hasta el primer cuadro de la sesión siguiente) y
   el reloj se ajusta también con el audio y los `PING`). El campo `fps` del `HELLO` pasa a ser el máximo.

## Seguridad

Solo LAN. El `PIN` de `HELLO` es **opcional en la v1**: el campo existe para no cambiar el protocolo
después; el receptor puede ignorarlo hasta el M4, donde se muestra un PIN en la TV en el primer
emparejamiento. Nada va cifrado en la v1.
