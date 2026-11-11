# Controlador del monitor virtual (incluido en el instalador)

Estos archivos son el controlador **Virtual-Display-Driver** («VDD by MTT»), sin cambios, tal como los publica
su repositorio oficial: <https://github.com/VirtualDrivers/Virtual-Display-Driver>. Licencia **MIT** (copia en
`LICENSE`). El instalador de Pantalla TV los lleva dentro (recursos incrustados en `PantallaTv.exe`) y los
instala con SetupAPI cuando se marca «Instalar el monitor virtual» (ver `PantallaTv.Emisor/Controlador.cs`).

| Dato | Valor |
|---|---|
| Release de origen | [`25.7.23`](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23), archivo `VirtualDisplayDriver-x86.Driver.Only.zip` (a pesar del nombre, es el de x64: el INF solo declara `NTamd64`) |
| Huella del `.zip` | `e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a` (coincide con la que publica GitHub para ese archivo) |
| Versión del controlador (`DriverVer` del INF) | `12/24/2024, 11.30.4.434` — la misma que la release `24.12.24` (`Signed-Driver-v24.12.24-x64.zip`, archivos idénticos byte a byte) |
| Hardware id | `Root\MttVDD`, clase `Display` (`{4D36E968-E325-11CE-BFC1-08002BE10318}`) |
| Firma | `MttVDD.dll` y `mttvdd.cat` firmados por **SignPath Foundation** (Authenticode válida al 10/11/2026) |
| Configuración que lee | `C:\VirtualDisplayDriver\vdd_settings.xml`, o la carpeta de `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH` si existe (comprobado en `Driver.cpp` de la etiqueta `24.12.24`) |
| Descargado y comprobado | 10/11/2026 |

Las huellas SHA-256 de cada archivo están en `SHA256SUMS.txt` (formato de `sha256sum -c`).
`herramientas/empaquetar.ps1` se niega a armar el instalador si falta un archivo o si una huella no coincide.

## Cómo actualizarlo

1. Descargar el paquete «Driver Only» de x64 **solo** de las releases del repositorio oficial, en una carpeta
   vacía, y comprobar la huella del `.zip` contra la que muestra GitHub.
2. Comprobar la firma (`Get-AuthenticodeSignature` del `.dll` y del `.cat`: SignPath Foundation, `Valid`).
3. Revisar en el INF que sigan el hardware id `Root\MttVDD` y los nombres de archivo, y en `Driver.cpp` de esa
   versión de dónde lee `vdd_settings.xml` y su formato.
4. Reemplazar los tres archivos, regenerar `SHA256SUMS.txt` (`sha256sum MttVDD.inf MttVDD.dll mttvdd.cat`) y
   poner al día la tabla de arriba.
