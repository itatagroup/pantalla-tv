namespace PantallaTv.Emisor;

/// <summary>
/// ffmpeg de respaldo, visto desde la ventana: saber si está, bajarlo con avance y poder cancelar. Solo se baja cuando la
/// persona lo pide y lo confirma (la ventana muestra de dónde, qué versión y cuánto pesa); nunca solo y nunca mientras se transmite.
/// </summary>
internal sealed partial class Mando
{
    /// <summary>Crea el descargador (las pruebas ponen uno con un servidor falso).</summary>
    internal Func<DescargadorFfmpeg> CrearDescargadorFfmpeg { get; set; } = DescargadorFfmpeg.Crear;

    /// <summary>Dónde busca ffmpeg el programa (las pruebas lo cambian para no mirar el PC).</summary>
    internal Func<string?> BuscarFfmpeg { get; set; } = () =>
    {
        try { return Ffmpeg.Buscar(null); }
        catch (EmisionException) { return null; }
    };

    private CancellationTokenSource? _cancelarFfmpeg;

    /// <summary>Porcentaje de la descarga de ffmpeg en curso; null si no se está descargando.</summary>
    public int? AvanceFfmpeg { get; private set; }

    /// <summary>Lo último que pasó con la descarga de ffmpeg, para la nota de «Avanzado». Null si nada.</summary>
    public string? EstadoFfmpeg { get; private set; }

    /// <summary>La ruta de ffmpeg si el programa lo encuentra ahora.</summary>
    public string? RutaFfmpeg => BuscarFfmpeg();

    /// <summary>Se puede bajar si no está, no se transmite y no hay otra descarga.</summary>
    public bool PuedeDescargarFfmpeg => AvanceFfmpeg is null && !Transmitiendo && RutaFfmpeg is null;

    /// <summary>Lo que se le dice a la persona antes de bajar: de dónde, qué versión, cuánto pesa y la licencia.</summary>
    public static string TextoConfirmacionFfmpeg()
    {
        var o = DescargadorFfmpeg.Fijado;
        return $"Se va a descargar ffmpeg {o.Version} (compilación «essentials» de Gyan Doshi, licencia GPL v3) desde {o.Url.Host}, "
               + $"unos {o.Tamano / 1048576} MB. Se comprueba su huella SHA-256 y se guarda solo ffmpeg.exe en {Ffmpeg.CarpetaDatos}. "
               + "Sirve de respaldo si la captura propia no funciona en este PC. Es lo único que el programa baja de internet además de las actualizaciones.";
    }

    public void DescargarFfmpegAhora() => _ = DescargarFfmpegAsync();

    /// <summary>Corta la descarga en curso (el archivo a medias se borra).</summary>
    public void CancelarDescargaFfmpeg() => _cancelarFfmpeg?.Cancel();

    internal async Task DescargarFfmpegAsync()
    {
        if (!PuedeDescargarFfmpeg) return;
        AvanceFfmpeg = 0;
        EstadoFfmpeg = "Descargando ffmpeg…";
        _cancelarFfmpeg?.Dispose();
        var cancelar = _cancelarFfmpeg = CancellationTokenSource.CreateLinkedTokenSource(_cancelarActualizacion.Token);
        Cambio?.Invoke();

        var ultimo = 0;
        void Avance(int porcentaje)
        {
            // Viene del hilo de la descarga: solo los cambios, y solo si sigue descargando.
            if (Interlocked.Exchange(ref ultimo, porcentaje) == porcentaje) return;
            EnLaInterfaz(() => { if (AvanceFfmpeg is not null) { AvanceFfmpeg = porcentaje; Cambio?.Invoke(); } });
        }

        ResultadoFfmpeg r;
        try
        {
            using var descargador = CrearDescargadorFfmpeg();
            r = await Task.Run(() => descargador.DescargarAsync(Avance, cancelar.Token)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // DescargarAsync no lanza por la red ni por el disco; algo inesperado no debe dejar «Descargando…» pegado.
            Registro.Error("ffmpeg: la descarga se interrumpió", e);
            r = new ResultadoFfmpeg(null, "No se pudo descargar ffmpeg: " + e.Message);
        }
        EnLaInterfaz(() =>
        {
            AvanceFfmpeg = null;
            EstadoFfmpeg = r.Correcto ? "ffmpeg quedó instalado y se usará de respaldo." : r.Motivo;
            if (!r.Cancelada)
                Aviso?.Invoke(r.Correcto ? "ffmpeg descargado y verificado. Queda de respaldo para la captura." : r.Motivo ?? "No se pudo descargar ffmpeg.",
                    r.Correcto ? ToolTipIcon.Info : ToolTipIcon.Error);
            Cambio?.Invoke();
        });
    }
}
