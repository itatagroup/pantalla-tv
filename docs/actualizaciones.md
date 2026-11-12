# Actualizaciones

Cómo se enteran las apps de que hay una versión nueva, qué comprueban antes de ofrecerla y cómo se publica una.

**Nada es automático.** Ninguna app se conecta sola a internet: solo cuando el usuario pulsa «Buscar
actualizaciones» (o «Buscar actualización»). Lo que muestra cada una está en el README («Actualizaciones»).

## Una versión por app

Las tres apps del repo se publican **por separado**, cada una con su versión, su release y su manifiesto. Publicar
un arreglo del receptor no obliga a sacar un emisor nuevo, ni al revés.

| App | Release (tag) | Archivo | Manifiesto |
|---|---|---|---|
| Emisor de Windows (`emisor-windows`) | `emisor-windows-vX.Y.Z` | `PantallaTvSetup-X.Y.Z.exe` | `emisor-windows.json` |
| Receptor Android, tele o TV Box (`receptor-android`) | `receptor-android-vX.Y.Z` | `pantalla-receptor-X.Y.Z.apk` | `receptor-android.json` |
| Emisor Android, teléfono o tablet (`emisor-android`) | `emisor-android-vX.Y.Z` | `pantalla-emisor-X.Y.Z.apk` | `emisor-android.json` |

Cada release trae su archivo, su manifiesto firmado y `SHA256SUMS.txt` (ver `verificar.md`). Las apps leen el
manifiesto de un release **fijo**, `actualizaciones`, donde cada publicación reemplaza solo el de su app:

```
https://github.com/itatagroup/pantalla-tv/releases/download/actualizaciones/<app>.json   (y <app>.json.sig)
```

Así cada app ve siempre la última versión de **sí misma**, sin depender de cuál fue el último release del repo.

## El manifiesto

`<app>.json`, en UTF-8 **sin BOM**:

```json
{
  "formato": 1,
  "repo": "itatagroup/pantalla-tv",
  "apps": {
    "receptor-android": { "version": "0.5.0", "versionCode": 7, "url": "https://github.com/itatagroup/pantalla-tv/releases/download/receptor-android-v0.5.0/pantalla-receptor-0.5.0.apk", "tamano": 45734, "sha256": "hex en minúsculas", "notas": "texto corto en español" }
  }
}
```

- Las apps Android llevan `versionCode`; el emisor de Windows no.
- `tamano` en bytes; `sha256` en hexadecimal y minúsculas; `notas`, una línea corta.
- Cada app busca solo **su** entrada (`emisor-windows`, `receptor-android`, `emisor-android`).

`<app>.json.sig` es la firma de los bytes **exactos** del manifiesto, en base64 y en una sola línea: **ECDSA P-256
+ SHA-256**, en formato **DER** (lo que da `crypto.sign("sha256", datos, clave)` en Node,
`Signature("SHA256withECDSA")` en Java y `ECDsa.VerifyData(…, HashAlgorithmName.SHA256,
DSASignatureFormat.Rfc3279DerSequence)` en .NET).

## Clave pública

SubjectPublicKeyInfo en DER, en base64. Va dentro de cada app como constante (en el emisor de Windows,
`ComprobadorActualizaciones.ClavePublica`; en Android, `Actualizador.CLAVE_PUBLICA`):

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENdrCrDU1A0B87/zG/2dD80YPGfx8+gK2CclanFT9+8hPOlWFc9Q4zYXJqMEdQEd6bdsQ2eMgjqwhea0vcj95zA==
```

La privada (PKCS#8 en PEM) vive en `keystore/actualizaciones.pem`, que Git ignora, y se respalda fuera del PC.
Solo la usa `herramientas/firmar-manifiesto.mjs`. Quien compile su propia versión con otra clave tiene que cambiar
esta constante: las apps oficiales rechazan cualquier manifiesto que no esté firmado con la clave del proyecto.

## Reglas de verificación

En este orden. Si algo falla **no se ofrece nada**: la app dice «No se pudo comprobar» y el detalle queda en el
registro local (en el emisor de Windows, `%LOCALAPPDATA%\PantallaTv\registro.txt`; en Android, `adb logcat -s Actualizador`).

1. Solo HTTPS. El manifiesto y su firma se leen con un tope de **64 KB** cada uno y **10 s**.
2. La firma, con la clave pública incluida. Sin firma válida: fin (antes de mirar el contenido).
3. `formato == 1` y `repo` igual al repo esperado; se busca la entrada de **esta** app.
4. Versión: el emisor de Windows compara `System.Version` de `version` con la propia (las partes que faltan cuentan
   como 0); Android compara `versionCode` con el instalado. Solo se ofrece si es **mayor**.
5. `url` tiene que ser de `https://github.com/itatagroup/pantalla-tv/releases/download/`, comprobado sobre la
   dirección ya analizada: se rechazan `..`, `.`, escapes con `%`, consulta y fragmento.
6. La descarga acepta redirecciones https (GitHub manda a sus servidores de archivos) y va a un archivo propio:
   se corta si pasa de `tamano`, y al terminar tiene que medir **exactamente** `tamano` y tener el `sha256`
   publicado. Si no, se borra.
7. Recién ahí se instala, **siempre visible y con confirmación**. Nada en silencio.

## Privacidad

La consulta es un GET sin identificadores, sin cookies y sin datos del equipo ni del uso; el User-Agent es
genérico (`PantallaTv/0.8.0`, `Pantalla/0.5.0`, `EnviarAPantalla/0.2.0`). Se hace solo cuando el usuario pulsa el
botón; no se envía nada más a ningún lado.

## Emisor de Windows: cómo se instala la actualización

El instalador es el mismo `PantallaTv.exe` con otro nombre (`PantallaTvSetup-x.y.z.exe` abre el asistente). Al
apretar «Actualizar ahora», el programa descarga y verifica el instalador en `%LOCALAPPDATA%\PantallaTv\actualizaciones\`
(se vacía antes de cada descarga y al abrir el programa) y lo abre con `--instalar --actualizar`: el asistente
empieza en sus opciones, marcadas como estaban (accesos directos, inicio con Windows; el monitor virtual no se
agrega). El programa sigue abierto hasta que el usuario aprieta «Actualizar»; entonces el asistente le pide que se
cierre (un evento con nombre, propio de la carpeta del ejecutable, que el programa escucha desde la 0.8.0; se cierra
como con «Salir»: detiene la transmisión y desconecta el monitor virtual), espera hasta 20 s a que suelte el
ejecutable, copia y, al terminar, lo vuelve a abrir. Si el usuario cancela, el programa queda abierto como estaba.

Lo mismo pasa al instalar a mano (o con `--silencioso`) con el programa abierto en la carpeta de destino. Una
versión anterior a la 0.8.0 no escucha ese pedido: hay que cerrarla a mano (clic derecho en el ícono › Salir) y el
asistente lo explica.

Para probar con otro manifiesto: `PantallaTv.exe --manifiesto-url https://…/emisor-windows.json` (la firma con la
clave del proyecto se exige igual y la descarga tiene que seguir siendo de los releases del repo). En el receptor:
`adb shell am start -n cl.itatagroup.pantalla/.Receptor --es manifiesto_url https://…/receptor-android.json`.

## Código común de Android

`android-comun/java/cl/itatagroup/actualizacion/` (`Actualizador`, `FirmaManifiesto`, `ResultadoInstalacion`) lo
compilan el receptor y el emisor Android (`herramientas/build-android.mjs`). El repo `launcher-tv` tiene una copia:
un arreglo aquí va también allá.

## Publicar una versión

Una app por vez:

1. Sube su versión: `<Version>` en `emisor-windows/PantallaTv.Emisor/PantallaTv.Emisor.csproj`, o
   `versionName`/`versionCode` en `<app>/version.json` (el `versionCode` **siempre** tiene que subir para que
   Android la ofrezca). Commitea y empuja: el release se ancla a ese commit.
2. Arma la publicación (compila, corre las pruebas del emisor y firma el manifiesto):

   ```powershell
   powershell -File herramientas/publicar.ps1 -App receptor-android -Version 0.5.0 -Notas "texto corto"
   ```

   Deja todo en `dist/publicar/<app>-vX.Y.Z/`: el archivo, `<app>.json`, `<app>.json.sig`, `SHA256SUMS.txt` y
   `notas.md`. Se niega a seguir con cambios sin commitear, con el commit sin empujar, o si la clave de
   `keystore/` no corresponde a la pública que llevan las apps. `-Archivo <ruta>` usa un archivo ya compilado.
3. Corre el primer comando que imprime (`gh release create <app>-vX.Y.Z --target <commit> --draft …`), revisa el
   borrador en GitHub y publícalo.
4. Corre el segundo (`gh release upload actualizaciones … --clobber`): sube el manifiesto al release fijo. Desde ese
   momento las apps la ofrecen cuando alguien busca. La primera vez hay que crear ese release (el script imprime
   el comando).
5. Si la página de descargas pública enlaza versiones fijas (la de Itatagroup, `itatagroup.cl/utilidades/pantalla-tv/`),
   actualiza ahí el enlace, el tamaño, la huella y el análisis de VirusTotal.

Para firmar a mano: `node herramientas/firmar-manifiesto.mjs <manifiesto.json> <clave.pem> [--esperada <pública>]`.
Escribe `<manifiesto.json>.sig`; nunca imprime la clave.
