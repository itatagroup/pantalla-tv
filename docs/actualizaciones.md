# Actualizaciones

Cómo se enteran el programa del PC y la app de la tele de que hay una versión nueva, qué comprueban antes de
ofrecerla y cómo se publica una. Lo que cada app muestra y cómo apagarlo está en el README («Actualizaciones»).

## Qué se publica en cada release

Cada release de GitHub (`vX.Y.Z`, con la versión del emisor de Windows) trae, además del instalador y la APK:

- `version.json`: el manifiesto, en UTF-8 **sin BOM**.
- `version.json.sig`: la firma de los bytes **exactos** de `version.json`, en base64 y en una sola línea.
  **ECDSA P-256 + SHA-256**, firma en formato **DER** (lo que da `crypto.sign("sha256", datos, clave)` en Node,
  `Signature("SHA256withECDSA")` en Java y `ECDsa.VerifyData(…, HashAlgorithmName.SHA256,
  DSASignatureFormat.Rfc3279DerSequence)` en .NET).
- `SHA256SUMS.txt`: las huellas de todos los archivos (ver `verificar.md`).

Las apps leen siempre lo último publicado; GitHub redirige esta dirección al release más nuevo:

```
https://github.com/itatagroup/pantalla-tv/releases/latest/download/version.json   (y version.json.sig)
```

`releases/latest` **no ve los borradores ni los pre-releases**: un release se ofrece recién cuando se publica como
release normal.

## Formato de `version.json`

```json
{
  "formato": 1,
  "repo": "itatagroup/pantalla-tv",
  "apps": {
    "emisor-windows":   { "version": "0.8.0", "url": "https://github.com/itatagroup/pantalla-tv/releases/download/v0.8.0/PantallaTvSetup-0.8.0.exe", "tamano": 52700000, "sha256": "hex en minúsculas", "notas": "texto corto en español" },
    "receptor-android": { "version": "0.5.0", "versionCode": 7, "url": "https://github.com/itatagroup/pantalla-tv/releases/download/v0.8.0/pantalla-receptor-0.5.0.apk", "tamano": 30000, "sha256": "…", "notas": "…" }
  }
}
```

- `tamano` en bytes; `sha256` en hexadecimal y minúsculas; `notas`, una línea corta (se muestra en un globo).
- Cada app busca solo **su** entrada (`emisor-windows`, `receptor-android`) e ignora las demás.

## Clave pública

SubjectPublicKeyInfo en DER, en base64. Va dentro de cada app como constante (en el emisor,
`ComprobadorActualizaciones.ClavePublica`):

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENdrCrDU1A0B87/zG/2dD80YPGfx8+gK2CclanFT9+8hPOlWFc9Q4zYXJqMEdQEd6bdsQ2eMgjqwhea0vcj95zA==
```

La privada (PKCS#8 en PEM) vive en `keystore/actualizaciones.pem`, que Git ignora, y se respalda fuera del PC.
Solo la usa `herramientas/firmar-manifiesto.mjs`. Quien compile su propia versión con otra clave tiene que cambiar
esta constante: las apps oficiales rechazan cualquier manifiesto que no esté firmado con la clave del proyecto.

## Reglas de verificación

En este orden. Si algo falla **no se ofrece nada** y no se le muestra un error al usuario: solo queda en el
registro local (en el emisor, `%LOCALAPPDATA%\PantallaTv\registro.txt`).

1. Solo HTTPS. `version.json` y `.sig` se leen con un tope de **64 KB** cada uno y **10 s**.
2. La firma, con la clave pública incluida. Sin firma válida: fin (antes de mirar el contenido).
3. `formato == 1` y `repo` igual al repo esperado; se busca la entrada de **esta** app.
4. Versión: el emisor compara `System.Version` de `version` con la propia (las partes que faltan cuentan como 0);
   Android compara `versionCode` con el instalado. Solo se ofrece si es **mayor**.
5. `url` tiene que empezar con `https://github.com/itatagroup/pantalla-tv/releases/download/`.
6. La descarga acepta redirecciones https (GitHub manda a sus servidores de archivos) y va a un archivo propio:
   se corta si pasa de `tamano`, y al terminar tiene que medir **exactamente** `tamano` y tener el `sha256`
   publicado. Si no, se borra.
7. Recién ahí se instala, **siempre visible y con confirmación**. Nada en silencio.

## Privacidad

La consulta es un GET sin identificadores, sin cookies y sin datos del equipo ni del uso; el User-Agent es
genérico (`PantallaTv/0.8.0` en el emisor). No se envía nada más a ningún lado. Apagada la búsqueda, no hay
ninguna conexión a internet.

## Emisor de Windows: cómo se instala la actualización

El instalador es el mismo `PantallaTv.exe` con otro nombre (`PantallaTvSetup-x.y.z.exe` abre el asistente). Al
apretar «Actualizar ahora», el programa descarga y verifica el instalador en `%LOCALAPPDATA%\PantallaTv\actualizaciones\`
(se vacía antes de cada descarga y al abrir el programa) y lo abre con `--instalar --actualizar`: el asistente
empieza en sus opciones, marcadas como estaban (accesos directos, inicio con Windows; el monitor virtual no se
agrega). El programa sigue abierto hasta que el usuario aprieta «Actualizar»; entonces el asistente le pide que se
cierre (un evento con nombre que el programa escucha desde la 0.8.0; se cierra como con «Salir»: detiene la
transmisión y desconecta el monitor virtual), espera hasta 20 s a que suelte el ejecutable, copia y, al terminar,
lo vuelve a abrir. Si el usuario cancela, el programa queda abierto como estaba.

Lo mismo pasa al instalar a mano (o con `--silencioso`) con el programa abierto. Una versión anterior a la 0.8.0
no escucha ese pedido: hay que cerrarla a mano (clic derecho en el ícono › Salir) y el asistente lo explica.

Para probar con otro manifiesto: `PantallaTv.exe --manifiesto-url https://…/version.json` (la firma con la clave
del proyecto se exige igual y la descarga tiene que seguir siendo de los releases del repo).

## Publicar una versión

1. Sube las versiones: `<Version>` en `emisor-windows/PantallaTv.Emisor/PantallaTv.Emisor.csproj`, y
   `versionName`/`versionCode` en `receptor-android/version.json` si cambió el receptor (el `versionCode`
   **siempre** tiene que subir para que Android lo ofrezca).
2. Compila el receptor: `cd receptor-android && node tools/build.mjs` (deja `out/receptor.apk`).
3. Arma el release (corre las pruebas, publica el emisor sin ffmpeg y firma el manifiesto):

   ```powershell
   powershell -File herramientas/publicar.ps1 -Version 0.8.0 -NotasEmisor "texto corto" -NotasReceptor "texto corto"
   ```

   Deja todo en `dist/publicar/v0.8.0/`: instalador, APK, `version.json`, `version.json.sig`, `SHA256SUMS.txt` y
   `notas.md`. Se niega a firmar si la clave de `keystore/` no corresponde a la pública que llevan las apps.
   `-Receptor <ruta>` usa otra APK; `-Instalador <ruta>`, un instalador ya armado.
4. Revisa los archivos y corre el `gh release create … --draft` que imprime al final. En GitHub, revisa el
   borrador y publícalo como release normal (no pre-release). Desde ese momento las apps lo ofrecen.

Para firmar a mano: `node herramientas/firmar-manifiesto.mjs <version.json> <clave.pem> [--esperada <pública>]`.
Escribe `<version.json>.sig`; nunca imprime la clave.
