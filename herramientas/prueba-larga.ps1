<#
  Prueba larga: transmite N minutos a la TV y guarda la telemetría del PC y de la TV para estudiar
  el comportamiento en tiempos largos (memoria, fps, cuadros tirados, cortes de audio, señal Wi-Fi).

  Uso:  powershell -File herramientas/prueba-larga.ps1 [-Minutos 15] [-RetardoMs 2000] -Ip 10.0.0.20
                                                      [-Tuberia nativa] [-Monitor virtual] [-SinAudio]
        -Tuberia y -Monitor se pasan tal cual al emisor (--tuberia, --monitor); sin ellos, los de siempre.

  Deja una carpeta en %LOCALAPPDATA%\PantallaTv\telemetria\prueba-<fecha>\ con:
    emisor.csv     una fila cada 2 s del emisor (fps, Mb/s, tirados, RTT, CPU y memoria; con la nativa, también
                   sus cuadros, latidos, claves, latencias y reinicios)
    tv.log         lo que registra el receptor cada 2 s (vídeo y audio)
    tv.csv         cada 30 s: memoria del receptor, señal y velocidad del Wi-Fi, temperatura
    resumen.txt    los números principales

  Requisitos: el emisor compilado en Release, adb con la TV autorizada y la app «Pantalla» instalada.
  Ojo: el contenido con DRM (Netflix, etc.) se captura en negro; usar un archivo o un sitio sin DRM.
#>
param(
  [int]$Minutos = 15,
  [int]$RetardoMs = 2000,
  [Parameter(Mandatory=$true)][string]$Ip,
  [string]$Tuberia = "",
  [string]$Monitor = "",
  [switch]$SinAudio
)
$ErrorActionPreference = "Stop"
$raiz = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $raiz "emisor-windows\PantallaTv.Emisor\bin\Release\net10.0-windows\PantallaTv.exe"
if (-not (Test-Path $exe)) { throw "Falta compilar el emisor: dotnet build emisor-windows\PantallaTv.sln -c Release" }
$adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
if (-not $adb) { $adb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe" }
if (-not (Test-Path $adb)) { throw "No encuentro adb." }
$d = "${Ip}:5555"
& $adb connect $d | Out-Null
if (((& $adb devices | Select-String $Ip) -join "") -notmatch "\sdevice") { throw "Sin conexión autorizada con $d." }

$base = Join-Path $env:LOCALAPPDATA "PantallaTv\telemetria"
$carpeta = Join-Path $base ("prueba-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $carpeta | Out-Null
$antes = @(Get-ChildItem $base -Filter "emision-*.csv" -ErrorAction SilentlyContinue | ForEach-Object FullName)

# La TV: abrir el receptor con el retardo pedido y empezar a guardar su registro.
& $adb -s $d shell am force-stop cl.itatagroup.pantalla
& $adb -s $d shell am start -n cl.itatagroup.pantalla/.Receptor --ei retardo_ms $RetardoMs | Out-Null
Start-Sleep -Seconds 3
& $adb -s $d logcat -c
$log = Start-Process $adb -ArgumentList "-s", $d, "logcat", "-v", "time", "-s", "Pantalla:*" `
  -RedirectStandardOutput (Join-Path $carpeta "tv.log") -PassThru -WindowStyle Hidden

$segundos = $Minutos * 60
$argumentos = @("--cli", "--ip", $Ip, "--seg", $segundos, "--retardo", $RetardoMs, "--telemetria")
if ($Tuberia) { $argumentos += @("--tuberia", $Tuberia) }
if ($Monitor) { $argumentos += @("--monitor", $Monitor) }
if ($SinAudio) { $argumentos += "--sin-audio" }
$emisor = Start-Process $exe -ArgumentList $argumentos `
  -RedirectStandardOutput (Join-Path $carpeta "emisor.out") -RedirectStandardError (Join-Path $carpeta "emisor.err") `
  -PassThru -WindowStyle Hidden
Write-Host "Transmitiendo $Minutos min a $Ip (retardo $RetardoMs ms). Telemetría en $carpeta"

# Cada 30 s, el estado de la TV.
$tvCsv = Join-Path $carpeta "tv.csv"
"hora;ram_receptor_kb;rssi_dbm;enlace_mbps;temperatura_c" | Set-Content $tvCsv -Encoding utf8
function Numero($texto, $patron) { if (($texto -join " ") -match $patron) { $Matches[1] } else { "" } }
while (-not $emisor.HasExited) {
  $mem = Numero (& $adb -s $d shell "dumpsys meminfo cl.itatagroup.pantalla | grep 'TOTAL:'") 'TOTAL:\s+(\d+)'
  $wifi = & $adb -s $d shell "dumpsys wifi | grep 'mWifiInfo'"
  $temp = Numero (& $adb -s $d shell "cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null") '(\d+)'
  if ($temp -and [int]$temp -gt 1000) { $temp = [math]::Round([int]$temp / 1000, 1) }
  ("{0};{1};{2};{3};{4}" -f (Get-Date -Format "HH:mm:ss"), $mem, (Numero $wifi 'RSSI: (-?\d+)'), (Numero $wifi 'Link speed: (\d+)'), $temp) |
    Add-Content $tvCsv -Encoding utf8
  $emisor | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2
if (-not $log.HasExited) { $log.Kill() }

# El CSV del emisor es el archivo nuevo de la carpeta de telemetría.
$nuevo = Get-ChildItem $base -Filter "emision-*.csv" | Where-Object { $antes -notcontains $_.FullName } | Select-Object -Last 1
if ($nuevo) { Move-Item $nuevo.FullName (Join-Path $carpeta "emisor.csv") }

# Resumen.
function Estad($valores) {
  $v = @($valores | ForEach-Object { [double]$_ })
  if ($v.Count -eq 0) { return "sin datos" }
  $m = $v | Measure-Object -Minimum -Maximum -Average
  "mín {0:N1} · media {1:N1} · máx {2:N1} (n={3})" -f $m.Minimum, $m.Average, $m.Maximum, $m.Count
}
$r = @("Prueba larga: $Minutos min, retardo $RetardoMs ms, tubería $(if ($Tuberia) { $Tuberia } else { 'por defecto' }), monitor $(if ($Monitor) { $Monitor } else { 'por defecto' }), $(Get-Date -Format 'dd/MM/yyyy HH:mm')", "")
$csv = Join-Path $carpeta "emisor.csv"
if (Test-Path $csv) {
  $f = Import-Csv $csv -Delimiter ";"
  $estable = @($f | Select-Object -Skip 3)   # sin el arranque
  $r += "EMISOR (tubería: $(($f | Select-Object -Last 1).tuberia))"
  $r += "  fps:            " + (Estad $estable.fps)
  $r += "  Mb/s:           " + (Estad $estable.mbps)
  $r += "  RTT ms:         " + (Estad ($estable | Where-Object rtt_ms).rtt_ms)
  $r += "  tirados total:  " + (($f | Measure-Object tirados -Sum).Sum)
  $r += "  cola máx:       " + (($f | Measure-Object cola_envio -Maximum).Maximum)
  $r += "  RAM ffmpeg MB:  primera {0} · última {1}" -f $estable[0].ram_ffmpeg_mb, $estable[-1].ram_ffmpeg_mb
  $r += "  RAM emisor MB:  primera {0} · última {1}" -f $estable[0].ram_emisor_mb, $estable[-1].ram_emisor_mb
  # CPU en núcleos (el CSV la da en % del total de núcleos), del emisor y de ffmpeg juntos.
  $nucleos = [Environment]::ProcessorCount
  $r += "  CPU núcleos:    " + (Estad ($estable | ForEach-Object { ([double]$_.cpu_emisor_pct + [double]$_.cpu_ffmpeg_pct) * $nucleos / 100 }))
  if ($estable[0].PSObject.Properties.Name -contains "nat_nuevos" -and $estable[0].nat_nuevos -ne "") {
    $r += "  nativa cuadros nuevos/s: " + (Estad ($estable | ForEach-Object { [double]$_.nat_nuevos / 2 }))
    $r += "  nativa latencia codif. ms: " + (Estad ($estable | Where-Object nat_lat_codif_ms).nat_lat_codif_ms)
    $r += "  nativa claves: {0} · reinicios: {1} · perdidos por la captura: {2}" -f `
      ($f | Measure-Object nat_claves -Sum).Sum, ($f | Measure-Object nat_reinicios -Sum).Sum, ($f | Measure-Object nat_perdidos -Sum).Sum
  }
  $r += "  audio paq/s:    " + (Estad $estable.audio_paq_s)
}
$lineas = Get-Content (Join-Path $carpeta "tv.log") -Encoding UTF8 -ErrorAction SilentlyContinue
$video = @($lineas | Where-Object { $_ -match 'video: ([\d,\.]+) fps, tirados (\d+), sin dibujar (\d+).*media (\d+) ms, p95 (\d+) ms' } |
  ForEach-Object { $null = $_ -match 'video: ([\d,\.]+) fps, tirados (\d+), sin dibujar (\d+).*media (\d+) ms, p95 (\d+) ms'
    [pscustomobject]@{ fps = $Matches[1].Replace(",", "."); tirados = [int]$Matches[2]; sinDibujar = [int]$Matches[3]; media = $Matches[4]; p95 = $Matches[5] } } | Select-Object -Skip 3)
$audio = @($lineas | Where-Object { $_ -match 'audio: \d+ paq/s' } |
  ForEach-Object { $null = $_ -match 'descartados (\d+), cortes (\d+), pico \d+ %, suena a: (\d+)?'
    [pscustomobject]@{ descartados = [int]$Matches[1]; cortes = [int]$Matches[2]; suena = $Matches[3] } } | Select-Object -Skip 3)
$r += @("", "TV (receptor)")
if ($video.Count) {
  $r += "  fps:                      " + (Estad $video.fps)
  $r += "  tránsito+decod. media ms: " + (Estad $video.media)
  $r += "  tránsito+decod. p95 ms:   " + (Estad $video.p95)
  $r += "  saltos de vídeo:          " + (($video | Measure-Object tirados -Sum).Sum)
  $r += "  cuadros sin dibujar:      " + (($video | Measure-Object sinDibujar -Sum).Sum)
}
if ($audio.Count) {
  $r += "  audio suena a (ms):       " + (Estad ($audio | Where-Object suena).suena)
  $r += "  audio descartados:        " + (($audio | Measure-Object descartados -Sum).Sum)
  $r += "  cortes de audio (total):  " + (($audio | Select-Object -Last 1).cortes)
}
$tv = @(Import-Csv $tvCsv -Delimiter ";")
if ($tv.Count) {
  $r += "  RAM receptor kB:          primera {0} · última {1}" -f $tv[0].ram_receptor_kb, $tv[-1].ram_receptor_kb
  $r += "  señal Wi-Fi dBm:          " + (Estad ($tv | Where-Object rssi_dbm).rssi_dbm)
  $r += "  temperatura °C:           " + (Estad ($tv | Where-Object temperatura_c).temperatura_c)
}
$err = Get-Content (Join-Path $carpeta "emisor.out") -Encoding UTF8 | Where-Object { $_ -match "^\[Error\]" }
if ($err) { $r += @("", "ERROR DEL EMISOR: $err") }
$r | Set-Content (Join-Path $carpeta "resumen.txt") -Encoding utf8
$r | ForEach-Object { Write-Host $_ }
Write-Host "`nCarpeta: $carpeta"
