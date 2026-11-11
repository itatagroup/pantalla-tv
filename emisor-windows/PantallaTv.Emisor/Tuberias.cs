namespace PantallaTv.Emisor;

/// <summary>
/// Las formas de capturar y codificar la pantalla («tuberías»), de la mejor a la más compatible: primero la propia
/// (<see cref="Nativa"/>) y después las de ffmpeg, que son el respaldo y solo se usan si ffmpeg está instalado. El
/// emisor las prueba en orden y se queda con la primera que produce vídeo (ver <see cref="Emision"/>).
/// </summary>
internal static class Tuberias
{
    /// <summary>AMD: captura y codifica sin salir de la GPU. Medido: 60 fps estables (la mejor en este PC).</summary>
    public const string AmfGpu = "amf-gpu";

    /// <summary>
    /// AMD: captura con ddagrab, copia el cuadro a memoria y codifica con AMF. Sin más, rinde ~37 fps
    /// (el hilo único que copia y entrega BGRA al codificador se queda al 100 % de un núcleo). Con la
    /// conversión a NV12 en CPU al final del filtro (<see cref="FiltroNv12"/>) rinde 60 fps estables a
    /// costa de ~2 núcleos (medido en el monitor virtual y en el principal de una gráfica AMD).
    /// </summary>
    public const string Amf = "amf";

    /// <summary>NVIDIA. Sin probar: no hay una gráfica NVIDIA donde medirla.</summary>
    public const string Nvenc = "nvenc";

    /// <summary>Intel Quick Sync. Sin probar: no hay una gráfica Intel donde medirla.</summary>
    public const string Qsv = "qsv";

    /// <summary>Codificador de Windows (Media Foundation), cualquier gráfica con controlador H.264.</summary>
    public const string Mf = "mf";

    /// <summary>Por CPU: funciona en cualquier PC, pero ocupa casi tres núcleos a 1080p60.</summary>
    public const string X264 = "x264";

    /// <summary>
    /// Sin ffmpeg: captura, conversión y codificación dentro del programa (<see cref="Nativa.TuberiaNativa"/>). La primera
    /// desde el 0.7.0 (hito N3), para todos los monitores: 60 fps con ~0,2 núcleos y casi nada de red con la pantalla quieta.
    /// </summary>
    public const string Nativa = "nativa";

    /// <summary>Conversión a NV12 por CPU que sube la tubería <see cref="Amf"/> de ~37 a 60 fps.</summary>
    internal const string FiltroNv12 = ",format=nv12";

    // Mf va al final: funciona, pero en este PC dio solo 14 fps (x264 da 60 a costa de CPU).
    /// <summary>
    /// Cada cuánto va un fotograma clave. Cuando el receptor salta por atraso, la imagen queda congelada
    /// hasta el siguiente; más seguido gasta tasa. El pulso que se vio con 1 s («un parpadeo cada
    /// segundo») era el fotograma clave demasiado comprimido: ver enforce_hrd más abajo.
    /// </summary>
    public const int SegundosEntreClaves = 4;

    public static readonly IReadOnlyList<string> Todas = [Nativa, AmfGpu, Amf, Nvenc, Qsv, X264, Mf];

    /// <summary>Cómo se muestra cada tubería en el menú de la bandeja.</summary>
    public static string Descripcion(string nombre) => nombre switch
    {
        Nativa => "Propia, sin ffmpeg (nativa)",
        AmfGpu => "ffmpeg · AMD en la GPU, solo el monitor principal (amf-gpu)",
        Amf => "ffmpeg · AMD (amf)",
        Nvenc => "ffmpeg · NVIDIA (nvenc)",
        Qsv => "ffmpeg · Intel (qsv)",
        X264 => "ffmpeg · por CPU (x264)",
        Mf => "ffmpeg · Media Foundation (mf)",
        _ => nombre,
    };

    /// <summary>Cuánto se deja de lado una tubería que acaba de fallar antes de volver a intentarla.</summary>
    public static readonly TimeSpan CastigoPorFallo = TimeSpan.FromMinutes(5);

    /// <summary>
    /// El orden en que se prueban: siempre de la mejor a la más compatible, dejando para el final las que
    /// fallaron hace poco (para no perder segundos en cada reconexión con una que no sirve en este PC).
    /// El castigo caduca: un fallo pasajero de la mejor —por ejemplo, otro programa capturando la
    /// pantalla en ese instante— no debe dejar la transmisión en una peor para siempre. Pasó el
    /// 10/11/2026: una reconexión cayó a «amf» (43 fps) y ahí se quedó hasta cerrar el programa.
    /// </summary>
    public static IReadOnlyList<string> Orden(IReadOnlyDictionary<string, DateTime> fallos, DateTime ahora) =>
        [.. Todas.OrderBy(t => fallos.TryGetValue(t, out var cuando) && ahora - cuando < CastigoPorFallo ? 1 : 0)];

    /// <summary>
    /// Las tuberías que tiene sentido probar para ese monitor. La captura de GPU de AMD (<see cref="AmfGpu"/>)
    /// **solo sirve para el monitor principal**: probado el 10/11/2026, con el monitor virtual
    /// (adaptador 0, salida 1) devolvía una imagen del tamaño de ese monitor pero con el contenido del
    /// escritorio principal, sin fallar ni avisar. Para cualquier otro monitor se salta y se captura con
    /// duplicación de escritorio (<c>ddagrab</c>), que sí ve el monitor pedido.
    /// </summary>
    public static IReadOnlyList<string> Candidatas(IReadOnlyList<string> orden, OpcionesEmision op) =>
        op.Adaptador == 0 && op.Monitor == 0 ? orden : [.. orden.Where(t => t != AmfGpu)];

    public static List<string> Argumentos(string nombre, OpcionesEmision op)
    {
        var fps = op.Fps.ToString();
        var tasa = $"{op.Kbps}k";
        List<string> a = ["-hide_banner", "-loglevel", "error"];
        var monitor = Math.Max(0, op.Monitor);
        var adaptador = Math.Max(0, op.Adaptador);
        if (nombre == AmfGpu)
        {
            a.AddRange(["-f", "lavfi", "-i", $"vsrc_amf=monitor_index={monitor}:framerate={fps}"]);
        }
        else
        {
            // La duplicación de escritorio captura «salida M del adaptador N»: se le dice a ffmpeg en qué
            // adaptador crear el dispositivo, porque el monitor puede no ser de la gráfica principal.
            // Solo «amf» añade la conversión a NV12: mf y x264 ya convierten por su cuenta (-pix_fmt).
            var conversion = nombre == Amf ? FiltroNv12 : "";
            a.AddRange(["-init_hw_device", $"d3d11va=cap:{adaptador}", "-filter_hw_device", "cap",
                "-f", "lavfi", "-i", $"ddagrab={monitor}:framerate={fps},hwdownload,format=bgra{conversion}"]);
        }
        a.AddRange(["-fps_mode", "cfr", "-r", fps]);

        a.AddRange(nombre switch
        {
            // enforce_hrd 0: sin él, el control de tasa aprieta el fotograma clave al tamaño de un cuadro
            // cualquiera (medido: 24 KB a 1080p contra 200 KB) y se ve como un pulso de imagen borrosa.
            AmfGpu or Amf => ["-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr", "-b:v", tasa, "-enforce_hrd", "0", "-bf", "0"],
            Nvenc => ["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-rc", "cbr", "-b:v", tasa, "-bf", "0"],
            Qsv => ["-c:v", "h264_qsv", "-preset", "veryfast", "-async_depth", "1", "-b:v", tasa, "-maxrate", tasa, "-bf", "0"],
            // h264_mf no acepta BGRA: hay que convertir a NV12 (por CPU).
            Mf => ["-pix_fmt", "nv12", "-c:v", "h264_mf", "-hw_encoding", "1", "-scenario", "display_remoting", "-rate_control", "cbr", "-b:v", tasa, "-bf", "0"],
            X264 => ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-b:v", tasa, "-maxrate", tasa, "-bufsize", $"{op.Kbps / 2}k"],
            _ => throw new EmisionException($"No existe la tubería «{nombre}». Opciones: {string.Join(", ", Todas)}."),
        });

        // Un fotograma clave cada SegundosEntreClaves, SPS/PPS en cada uno y una marca AUD por cuadro
        // (así se separan).
        if (!string.IsNullOrWhiteSpace(op.ExtraCodificador))
            a.AddRange(op.ExtraCodificador.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        a.AddRange(["-g", (op.Fps * (op.SegClave > 0 ? op.SegClave : SegundosEntreClaves)).ToString(), "-bsf:v", "dump_extra=freq=keyframe,h264_metadata=aud=insert", "-f", "h264", "pipe:1"]);
        return a;
    }
}
