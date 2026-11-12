<#
  Arma la publicación de UNA app del repo en dist/publicar/<app>-v<versión>/ (fuera de git) SIN publicar nada.
  Cada app tiene su versión, su release y su manifiesto: publicar una no obliga a publicar las otras.

    App               Archivo                                 Release (tag)            Manifiesto
    emisor-windows    PantallaTvSetup-<versión>.exe           emisor-windows-v<versión>   emisor-windows.json
    receptor-android  pantalla-receptor-<versión>.apk         receptor-android-v<versión> receptor-android.json
    emisor-android    pantalla-emisor-<versión>.apk           emisor-android-v<versión>   emisor-android.json

  En la carpeta quedan el archivo, <app>.json (UTF-8 sin BOM) y <app>.json.sig (firmar-manifiesto.mjs, con
  keystore/actualizaciones.pem), SHA256SUMS.txt y notas.md. Al final imprime los comandos de gh para el mantenedor:
    1. crear el borrador del release de la app, anclado al commit compilado;
    2. después de publicarlo, subir el manifiesto al release fijo «actualizaciones» (de donde lo leen las apps).

  Uso:  powershell -File herramientas/publicar.ps1 -App receptor-android -Version 0.5.0 [-Notas "texto corto"]
          [-Clave keystore\actualizaciones.pem] [-Archivo ruta\ya\compilada] [-SinPruebas]

  - La versión tiene que ser la del proyecto: PantallaTv.Emisor.csproj (emisor-windows) o <app>/version.json (Android).
  - Sin -Archivo: el emisor de Windows se arma con empaquetar.ps1 -SoloEmisor (corre las pruebas salvo -SinPruebas);
    las apps Android se compilan con su tools/build.mjs.
  - El árbol de git tiene que estar limpio y el commit ya empujado: el release se ancla a ese commit, así quien
    compile desde el tag obtiene lo mismo que se publica (docs/verificar.md).
#>
param(
  [Parameter(Mandatory = $true)][ValidateSet("emisor-windows", "receptor-android", "emisor-android")][string]$App,
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Notas,
  [string]$Clave,
  [string]$Archivo,
  [switch]$SinPruebas
)
$ErrorActionPreference = "Stop"
$raiz = Split-Path $PSScriptRoot -Parent
$repo = "itatagroup/pantalla-tv"
$utf8 = New-Object Text.UTF8Encoding($false)
if (-not $Clave) { $Clave = Join-Path $raiz "keystore\actualizaciones.pem" }

# ── Comprobaciones antes de compilar nada ────────────────────────────────────────────────────
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version debe ser x.y.z (por ejemplo 0.8.0)" }
if (-not (Test-Path $Clave)) { throw "Falta la clave de firma $Clave (ver docs/actualizaciones.md)" }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "Hace falta Node.js para compilar y firmar" }

Push-Location $raiz
try {
  if (git status --porcelain) { throw "Hay cambios sin commitear: el release tiene que salir de un commit limpio." }
  $commit = (git rev-parse HEAD).Trim()
  git fetch --quiet origin
  if (-not (git branch -r --contains $commit)) { throw "El commit $commit no está en GitHub: empújalo antes de publicar." }
} finally { Pop-Location }

# La clave pública que llevan las apps: la firma tiene que corresponder a ella o nadie aceptaría el manifiesto.
$comprobador = Join-Path $raiz "emisor-windows\PantallaTv.Emisor\ComprobadorActualizaciones.cs"
if ((Get-Content $comprobador -Raw) -notmatch 'ClavePublica\s*=\s*"([A-Za-z0-9+/=]+)"') { throw "No encuentro ClavePublica en $comprobador" }
$publica = $Matches[1]

$tag = "$App-v$Version"
$salida = Join-Path $raiz "dist\publicar\$tag"
if (Test-Path $salida) { Remove-Item $salida -Recurse -Force }
New-Item -ItemType Directory $salida | Out-Null
$versionCode = $null

# ── El archivo de la app ────────────────────────────────────────────────────────────────────
if ($App -eq "emisor-windows") {
  $proyecto = Join-Path $raiz "emisor-windows\PantallaTv.Emisor\PantallaTv.Emisor.csproj"
  $v = ([xml](Get-Content $proyecto)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  if ($v -ne $Version) { throw "El emisor está en la versión $v (PantallaTv.Emisor.csproj), no en $Version" }
  if (-not $Archivo) {
    $argsEmpaquetar = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "empaquetar.ps1"), "-SoloEmisor")
    if ($SinPruebas) { $argsEmpaquetar += "-SinPruebas" }
    & powershell @argsEmpaquetar
    if ($LASTEXITCODE -ne 0) { throw "Falló empaquetar.ps1" }
    $Archivo = Join-Path $raiz "dist\PantallaTvSetup-$Version.exe"
  }
  # Un instalador de otra versión dejaría al usuario «desactualizado» para siempre.
  $vArchivo = ((Get-Item $Archivo).VersionInfo.ProductVersion -split '\+')[0]
  if ($vArchivo -ne $Version) { throw "$Archivo es la versión $vArchivo, no la $Version" }
  $nombre = "PantallaTvSetup-$Version.exe"
  $titulo = "Programa del PC $Version"
  $descripcion = "el programa del PC (Windows 10 u 11 de 64 bits) con su instalador. Trae el runtime de .NET."
} else {
  $carpeta = Join-Path $raiz $App
  $info = Get-Content (Join-Path $carpeta "version.json") -Raw | ConvertFrom-Json
  if ($info.versionName -ne $Version) { throw "$App/version.json dice $($info.versionName), no $Version. Súbela (y el versionCode) antes." }
  $versionCode = [int]$info.versionCode
  if (-not $Archivo) {
    Push-Location $carpeta
    try {
      & node tools/build.mjs
      if ($LASTEXITCODE -ne 0) { throw "Falló la compilación de $App" }
    } finally { Pop-Location }
    $Archivo = Join-Path $carpeta "out\$($info.nombre).apk"
  }
  if ($App -eq "receptor-android") {
    $nombre = "pantalla-receptor-$Version.apk"
    $titulo = "App de la tele $Version"
    $descripcion = "la app «Pantalla» que recibe: tele o TV Box con Android 8 o superior."
  } else {
    $nombre = "pantalla-emisor-$Version.apk"
    $titulo = "Emisor Android $Version"
    $descripcion = "la app que transmite la pantalla de un teléfono o tablet Android 10 o superior."
  }
}
if (-not (Test-Path $Archivo)) { throw "No existe $Archivo" }
Copy-Item $Archivo (Join-Path $salida $nombre)
$rutaArchivo = Join-Path $salida $nombre

# ── Manifiesto propio de esta app ───────────────────────────────────────────────────────────
if (-not $Notas) { $Notas = "$titulo." }
$entrada = [ordered]@{ version = $Version }
if ($null -ne $versionCode) { $entrada.versionCode = $versionCode }
$entrada.url = "https://github.com/$repo/releases/download/$tag/$nombre"
$entrada.tamano = [long](Get-Item $rutaArchivo).Length
$entrada.sha256 = (Get-FileHash $rutaArchivo -Algorithm SHA256).Hash.ToLower()
$entrada.notas = $Notas
$manifiesto = [ordered]@{ formato = 1; repo = $repo; apps = [ordered]@{ $App = $entrada } }
$nombreManifiesto = "$App.json"
$rutaManifiesto = Join-Path $salida $nombreManifiesto
[IO.File]::WriteAllText($rutaManifiesto, ($manifiesto | ConvertTo-Json -Depth 5), $utf8) # se firman estos bytes exactos

& node (Join-Path $PSScriptRoot "firmar-manifiesto.mjs") $rutaManifiesto $Clave --esperada $publica
if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar el manifiesto" }

# ── Huellas y notas ─────────────────────────────────────────────────────────────────────────
$archivos = @($nombre, $nombreManifiesto, "$nombreManifiesto.sig")
$archivos | ForEach-Object { "{0}  {1}" -f (Get-FileHash (Join-Path $salida $_) -Algorithm SHA256).Hash.ToLower(), $_ } |
  Set-Content (Join-Path $salida "SHA256SUMS.txt") -Encoding ascii

$smartscreen = if ($App -eq "emisor-windows") { @"

## Aviso de SmartScreen

El instalador **no tiene firma de código** (es un proyecto personal), así que Windows SmartScreen puede mostrar
«Windows protegió su PC». Después de comprobar la huella, aprieta «Más información» › «Ejecutar de todas formas».
"@ } else { "" }

$textoNotas = @"
# Pantalla TV — $titulo (beta)

$Notas

> **Beta.** Funciona y se usa a diario, pero solo se probó en pocos equipos. Si algo falla, abre un issue.

## Archivos

- ``$nombre``: $descripcion
- ``$nombreManifiesto`` y ``$nombreManifiesto.sig``: el manifiesto firmado de esta versión. Las apps no lo leen de
  aquí sino del release «actualizaciones», y solo cuando pulsas «Buscar actualizaciones»
  (ver [docs/actualizaciones.md](https://github.com/$repo/blob/$commit/docs/actualizaciones.md)).
- ``SHA256SUMS.txt``: las huellas SHA-256.

Compilado desde el commit ``$commit`` (el de este tag).

## Verificar la descarga

``````bash
sha256sum -c SHA256SUMS.txt --ignore-missing
``````

En Windows: ``Get-FileHash .\$nombre -Algorithm SHA256`` y compárala con su línea en ``SHA256SUMS.txt``. Más detalle
(y cómo comprobar el certificado de una APK) en [docs/verificar.md](https://github.com/$repo/blob/$commit/docs/verificar.md).
$smartscreen
"@
[IO.File]::WriteAllText((Join-Path $salida "notas.md"), $textoNotas, $utf8)

# ── Lo que queda por hacer, a mano ──────────────────────────────────────────────────────────
Write-Host "`nListo en ${salida}:"
Get-ChildItem $salida -File | ForEach-Object { "  {0,-40} {1,10:N0} bytes" -f $_.Name, $_.Length }
$subir = ($archivos + "SHA256SUMS.txt" | ForEach-Object { "`"$(Join-Path $salida $_)`"" }) -join " "
$manifiestos = ($nombreManifiesto, "$nombreManifiesto.sig" | ForEach-Object { "`"$(Join-Path $salida $_)`"" }) -join " "
Write-Host "`n1. Borrador del release (revísalo en GitHub y publícalo; no lo marques como «latest» si no quieres):`n"
Write-Host "gh release create $tag --repo $repo --target $commit --draft --latest=false --title `"$titulo`" --notes-file `"$(Join-Path $salida 'notas.md')`" $subir"
Write-Host "`n2. Recién publicado el release, avisar a las apps (subir su manifiesto al release fijo «actualizaciones»):`n"
Write-Host "gh release upload actualizaciones --repo $repo --clobber $manifiestos"
Write-Host "`n   La primera vez, crear antes ese release:"
Write-Host "   gh release create actualizaciones --repo $repo --latest=false --title `"Manifiestos de actualización`" --notes `"Lo consultan las apps cuando el usuario pulsa «Buscar actualizaciones». Ver docs/actualizaciones.md.`""
