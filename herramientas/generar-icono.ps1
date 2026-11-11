<#
  Genera el ícono del programa (emisor-windows/PantallaTv.Emisor/pantalla.ico) en 16, 32, 48 y 256 px:
  el mismo dibujo que el ícono de la app de la TV (un televisor con una señal, en verde oliva).
  Solo hay que correrlo si se cambia el dibujo; el .ico generado se guarda en el repo.

  Uso:  powershell -File herramientas/generar-icono.ps1
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$destino = Join-Path (Split-Path $PSScriptRoot -Parent) "emisor-windows\PantallaTv.Emisor\pantalla.ico"

function Dibujar([int]$lado) {
  $bmp = New-Object System.Drawing.Bitmap $lado, $lado
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = "AntiAlias"
  $g.Clear([System.Drawing.Color]::Transparent)
  $e = $lado / 96.0   # el dibujo está pensado sobre una cuadrícula de 96

  function Redondeado($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
  }
  $oliva = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x3F, 0x62, 0x12))
  $claro = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xEC, 0xED, 0xE6))
  $oscuro = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x22, 0x25, 0x1E))
  $lima = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xB5, 0xD6, 0x7A))

  $g.FillPath($oliva, (Redondeado 0 0 (96 * $e) (96 * $e) (16 * $e)))
  $g.FillPath($claro, (Redondeado (18 * $e) (26 * $e) (60 * $e) (38 * $e) (4 * $e)))
  $g.FillRectangle($oscuro, [single](24 * $e), [single](31 * $e), [single](48 * $e), [single](28 * $e))
  $g.FillRectangle($claro, [single](36 * $e), [single](70 * $e), [single](24 * $e), [single](4 * $e))
  $tri = @(
    (New-Object System.Drawing.PointF ([single](42 * $e)), ([single](37 * $e))),
    (New-Object System.Drawing.PointF ([single](56 * $e)), ([single](45 * $e))),
    (New-Object System.Drawing.PointF ([single](42 * $e)), ([single](53 * $e))))
  $g.FillPolygon($lima, [System.Drawing.PointF[]]$tri)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  , $ms.ToArray()
}

# Un .ico es una cabecera, una entrada por tamaño y las imágenes (aquí, PNG) una tras otra.
$lados = 16, 32, 48, 256
$imagenes = $lados | ForEach-Object { , (Dibujar $_) }
$salida = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $salida
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$lados.Count)
$desplazamiento = 6 + 16 * $lados.Count
for ($i = 0; $i -lt $lados.Count; $i++) {
  $lado = $lados[$i]
  $w.Write([byte]($lado % 256)); $w.Write([byte]($lado % 256)); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$imagenes[$i].Length); $w.Write([uint32]$desplazamiento)
  $desplazamiento += $imagenes[$i].Length
}
foreach ($img in $imagenes) { $w.Write([byte[]]$img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($destino, $salida.ToArray())
Write-Host "Ícono generado: $destino ($($salida.Length) bytes)"
