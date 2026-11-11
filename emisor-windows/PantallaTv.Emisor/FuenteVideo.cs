using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// De dónde salen las unidades H.264 de una transmisión: ffmpeg como proceso aparte (<see cref="FuenteFfmpeg"/>) o la
/// captura propia (<see cref="Nativa.TuberiaNativa"/>). <see cref="Emision"/> las prueba en orden (<see cref="Tuberias"/>)
/// con un mismo plazo para el primer cuadro y una misma política de caída.
/// </summary>
internal interface IFuenteVideo : IDisposable
{
    /// <summary>El nombre de la tubería (<see cref="Tuberias"/>).</summary>
    string Nombre { get; }

    /// <summary>Qué codificador quedó en uso, para mostrarlo; null si no hay nada que agregar al nombre.</summary>
    string? Detalle { get; }

    /// <summary>Unidades entregadas desde que empezó.</summary>
    long Unidades { get; }

    /// <summary>
    /// Se completa cuando la fuente ya tiene lo que necesita para capturar: desde ahí corre el plazo del primer cuadro.
    /// La nativa espera antes al monitor (hasta <see cref="Nativa.TuberiaNativa.PlazoMonitor"/>): esa espera no cuenta
    /// como «esta tubería no sirve».
    /// </summary>
    Task Lista { get; }

    /// <summary>
    /// Captura hasta que se cancela. Lanza <see cref="FuenteNoSirveException"/> si no puede seguir en este PC (se prueba la
    /// siguiente tubería) o <see cref="EmisionException"/> si hay que cortar y reconectar.
    /// </summary>
    /// <param name="enviar">Cada unidad de acceso (Annex B), apenas sale. Puede llamarse desde otro hilo.</param>
    Task EjecutarAsync(Action<byte[]> enviar, CancellationToken ct);

    /// <summary>La TV pidió un fotograma clave (o se llenó la cola). Una fuente que no puede darlo al momento lo ignora.</summary>
    void PedirClave();

    /// <summary>Lo ocurrido dentro de la captura desde la última consulta; null si la fuente no lo sabe (ffmpeg).</summary>
    Nativa.EstadisticasNativa? Tomar();
}

/// <summary>La fuente no puede seguir en este PC (falta el codificador, falla una y otra vez): se pasa a la tubería siguiente.</summary>
internal class FuenteNoSirveException(string mensaje, Exception? causa = null) : Exception(mensaje, causa);

/// <summary>
/// Una de las tuberías de ffmpeg (<see cref="Tuberias.Argumentos"/>): ffmpeg como proceso hijo que escribe H.264 por su
/// salida estándar. No puede dar un fotograma clave a pedido (manda uno cada <see cref="Tuberias.SegundosEntreClaves"/>).
/// </summary>
internal sealed class FuenteFfmpeg(string nombre, string ruta, OpcionesEmision op, Telemetria? telemetria) : IFuenteVideo
{
    private readonly TaskCompletionSource _lista = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _proceso;
    private long _unidades;

    public string Nombre => nombre;
    public string? Detalle => null;
    public long Unidades => Interlocked.Read(ref _unidades);
    public Task Lista => _lista.Task;
    public void PedirClave() { }
    public Nativa.EstadisticasNativa? Tomar() => null;

    public async Task EjecutarAsync(Action<byte[]> enviar, CancellationToken ct)
    {
        var proceso = Iniciar(ruta, Tuberias.Argumentos(nombre, op));
        _proceso = proceso;
        telemetria?.UsarFfmpeg(proceso);
        // Matarlo al cancelar: así la lectura de su salida termina aunque ffmpeg esté colgado.
        using var _ = ct.Register(Matar);
        _lista.TrySetResult();

        var errores = proceso.StandardError.ReadToEndAsync(CancellationToken.None);
        var separador = new SeparadorUnidades();
        var buf = new byte[1 << 16];
        var salida = proceso.StandardOutput.BaseStream;
        while (true)
        {
            int n;
            try { n = await salida.ReadAsync(buf, ct); }
            catch (Exception e) when (ct.IsCancellationRequested && e is IOException or ObjectDisposedException)
            {
                throw new OperationCanceledException(ct); // lo mató el Register de arriba
            }
            if (n == 0) break;
            separador.Agregar(buf.AsSpan(0, n), au =>
            {
                Interlocked.Increment(ref _unidades);
                enviar(au);
            });
        }

        ct.ThrowIfCancellationRequested();
        var detalle = (await errores).Trim();
        if (Unidades > 0)
            throw new EmisionException($"ffmpeg terminó ({nombre})" + (detalle.Length == 0 ? "." : ": " + detalle), reintentable: true);
        throw new FuenteNoSirveException(PrimeraLinea(detalle));
    }

    private static string PrimeraLinea(string texto)
    {
        var linea = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(linea) ? "terminó sin producir vídeo" : linea;
    }

    private static Process Iniciar(string ruta, List<string> argumentos)
    {
        var psi = new ProcessStartInfo(ruta)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argumentos) psi.ArgumentList.Add(a);
        try
        {
            return Process.Start(psi) ?? throw new EmisionException("No se pudo iniciar ffmpeg.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new EmisionException($"No se pudo ejecutar ffmpeg ({ruta}): {e.Message}");
        }
    }

    private void Matar()
    {
        var p = _proceso;
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // ya había terminado
        }
    }

    public void Dispose()
    {
        Matar();
        var p = Interlocked.Exchange(ref _proceso, null);
        if (p is null) return;
        telemetria?.UsarFfmpeg(null);
        p.Dispose();
    }
}

/// <summary>Busca ffmpeg junto al programa o en el PATH. Desde el 0.7.0 es opcional: solo lo usan las tuberías de respaldo.</summary>
internal static class Ffmpeg
{
    /// <summary>La ruta de ffmpeg.exe, o null si no está.</summary>
    /// <exception cref="EmisionException">Se dio una ruta y no existe.</exception>
    public static string? Buscar(string? ruta)
    {
        if (!string.IsNullOrWhiteSpace(ruta))
            return File.Exists(ruta) ? ruta : throw new EmisionException($"No existe ffmpeg en {ruta}.");

        var junto = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(junto)) return junto;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidato = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(candidato)) return candidato;
        }
        return null;
    }

    public const string NoEsta = "No encuentro ffmpeg.exe: ponlo junto a PantallaTv.exe o en el PATH (winget install Gyan.FFmpeg).";
}
