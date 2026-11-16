using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PantallaTv.Emisor;

/// <summary>Una de las opciones de una lista del menú o de la ventana (<paramref name="Disponible"/> falso: se ve pero no se puede elegir).</summary>
internal sealed record Opcion<T>(string Texto, T Valor, bool Elegida, bool Disponible = true);

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
    /// <summary>Las transmisiones (se reemplaza entera, nunca se modifica: se lee desde varios hilos).</summary>
    private Transmision[] _transmisiones = [];
    private readonly object _cerrojo = new();
    /// <summary>Número de la última transmisión iniciada (cada una tiene el suyo).</summary>
    private int _numero;
    private Fase _faseReposo = Fase.Inactivo;
    private string _estadoReposo = SinTransmitir;

    /// <summary>Cuántas transmisiones puede haber a la vez (una conexión TCP por TV).</summary>
    public const int MaxTransmisiones = 4;

    /// <summary>Para las pruebas: crea la emisión de cada transmisión (si no, <see cref="EmisionPersistente"/> o <see cref="Emision"/> según el ajuste).</summary>
    internal Func<IEmision>? FabricaEmision { get; init; }

    /// <summary>Para las pruebas: deja lista la pantalla (si no, <see cref="Monitores.Preparar"/>, que conecta el monitor virtual).</summary>
    internal Func<string?, Action<string>?, (int Ancho, int Alto, int Hz)?, Monitores.Preparado>? Preparador { get; init; }

    /// <summary>Para las pruebas: si está VB-CABLE (si no, <see cref="AudioExclusivo.HayCable"/>).</summary>
    internal Func<bool> HayCable { get; init; } = AudioExclusivo.HayCable;

    /// <summary>Para las pruebas: el rectángulo del monitor en el escritorio (si no, el de <see cref="Monitores.Pantallas"/>); null si no se encuentra.</summary>
    internal Func<Monitor, System.Drawing.Rectangle?> LimitesDeMonitor { get; init; } = LimitesEnElEscritorio;

    /// <summary>
    /// ¿Se usa el audio exclusivo de la tele (F5: las apps con ventana en el monitor suenan solo allí) en vez de silenciar todo el
    /// PC? Solo si se envía sonido, el usuario quiere el PC en silencio, hay VB-CABLE y se transmite un monitor que no es el
    /// principal: con el principal, donde está todo, o sin cable, se sigue como siempre.
    /// </summary>
    internal static bool UsaAudioExclusivo(bool conSonido, bool silenciarPc, bool esPrincipal, bool hayCable) =>
        conSonido && silenciarPc && !esPrincipal && hayCable;

    private static System.Drawing.Rectangle? LimitesEnElEscritorio(Monitor monitor) =>
        Monitores.Pantallas().Where(p => string.Equals(p.Nombre, monitor.Nombre, StringComparison.OrdinalIgnoreCase))
            .Select(p => (System.Drawing.Rectangle?)p.Limites).FirstOrDefault();

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

    /// <summary>Las transmisiones, en el orden en que se iniciaron (una fila de la ventana cada una).</summary>
    public IReadOnlyList<Transmision> Transmisiones => _transmisiones;

    /// <summary>
    /// El estado de todas juntas (el ícono de la bandeja y la línea de estado): con una sola, el suyo; con varias, el más
    /// urgente (reconectando, transmitiendo, conectando, error). Sin ninguna, cómo terminó la última.
    /// </summary>
    public Fase Fase
    {
        get
        {
            var ts = _transmisiones;
            if (ts.Length == 0) return _faseReposo;
            if (ts.Length == 1) return ts[0].Fase;
            foreach (var f in new[] { Fase.Reconectando, Fase.Transmitiendo, Fase.Conectando, Fase.Error })
                if (ts.Any(t => t.Fase == f)) return f;
            return Fase.Inactivo;
        }
    }

    public string Estado
    {
        get
        {
            var ts = _transmisiones;
            if (ts.Length == 0) return _estadoReposo;
            if (ts.Length == 1) return ts[0].Estado;
            var reconectando = ts.Count(t => t.Fase == Fase.Reconectando);
            var conectando = ts.Count(t => t.Fase == Fase.Conectando);
            var errores = ts.Count(t => t.Fase == Fase.Error);
            return $"{ts.Length} transmisiones"
                   + (reconectando > 0 ? $", {reconectando} reconectando" : "")
                   + (conectando > 0 ? $", {conectando} conectando" : "")
                   + (errores > 0 ? $", {errores} con error" : "");
        }
    }

    /// <summary>¿Hay alguna transmisión en curso?</summary>
    public bool Transmitiendo => _transmisiones.Any(t => t.EnCurso);
    /// <summary>Hay una instalación, reinstalación o desinstalación del monitor virtual en curso (pidiendo UAC).</summary>
    public bool InstalandoControlador { get; private set; }
    private bool _quitandoControlador;
    public bool Diagnosticando { get; private set; }
    /// <summary>Lo de la primera transmisión (compatibilidad: con una sola es «la» transmisión); null si no hay ninguna.</summary>
    public EnVivo? EnVivo => _transmisiones.FirstOrDefault()?.Vivo;

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
    private IReadOnlyList<(int Numero, string Dispositivo)> _virtuales = [];

    /// <summary>
    /// Vuelve a preguntarle a Windows qué monitores hay (llamadas de enumeración: al abrir el menú, al mostrar la
    /// ventana, cuando cambian las pantallas o termina de instalarse el monitor virtual; no en cada refresco).
    /// </summary>
    public void ActualizarPantallas()
    {
        _monitores = Monitores.Listar();
        _virtuales = [.. MonitorVirtual.DetectarTodos().Select(v => (v.Numero, v.Dispositivo))];
    }

    /// <summary>
    /// Las pantallas de la última <see cref="ActualizarPantallas"/>. La elegida es la de los ajustes y, si ya se está transmitiendo, la
    /// primera libre (las que ya van a una TV se ven pero no se pueden elegir).
    /// </summary>
    public Pantallas OpcionesPantalla() => ArmarPantallas(_monitores, _virtuales, Ajustes.MonitorNombre, PantallasOcupadas());

    /// <summary>La pantalla con la que empezaría la próxima transmisión: la de los ajustes si está libre, si no la primera libre.</summary>
    public string? PantallaLibreElegida() =>
        OpcionesPantalla().Opciones.FirstOrDefault(o => o.Elegida) is { } elegida ? elegida.Valor : Ajustes.MonitorNombre;

    /// <summary>Elige la pantalla de la próxima transmisión. A diferencia de los ajustes de la emisión, se puede mientras se transmite (se lee al empezar cada una).</summary>
    public void ElegirPantalla(string? valor)
    {
        if (Ajustes.MonitorNombre == valor) return;
        Ajustes.MonitorNombre = valor;
        Guardar();
    }

    /// <summary>Las pantallas que ya van a una TV (por su clave: nombre de Windows o «virtual2»).</summary>
    private HashSet<string> PantallasOcupadas() =>
        new(_transmisiones.Where(t => t.EnCurso).Select(t => t.ClavePantalla), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// La clave con que se compara una pantalla pedida: «virtual», «virtual2»… tal cual; un monitor, su nombre de Windows (si ya no
    /// está o no se pidió ninguno, el de la principal, que es la que se transmite).
    /// </summary>
    private string ClavePantalla(string? nombre)
    {
        if (MonitorVirtual.NumeroDeNombre(nombre) is { } n) return MonitorVirtual.NombreDeNumero(n);
        var monitores = _monitores;
        if (monitores.Count == 0) return nombre ?? "";
        return (monitores.FirstOrDefault(m => string.Equals(m.Nombre, nombre, StringComparison.OrdinalIgnoreCase))
                ?? monitores.FirstOrDefault(m => m.Principal) ?? monitores[0]).Nombre;
    }

    /// <summary>
    /// Los monitores reales (sin las salidas de los monitores virtuales, que van aparte con su nombre) y los monitores virtuales
    /// instalados (hasta 4). Un virtual se conecta al escritorio solo mientras se transmite: listarlo no mueve nada.
    /// </summary>
    /// <param name="ocupadas">Las pantallas que ya van a una TV (claves): se ven pero no se pueden elegir.</param>
    internal static Pantallas ArmarPantallas(IReadOnlyList<Monitor> todos, IReadOnlyList<(int Numero, string Dispositivo)> virtuales,
        string? elegido, ISet<string>? ocupadas = null)
    {
        bool Ocupada(string clave) => ocupadas is not null && ocupadas.Contains(clave);
        var reales = todos.Where(m => !virtuales.Any(v => string.Equals(m.Nombre, v.Dispositivo, StringComparison.OrdinalIgnoreCase))).ToList();
        var numeroElegido = MonitorVirtual.NumeroDeNombre(elegido) is { } n && virtuales.Any(v => v.Numero == n) ? n : (int?)null;
        var real = numeroElegido is not null || reales.Count == 0 ? null : Monitores.Elegir(reales, m => m.Nombre == elegido);
        var opciones = reales.Select(m => new Opcion<string?>(m.ToString() + (Ocupada(m.Nombre) ? TextoEnUso : ""), m.Nombre,
            real is not null && m.Indice == real.Indice, !Ocupada(m.Nombre))).ToList();
        foreach (var v in virtuales.OrderBy(v => v.Numero))
        {
            var nombre = MonitorVirtual.NombreDeNumero(v.Numero);
            opciones.Add(new Opcion<string?>(
                (v.Numero == 1 ? "Monitor virtual" : $"Monitor virtual {v.Numero}") + " (se conecta solo al transmitir)" + (Ocupada(nombre) ? TextoEnUso : ""),
                nombre, numeroElegido == v.Numero, !Ocupada(nombre)));
        }
        // La elegida ya va a una TV: se propone la primera libre (los ajustes no cambian).
        var marcada = opciones.FindIndex(o => o.Elegida);
        if (marcada >= 0 && !opciones[marcada].Disponible)
        {
            var libre = opciones.FindIndex(o => o.Disponible);
            if (libre >= 0)
            {
                opciones[marcada] = opciones[marcada] with { Elegida = false };
                opciones[libre] = opciones[libre] with { Elegida = true };
            }
        }
        return new Pantallas(opciones, virtuales.Count > 0);
    }

    internal const string TextoEnUso = " · ya se transmite";

    public IReadOnlyList<Destino> Destinos() => ArmarDestinos(_tvs(), Ajustes.UltimaIp);

    /// <summary>Las TV que se pueden elegir para una transmisión nueva: las que no tienen ya una.</summary>
    public IReadOnlyList<Destino> DestinosLibres()
    {
        var ocupadas = _transmisiones.Where(t => t.EnCurso).Select(t => t.ClaveTv).ToHashSet();
        return [.. Destinos().Where(d => !ocupadas.Contains(d.Clave))];
    }

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

    public string TextoInstalarVirtual => InstalandoControlador && !_quitandoControlador && !_reubicandoControlador ? "Instalando el monitor virtual…" : "Instalar el monitor virtual…";

    private bool _reubicandoControlador;

    /// <summary>
    /// Si la última vez que se conectó el monitor virtual lo dibujaba otra gráfica que la pantalla principal, el aviso (el mismo que
    /// se mostró al transmitir); null si coinciden, o si aún no se ha conectado en esta sesión.
    /// </summary>
    public string? AvisoGrafica { get; private set; }

    /// <summary>¿Se muestra el botón «Mover el monitor virtual a la gráfica de la pantalla»? Solo si hace falta y si la configuración es la nuestra.</summary>
    public bool PuedeReubicarMonitorVirtual => (AvisoGrafica is not null && _graficaReubicable && PuedeCambiarAjustes && !InstalandoControlador)
                                               || _reubicandoControlador;

    /// <summary>Si la configuración era la nuestra cuando se fijó <see cref="AvisoGrafica"/> (se calcula una vez, no en cada refresco).</summary>
    private bool _graficaReubicable;

    public string TextoReubicarVirtual => _reubicandoControlador ? "Moviendo…" : "Mover el monitor virtual a la &gráfica de la pantalla…";

    /// <summary>Mueve el monitor virtual a la gráfica de la pantalla principal (pide UAC): reescribe su configuración y lo reinicia.</summary>
    public void ReubicarMonitorVirtual()
    {
        if (InstalandoControlador || !PuedeReubicarMonitorVirtual || !PuedeCambiarAjustes) return; // transmitiendo podría estar usándolo
        _reubicandoControlador = true;
        OperarControlador(quitar: false, Controlador.ReubicarConPermiso, r => r.Resultado switch
        {
            Controlador.Resultado.Hecho => ("Monitor virtual movido a la gráfica de la pantalla." + (r.Detalle is null ? "" : " " + r.Detalle), ToolTipIcon.Info),
            Controlador.Resultado.Rechazado => ("El monitor virtual no se movió: no se dio permiso de administrador.", ToolTipIcon.Warning),
            _ => ("El monitor virtual no se pudo mover: " + r.Detalle, ToolTipIcon.Error),
        }, "reubicar");
    }

    /// <summary>¿Está el monitor virtual instalado? (Para ofrecer reinstalarlo o quitarlo en «Avanzado».)</summary>
    public static bool HayMonitorVirtual => Controlador.Instalado();

    public string TextoReinstalarVirtual(bool hay) => InstalandoControlador && !_quitandoControlador && !_reubicandoControlador
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
            texto += "Reinstala el monitor virtual para tener más tamaños y frecuencias y hasta 4 monitores virtuales (pide permiso de administrador). ";
        if (AvisoGrafica is { } grafica) texto += grafica;
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
        if (LeerDestino(texto) is not { } destino)
        {
            Aviso?.Invoke("Esa no es una dirección IP válida: escribe cuatro números separados por puntos (por ejemplo 10.0.0.20).", ToolTipIcon.Warning);
            return false;
        }
        return Iniciar(new Receptor("TV", destino.Direccion, destino.Puerto));
    }

    /// <summary>
    /// Una IP escrita a mano, con un puerto opcional («10.0.0.20:5602»: un receptor detrás de una redirección, como un emulador de
    /// Android con <c>adb forward</c>). Sin puerto, el de siempre.
    /// </summary>
    internal static (IPAddress Direccion, int Puerto)? LeerDestino(string texto)
    {
        texto = texto.Trim();
        var puerto = Protocolo.PuertoTcp;
        var dosPuntos = texto.LastIndexOf(':');
        if (dosPuntos > 0 && texto.IndexOf(':') == dosPuntos) // IPv4 con puerto (una IPv6 tiene varios «:»)
        {
            if (!int.TryParse(texto[(dosPuntos + 1)..], out puerto) || puerto is < 1 or > 65535) return null;
            texto = texto[..dosPuntos];
        }
        return LeerIp(texto) is { } dir ? (dir, puerto) : null;
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

    /// <summary>
    /// Empieza una transmisión a esa TV con la pantalla que toque: la de los ajustes si está libre; si ya va a otra TV, la primera libre.
    /// Devuelve false (y avisa qué hacer) si no se puede: ver <see cref="MotivoNoPuedeIniciar"/>.
    /// </summary>
    public bool Iniciar(Receptor receptor) => Iniciar(receptor, PantallaLibreElegida());

    /// <summary>¿Por qué no se puede empezar esa transmisión (la TV o la pantalla ya están en una, o ya hay 4)? Null si se puede.</summary>
    public string? MotivoNoPuedeIniciar(Receptor receptor, string? pantalla)
    {
        var vivas = _transmisiones.Where(t => t.EnCurso).ToList();
        if (vivas.Count >= MaxTransmisiones)
            return $"Ya hay {MaxTransmisiones} transmisiones a la vez, que es el máximo. Detén una para empezar otra.";
        if (vivas.FirstOrDefault(t => t.ClaveTv == ClaveTv(receptor)) is not null)
            return $"{receptor} ya está recibiendo una transmisión. Elige otra TV o detén esa.";
        if (vivas.FirstOrDefault(t => string.Equals(t.ClavePantalla, ClavePantalla(pantalla), StringComparison.OrdinalIgnoreCase)) is { } conPantalla)
            return $"Esa pantalla ya se transmite a {conPantalla.Receptor}. Elige otra pantalla o detén esa transmisión.";
        return null;
    }

    /// <summary>¿Por qué no se puede sumar otra transmisión ahora (ya hay 4, o no queda una pantalla libre)? Null si se puede: sin TV libre a la vista queda escribir la IP.</summary>
    public string? MotivoNoOtra()
    {
        if (_transmisiones.Count(t => t.EnCurso) >= MaxTransmisiones)
            return $"Ya hay {MaxTransmisiones} transmisiones a la vez, que es el máximo. Detén una para empezar otra.";
        // Sin TV libre a la vista no se bloquea: queda «Escribir IP…» (una TV que no se anuncia, o un receptor tras adb forward).
        if (!OpcionesPantalla().Opciones.Any(o => o.Disponible) && _monitores.Count > 0)
            return "Todas las pantallas ya se están transmitiendo. Para más, instala más monitores virtuales en «Avanzado».";
        return null;
    }

    private static string ClaveTv(Receptor receptor) => $"{receptor.Direccion}:{receptor.Puerto}";

    /// <summary>Un aviso de una transmisión empieza por la TV a la que se refiere (con varias, si no, no se sabría cuál).</summary>
    private static string ConTv(Transmision t, string texto) => $"{t.Receptor}: {texto}";

    /// <summary>Los textos de los avisos del sonido de la tele y los nombres de los programas (con su caché).</summary>
    internal AvisosAudio Avisos { get; init; } = new();

    public bool Iniciar(Receptor receptor, string? pantalla)
    {
        if (MotivoNoPuedeIniciar(receptor, pantalla) is { } motivo)
        {
            Aviso?.Invoke(motivo, ToolTipIcon.Warning);
            return false;
        }
        Ajustes.UltimaIp = receptor.Direccion.ToString();
        Ajustes.Guardar();
        // Una que quedó con su error a la vista para esa TV o esa pantalla deja su fila a la nueva.
        var claveTv = ClaveTv(receptor);
        var clavePantalla = ClavePantalla(pantalla);
        Quitar(x => x.ClaveTv == claveTv || string.Equals(x.ClavePantalla, clavePantalla, StringComparison.OrdinalIgnoreCase));

        var primera = !_transmisiones.Any(x => x.EnCurso);
        if (primera)
        {
            _faseReposo = Fase.Inactivo;
            _estadoReposo = SinTransmitir;
        }
        var a = Ajustes;
        var numero = ++_numero;
        var telemetria = a.GuardarTelemetria ? new Telemetria(Ajustes.CarpetaTelemetria, primera ? null : $"tv{numero}") : null;

        var cancelar = new CancellationTokenSource();
        var ct = cancelar.Token;
        // Solo la primera lleva el sonido (y silencia el PC): dos capturas de audio a la vez no tienen sentido. Si esa se detiene,
        // las otras siguen sin sonido.
        var conSonido = primera && a.ConAudio;
        var t = new Transmision
        {
            Numero = numero,
            Receptor = receptor,
            Pantalla = pantalla,
            ClaveTv = ClaveTv(receptor),
            ClavePantalla = ClavePantalla(pantalla),
            ConSonido = conSonido,
            Vivo = new EnVivo { Tv = receptor.ToString(), FpsPedidos = a.Fps },
            Cancelar = cancelar,
            Fase = Fase.Conectando, // desde ya: un error de la anterior no debe quedar a la vista mientras empieza esta
            Estado = $"Conectando con {receptor.Nombre}…",
        };

        // Todo vuelve por el hilo de la interfaz y solo cuenta si la transmisión sigue en la lista: la tarea termina
        // (EnCurso pasa a falso) antes de que corra lo que dejó posteado.
        void EnLaInterfaz(Action accion) => _ui.Post(_ => { if (_transmisiones.Contains(t)) accion(); }, null);
        void AlEstado(EstadoEmision e) => EnLaInterfaz(() => AlCambiarEstado(t, e));
        void AlNumeros(Estadisticas e) => EnLaInterfaz(() => AlEstadisticas(t, e));
        var emision = FabricaEmision?.Invoke() ?? (a.Reconectar ? new EmisionPersistente() : new Emision());
        emision.CambioDeEstado += AlEstado;
        emision.NuevasEstadisticas += AlNumeros;
        emision.CambioDeAudio += c => EnLaInterfaz(() => AlCambiarAudio(t, c));
        t.Emision = emision;

        lock (_cerrojo) _transmisiones = [.. _transmisiones, t];
        var vivo = t.Vivo;
        var nombreMonitor = pantalla;
        // El monitor virtual a la medida de la tele: el modo elegido a mano o, si no, el que ella informa.
        var tele = _pantallaDe(receptor);
        var modoVirtual = LeerModo(a.ModoMonitorVirtual) ?? (tele is { } tl ? ModoAutomatico(tl) : null);
        t.Tarea = Task.Run(async () =>
        {
            Monitores.Preparado? preparado = null;
            try
            {
                // Con una preferencia de gráfica de Windows para este programa la captura propia no puede leer las pantallas de
                // la otra gráfica: se dice ahora y con el arreglo, no solo cuando falle (solo importa si se puede usar la nativa).
                if (primera && Preparador is null && a.Tuberia is null or Tuberias.Nativa
                    && Controlador.PreferenciaDeGrafica(Environment.ProcessPath) is { } preferencia // lo normal: ninguna, y no se enumera nada
                    && AvisoPreferenciaDeGrafica(preferencia, Controlador.NombresDeHardware(Controlador.GraficasDelPc()).Count) is { } avisoPreferencia)
                {
                    Registro.Anotar("gráfica: " + avisoPreferencia);
                    EnLaInterfaz(() => Aviso?.Invoke(avisoPreferencia, ToolTipIcon.Warning));
                }
                // Dentro de la tarea: conectar el monitor virtual tarda unos segundos y no debe congelar la interfaz.
                Action<string> avisar = m => AlEstado(new EstadoEmision(Fase.Conectando, m));
                preparado = Preparador is { } preparar ? preparar(nombreMonitor, avisar, modoVirtual) : Monitores.Preparar(nombreMonitor, avisar, modoVirtual);
                if (preparado.Aviso is { } avisoPantallas)
                    EnLaInterfaz(() => Aviso?.Invoke(ConTv(t, avisoPantallas), ToolTipIcon.Info));
                var monitor = preparado.Monitor; // en píxeles reales (el programa es consciente del DPI)
                var esVirtual = Monitores.EsElVirtual(monitor);
                if (esVirtual)
                {
                    var reubicable = preparado.AvisoGrafica is not null && Controlador.ConfiguracionEsNuestra(); // fuera del hilo de la interfaz
                    EnLaInterfaz(() => { AvisoGrafica = preparado.AvisoGrafica; _graficaReubicable = reubicable; Cambio?.Invoke(); });
                }
                // Con varios virtuales la fila dice cuál («Monitor virtual 2»), como en «Qué pantalla».
                var textoVirtual = MonitorVirtual.NumeroDeNombre(nombreMonitor) is int nv and > 1 ? $"Monitor virtual {nv}" : "Monitor virtual";
                EnLaInterfaz(() => { vivo.Pantalla = esVirtual ? $"{textoVirtual} · {monitor.Ancho}×{monitor.Alto}" : monitor.ToString(); Cambio?.Invoke(); });
                // Más cuadros que los Hz de la pantalla (la del monitor virtual o la de la tele) no se ven y gastan más.
                var fps = Math.Min(a.Fps, preparado.Hz ?? (tele?.Hz is int hz && hz >= 24 ? hz : a.Fps));
                if (fps != a.Fps)
                {
                    Registro.Anotar($"cuadros por segundo: {fps} en vez de {a.Fps} (la pantalla va a {preparado.Hz ?? tele?.Hz} Hz)");
                    EnLaInterfaz(() => vivo.FpsPedidos = fps);
                }
                // Con VB-CABLE y un monitor que no es el principal, el sonido de las apps de ese monitor va solo a la tele.
                var audioDeMonitor = UsaAudioExclusivo(conSonido, a.SilenciarPc, monitor.Principal, conSonido && HayCable())
                    ? LimitesDeMonitor(monitor) : null;
                var op = new OpcionesEmision(fps, a.Kbps, monitor.Ancho, monitor.Alto, esVirtual, null,
                    Tuberia: a.Tuberia, ConAudio: conSonido, SilenciarPc: conSonido && a.SilenciarPc, Monitor: monitor.Salida, Adaptador: monitor.Adaptador, MonitorNombre: monitor.Nombre,
                    AudioDeMonitor: audioDeMonitor);
                await emision.EjecutarAsync(receptor, op, telemetria, ct);
            }
            catch (EmisionException e)
            {
                // Los de la transmisión ya se mostraron con CambioDeEstado; este es de antes de empezar.
                if (preparado is null)
                {
                    Registro.Error($"antes de transmitir a {receptor} (pantalla «{nombreMonitor ?? "principal"}»): {e.Message}");
                    AlEstado(new EstadoEmision(Fase.Error, e.Message));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Una transmisión que falla de forma inesperada no tumba a las otras.
                Registro.Error($"transmisión a {receptor}: error inesperado", e);
                AlEstado(new EstadoEmision(Fase.Error, "La transmisión falló por un error inesperado. Vuelve a empezarla; si se repite, mira el registro (Diagnóstico en «Avanzado»)."));
            }
            finally
            {
                preparado?.Liberar(); // quita el monitor virtual del escritorio si lo conectamos nosotros
                telemetria?.Dispose();
                EnLaInterfaz(() => Reposar(t));
            }
        }, CancellationToken.None);
        Cambio?.Invoke();
        return true;
    }

    /// <summary>Detiene todas las transmisiones.</summary>
    public void Detener()
    {
        foreach (var t in _transmisiones) Detener(t);
    }

    /// <summary>Detiene solo esa; las demás siguen. Si ya terminó (quedó con su error a la vista), la quita de la lista.</summary>
    public void Detener(Transmision t)
    {
        if (!t.EnCurso)
        {
            Quitar(x => x == t);
            return;
        }
        try { t.Cancelar?.Cancel(); }
        catch (ObjectDisposedException) { } // ya terminó y se liberó
    }

    /// <summary>«Detener» mientras corre; «Quitar» la que terminó con error y sigue a la vista.</summary>
    public static string TextoBotonFila(Transmision t) => t.EnCurso ? "Detener" : "Quitar";

    /// <summary>Saca de la lista las que ya terminaron y cumplen <paramref name="cual"/>.</summary>
    private void Quitar(Func<Transmision, bool> cual)
    {
        lock (_cerrojo) _transmisiones = [.. _transmisiones.Where(x => x.EnCurso || !cual(x))];
        Cambio?.Invoke();
    }

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
        Func<Controlador.Respuesta, (string Texto, ToolTipIcon Icono)> aviso, string? accion = null)
    {
        if (InstalandoControlador) return;
        InstalandoControlador = true;
        _quitandoControlador = quitar;
        Cambio?.Invoke();
        accion ??= quitar ? "quitar" : "instalar";
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
                // Reubicar, reinstalar (escribe la gráfica de la pantalla) o quitar: el aviso ya no vale.
                if (r.Resultado == Controlador.Resultado.Hecho) AvisoGrafica = null;
                _reubicandoControlador = false;
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
            Task.WaitAll([.. _transmisiones.Select(t => t.Tarea).OfType<Task>()], TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // la transmisión terminó con error: al salir ya no importa
        }
        MonitorVirtual.LiberarSiEsNuestro(); // por si la tarea no alcanzó
    }

    // ── Lo que llega de la emisión (en el hilo de la interfaz) ──────────────────────────────────

    internal void AlCambiarEstado(Transmision t, EstadoEmision e)
    {
        var antes = t.Fase;
        t.Fase = e.Fase;
        t.Estado = e.Detalle;
        if (e.Fase == Fase.Transmitiendo) t.Vivo.Desde ??= DateTime.UtcNow;
        else t.Vivo.Ultimas = null; // reconectando: los números viejos ya no dicen nada
        // Cada aviso dice a qué TV se refiere: con varias transmisiones, si no, no se sabría cuál falló.
        if (e.Fase == Fase.Error)
            Aviso?.Invoke(ConTv(t, e.Detalle), ToolTipIcon.Error);
        else if (e.Fase == Fase.Inactivo && e.Detalle == Emision.TextoTerminadaPorElReceptor)
            Aviso?.Invoke(ConTv(t, e.Detalle), ToolTipIcon.Info);
        else if (e.Fase == Fase.Reconectando && antes != Fase.Reconectando)
            Aviso?.Invoke(ConTv(t, antes == Fase.Transmitiendo
                    ? "Se cortó la transmisión. Reintentando…"
                    : "Todavía no hay respuesta de la tele (¿está abierta la app «Pantalla»?). Reintentando…"),
                ToolTipIcon.Warning);
        Cambio?.Invoke();
    }

    /// <summary>
    /// El sonido separado de la tele cambió (H3). Cada aviso sale como globo en el PC y como franja en la tele. «Pasaron a la tele» y
    /// «volvieron» se avisan cada vez; «también tiene ventanas en tu pantalla», una sola vez por programa y por transmisión.
    /// </summary>
    internal void AlCambiarAudio(Transmision t, CambioAudio c)
    {
        string? texto = null;
        switch (c.Tipo)
        {
            case TipoCambioAudio.PasaronATele when c.Exes.Count > 0:
                texto = Avisos.PasaronATele(c.Exes);
                break;
            case TipoCambioAudio.VolvieronAlPc when c.Exes.Count > 0:
                texto = Avisos.VolvieronAlPc(c.Exes);
                break;
            case TipoCambioAudio.TambienEnPc:
                t.TambienEnPc = c.Exes;
                var nuevos = c.Exes.Where(t.TambienEnPcAvisados.Add).ToList();
                if (nuevos.Count > 0) texto = Avisos.TambienEnPc(nuevos) + ".";
                Cambio?.Invoke(); // la nota de la fila cambió
                break;
        }
        if (texto is null) return;
        Aviso?.Invoke(ConTv(t, texto), ToolTipIcon.Info);
        t.Emision?.AvisarATele(texto);
    }

    internal void AlEstadisticas(Transmision t, Estadisticas e)
    {
        if (t.Fase != Fase.Transmitiendo) return;
        t.Vivo.Ultimas = e;
        Cambio?.Invoke();
    }

    /// <summary>
    /// Una transmisión terminó y sale de la lista; las otras siguen. Si fue por un error, su mensaje (con la TV) se queda a la vista
    /// cuando no queda ninguna.
    /// </summary>
    internal void Reposar(Transmision t)
    {
        t.Cancelar?.Dispose();
        t.Cancelar = null;
        // Con otras en curso, una que terminó con error se queda en su fila con el motivo (si no, se borraría al instante y solo
        // quedaría el globo, cortado): sale con «Quitar» o al empezar otra a esa TV o con esa pantalla.
        if (t.Fase == Fase.Error && _transmisiones.Any(x => x != t && x.EnCurso))
        {
            Cambio?.Invoke();
            return;
        }
        // Sin ninguna en curso: salen todas las que quedaban con error y el último error queda como estado general (como antes).
        lock (_cerrojo) _transmisiones = [.. _transmisiones.Where(x => x != t && x.EnCurso)];
        if (t.Fase == Fase.Error)
        {
            _faseReposo = Fase.Error;
            _estadoReposo = t.Estado.StartsWith(t.Receptor.ToString(), StringComparison.Ordinal) ? t.Estado : ConTv(t, t.Estado);
        }
        else if (_faseReposo != Fase.Error)
        {
            _faseReposo = Fase.Inactivo;
            _estadoReposo = SinTransmitir;
        }
        if (!Transmitiendo) AlTerminarTransmision(); // lo de las actualizaciones que esperaba a que no se transmitiera
        Cambio?.Invoke();
    }

    // ── Textos del panel en vivo ────────────────────────────────────────────────────────────────

    public static string TextoTiempo(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>El título de la fila de una transmisión: «TV ← pantalla».</summary>
    public static string TituloFila(Transmision t) => $"{t.Vivo.Tv} ← {t.Vivo.Pantalla}";

    /// <summary>
    /// El estado de la fila: conectando, reconectando o el error con su motivo tal cual; transmitiendo, con los cuadros por
    /// segundo, los Mb/s y el tiempo.
    /// </summary>
    public static string EstadoFila(Transmision t) => t.Fase switch
    {
        Fase.Transmitiendo => $"Transmitiendo · {TextoFps(t.Vivo.Ultimas)} fps · {TextoMbps(t.Vivo.Ultimas)} Mb/s"
                              + (t.Vivo.Desde is { } desde ? " · " + TextoTiempo(DateTime.UtcNow - desde) : ""),
        _ => t.Estado,
    };

    public const string TextoSinSonido = "sin sonido (solo una transmisión lleva sonido)";

    /// <summary>La nota de la fila: la forma de capturar y, si se pidió el sonido y esta no lo lleva, por qué no. Vacía si no hay nada que decir.</summary>
    public string NotaFila(Transmision t)
    {
        var partes = new List<string>();
        if (t.Fase == Fase.Transmitiendo && TextoForma(t.Vivo.Ultimas) is var forma && forma != "—") partes.Add(forma);
        if (!t.ConSonido && Ajustes.ConAudio) partes.Add(TextoSinSonido);
        else if (t.ConSonido) partes.Add("con sonido");
        if (t.Fase == Fase.Transmitiendo && t.TambienEnPc.Count > 0) partes.Add(Avisos.TambienEnPc(t.TambienEnPc));
        return string.Join(" · ", partes);
    }

    public static string TextoFps(Estadisticas? e) => e is null ? "—" : e.Fps.ToString("0", Es);

    public static string TextoMbps(Estadisticas? e) => e is null ? "—" : e.Mbps.ToString("0.0", Es);

    /// <summary>La captura con copia entre gráficas gasta más CPU: se le dice a la persona.</summary>
    public const string TextoCopiaEntreGraficas = "Captura propia · copia entre gráficas (más CPU)";

    public static string TextoForma(Estadisticas? e) =>
        e is { Tuberia.Length: > 0 } ? e.CopiaEntreGpus ? TextoCopiaEntreGraficas : Tuberias.Descripcion(e.Tuberia) : "—";

    /// <summary>
    /// El aviso al empezar si Windows tiene a Pantalla TV asignado a una gráfica (<paramref name="preferencia"/>: «Alto rendimiento»,
    /// «Ahorro de energía»); null si no hay ninguna. Cabe en un globo (255 caracteres).
    /// </summary>
    /// <param name="graficas">Cuántas gráficas de hardware distintas hay: con una sola la preferencia no molesta y no se avisa.</param>
    internal static string? AvisoPreferenciaDeGrafica(string? preferencia, int graficas) =>
        preferencia is null || graficas < 2 ? null
            // Antes de transmitir no se sabe si molestará (depende de qué gráfica dibuja cada pantalla): se dice como posibilidad.
            : $"Windows tiene a Pantalla TV en «{preferencia}» (Configuración › Sistema › Pantalla › Gráficos): con dos gráficas "
              + "puede impedir la captura propia y obligar al respaldo, más lento. Si pasa, déjalo en «Dejar que Windows decida».";

    /// <summary>Lo que cabe en un globo de Windows (corta en ~255 caracteres).</summary>
    internal const int LargoGlobo = 255;

    /// <summary>
    /// El texto para un globo: si no cabe, se corta en una palabra y se avisa que el detalle está en la ventana (y en el
    /// registro), en vez de que Windows lo corte a ciegas justo donde suele estar el arreglo.
    /// </summary>
    public static string ParaGlobo(string texto)
    {
        if (texto.Length <= LargoGlobo) return texto;
        const string resto = "… (detalle en la ventana)";
        var corte = texto.LastIndexOf(' ', LargoGlobo - resto.Length);
        return texto[..(corte > 0 ? corte : LargoGlobo - resto.Length)].TrimEnd(' ', ',', ';', ':', '.') + resto;
    }

    public void Dispose()
    {
        _propio?.Dispose();
        foreach (var t in _transmisiones) t.Cancelar?.Dispose();
        LiberarActualizaciones();
    }
}
