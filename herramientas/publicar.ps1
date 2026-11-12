<#
  Arma un release en dist/publicar/v<Version>/ (fuera de git) SIN publicar nada:
    PantallaTvSetup-<Version>.exe        el instalador del emisor (empaquetar.ps1 -SoloEmisor, sin ffmpeg)
    pantalla-receptor-<versionName>.apk  el receptor ya compilado (lo compila receptor-android/tools/build.mjs)
    version.json                         el manifiesto de docs/actualizaciones.md (UTF-8 sin BOM)
    version.json.sig                     su firma (firmar-manifiesto.mjs, con keystore/actualizaciones.pem)
    SHA256SUMS.txt                       huellas de todo lo anterior
    notas.md                             el texto del release (no se sube como archivo)
  Al final imprime el «gh release create … --draft» para que lo corra el mantenedor.

  Uso:  powershell -File herramientas/publicar.ps1 -Version 0.8.0 [-Receptor ruta\al\receptor.apk]
          [-NotasEmisor "texto corto"] [-NotasReceptor "texto corto"] [-Clave keystore\actualizaciones.pem] [-SinPruebas]
          [-Instalador dist\PantallaTvSetup-<Version>.exe]

  -Instalador usa un instalador ya armado en vez de llamar a empaquetar.ps1 (que vuelve a correr las pruebas y
  publica el emisor).

  Sin -Receptor toma receptor-android/out/receptor.apk. La versión del receptor (versionName y versionCode) sale de
  receptor-android/version.json: compílalo después de subirla. -Version tiene que ser la <Version> del emisor.
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Receptor,
  [string]$NotasEmisor,
  [string]$NotasReceptor,
  [string]$Clave,
  [string]$Instalador,
  [switch]$SinPruebas
)
$ErrorActionPreference = "Stop"
$raiz = Split-Path $PSScriptRoot -Parent
$repo = "itatagroup/pantalla-tv"
$proyecto = Join-Path $raiz "emisor-windows\PantallaTv.Emisor\PantallaTv.Emisor.csproj"
$comprobador = Join-Path $raiz "emisor-windows\PantallaTv.Emisor\ComprobadorActualizaciones.cs"
$versionReceptor = Join-Path $raiz "receptor-android\version.json"
if (-not $Clave) { $Clave = Join-Path $raiz "keystore\actualizaciones.pem" }

# ── Comprobaciones antes de compilar nada ────────────────────────────────────────────────────
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version debe ser x.y.z (por ejemplo 0.8.0)" }
$versionEmisor = ([xml](Get-Content $proyecto)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($versionEmisor -ne $Version) { throw "El emisor está en la versión $versionEmisor (PantallaTv.Emisor.csproj), no en $Version" }
if (-not (Test-Path $Clave)) { throw "Falta la clave de firma $Clave (ver docs/actualizaciones.md)" }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "Hace falta Node.js para firmar el manifiesto" }

# La clave pública que llevan las apps: la firma tiene que corresponder a ella o nadie aceptaría el manifiesto.
$codigo = Get-Content $comprobador -Raw
if ($codigo -notmatch 'ClavePublica\s*=\s*"([A-Za-z0-9+/=]+)"') { throw "No encuentro ClavePublica en $comprobador" }
$publica = $Matches[1]

$r = Get-Content $versionReceptor -Raw | ConvertFrom-Json
if (-not $r.versionName -or -not $r.versionCode) { throw "receptor-android/version.json sin versionName o versionCode" }
if (-not $Receptor) { $Receptor = Join-Path $raiz "receptor-android\out\receptor.apk" }
if (-not (Test-Path $Receptor)) { throw "No encuentro el APK del receptor en $Receptor (compílalo con: cd receptor-android; node tools/build.mjs)" }
# El manifiesto anuncia el versionCode de version.json: si el APK es de antes, los receptores instalarían el viejo y
# se les volvería a ofrecer la misma «actualización» en cada búsqueda.
if ((Get-Item $Receptor).LastWriteTime -lt (Get-Item $versionReceptor).LastWriteTime) {
  throw "El APK ($Receptor) es más viejo que receptor-android/version.json: compílalo de nuevo (node tools/build.mjs)"
}

# ── Instalador del emisor ───────────────────────────────────────────────────────────────────
if ($Instalador) {
  if (-not (Test-Path $Instalador)) { throw "No existe $Instalador" }
} else {
  $argsEmpaquetar = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "empaquetar.ps1"), "-SoloEmisor")
  if ($SinPruebas) { $argsEmpaquetar += "-SinPruebas" }
  & powershell @argsEmpaquetar
  if ($LASTEXITCODE -ne 0) { throw "Falló empaquetar.ps1" }
  $Instalador = Join-Path $raiz "dist\PantallaTvSetup-$Version.exe"
}
# El instalador tiene que ser de esta versión: si no, quien lo instale seguiría «desactualizado» y se le ofrecería
# lo mismo para siempre.
$versionInstalador = ((Get-Item $Instalador).VersionInfo.ProductVersion -split '\+')[0]
if ($versionInstalador -ne $Version) { throw "$Instalador es la versión $versionInstalador, no la $Version" }

$salida = Join-Path $raiz "dist\publicar\v$Version"
if (Test-Path $salida) { Remove-Item $salida -Recurse -Force }
New-Item -ItemType Directory $salida | Out-Null

$nombreSetup = "PantallaTvSetup-$Version.exe"
$nombreApk = "pantalla-receptor-$($r.versionName).apk"
Copy-Item $Instalador (Join-Path $salida $nombreSetup)
Copy-Item $Receptor (Join-Path $salida $nombreApk)

# ── Manifiesto ──────────────────────────────────────────────────────────────────────────────
function Entrada($archivo, $versionApp, $notas) {
  $ruta = Join-Path $salida $archivo
  [ordered]@{
    version = $versionApp
    url     = "https://github.com/$repo/releases/download/v$Version/$archivo"
    tamano  = [long](Get-Item $ruta).Length
    sha256  = (Get-FileHash $ruta -Algorithm SHA256).Hash.ToLower()
    notas   = $notas
  }
}
if (-not $NotasEmisor) { $NotasEmisor = "Versión $Version del programa del PC." }
if (-not $NotasReceptor) { $NotasReceptor = "Versión $($r.versionName) de la app de la tele." }
$emisor = Entrada $nombreSetup $Version $NotasEmisor
$receptorApp = Entrada $nombreApk $r.versionName $NotasReceptor
$receptorApp.Insert(1, "versionCode", [int]$r.versionCode)
$manifiesto = [ordered]@{
  formato = 1
  repo    = $repo
  apps    = [ordered]@{ "emisor-windows" = $emisor; "receptor-android" = $receptorApp }
}
$rutaManifiesto = Join-Path $salida "version.json"
# UTF-8 sin BOM: se firman estos bytes exactos.
[IO.File]::WriteAllText($rutaManifiesto, ($manifiesto | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))

& node (Join-Path $PSScriptRoot "firmar-manifiesto.mjs") $rutaManifiesto $Clave --esperada $publica
if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar el manifiesto" }

# ── Huellas y notas ─────────────────────────────────────────────────────────────────────────
$archivos = @($nombreSetup, $nombreApk, "version.json", "version.json.sig")
$archivos | ForEach-Object { "{0}  {1}" -f (Get-FileHash (Join-Path $salida $_) -Algorithm SHA256).Hash.ToLower(), $_ } |
  Set-Content (Join-Path $salida "SHA256SUMS.txt") -Encoding ascii

$notas = @"
# Pantalla TV $Version (beta)

Ver y oír la pantalla de un PC con Windows en la tele, por la red local. Software libre (GPL-3.0), en español.

> **Beta.** Funciona y se usa a diario, pero solo se probó en pocos equipos (una gráfica AMD; ver
> [docs/prueba-otro-pc.md](https://github.com/$repo/blob/main/docs/prueba-otro-pc.md)). Si algo falla, abre un issue
> con el «Diagnóstico para enviar…» del programa.

## Archivos

- ``$nombreSetup``: el programa del PC (Windows 10 u 11 de 64 bits) con su instalador. Trae el runtime de .NET.
- ``$nombreApk``: la app «Pantalla» de la tele o TV Box (Android 8 o superior).
- ``version.json`` y ``version.json.sig``: el manifiesto firmado que consultan las apps para avisar de versiones nuevas
  (ver [docs/actualizaciones.md](https://github.com/$repo/blob/main/docs/actualizaciones.md)).
- ``SHA256SUMS.txt``: las huellas SHA-256 de todo lo anterior.

## Novedades

- Programa del PC $($Version): $NotasEmisor
- App de la tele $($r.versionName): $NotasReceptor

## Verificar la descarga

Antes de instalar, comprueba que lo descargado es lo publicado:

``````powershell
Get-FileHash .\$nombreSetup -Algorithm SHA256   # compárala con su línea en SHA256SUMS.txt
``````

``````bash
sha256sum -c SHA256SUMS.txt --ignore-missing
``````

Más detalle (y cómo comprobar el certificado de la APK) en
[docs/verificar.md](https://github.com/$repo/blob/main/docs/verificar.md).

## Aviso de SmartScreen

El instalador **no tiene firma de código** (es un proyecto personal), así que Windows SmartScreen puede mostrar
«Windows protegió su PC». Después de comprobar la huella, aprieta «Más información» › «Ejecutar de todas formas».
"@
[IO.File]::WriteAllText((Join-Path $salida "notas.md"), $notas, (New-Object Text.UTF8Encoding($false)))

# ── Lo que queda por hacer, a mano ──────────────────────────────────────────────────────────
Write-Host "`nListo en ${salida}:"
Get-ChildItem $salida -File | ForEach-Object { "  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
$subir = ($archivos + "SHA256SUMS.txt" | ForEach-Object { "`"$(Join-Path $salida $_)`"" }) -join " "
Write-Host "`nPara crear el borrador del release (revísalo en GitHub y publícalo; NO lo marques como pre-release:"
Write-Host "releases/latest/ no los ve y las apps no se enterarían):`n"
Write-Host "gh release create v$Version --repo $repo --draft --title `"Pantalla TV $Version`" --notes-file `"$(Join-Path $salida 'notas.md')`" $subir"
