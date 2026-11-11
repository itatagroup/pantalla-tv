using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PantallaTv.Emisor;

/// <summary>Una de las opciones de una lista del menú o de la ventana.</summary>
internal sealed record Opcion<T>(string Texto, T Valor, bool Elegida);

/// <summary>Las pantallas que se pueden transmitir y si está el monitor virtual (si no, se ofrece instalarlo).</summary>
internal sealed record Pantallas(IReadOnlyList<Opcion<string?>> Opciones, bool HayVirtual);

/// <summary>A dónde se puede transmitir: una TV que se anuncia en la red o la última IP usada.</summary>
/// <param name="UltimaIp">No se está anunciando: es la última IP a la que se transmitió (sirve si el firewall tapa los anuncios).</param>
internal sealed record Destino(Receptor Receptor, bool UltimaIp)
{
    public string Clave => $"{Receptor.Direccion}:{Receptor.Puerto}";
    public string TextoLista => UltimaIp ? $"Última IP usada ({Receptor.Direccion})" : Receptor.ToString();
    public string TextoMenu => UltimaIp ? $"Transmitir a la última IP ({Receptor.Direccion})" : $"Transmitir a {Receptor}";
}

/// <summary>Lo que se muestra mientras hay una transmisión: a dónde, qué pantalla, desde cuándo y sus últimos números.</summary>
internal sealed class EnVivo
{
    public required string Tv { get; init; }
    public required int FpsPedidos { get; init; }
    public string Pantalla { get; set; } = "Preparando…";
    /// <summary>Cuándo empezó a llegar imagen (la primera vez: las reconexiones no reinician la cuenta).</summary>
    public DateTime? Desde { get; set; }
    /// <summary>Las de la emisión, cada 2 s (no hace falta «Guardar telemetría»). Null mientras reconecta.</summary>
    public Estadisticas? Ultimas { get; set; }
}

/// <summary>
/// El estado del emisor y todo lo que se puede hacer con él. Es la **única fuente** para el menú de la bandeja
/// y la ventana principal: los dos leen y escriben estos <see cref="Ajustes"/> y llaman a estas acciones, y se
/// enteran de cualquier cambio por <see cref="Cambio"/> (siempre en el hilo de la interfaz).
/// </summary>
internal sealed class Mando : IDisposable
{
    public static readonly int[] Retardos = [0, 500, 1000, 2000];
    public const string SinTransmitir = "Sin transmitir";

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-CL");

    private readonly SynchronizationContext _ui;
    private readonly Func<IReadOnlyList<Receptor>> _tvs;
    private readonly IDisposable? _propio;
    private CancellationTokenSource? _cancelar;
    private Task? _tarea;
    /// <summary>Número de la transmisión en curso: lo que llega tarde de una anterior no la pisa.</summary>
    private int _numero;

    /// <param name="tvs">Las TV que se ven en la red ahora (el <see cref="Descubridor"/>).</param>
    /// <param name="propio">Lo que se libera con el mando (el descubridor).</param>
    public Mando(Ajustes ajustes, SynchronizationContext ui, Func<IReadOnlyList<Receptor>> tvs, IDisposable? propio = null)
    {
        Ajustes = ajustes;
        _ui = ui;
        _tvs = tvs;
        _propio = propio;
        PuedeIniciarConWindows = Instalacion.CorreDesdeLaInstalacion(); // no cambia mientras corre
    }

    /// <summary>El del programa de bandeja: con los ajustes guardados y escuchando los anuncios de las TV.</summary>
    public static Mando Crear(SynchronizationContext ui, out string? problemaRed)
    {
        var descubridor = new Descubridor();
        descubridor.Iniciar();
        problemaRed = descubridor.Problema;
        var mando = new Mando(Ajustes.Cargar(), ui, descubridor.Vigentes, descubridor);
        if (mando.PuedeIniciarConWindows) Instalacion.ActualizarInicioConWindows(Environment.ProcessPath!);
        return mando;
    }

    public Ajustes Ajustes { get; }

    /// <summary>Hubo un cambio de estado, de ajustes o llegaron números nuevos. Siempre en el hilo de la interfaz.</summary>
    public event Action? Cambio;

    /// <summary>Algo que hay que contarle al usuario (el globo de la bandeja). Siempre en el hilo de la interfaz.</summary>
    public event Action<string, ToolTipIcon>? Aviso;

    public Fase Fase { get; private set; } = Fase.Inactivo;
    public string Estado { get; private set; } = SinTransmitir;
    public bool Transmitiendo => _tarea is { IsCompleted: false };
    public bool InstalandoControlador { get; private set; }
    public bool Diagnosticando { get; private set; }
    public EnVivo? EnVivo { get; internal set; }

    // ── Ajustes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Los ajustes de la emisión se leen al empezar: cambiarlos con una transmisión en curso no haría nada hasta la
    /// próxima, y eso confunde. Se bloquean mientras tanto (en el menú y en la ventana).
    /// </summary>
    public bool PuedeCambiarAjustes => !Transmitiendo;

    public const string PorQueBloqueado = "Detén la transmisión para cambiar estos ajustes.";

    /// <summary>Cambia un ajuste de la emisión, lo guarda y avisa. No hace nada mientras se transmite.</summary>
    public void Cambiar(Action<Ajustes> cambio)
    {
        if (!PuedeCambiarAjustes) return;
        cambio(Ajustes);
        Guardar();
    }

    private void Guardar()
    {
        Ajustes.Guardar();
        Cambio?.Invoke();
    }

    /// <summary>El primer cierre de la ventana avisa que el programa sigue en la bandeja; los siguientes, no.</summary>
    public bool MarcarAvisoBandeja()
    {
        if (Ajustes.AvisoBandejaVisto) return false;
        Ajustes.AvisoBandejaVisto = true;
        Ajustes.Guardar();
        return true;
    }

    /// <summary>Los retardos de siempre y, si el guardado es otro (editado a mano), también ese: si no, se vería uno que no se usa.</summary>
    public IReadOnlyList<Opcion<int>> OpcionesRetardo() =>
        [.. Retardos.Append(Ajustes.RetardoMs).Distinct().Order().Select(ms => new Opcion<int>(TextoRetardo(ms), ms, Ajustes.RetardoMs == ms))];

    public static string TextoRetardo(int ms) =>
        ms == 0 ? "Sin retardo (mínima latencia)" : (ms / 1000.0).ToString("0.#", Es) + " s";

    /// <summary>Por defecto, automática. Elegir una a mano sirve para comparar o si la automática falla en este PC.</summary>
    public IReadOnlyList<Opcion<string?>> OpcionesForma() =>
        [.. new string?[] { null }.Concat(Tuberias.Todas)
            .Select(t => new Opcion<string?>(t is null ? "Automática (recomendada)" : Tuberias.Descripcion(t), t, Ajustes.Tuberia == t))];

    private IReadOnlyList<Monitor> _monitores = [];
    private string? _dispositivoVirtual;

    /// <summary>
    /// Vuelve a preguntarle a Windows qué monitores hay (llamadas de enumeración: al abrir el menú, al mostrar la
    /// ventana, cuando cambian las pantallas o termina de instalarse el monitor virtual; no en cada refresco).
    /// </summary>
    public void ActualizarPantallas()
    {
        _monitores = Monitores.Listar();
        _dispositivoVirtual = MonitorVirtual.Detectar()?.Dispositivo;
    }

    /// <summary>Las pantallas de la última <see cref="ActualizarPantallas"/>, con la elegida según los ajustes de ahora.</summary>
    public Pantallas OpcionesPantalla() => ArmarPantallas(_monitores, _dispositivoVirtual, Ajustes.MonitorNombre);

    /// <summary>
    /// Los monitores reales (sin la salida del virtual, que va aparte) y el monitor virtual si está. El virtual
    /// se conecta al escritorio solo mientras se transmite: listarlo no mueve nada.
    /// </summary>
    internal static Pantallas ArmarPantallas(IReadOnlyList<Monitor> todos, string? dispositivoVirtual, string? elegido)
    {
        var reales = todos.Where(m => dispositivoVirtual is null || !string.Equals(m.Nombre, dispositivoVirtual, StringComparison.OrdinalIgnoreCase)).ToList();
        var esElVirtual = elegido == Monitores.ElMonitorVirtual && dispositivoVirtual is not null;
        var real = esElVirtual || reales.Count == 0 ? null : Monitores.Elegir(reales, m => m.Nombre == elegido);
        var opciones = reales.Select(m => new Opcion<string?>(m.ToString(), m.Nombre, real is not null && m.Indice == real.Indice)).ToList();
        if (dispositivoVirtual is not null)
            opciones.Add(new Opcion<string?>("Monitor virtual (se conecta solo al transmitir)", Monitores.ElMonitorVirtual, esElVirtual));
        return new Pantallas(opciones, dispositivoVirtual is not null);
    }

    public IReadOnlyList<Destino> Destinos() => ArmarDestinos(_tvs(), Ajustes.UltimaIp);

    /// <summary>¿Alguna TV se anuncia? Si no, el menú y la ventana lo dicen (aunque quede la última IP).</summary>
    public static bool HayTvAnunciada(IReadOnlyList<Destino> destinos) => destinos.Any(d => !d.UltimaIp);

    /// <summary>«Silenciar el PC» solo tiene sentido si se envía el sonido.</summary>
    public bool PuedeSilenciar => PuedeCambiarAjustes && Ajustes.ConAudio;

    public string TextoInstalarVirtual => InstalandoControlador ? "Instalando el monitor virtual…" : "Instalar el monitor virtual…";

    internal static IReadOnlyList<Destino> ArmarDestinos(IReadOnlyList<Receptor> tvs, string? ultimaIp)
    {
        var destinos = tvs.Select(tv => new Destino(tv, false)).ToList();
        if (ultimaIp is not null && IPAddress.TryParse(ultimaIp, out var dir) && !tvs.Any(tv => tv.Direccion.Equals(dir)))
            destinos.Add(new Destino(new Receptor("TV", dir, Protocolo.PuertoTcp), true));
        return destinos;
    }

    /// <summary>La que se propone en la ventana: la de la última IP usada si está, si no la primera.</summary>
    internal static Destino? Preferido(IReadOnlyList<Destino> destinos, string? ultimaIp) =>
        destinos.FirstOrDefault(d => d.Receptor.Direccion.ToString() == ultimaIp) ?? destinos.FirstOrDefault();

    /// <summary>«Abrir al iniciar Windows» solo es para el programa instalado (una copia suelta dejaría un inicio roto al borrarla).</summary>
    public bool PuedeIniciarConWindows { get; }

    /// <summary>Se lee del registro una vez (y al cambiarlo desde aquí), no en cada refresco de la ventana.</summary>
    public bool IniciaConWindows => _iniciaConWindows ??= Instalacion.IniciaConWindows();
    private bool? _iniciaConWindows;

    public void CambiarInicioConWindows(bool activar)
    {
        if (!PuedeIniciarConWindows) return;
        Instalacion.IniciarConWindows(activar, Environment.ProcessPath!);
        _iniciaConWindows = activar;
        Cambio?.Invoke();
    }

    // ── Acciones ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Transmite a la IP escrita a mano. Devuelve false (y avisa) si no es una IP válida.</summary>
    public bool IniciarConIp(string texto)
    {
        if (LeerIp(texto) is not { } dir)
        {
            Aviso?.Invoke("Esa no es una dirección IP válida.", ToolTipIcon.Warning);
            return false;
        }
        Iniciar(new Receptor("TV", dir, Protocolo.PuertoTcp));
        return true;
    }

    /// <summary>
    /// Una IP escrita a mano. <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> acepta formas abreviadas
    /// («10.0.0» es 10.0.0.0): una IPv4 tiene que traer sus cuatro números.
    /// </summary>
    internal static IPAddress? LeerIp(string texto)
    {
        texto = texto.Trim();
        if (!IPAddress.TryParse(texto, out var dir)) return null;
        return dir.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && texto.Split('.').Length != 4 ? null : dir;
    }

    public void Iniciar(Receptor receptor)
    {
        if (Transmitiendo) return;
        Ajustes.UltimaIp = receptor.Direccion.ToString();
        Ajustes.Guardar();

        var telemetria = Ajustes.GuardarTelemetria ? new Telemetria(Ajustes.CarpetaTelemetria) : null;

        _cancelar?.Dispose(); // la anterior ya terminó (Transmitiendo era falso)
        _cancelar = new CancellationTokenSource();
        var ct = _cancelar.Token;

        // Todo vuelve por el hilo de la interfaz y solo cuenta si sigue siendo esta transmisión: la tarea termina
        // (Transmitiendo pasa a falso) antes de que corra lo que dejó posteado, y para entonces pudo empezar otra.
        var esta = ++_numero;
        void EnLaInterfaz(Action accion) => _ui.Post(_ => { if (esta == _numero) accion(); }, null);
        void AlEstado(EstadoEmision e) => EnLaInterfaz(() => AlCambiarEstado(e));
        void AlNumeros(Estadisticas e) => EnLaInterfaz(() => AlEstadisticas(e));
        IEmision emision = Ajustes.Reconectar ? new EmisionPersistente() : new Emision();
        emision.CambioDeEstado += AlEstado;
        emision.NuevasEstadisticas += AlNumeros;

        var vivo = new EnVivo { Tv = receptor.ToString(), FpsPedidos = Ajustes.Fps };
        EnVivo = vivo;
        // Desde ya «conectando»: un error de la anterior no debe quedar a la vista mientras empieza esta.
        Fase = Fase.Conectando;
        Estado = $"Conectando con {receptor.Nombre}…";
        var a = Ajustes;
        var nombreMonitor = a.MonitorNombre;
        _tarea = Task.Run(async () =>
        {
            Monitores.Preparado? preparado = null;
            try
            {
                // Dentro de la tarea: conectar el monitor virtual tarda unos segundos y no debe congelar la interfaz.
                preparado = Monitores.Preparar(nombreMonitor, m => AlEstado(new EstadoEmision(Fase.Conectando, m)));
                var monitor = preparado.Monitor; // en píxeles reales (el programa es consciente del DPI)
                var esVirtual = Monitores.EsElVirtual(monitor);
                EnLaInterfaz(() => { vivo.Pantalla = esVirtual ? $"Monitor virtual · {monitor.Ancho}×{monitor.Alto}" : monitor.ToString(); Cambio?.Invoke(); });
                var op = new OpcionesEmision(a.Fps, a.Kbps, monitor.Ancho, monitor.Alto, Monitores.RetardoPara(esVirtual, null, a.RetardoMs), null,
                    Tuberia: a.Tuberia, ConAudio: a.ConAudio, SilenciarPc: a.SilenciarPc, Monitor: monitor.Salida, Adaptador: monitor.Adaptador, MonitorNombre: monitor.Nombre);
                await emision.EjecutarAsync(receptor, op, telemetria, ct);
            }
            catch (EmisionException e)
            {
                // Los de la transmisión ya se mostraron con CambioDeEstado; este es de antes de empezar.
                if (preparado is null) AlEstado(new EstadoEmision(Fase.Error, e.Message));
            }
            finally
            {
                preparado?.Liberar(); // quita el monitor virtual del escritorio si lo conectamos nosotros
                telemetria?.Dispose();
                EnLaInterfaz(Reposar);
            }
        }, CancellationToken.None);
        Cambio?.Invoke();
    }

    public void Detener() => _cancelar?.Cancel();

    /// <summary>El diagnóstico prueba la captura: mejor no a la vez que una transmisión, ni dos a la vez.</summary>
    public bool PuedeDiagnosticar => !Transmitiendo && !Diagnosticando;

    /// <summary>Arma el informe de <see cref="Diagnostico"/> en segundo plano y lo abre en el Bloc de notas.</summary>
    public void Diagnosticar()
    {
        // Prueba la captura: mejor no a la vez que una transmisión.
        if (!PuedeDiagnosticar) return;
        Diagnosticando = true;
        Aviso?.Invoke("Preparando el diagnóstico (unos segundos)…", ToolTipIcon.Info);
        Cambio?.Invoke();
        Task.Run(() =>
        {
            try
            {
                Diagnostico.Abrir(Diagnostico.Generar());
            }
            catch (Exception e)
            {
                _ui.Post(_ => Aviso?.Invoke("No se pudo armar el diagnóstico: " + e.Message, ToolTipIcon.Error), null);
            }
            finally
            {
                _ui.Post(_ => { Diagnosticando = false; Cambio?.Invoke(); }, null);
            }
        });
    }

    /// <summary>Instala el controlador (pide UAC) en segundo plano y avisa cómo terminó.</summary>
    public void InstalarMonitorVirtual()
    {
        if (InstalandoControlador) return;
        InstalandoControlador = true;
        Cambio?.Invoke();
        // La marca «lo instalamos nosotros» solo tiene sentido en la instalación (si no, no se puede desinstalar).
        var id = PuedeIniciarConWindows ? Instalacion.IdPorDefecto : null; // = corre desde la instalación
        Task.Run(() =>
        {
            Controlador.Respuesta r;
            try { r = Controlador.InstalarConPermiso(id); }
            catch (Exception e) { r = new Controlador.Respuesta(Controlador.Resultado.Fallo, e.Message); }
            _ui.Post(_ =>
            {
                InstalandoControlador = false;
                ActualizarPantallas(); // si se instaló, ahora está el virtual
                var (texto, icono) = r.Resultado switch
                {
                    Controlador.Resultado.Hecho => ("Monitor virtual instalado. Elígelo en «Qué pantalla se transmite»." + (r.Detalle is null ? "" : " " + r.Detalle), ToolTipIcon.Info),
                    Controlador.Resultado.Rechazado => ("El monitor virtual no se instaló: no se dio permiso de administrador.", ToolTipIcon.Warning),
                    _ => ("El monitor virtual no se pudo instalar: " + r.Detalle, ToolTipIcon.Error),
                };
                Aviso?.Invoke(texto, icono);
                Cambio?.Invoke();
            }, null);
        });
    }

    public static void AbrirTelemetria()
    {
        Directory.CreateDirectory(Ajustes.CarpetaTelemetria);
        Process.Start(new ProcessStartInfo(Ajustes.CarpetaTelemetria) { UseShellExecute = true });
    }

    /// <summary>Al salir: detiene y espera a que la tarea desconecte el monitor virtual.</summary>
    public void Terminar()
    {
        Detener();
        try
        {
            _tarea?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // la transmisión terminó con error: al salir ya no importa
        }
        MonitorVirtual.LiberarSiEsNuestro(); // por si la tarea no alcanzó
    }

    // ── Lo que llega de la emisión (en el hilo de la interfaz) ──────────────────────────────────

    internal void AlCambiarEstado(EstadoEmision e)
    {
        var antes = Fase;
        Fase = e.Fase;
        Estado = e.Detalle;
        if (EnVivo is { } vivo)
        {
            if (e.Fase == Fase.Transmitiendo) vivo.Desde ??= DateTime.UtcNow;
            else vivo.Ultimas = null; // reconectando: los números viejos ya no dicen nada
        }
        if (e.Fase == Fase.Error)
            Aviso?.Invoke(e.Detalle, ToolTipIcon.Error);
        else if (e.Fase == Fase.Reconectando && antes != Fase.Reconectando)
            Aviso?.Invoke(antes == Fase.Transmitiendo
                    ? "Se cortó la transmisión. Reintentando…"
                    : "Todavía no hay respuesta de la tele (¿está abierta la app «Pantalla»?). Reintentando…",
                ToolTipIcon.Warning);
        Cambio?.Invoke();
    }

    internal void AlEstadisticas(Estadisticas e)
    {
        if (EnVivo is not { } vivo || Fase != Fase.Transmitiendo) return;
        vivo.Ultimas = e;
        Cambio?.Invoke();
    }

    /// <summary>La transmisión terminó. Si fue por un error, su mensaje se queda a la vista.</summary>
    internal void Reposar()
    {
        EnVivo = null;
        if (Fase != Fase.Error)
        {
            Fase = Fase.Inactivo;
            Estado = SinTransmitir;
        }
        Cambio?.Invoke();
    }

    // ── Textos del panel en vivo ────────────────────────────────────────────────────────────────

    public static string TextoTiempo(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public static string TextoFps(Estadisticas? e) => e is null ? "—" : e.Fps.ToString("0", Es);

    public static string TextoMbps(Estadisticas? e) => e is null ? "—" : e.Mbps.ToString("0.0", Es);

    public static string TextoForma(Estadisticas? e) =>
        e is { Tuberia.Length: > 0 } ? Tuberias.Descripcion(e.Tuberia) : "—";

    public void Dispose()
    {
        _propio?.Dispose();
        _cancelar?.Dispose();
    }
}
