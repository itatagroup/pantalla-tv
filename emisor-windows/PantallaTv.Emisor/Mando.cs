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
    public required int FpsPedidos { get; set; }
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
internal sealed partial class Mando : IDisposable
{
    public const string SinTransmitir = "Sin transmitir";

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-CL");

    private readonly SynchronizationContext _ui;
    private readonly Func<IReadOnlyList<Receptor>> _tvs;
    /// <summary>La pantalla que informó cada tele (receptor 0.8.0+), para ajustar el monitor virtual y los fps.</summary>
    private readonly Func<Receptor, PantallaTele?> _pantallaDe;
    private readonly IDisposable? _propio;
    private CancellationTokenSource? _cancelar;
    private Task? _tarea;
    /// <summary>Número de la transmisión en curso: lo que llega tarde de una anterior no la pisa.</summary>
    private int _numero;

    /// <param name="tvs">Las TV que se ven en la red ahora (el <see cref="Descubridor"/>).</param>
    /// <param name="propio">Lo que se libera con el mando (el descubridor).</param>
    /// <param name="comprobador">El que busca versiones nuevas (null = no se busca: las pruebas que no lo usan).</param>
    /// <param name="reloj">La hora de la última búsqueda (las pruebas ponen uno propio).</param>
    public Mando(Ajustes ajustes, SynchronizationContext ui, Func<IReadOnlyList<Receptor>> tvs, IDisposable? propio = null,
        ComprobadorActualizaciones? comprobador = null, TimeProvider? reloj = null, Func<Receptor, PantallaTele?>? pantallaDe = null)
    {
        _pantallaDe = pantallaDe ?? (_ => null);
        Ajustes = ajustes;
        _ui = ui;
        _tvs = tvs;
        _propio = propio;
        _comprobador = comprobador;
        _reloj = reloj ?? TimeProvider.System;
        PuedeIniciarConWindows = Instalacion.CorreDesdeLaInstalacion(); // no cambia mientras corre
    }

    /// <summary>
    /// El del programa de bandeja: con los ajustes guardados y escuchando los anuncios de las TV. Las versiones nuevas
    /// se buscan solo cuando el usuario lo pide; <paramref name="manifiesto"/> cambia de dónde se lee el manifiesto (pruebas).
    /// </summary>
    public static Mando Crear(SynchronizationContext ui, out string? problemaRed, Uri? manifiesto = null)
    {
        var descubridor = new Descubridor();
        descubridor.Iniciar();
        problemaRed = descubridor.Problema;
        var mando = new Mando(Ajustes.Cargar(), ui, descubridor.Vigentes, descubridor, ComprobadorActualizaciones.Crear(manifiesto),
            pantallaDe: descubridor.PantallaDe);
        if (mando.PuedeIniciarConWindows) Instalacion.ActualizarInicioConWindows(Environment.ProcessPath!);
        mando.IniciarActualizaciones();
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
    /// <summary>Hay una instalación, reinstalación o desinstalación del monitor virtual en curso (pidiendo UAC).</summary>
    public bool InstalandoControlador { get; private set; }
    private bool _quitandoControlador;
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

    /// <summary>La pantalla que informó esa tele (o null si es anterior a la 0.8.0 o no se ha visto).</summary>
    public PantallaTele? PantallaDe(Receptor receptor) => _pantallaDe(receptor);

    /// <summary>«1920x1080@60» → el modo; null si no es eso.</summary>
    internal static (int Ancho, int Alto, int Hz)? LeerModo(string? texto)
    {
        if (texto is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(texto, @"^(\d{3,5})x(\d{3,5})@(\d{2,3})$");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    internal static string TextoModo((int Ancho, int Alto, int Hz) m) => $"{m.Ancho}×{m.Alto} a {m.Hz} Hz";

    /// <summary>Más líneas que esto, solo eligiéndolo a mano: 4K a 60 Hz carga la codificación y sube la latencia.</summary>
    internal const int LineasAutomaticas = 1080;

    /// <summary>
    /// El modo que se pide en automático para esa tele: su pantalla en horizontal, reducida (con la misma forma) si
    /// pasa de <see cref="LineasAutomaticas"/> líneas.
    /// </summary>
    internal static (int Ancho, int Alto, int Hz) ModoAutomatico(PantallaTele tele)
    {
        var (ancho, alto) = tele.Horizontal;
        if (alto > LineasAutomaticas)
            (ancho, alto) = ((int)Math.Round((double)ancho * LineasAutomaticas / alto), LineasAutomaticas);
        return (ancho, alto, tele.Hz);
    }

    /// <summary>
    /// «Automática (según la tele)» y los modos que ofrece el controlador. Uno guardado que el controlador ya no ofrece
    /// también aparece (marcado), para no mostrar uno que no se usa.
    /// </summary>
    public IReadOnlyList<Opcion<string?>> OpcionesModoVirtual()
    {
        var ofrecidos = MonitorVirtual.Detectar()?.ModosOfrecidos() ?? [];
        var claves = ofrecidos.Select(m => $"{m.Ancho}x{m.Alto}@{m.Hz}").ToList();
        if (Ajustes.ModoMonitorVirtual is { } guardado && !claves.Contains(guardado)) claves.Add(guardado);
        return [new Opcion<string?>("Automática (según la tele)", null, Ajustes.ModoMonitorVirtual is null),
            .. claves.Select(c => new Opcion<string?>(LeerModo(c) is { } m ? TextoModo(m) : c, c, Ajustes.ModoMonitorVirtual == c))];
    }

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

    private string? _avisoRed;
    private DateTime _redLeida = DateTime.MinValue;

    /// <summary>
    /// Si Windows trata la red como pública (y por eso no se ven las teles), el aviso; si no, null. Se vuelve a leer
    /// cada pocos segundos: el usuario puede cambiarla mientras la ventana está abierta.
    /// </summary>
    public string? AvisoRed()
    {
        if (DateTime.UtcNow - _redLeida < TimeSpan.FromSeconds(5)) return _avisoRed;
        _redLeida = DateTime.UtcNow;
        return _avisoRed = Red.Aviso(Red.PublicasConectadas());
    }

    /// <summary>¿Alguna TV se anuncia? Si no, el menú y la ventana lo dicen (aunque quede la última IP).</summary>
    public static bool HayTvAnunciada(IReadOnlyList<Destino> destinos) => destinos.Any(d => !d.UltimaIp);

    /// <summary>«Silenciar el PC» solo tiene sentido si se envía el sonido.</summary>
    public bool PuedeSilenciar => PuedeCambiarAjustes && Ajustes.ConAudio;

    public string TextoInstalarVirtual => InstalandoControlador && !_quitandoControlador ? "Instalando el monitor virtual…" : "Instalar el monitor virtual…";

    /// <summary>¿Está el monitor virtual instalado? (Para ofrecer reinstalarlo o quitarlo en «Avanzado».)</summary>
    public static bool HayMonitorVirtual => Controlador.Instalado();

    public string TextoReinstalarVirtual(bool hay) => InstalandoControlador && !_quitandoControlador
        ? (hay ? "Reinstalando…" : "Instalando…")
        : (hay ? "&Reinstalar…" : "Instala&r…");

    public string TextoQuitarVirtual => _quitandoControlador ? "Quitando…" : "&Quitar…";

    /// <summary>
    /// La nota bajo el modo del monitor virtual: qué pantalla informó la tele elegida, y si conviene reinstalar para
    /// tener los modos nuevos.
    /// </summary>
    public string NotaMonitorVirtual(Receptor? tele, bool hay)
    {
        if (!hay)
            return "El monitor virtual no está instalado. Instalarlo pide permiso de administrador; se puede quitar cuando quieras.";
        var texto = tele is null ? "" : PantallaDe(tele) is { } p
            ? $"«{tele.Nombre}» informa {p}: en automático el monitor virtual se ajusta a eso"
              + (p.Horizontal.Alto > LineasAutomaticas ? $" (hasta {LineasAutomaticas} líneas; más, eligiéndolo aquí). " : ". ")
            : $"«{tele.Nombre}» no informa su pantalla (versión anterior a la 0.8.0): en automático se usa 1920×1080 a 60 Hz. ";
        if (Controlador.ConfiguracionDesactualizada())
            texto += "Reinstala el monitor virtual para tener más tamaños y frecuencias (pide permiso de administrador).";
        return texto.Trim();
    }

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
        // El monitor virtual a la medida de la tele: el modo elegido a mano o, si no, el que ella informa.
        var tele = _pantallaDe(receptor);
        var modoVirtual = LeerModo(a.ModoMonitorVirtual) ?? (tele is { } t ? ModoAutomatico(t) : null);
        _tarea = Task.Run(async () =>
        {
            Monitores.Preparado? preparado = null;
            try
            {
                // Dentro de la tarea: conectar el monitor virtual tarda unos segundos y no debe congelar la interfaz.
                preparado = Monitores.Preparar(nombreMonitor, m => AlEstado(new EstadoEmision(Fase.Conectando, m)), modoVirtual);
                var monitor = preparado.Monitor; // en píxeles reales (el programa es consciente del DPI)
                var esVirtual = Monitores.EsElVirtual(monitor);
                EnLaInterfaz(() => { vivo.Pantalla = esVirtual ? $"Monitor virtual · {monitor.Ancho}×{monitor.Alto}" : monitor.ToString(); Cambio?.Invoke(); });
                // Más cuadros que los Hz de la pantalla (la del monitor virtual o la de la tele) no se ven y gastan más.
                var fps = Math.Min(a.Fps, preparado.Hz ?? (tele?.Hz is int hz && hz >= 24 ? hz : a.Fps));
                if (fps != a.Fps)
                {
                    Registro.Anotar($"cuadros por segundo: {fps} en vez de {a.Fps} (la pantalla va a {preparado.Hz ?? tele?.Hz} Hz)");
                    EnLaInterfaz(() => vivo.FpsPedidos = fps);
                }
                var op = new OpcionesEmision(fps, a.Kbps, monitor.Ancho, monitor.Alto, esVirtual, null,
                    Tuberia: a.Tuberia, ConAudio: a.ConAudio, SilenciarPc: a.SilenciarPc, Monitor: monitor.Salida, Adaptador: monitor.Adaptador, MonitorNombre: monitor.Nombre);
                await emision.EjecutarAsync(receptor, op, telemetria, ct);
            }
            catch (EmisionException e)
            {
                // Los de la transmisión ya se mostraron con CambioDeEstado; este es de antes de empezar.
                if (preparado is null)
                {
                    Registro.Error($"antes de transmitir (pantalla «{nombreMonitor ?? "principal"}»): {e.Message}");
                    AlEstado(new EstadoEmision(Fase.Error, e.Message));
                }
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

    /// <summary>
    /// Instala el controlador (pide UAC) en segundo plano y avisa cómo terminó. Si ya estaba, lo reinstala: actualiza
    /// su configuración (si es la de Pantalla TV) y lo reinicia, lo que también arregla uno que quedó a medias.
    /// </summary>
    public void InstalarMonitorVirtual()
    {
        if (!PuedeCambiarAjustes) return; // transmitiendo podría estar usándolo
        var reinstalar = HayMonitorVirtual;
        // La marca «lo instalamos nosotros» solo tiene sentido en la instalación (si no, no se puede desinstalar).
        var id = PuedeIniciarConWindows ? Instalacion.IdPorDefecto : null; // = corre desde la instalación
        OperarControlador(quitar: false, () => Controlador.InstalarConPermiso(id), r => r.Resultado switch
        {
            Controlador.Resultado.Hecho => ((reinstalar ? "Monitor virtual reinstalado." : "Monitor virtual instalado. Elígelo en «Qué pantalla se transmite».")
                                            + (r.Detalle is null ? "" : " " + r.Detalle), ToolTipIcon.Info),
            Controlador.Resultado.Rechazado => ($"El monitor virtual no se {(reinstalar ? "reinstaló" : "instaló")}: no se dio permiso de administrador.", ToolTipIcon.Warning),
            _ => ($"El monitor virtual no se pudo {(reinstalar ? "reinstalar" : "instalar")}: " + r.Detalle, ToolTipIcon.Error),
        });
    }

    /// <summary>Quita el monitor virtual (dispositivo, controlador y su configuración si es la nuestra), pidiendo UAC.</summary>
    public void QuitarMonitorVirtual()
    {
        if (!PuedeCambiarAjustes) return; // transmitiendo podría estar usándolo
        OperarControlador(quitar: true, () => Controlador.QuitarConPermiso(Instalacion.IdPorDefecto), r => r.Resultado switch
        {
            Controlador.Resultado.Hecho => ("Monitor virtual quitado." + (r.Detalle is null ? "" : " " + r.Detalle), ToolTipIcon.Info),
            Controlador.Resultado.Rechazado => ("El monitor virtual no se quitó: no se dio permiso de administrador.", ToolTipIcon.Warning),
            _ => ("El monitor virtual no se pudo quitar: " + r.Detalle, ToolTipIcon.Error),
        });
    }

    private void OperarControlador(bool quitar, Func<Controlador.Respuesta> operacion,
        Func<Controlador.Respuesta, (string Texto, ToolTipIcon Icono)> aviso)
    {
        if (InstalandoControlador) return;
        InstalandoControlador = true;
        _quitandoControlador = quitar;
        Cambio?.Invoke();
        var accion = quitar ? "quitar" : "instalar";
        Task.Run(() =>
        {
            Controlador.Respuesta r;
            try { r = operacion(); }
            catch (Exception e) { r = new Controlador.Respuesta(Controlador.Resultado.Fallo, e.Message); }
            if (r.Resultado == Controlador.Resultado.Fallo)
                Registro.Error($"monitor virtual: no se pudo {accion}: {r.Detalle}");
            else
                Registro.Anotar($"monitor virtual: {accion} → {r.Resultado}{(r.Detalle is null ? "" : ": " + r.Detalle)}");
            _ui.Post(_ =>
            {
                InstalandoControlador = false;
                _quitandoControlador = false;
                ActualizarPantallas(); // ahora está (o ya no está) el virtual
                var (texto, icono) = aviso(r);
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
        _cancelarActualizacion.Cancel();
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
        else if (e.Fase == Fase.Inactivo && e.Detalle == Emision.TextoTerminadaPorElReceptor)
            Aviso?.Invoke(e.Detalle, ToolTipIcon.Info);
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
        AlTerminarTransmision(); // lo de las actualizaciones que esperaba a que no se transmitiera
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
        LiberarActualizaciones();
    }
}
