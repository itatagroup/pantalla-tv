<#
  Arma lo que se publica, en dist/ (fuera de git):
    PantallaTvSetup-<versión>.exe   instalador con asistente (trae dentro el controlador del monitor virtual). Es el propio programa, autónomo (no pide
                                    tener .NET instalado): al abrirlo con ese nombre muestra el asistente.
    pantalla-receptor-<versión>.apk la app «Pantalla» para la tele o TV Box
    SHA256SUMS.txt                  huellas para comprobar las descargas

  Uso:  powershell -File herramientas/empaquetar.ps1 [-SinPruebas] [-SoloEmisor] [-ConFfmpeg C:\ruta\ffmpeg.exe]

  -SoloEmisor arma solo el instalador (sin compilar las apps de Android): lo usa publicar.ps1, que toma el APK del
  receptor ya compilado. dist/publicar/ (los releases que arma publicar.ps1) no se borra.

  -ConFfmpeg copia ese ffmpeg.exe junto al instalador; el asistente lo instala con el programa.
  Ojo con el tamaño (la compilación «full» pesa ~217 MB) y con su licencia (GPL) antes de publicarlo.
#>
param(
  [switch]$SinPruebas,
  [switch]$SoloEmisor,
  [string]$ConFfmpeg
)
$ErrorActionPreference = "Stop"
$raiz = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $raiz "dist"
$sln = Join-Path $raiz "emisor-windows\PantallaTv.sln"
$proyecto = Join-Path $raiz "emisor-windows\PantallaTv.Emisor\PantallaTv.Emisor.csproj"

function Correr($descripcion, [scriptblock]$accion) {
  Write-Host "== $descripcion"
  & $accion
  if ($LASTEXITCODE -ne 0) { throw "Falló: $descripcion" }
}

# El controlador del monitor virtual va dentro del instalador: tiene que ser exactamente el comprobado
# (emisor-windows/controlador/LEEME.md). Un archivo que falte o cambie corta el empaquetado.
Write-Host "== Comprobar el controlador del monitor virtual"
$controlador = Join-Path $raiz "emisor-windows\controlador"
$sumasControlador = Join-Path $controlador "SHA256SUMS.txt"
if (-not (Test-Path $sumasControlador)) { throw "Falta $sumasControlador" }
$lineas = Get-Content $sumasControlador | Where-Object { $_.Trim() }
if ($lineas.Count -lt 3) { throw "SHA256SUMS.txt del controlador debería listar sus 3 archivos" }
foreach ($linea in $lineas) {
  if ($linea -notmatch '^([0-9a-fA-F]{64}) [ *](.+)$') { throw "Línea inválida en SHA256SUMS.txt: $linea" }
  $esperada = $Matches[1].ToLower(); $archivo = Join-Path $controlador $Matches[2].Trim()
  if (-not (Test-Path $archivo)) { throw "Falta el archivo del controlador: $archivo" }
  $real = (Get-FileHash $archivo -Algorithm SHA256).Hash.ToLower()
  if ($real -ne $esperada) { throw "La huella de $archivo no coincide con SHA256SUMS.txt ($real)" }
}
foreach ($licencia in @("LICENSE", "LEEME.md")) {
  if (-not (Test-Path (Join-Path $controlador $licencia))) { throw "Falta $licencia en $controlador" }
}

if (-not $SinPruebas) { Correr "Pruebas del emisor" { dotnet test $sln -c Release --nologo -v q } }

$version = ([xml](Get-Content $proyecto)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No encuentro <Version> en $proyecto" }

if (Test-Path $dist) { Get-ChildItem $dist | Where-Object Name -ne "publicar" | Remove-Item -Recurse -Force }
$tmp = Join-Path $dist "tmp"
Correr "Publicar el emisor $version (un solo archivo, con el runtime dentro)" {
  dotnet publish $proyecto -c Release -r win-x64 --self-contained true --nologo -v q -o $tmp `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none
}
$setup = Join-Path $dist "PantallaTvSetup-$version.exe"
Move-Item (Join-Path $tmp "PantallaTv.exe") $setup
Remove-Item $tmp -Recurse -Force

if ($ConFfmpeg) {
  if (-not (Test-Path $ConFfmpeg)) { throw "No existe $ConFfmpeg" }
  Copy-Item $ConFfmpeg (Join-Path $dist "ffmpeg.exe")
}

if (-not $SoloEmisor) {
Push-Location (Join-Path $raiz "receptor-android")
try {
  Correr "Compilar el receptor Android" { node tools/build.mjs | Out-Null }
  $v = (Get-Content version.json | ConvertFrom-Json).versionName
  Copy-Item "out\receptor.apk" (Join-Path $dist "pantalla-receptor-$v.apk")
} finally { Pop-Location }

$emisorAndroid = Join-Path $raiz "emisor-android"
if (Test-Path (Join-Path $emisorAndroid "tools\build.mjs")) {
  Push-Location $emisorAndroid
  try {
    Correr "Compilar el emisor Android" { node tools/build.mjs | Out-Null }
    $v = (Get-Content version.json | ConvertFrom-Json).versionName
    Copy-Item "out\emisor.apk" (Join-Path $dist "pantalla-emisor-$v.apk")
  } finally { Pop-Location }
}
}

$sumas = Get-ChildItem $dist -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
$sumas | Set-Content (Join-Path $dist "SHA256SUMS.txt") -Encoding ascii

Write-Host "`nListo en ${dist}:"
Get-ChildItem $dist -File | ForEach-Object { "  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
