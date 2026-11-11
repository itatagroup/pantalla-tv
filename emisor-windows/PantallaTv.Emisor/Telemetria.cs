using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PantallaTv.Emisor;

/// <summary>
/// Guarda una fila de CSV cada vez que se lo piden, para estudiar el comportamiento en sesiones
/// largas (fugas de memoria, deriva, caídas de red). Se escribe y vacía en cada fila: si el programa
/// se cierra de golpe, no se pierde nada.
/// </summary>
internal sealed class Telemetria : IDisposable
{
    private const string Cabecera =
        "hora_local;t_s;fps;mbps;tirados;cola_envio;rtt_ms;ram_emisor_mb;cpu_emisor_pct;ram_ffmpeg_mb;cpu_ffmpeg_pct;tuberia;audio_paq_s;" + Nativa.EstadisticasNativa.CabeceraCsv;

    private readonly StreamWriter _csv;
    private readonly Stopwatch _reloj = Stopwatch.StartNew();
    private readonly Process _yo = Process.GetCurrentProcess();
    private TimeSpan _cpuYoAntes;
    private TimeSpan _cpuFfmpegAntes;
    private int _pidFfmpeg = -1;
    private long _tAntesMs;

    public string Ruta { get; }

    public Telemetria(string carpeta)
    {
        Directory.CreateDirectory(carpeta);
        Ruta = Path.Combine(carpeta, $"emision-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _csv = new StreamWriter(Ruta, false, new UTF8Encoding(true)) { AutoFlush = true };
        _csv.WriteLine(Cabecera);
        _cpuYoAntes = _yo.TotalProcessorTime;
    }

    /// <summary>Hay que decirle qué proceso de ffmpeg medir (cambia en cada transmisión).</summary>
    public void UsarFfmpeg(Process? p)
    {
        _pidFfmpeg = p?.Id ?? -1;
        _cpuFfmpegAntes = TimeSpan.Zero;
        if (p is null) return;
        try { _cpuFfmpegAntes = p.TotalProcessorTime; } catch (InvalidOperationException) { }
    }

    public void Anotar(Estadisticas e, int colaEnvio, string tuberia)
    {
        var ahoraMs = _reloj.ElapsedMilliseconds;
        var dtMs = Math.Max(1, ahoraMs - _tAntesMs);
        _tAntesMs = ahoraMs;
        var nucleos = Environment.ProcessorCount;

        _yo.Refresh();
        var cpuYo = _yo.TotalProcessorTime;
        var pctYo = (cpuYo - _cpuYoAntes).TotalMilliseconds / dtMs / nucleos * 100;
        _cpuYoAntes = cpuYo;

        double ramFfmpeg = 0, pctFfmpeg = 0;
        if (_pidFfmpeg > 0)
        {
            try
            {
                using var f = Process.GetProcessById(_pidFfmpeg);
                ramFfmpeg = f.WorkingSet64 / 1048576.0;
                var cpuF = f.TotalProcessorTime;
                pctFfmpeg = (cpuF - _cpuFfmpegAntes).TotalMilliseconds / dtMs / nucleos * 100;
                _cpuFfmpegAntes = cpuF;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _pidFfmpeg = -1; // ffmpeg terminó
            }
        }

        var c = CultureInfo.InvariantCulture;
        _csv.WriteLine(string.Join(';',
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", c),
            (ahoraMs / 1000.0).ToString("F1", c),
            e.Fps.ToString("F1", c),
            e.Mbps.ToString("F2", c),
            e.Tirados.ToString(c),
            colaEnvio.ToString(c),
            e.RttMs?.ToString("F1", c) ?? "",
            (_yo.WorkingSet64 / 1048576.0).ToString("F0", c),
            pctYo.ToString("F1", c),
            ramFfmpeg.ToString("F0", c),
            pctFfmpeg.ToString("F1", c),
            tuberia,
            e.AudioPaqS.ToString("F0", c),
            Nativa.EstadisticasNativa.Csv(e.Nativa))); // vacías con ffmpeg
    }

    public void Dispose()
    {
        _csv.Dispose();
        _yo.Dispose();
    }
}
