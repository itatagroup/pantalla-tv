namespace PantallaTv.Emisor;

/// <summary>
/// Una de las transmisiones en curso (hasta <see cref="Mando.MaxTransmisiones"/>): a qué TV va, qué pantalla lleva, si lleva
/// sonido y en qué estado está. La crea y la actualiza <see cref="Mando"/> (siempre en el hilo de la interfaz); la ventana y la
/// bandeja solo la leen.
/// </summary>
internal sealed class Transmision
{
    public required int Numero { get; init; }
    public required Receptor Receptor { get; init; }
    /// <summary>La pantalla que se pidió (nombre de Windows, «virtual», «virtual2»…; null = la principal).</summary>
    public string? Pantalla { get; init; }
    /// <summary>La TV vista como clave (<c>IP:puerto</c>): una TV solo puede estar en una transmisión.</summary>
    public required string ClaveTv { get; init; }
    /// <summary>La pantalla vista como clave (su nombre de Windows o «virtual2»): una pantalla solo puede estar en una transmisión.</summary>
    public required string ClavePantalla { get; init; }
    /// <summary>Solo la primera transmisión que se inicia lleva el sonido (y silencia el PC si se pidió).</summary>
    public required bool ConSonido { get; init; }

    public Fase Fase { get; set; } = Fase.Conectando;
    public string Estado { get; set; } = "";
    public required EnVivo Vivo { get; init; }

    /// <summary>La emisión en curso: <see cref="Mando"/> le manda los avisos para la tele.</summary>
    internal IEmision? Emision { get; set; }

    /// <summary>
    /// Programas que ahora tienen ventanas en la tele y en otra pantalla (su sonido sale por la tele): rutas de .exe.
    /// Solo en el hilo de la interfaz.
    /// </summary>
    public IReadOnlyCollection<string> TambienEnPc { get; set; } = [];

    /// <summary>Los de <see cref="TambienEnPc"/> de los que ya se avisó en esta transmisión (una sola vez por programa).</summary>
    internal HashSet<string> TambienEnPcAvisados { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>El último aviso del sonido y cuándo salió (<see cref="Mando.Reloj"/>), para sumarle el que llegue enseguida.</summary>
    internal (string Texto, long Ms)? UltimoAvisoAudio { get; set; }

    internal CancellationTokenSource? Cancelar { get; set; }
    internal Task? Tarea { get; set; }

    /// <summary>Sigue corriendo (mientras la tarea no se asigna, ya está por empezar).</summary>
    public bool EnCurso => Tarea is not { IsCompleted: true };
}
