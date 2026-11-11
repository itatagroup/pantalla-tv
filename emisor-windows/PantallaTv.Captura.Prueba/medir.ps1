# Mide una combinación del prototipo N1 con el mismo contenido que herramientas/medir-captura.ps1.
#   powershell -File medir.ps1 -Monitor virtual -Codificador mf -Tasa vbr -Seg 25
#   powershell -File medir.ps1 -Monitor 0 -Codificador av -Quieto
# Con movimiento pone encima del monitor una ventana ffplay (testsrc2 1920×1080 a 60 fps); con -Quieto pone
# un color liso (sin cambios), para que lo que haya detrás en el escritorio no cuente.
# Usa el ffplay del PATH solo para la ventana de contenido. Imprime el resumen del prototipo.
param(
    [string]$Monitor = '0',
    [ValidateSet('mf','av','ninguno')][string]$Codificador = 'mf',
    [ValidateSet('cbr','vbr')][string]$Tasa = 'cbr',
    [ValidateSet('sombreador','vp')][string]$Conversion = 'sombreador',
    [int]$Seg = 25,
    [int]$Kbps = 8000,
    [switch]$Quieto,
    [string]$Salida
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\CapturaPrueba.exe'
if (-not (Test-Path $exe)) { throw "Falta $exe (dotnet build -c Release)" }

# Posición del monitor pedido: se la pregunta al propio prototipo.
$lista = & $exe --listar
$linea = $lista | Where-Object {
    if ($Monitor -eq 'virtual') { $_ -match 'VDD' } else { $_ -match "^$Monitor`:" }
} | Select-Object -First 1
if (-not $linea -or $linea -notmatch 'en \((-?\d+),(-?\d+)\)') { throw "No encuentro el monitor $Monitor en: $($lista -join ' | ')" }
$x = $Matches[1]; $y = $Matches[2]

$fuente = if ($Quieto) { 'color=c=0x4a5a3a:size=1920x1080:rate=1' } else { 'testsrc2=size=1920x1080:rate=60' }
$play = Start-Process ffplay -ArgumentList @('-loglevel','quiet','-f','lavfi','-i',$fuente,'-noborder','-left',$x,'-top',$y,'-x','1920','-y','1080','-alwaysontop') -PassThru
Start-Sleep -Seconds 3
try {
    $a = @('--monitor', $Monitor, '--codificador', $Codificador, '--tasa', $Tasa, '--conversion', $Conversion, '--seg', "$Seg", '--kbps', "$Kbps")
    if ($Salida) { $a += @('--salida', $Salida) }
    $texto = & $exe @a
    $texto | Where-Object { $_ -match '^(Monitor|Conversión|RESUMEN|Claves|MF)' }
} finally {
    Stop-Process -Id $play.Id -Force -ErrorAction SilentlyContinue
}
