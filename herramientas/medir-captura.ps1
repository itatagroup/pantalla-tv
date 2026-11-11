# Mide una variante de captura+codificación con ffmpeg directo a «-f null -» (sin la tele).
# Medición de rendimiento de la captura. NO usa el emisor ni cambia nada del programa.
#
#   powershell -File herramientas/medir-captura.ps1 -Variante amf -Monitor 1 -Seg 20
#
# Pone una ventana ffplay con movimiento (testsrc2 a 60 fps) cubriendo el monitor medido, para que la
# duplicación de escritorio tenga cuadros nuevos (con el escritorio quieto los números engañan).
# Por cada segundo cuenta los cuadros que ffmpeg dice haber codificado; da fps medio y mínimo, y la CPU de ffmpeg.
param(
    [Parameter(Mandatory)][string]$Variante,
    [int]$Monitor = 0,
    [int]$Seg = 20,
    [int]$Fps = 60,
    [int]$X = -1,            # esquina izquierda del monitor para la ventana de movimiento (por defecto: 0 si Monitor=0, 1920 si no)
    [switch]$SinMovimiento
)
$ErrorActionPreference = 'Stop'
$ff = 'ffmpeg'
$m = $Monitor
$enc = @('-c:v','h264_amf','-usage','ultralowlatency','-quality','speed','-rc','cbr','-b:v','8000k','-enforce_hrd','0','-bf','0')
$hw  = @('-init_hw_device','d3d11va=cap:0','-filter_hw_device','cap')
$cfr = @('-fps_mode','cfr','-r',"$Fps")

switch -Regex ($Variante) {
    '^amf-gpu$'      { $in = @('-f','lavfi','-i',"vsrc_amf=monitor_index=${m}:framerate=$Fps") + $cfr + $enc }
    '^amf$'          { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps,hwdownload,format=bgra") + $cfr + $enc }
    '^amf-nomouse$'  { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps`:draw_mouse=0,hwdownload,format=bgra") + $cfr + $enc }
    '^amf-nodup$'    { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps`:dup_frames=0,hwdownload,format=bgra") + $cfr + $enc }
    '^amf-120$'      { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=120,hwdownload,format=bgra") + @('-fps_mode','cfr','-r',"$Fps") + $enc }
    '^amf-nv12cpu$'  { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps,hwdownload,format=bgra,format=nv12") + $cfr + $enc }
    '^gfx-hw$'       { $in = $hw + @('-f','lavfi','-i',"gfxcapture=monitor_idx=${m}:max_framerate=$Fps`:capture_cursor=false,hwdownload,format=bgra") + $cfr + $enc }
    '^amf-nv12fast$' { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps,hwdownload,format=bgra,scale=flags=fast_bilinear:out_color_matrix=bt709,format=nv12") + $cfr + $enc }
    '^amf-vpp$'      { $in = @('-init_hw_device','d3d11va=cap:0','-init_hw_device','amf=gpu:0','-filter_hw_device','gpu','-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps,hwdownload,format=bgra,hwupload,vpp_amf=format=nv12") + $cfr + $enc }
    '^dda-directo$'  { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps") + $cfr + $enc }
    '^dda-nv12$'     { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps,scale_d3d11=format=nv12") + $cfr + $enc }
    '^dda-vpp$'      { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps,hwmap=derive_device=amf,vpp_amf=format=nv12") + $cfr + $enc }
    '^dda-nomouse-directo$' { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps`:draw_mouse=0") + $cfr + $enc }
    '^dda-nodup-directo$'   { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=$Fps`:dup_frames=0") + @('-fps_mode','vfr') + $enc }
    '^dda-120-directo$'     { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=120") + @('-fps_mode','cfr','-r','120') + $enc }
    '^dda-120-a-60$'        { $in = $hw + @('-f','lavfi','-i',"ddagrab=${m}:framerate=120") + @('-fps_mode','cfr','-r',"$Fps") + $enc }
    '^gfx$'          { $in = $hw + @('-f','lavfi','-i',"gfxcapture=monitor_idx=${m}:max_framerate=$Fps`:capture_cursor=false") + $cfr + $enc }
    '^gfx-nv12$'     { $in = $hw + @('-f','lavfi','-i',"gfxcapture=monitor_idx=${m}:max_framerate=$Fps`:capture_cursor=false,scale_d3d11=format=nv12") + $cfr + $enc }
    default {
        # «extra:<todo lo que va antes de -c:v>» para pruebas sueltas
        if ($Variante -like 'crudo:*') { $in = $Variante.Substring(6) -split ' ' } else { throw "Variante desconocida: $Variante" }
    }
}

$tmp = Join-Path $env:TEMP "medir-$Variante-$m-$([guid]::NewGuid().ToString('N').Substring(0,6))"
New-Item -ItemType Directory $tmp | Out-Null
$prog = Join-Path $tmp 'progress.txt'; $err = Join-Path $tmp 'err.txt'

$play = $null
if (-not $SinMovimiento) {
    if ($X -lt 0) { $X = if ($m -eq 0) { 0 } else { 1920 } }
    $play = Start-Process ffplay -ArgumentList @('-loglevel','quiet','-f','lavfi','-i','testsrc2=size=1920x1080:rate=60','-noborder','-left',"$X",'-top','0','-x','1920','-y','1080','-alwaysontop') -PassThru
    Start-Sleep -Seconds 3
}

$a = @('-hide_banner','-loglevel','warning','-y') + $in + @('-t',"$Seg",'-progress',$prog,'-stats_period','1','-f','null','-')
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $ff -ArgumentList $a -PassThru -NoNewWindow -RedirectStandardError $err -RedirectStandardOutput (Join-Path $tmp 'out.txt')
$null = $p.Handle
$p.WaitForExit()
$cpu = $p.TotalProcessorTime.TotalSeconds
$wall = $sw.Elapsed.TotalSeconds
if ($play) { Stop-Process -Id $play.Id -Force -ErrorAction SilentlyContinue }

$frames = @()
if (Test-Path $prog) { $frames = Get-Content $prog | Where-Object { $_ -match '^frame=' } | ForEach-Object { [int]($_ -replace 'frame=','') } }
$errTxt = (Get-Content $err -ErrorAction SilentlyContinue) -join ' | '
if ($p.ExitCode -ne 0 -or $frames.Count -lt 3) {
    "{0,-22} mon={1} FALLÓ (salida {2}): {3}" -f $Variante, $m, $p.ExitCode, ($errTxt.Substring(0, [Math]::Min(300, $errTxt.Length)))
    return
}
# diferencias por segundo; se descarta el primer segundo (arranque)
$d = @(); for ($i = 1; $i -lt $frames.Count; $i++) { $d += ($frames[$i] - $frames[$i-1]) }
if ($d.Count -gt 3) { $d = $d[1..($d.Count-2)] }  # sin el primer segundo (arranque) ni el último (parcial)
$min = ($d | Measure-Object -Minimum).Minimum
$avg = [Math]::Round(($d | Measure-Object -Average).Average, 1)
$tot = $frames[-1]
"{0,-22} mon={1} fps medio={2} mín={3} cuadros={4}/{5:N0}s  CPU ffmpeg={6:N1}% de un núcleo  [{7}]" -f $Variante, $m, $avg, $min, $tot, $wall, (100*$cpu/$wall), ($d -join ',')
if ($errTxt) { "   avisos: " + $errTxt.Substring(0, [Math]::Min(300, $errTxt.Length)) }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
