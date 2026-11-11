# Descarga la compilación LGPL compartida de FFmpeg que usa el candidato «libavcodec» del prototipo N1.
# Fuente: FFmpeg-Builds (antes BtbN/FFmpeg-Builds), etiqueta fija del 10/11/2026, variante «win64-lgpl-shared-9.0».
# Licencia de las DLL: LGPL-2.1 o posterior (ver LICENSE.txt dentro del zip). No se usan en el emisor todavía.
#   powershell -File descargar-ffmpeg-lgpl.ps1
# Deja las DLL en .\ffmpeg-lgpl\bin (ignorado por git). Se niega a seguir si la huella no coincide.
$ErrorActionPreference = 'Stop'
$nombre = 'ffmpeg-n9.0.2-48-gd4070a14ce-win64-lgpl-shared-9.0'
$url = "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-10-12-39/$nombre.zip"
$huella = 'cdc19c20aa80d46f78cfc69c8d87430ee34990864e30379db02d1447cd111e83'
$destino = Join-Path $PSScriptRoot 'ffmpeg-lgpl'
if (Test-Path (Join-Path $destino 'bin\avcodec-*.dll')) { "Ya está en $destino"; return }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-lgpl-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp "$nombre.zip"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $huella) { throw "Huella distinta: $h (se esperaba $huella). No se instala nada." }
    Expand-Archive $zip -DestinationPath $tmp
    if (Test-Path $destino) { Remove-Item $destino -Recurse -Force }
    Move-Item (Join-Path $tmp $nombre) $destino
    "Listo: $destino (huella $h)"
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
