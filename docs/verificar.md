# Verificar una descarga

Cada release publica los instaladores junto a un `SHA256SUMS.txt` con la huella SHA-256 de cada archivo. Antes de
instalar, comprueba que lo que descargaste es lo que se publicó.

## 1. La huella SHA-256

Descarga `SHA256SUMS.txt` en la misma carpeta que los archivos y, según tu sistema:

```powershell
# Windows (PowerShell): muestra la huella; compárala con la línea del archivo en SHA256SUMS.txt
Get-FileHash .\PantallaTvSetup-<versión>.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

```bash
# Linux / macOS / Git Bash: comprueba todos los archivos que estén en la carpeta
sha256sum -c SHA256SUMS.txt --ignore-missing
```

Las dos huellas deben ser idénticas (en `Get-FileHash` sale en mayúsculas; da igual).

## 2. El certificado de las APK

Las APK van firmadas con la clave del proyecto. Comprueba que el certificado es el publicado (necesitas
`apksigner`, que viene en las *build-tools* del SDK de Android):

```bash
apksigner verify --print-certs pantalla-receptor-<versión>.apk
apksigner verify --print-certs pantalla-emisor-<versión>.apk
```

La línea `Signer #1 certificate SHA-256 digest` debe ser, según la app (cada una tiene su clave):

| APK | Certificado | SHA-256 del certificado |
|---|---|---|
| `pantalla-receptor-<versión>.apk` | `CN=Pantalla TV` | `0c23e5c877f9a6cfa4d6d3bd7ddd626fe7d4cd879d58041a1070f8a33d074ee6` |
| `pantalla-emisor-<versión>.apk` | `CN=Pantalla TV emisor` | `3927166f42a76e3bf695fcb7a649fa4416c5fcffa006a38f7cc4aadbea4aa788` |

Si es otra, no instales la APK. Una APK firmada con otra clave tampoco se instalará encima de una versión
oficial (Android lo rechaza).

## 3. Compilarlo tú y comparar

El código es abierto: puedes compilar el mismo commit (ver «Compilar» en el README) y comparar el contenido
con lo publicado. Las APK se firman con claves distintas, así que **no serán idénticas byte a byte**: compara
su contenido salvo la firma (`META-INF/`):

```bash
mkdir mia oficial
unzip -q -o mi-receptor.apk -d mia
unzip -q -o pantalla-receptor-<versión>.apk -d oficial
diff -r -x META-INF mia oficial && echo "Mismo contenido"
```

Para el instalador de Windows, el `.exe` de un `dotnet publish` puede diferir en bytes (marcas de tiempo del
compilador); compara al menos que las pruebas pasen y el comportamiento sea el mismo.

## 4. El `.exe` no tiene firma de código

`PantallaTvSetup-<versión>.exe` **no está firmado** con un certificado de firma de código (cuestan dinero y este
es un proyecto personal). Por eso Windows SmartScreen muestra «Windows protegió su PC»; ahí aprieta «Más
información» › «Ejecutar de todas formas» **solo después de comprobar la huella** (paso 1).

Para ver que no tiene firma: clic derecho en el `.exe` › *Propiedades*: no aparece la pestaña «Firmas digitales»;
o en PowerShell:

```powershell
Get-AuthenticodeSignature .\PantallaTvSetup-<versión>.exe    # Status: NotSigned
```

Lo que sí está firmado es el controlador del monitor virtual que lleva dentro (`MttVDD.dll` y `mttvdd.cat`, por
SignPath Foundation, el firmante del proyecto Virtual-Display-Driver). Sus huellas, para compararlas con el
proyecto original, están en `emisor-windows/controlador/SHA256SUMS.txt`.
