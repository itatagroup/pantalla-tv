namespace PantallaTv.Emisor;

/// <summary>
/// VB-CABLE visto desde la ventana: si está, el aviso cuando hace falta y la instalación a pedido (con confirmación, UAC y
/// créditos a VB-Audio). Nunca solo y nunca mientras se transmite.
/// </summary>
internal sealed partial class Mando
{
    /// <summary>Crea el descargador (las pruebas ponen uno falso).</summary>
    internal Func<DescargadorVbCable> CrearDescargadorVbCable { get; set; } = DescargadorVbCable.Crear;

    public const string TextoFaltaVbCable =
        "Para que el PC siga sonando y la tele oiga solo lo del monitor virtual, instala VB-CABLE en «Avanzado». Sin él, el PC se silencia.";

    /// <summary>Instalando VB-CABLE ahora (descarga + UAC).</summary>
    public bool InstalandoVbCable { get; private set; }

    /// <summary>Lo último que pasó al instalar VB-CABLE, para la nota de «Avanzado». Null si nada.</summary>
    public string? EstadoVbCable { get; private set; }

    private (bool Hay, long Ms)? _cacheCable;

    /// <summary>¿Está VB-CABLE? Mirarlo enumera los dispositivos de audio: se recuerda unos segundos (la ventana se refresca seguido).</summary>
    public bool HayVbCable
    {
        get
        {
            var ahora = Environment.TickCount64;
            if (_cacheCable is { } c && ahora - c.Ms < 5000) return c.Hay;
            var hay = HayCable();
            _cacheCable = (hay, ahora);
            return hay;
        }
    }

    public bool PuedeInstalarVbCable => !InstalandoVbCable && !Transmitiendo && !HayVbCable;

    /// <summary>
    /// El aviso de que falta VB-CABLE: solo cuando haría falta (con sonido, «Silenciar el PC» y una pantalla que no es la
    /// principal, como el monitor virtual). Null si no hay nada que decir.
    /// </summary>
    public string? AvisoFaltaVbCable()
    {
        if (!Ajustes.ConAudio || !Ajustes.SilenciarPc || InstalandoVbCable) return null;
        var elegida = PantallaLibreElegida();
        var esPrincipal = MonitorVirtual.NumeroDeNombre(elegida) is null
                          && (_monitores.FirstOrDefault(m => string.Equals(m.Nombre, elegida, StringComparison.OrdinalIgnoreCase))?.Principal ?? true);
        return HaceFaltaVbCable(Ajustes.ConAudio, Ajustes.SilenciarPc, esPrincipal, () => HayVbCable) ? TextoFaltaVbCable : null;
    }

    /// <summary>
    /// ¿Habría que avisar que falta VB-CABLE? Lo mismo que pide el audio exclusivo (<see cref="UsaAudioExclusivo"/>) salvo el
    /// cable. <paramref name="hayCable"/> se consulta solo si lo demás se cumple (mirarlo enumera dispositivos).
    /// </summary>
    internal static bool HaceFaltaVbCable(bool conSonido, bool silenciarPc, bool esPrincipal, Func<bool> hayCable) =>
        conSonido && silenciarPc && !esPrincipal && !hayCable();

    /// <summary>Lo que se le dice a la persona antes de instalar: qué es, de quién, de dónde y que pide permiso de administrador.</summary>
    public static string TextoConfirmacionVbCable()
    {
        var o = DescargadorVbCable.Fijado;
        return $"Se va a descargar VB-CABLE ({o.Version}, unos {o.Tamano / 1024} KB) desde {o.Url.Host} y a instalarlo. Windows pedirá permiso de administrador.\n\n"
               + "Es una salida de sonido virtual: lo que suena en el monitor virtual va por ella a la tele y el PC sigue sonando con lo demás. "
               + "Se comprueba su huella SHA-256 y la firma de VB-Audio, y se devuelve tu salida de sonido por defecto.\n\n"
               + DescargadorVbCable.Creditos + " Si te sirve, considera donar: " + DescargadorVbCable.UrlDonar;
    }

    public void InstalarVbCableAhora() => _ = InstalarVbCableAsync();

    internal async Task InstalarVbCableAsync()
    {
        if (!PuedeInstalarVbCable) return;
        InstalandoVbCable = true;
        EstadoVbCable = "Instalando VB-CABLE…";
        Cambio?.Invoke();

        ResultadoVbCable r;
        try
        {
            using var descargador = CrearDescargadorVbCable();
            r = await Task.Run(() => descargador.InstalarAsync()).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Registro.Error("VB-CABLE: la instalación se interrumpió", e);
            r = new ResultadoVbCable(false, "No se pudo instalar VB-CABLE: " + e.Message);
        }
        EnLaInterfaz(() =>
        {
            InstalandoVbCable = false;
            _cacheCable = null;
            EstadoVbCable = r.Hecho ? "VB-CABLE quedó instalado: el PC seguirá sonando mientras transmites el monitor virtual." : r.Motivo;
            Aviso?.Invoke(r.Hecho ? "VB-CABLE instalado. " + DescargadorVbCable.Creditos : r.Motivo ?? "No se pudo instalar VB-CABLE.",
                r.Hecho ? ToolTipIcon.Info : r.Rechazado ? ToolTipIcon.Warning : ToolTipIcon.Error);
            Cambio?.Invoke();
        });
    }
}
