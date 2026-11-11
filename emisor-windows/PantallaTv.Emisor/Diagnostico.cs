using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.DXGI;

namespace PantallaTv.Emisor;

/// <summary>
/// <c>PantallaTv.exe --diagnostico</c> (o «Diagnóstico para enviar…» en la bandeja): un informe de texto de lo que este PC
/// tiene para transmitir, pensado para que alguien sin la TV a mano lo mande de vuelta. Gráficas y monitores, los
/// codificadores H.264 por hardware, una prueba de 3 s de la captura propia en cada monitor (sin TV: lo codificado se
/// descarta), ffmpeg si está, el sonido y las últimas líneas del <see cref="Registro"/>. No cambia nada en el sistema.
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

        L("== ffmpeg (opcional: solo de respaldo)");
        var ffmpeg = Ffmpeg.Buscar(null);
        L(ffmpeg is null ? "no está instalado" : $"{ffmpeg} · {VersionFfmpeg(ffmpeg)}");
        L();

        L("== Sonido");
        try { L(Cli.DescribirAudio()); }
        catch (Exception e) when (e is not OutOfMemoryException) { L($"sin dispositivo de sonido ({e.Message})"); }
        L();

        L("== Registro (últimas líneas)");
        foreach (var linea in Registro.Ultimas(40)) L(linea);

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
