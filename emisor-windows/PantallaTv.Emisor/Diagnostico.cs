using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.DXGI;

namespace PantallaTv.Emisor;

/// <summary>
/// <c>PantallaTv.exe --diagnostico</c> (o «Diagnóstico para enviar…» en la bandeja): un informe de texto de lo que este PC
/// tiene para transmitir, pensado para que alguien sin la TV a mano lo mande de vuelta. Gráficas y monitores, los
/// codificadores H.264 por hardware, una prueba de 3 s de la captura propia en cada monitor (sin TV: lo codificado se
/// descarta), una prueba del monitor virtual (lo conecta unos segundos, lo captura y lo vuelve a quitar), ffmpeg si
/// está, el sonido, los errores registrados con su detalle y las últimas líneas del <see cref="Registro"/>. Lo único que
/// cambia, y solo por unos segundos, es el monitor virtual: queda como estaba.
/// </summary>
internal static class Diagnostico
{
    internal static readonly TimeSpan DuracionPrueba = TimeSpan.FromSeconds(3);

    /// <summary>Arma el informe y lo guarda en <c>%LOCALAPPDATA%\PantallaTv\</c>. Devuelve la ruta del archivo.</summary>
    public static string Generar(Action<string>? avance = null)
    {
        var t = new StringBuilder();
        void L(string linea = "") { t.AppendLine(linea); avance?.Invoke(linea); }

        L($"Pantalla TV {Instalacion.Version} — diagnóstico del {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); .NET {Environment.Version}");
        L();

        L("== Gráficas (DXGI)");
        try
        {
            using var fabrica = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; fabrica.EnumAdapters1(a, out var ad).Success; a++)
            {
                using (ad)
                {
                    var d = ad.Description1;
                    L($"[{a}] {d.Description.Trim()} · {Fabricante(d.VendorId)} (VEN_{d.VendorId:X4} DEV_{d.DeviceId:X4}) · "
                      + $"{d.DedicatedVideoMemory / 1048576} MB · LUID {d.Luid.HighPart:X8}:{d.Luid.LowPart:X8}"
                      + ((d.Flags & AdapterFlags.Software) != 0 ? " · por software" : ""));
                    for (uint s = 0; ad.EnumOutputs(s, out var sal).Success; s++)
                        using (sal)
                        {
                            var ds = sal.Description;
                            L($"     salida {s}: {ds.DeviceName} {(ds.AttachedToDesktop ? "en el escritorio" : "desconectada")}"
                              + $" ({ds.DesktopCoordinates.Right - ds.DesktopCoordinates.Left}×{ds.DesktopCoordinates.Bottom - ds.DesktopCoordinates.Top})");
                        }
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    try
                    {
                        var mft = Nativa.CodificadorMf.Listar(d.Luid);
                        L("     codificadores H.264 por hardware, en el orden en que se prueban: "
                          + (mft.Count == 0 ? "ninguno" : string.Join(" · ", mft)));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { L($"     codificadores: no se pudieron listar ({e.Message})"); }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { L($"DXGI falló: {e.Message}"); }
        L();

        L("== Monitores");
        IReadOnlyList<Monitor> monitores = [];
        try
        {
            monitores = Monitores.Listar();
            foreach (var m in monitores)
                L($"{m.Indice}: {m.Nombre} {m.Ancho}×{m.Alto}{(m.Principal ? " principal" : "")}{(m.Descripcion.Length > 0 ? " · " + m.Descripcion : "")} · adaptador {m.Adaptador} ({m.NombreAdaptador}), salida {m.Salida}");
            L($"monitor virtual: {Cli.EstadoMonitorVirtual()}");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { L($"No se pudieron listar: {e.Message}"); }
        L();

        L($"== Prueba de la captura propia ({DuracionPrueba.TotalSeconds:0} s por monitor, sin TV)");
        foreach (var m in monitores) L(ProbarNativa(m));
        L();

        L("== Monitor virtual (prueba)");
        foreach (var linea in ProbarMonitorVirtual()) L(linea);
        L();

        L("== ffmpeg (opcional: solo de respaldo)");
        var ffmpeg = Ffmpeg.Buscar(null);
        L(ffmpeg is null ? "no está instalado" : $"{ffmpeg} · {VersionFfmpeg(ffmpeg)}");
        L();

        L("== Sonido");
        try { L(Cli.DescribirAudio()); }
        catch (Exception e) when (e is not OutOfMemoryException) { L($"sin dispositivo de sonido ({e.Message})"); }
        L();

        L("== Errores registrados (los últimos 30, con su detalle)");
        var errores = Registro.Errores();
        if (errores.Count == 0) L("ninguno");
        foreach (var error in errores) L(error);
        L();

        L("== Registro (últimas líneas)");
        foreach (var linea in Registro.Ultimas(120)) L(linea);

        var carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PantallaTv");
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, $"diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(ruta, t.ToString(), Encoding.UTF8);
        return ruta;
    }

    /// <summary>Abre el informe en el Bloc de notas (para quien no lo corrió desde una consola).</summary>
    public static void Abrir(string ruta) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ruta}\"") { UseShellExecute = true });

    /// <summary>Corre la tubería nativa unos segundos sobre ese monitor y resume qué pasó.</summary>
    /// <summary>
    /// El monitor virtual de punta a punta, como al transmitir: el controlador, conectarlo, que Windows y la gráfica lo
    /// muestren, capturarlo 3 s y quitarlo. Cada paso con su tiempo; si falla, en qué paso y por qué (el detalle también
    /// queda en los errores del registro). Si ya estaba conectado, solo se captura y se deja como estaba.
    /// </summary>
    internal static IEnumerable<string> ProbarMonitorVirtual()
    {
        var lineas = new List<string>();
        try
        {
            lineas.Add("controlador: " + DescribirControlador());
            var virtualMonitor = MonitorVirtual.Detectar();
            if (virtualMonitor is null)
            {
                lineas.Add("no hay monitor virtual (el controlador no está instalado o Windows no lo muestra): nada que probar");
                return lineas;
            }
            lineas.Add($"salida de Windows: {virtualMonitor.Dispositivo}");
            var reloj = Stopwatch.StartNew();
            var resultado = virtualMonitor.Conectar(TimeSpan.FromSeconds(20));
            lineas.Add($"conectar: {resultado} en {reloj.Elapsed.TotalSeconds:0.0} s"
                       + (resultado == MonitorVirtual.Resultado.Fallo ? " — NO SE PUDO (el motivo está en «Errores registrados»)" : ""));
            if (resultado == MonitorVirtual.Resultado.Fallo) return lineas;
            try
            {
                var monitor = Monitores.Listar().FirstOrDefault(m => string.Equals(m.Nombre, virtualMonitor.Dispositivo, StringComparison.OrdinalIgnoreCase));
                if (monitor is null)
                {
                    lineas.Add("Windows lo conectó, pero no aparece en la lista de pantallas que se pueden capturar");
                    Registro.Error($"diagnóstico: el monitor virtual {virtualMonitor.Dispositivo} se conectó pero no aparece en la lista de pantallas");
                    return lineas;
                }
                lineas.Add($"aparece como {monitor.Nombre} {monitor.Ancho}×{monitor.Alto} · adaptador {monitor.Adaptador} ({monitor.NombreAdaptador}), salida {monitor.Salida}");
                lineas.Add("captura: " + ProbarNativa(monitor));
            }
            finally
            {
                if (resultado == MonitorVirtual.Resultado.Conectado)
                    lineas.Add(virtualMonitor.Liberar() ? "quitado del escritorio otra vez" : "NO se pudo quitar: se reintenta al abrir el programa");
                else
                    lineas.Add("ya estaba conectado: se deja así");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Registro.Error("diagnóstico: la prueba del monitor virtual falló", e);
            lineas.Add($"la prueba falló: {e.Message}");
        }
        return lineas;
    }

    private static Encoding CodificacionAnsi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>Lo que dice Windows del controlador del monitor virtual (pnputil, sin administrador).</summary>
    private static string DescribirControlador()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pnputil.exe", "/enum-devices /class Display")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Redirigido, pnputil escribe en la página de códigos ANSI (1252 en Windows en español), no en UTF-8.
                StandardOutputEncoding = CodificacionAnsi(),
            })!;
            var salida = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            // pnputil separa cada dispositivo con una línea en blanco; interesa el bloque del controlador.
            var bloque = salida.Replace("\r\n", "\n").Split("\n\n")
                .FirstOrDefault(b => b.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase)
                                     || b.Contains(MonitorVirtual.IdControlador, StringComparison.OrdinalIgnoreCase));
            return bloque is null
                ? "pnputil no lo lista entre las pantallas"
                : string.Join(" · ", bloque.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Skip(1).Select(l => System.Text.RegularExpressions.Regex.Replace(l, @":\s+", ": ")));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"no se pudo consultar ({e.Message})";
        }
    }

    private static string ProbarNativa(Monitor m)
    {
        var reloj = Stopwatch.StartNew();
        ulong AhoraUs() => (ulong)(reloj.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
        var op = new Nativa.OpcionesNativa(m.Nombre, m.Adaptador, m.Salida, m.Ancho & ~1, m.Alto & ~1, 60, 8000);
        var nativa = new Nativa.TuberiaNativa(op, AhoraUs);
        long bytes = 0, claves = 0;
        var primeraEsClave = (bool?)null;
        double? primeraMs = null;
        using var cts = new CancellationTokenSource(DuracionPrueba);
        string? error = null;
        try
        {
            nativa.EjecutarAsync(au =>
            {
                var nals = AnnexB.Nals(au);
                var clave = nals.Any(n => n.Tipo == AnnexB.CuadroClave);
                primeraEsClave ??= clave && nals.Any(n => n.Tipo == AnnexB.Sps);
                primeraMs ??= reloj.Elapsed.TotalMilliseconds;
                Interlocked.Add(ref bytes, au.Length);
                if (clave) Interlocked.Increment(ref claves);
            }, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { error = e.Message; }

        var s = nativa.Tomar();
        var r = new StringBuilder($"{m.Nombre} ({m.Ancho}×{m.Alto}): ");
        r.Append(error is null && nativa.Unidades > 0 ? "FUNCIONA" : "NO FUNCIONA");
        if (error is not null) r.Append($" — {error}");
        r.Append($"\n     unidades {nativa.Unidades} ({bytes / 1024} KB), claves {claves}");
        if (primeraMs is { } p) r.Append($", primera a los {p:F0} ms{(primeraEsClave == true ? " (clave con SPS/PPS)" : " (¡sin clave!)")}");
        r.Append($"; captura: nuevos {s.Nuevos}, ratón {s.SoloRaton}, latidos {s.Latidos}, reinicios {s.Reinicios}");
        if (s.LatCodifMs is { } lc) r.Append($"; codificador {lc:F1} ms de media (máx {s.LatCodifMaxMs:F1})");
        if (nativa.Detalle is { } d) r.Append($"\n     {d}");
        return r.ToString();
    }

    private static string VersionFfmpeg(string ruta)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ruta, "-hide_banner -version")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var linea = p.StandardOutput.ReadLine() ?? "";
            if (!p.WaitForExit(3000)) p.Kill();
            return linea;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return $"no responde ({e.Message})"; }
    }

    private static string Fabricante(uint vendorId) => vendorId switch
    {
        0x1002 or 0x1022 => "AMD",
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        _ => "otro fabricante",
    };
}
