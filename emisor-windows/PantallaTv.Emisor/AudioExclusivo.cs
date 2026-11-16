using System.Drawing;

namespace PantallaTv.Emisor;

/// <summary>
/// F5/H2. Mientras se transmite un monitor, las apps que tienen ventana en él suenan SOLO en la tele y el PC sigue
/// sonando con lo demás. Cómo: a cada programa con ventana en ese monitor se le fija como salida VB-CABLE
/// (<see cref="IRutasAudio"/>: la política de audio por app de Windows, que no se aplica en caliente: solo la toman los
/// sonidos que empiezan después) y se captura su audio por proceso (<see cref="CapturaDeProceso"/>, que no depende de
/// qué salida use ni de su volumen). Las capturas se mezclan en un solo flujo (<see cref="MezcladorPcm"/>) con la misma
/// forma que <see cref="CapturaAudio"/>. Una mirada por segundo (<see cref="Sondear"/>) agrega los programas que entran
/// al monitor y devuelve la salida de los que salen.
/// Las rutas que cambia se anotan antes en <see cref="RutasPersistidas"/> y se devuelven al detener, al cerrar el
/// programa y, si se cayó, al abrirlo otra vez o con <c>--restaurar-audio</c>.
/// </summary>
internal sealed class AudioExclusivo : IFuenteAudio
{
    public const int MsPorPaquete = MezcladorPcm.MsPorPaquete;
    private static readonly TimeSpan Mirada = TimeSpan.FromSeconds(1);

    private readonly Rectangle _monitor;
    private readonly string _idCable;
    private readonly IVentanas _ventanas;
    private readonly IProcesos _procesos;
    private readonly IRutasAudio _rutas;
    private readonly IFabricaCaptura _fabrica;
    private readonly RutasPersistidas _guardadas;
    private readonly IActividadAudio? _actividad;
    private readonly MezcladorPcm _mezclador = new();
    private readonly object _cerrojo = new();
    private readonly CancellationTokenSource _fin = new();

    private HashSet<string> _enTele = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _tambienEnPc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exesFijados = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _pidsSinRuta = [];
    private int _miradas;
    private readonly Dictionary<int, ICapturaDeProceso> _capturas = [];
    private readonly string? _defectoInicial;
    private Action<byte[]>? _alTenerPaquete;
    private Thread? _hiloMirada, _hiloReloj;
    private bool _liberado;

    public int Tasa => MezcladorPcm.Tasa;
    public int Canales => MezcladorPcm.Canales;

    /// <summary>Programas (ruta del .exe) cuyo sonido va ahora a la tele.</summary>
    public IReadOnlyCollection<string> EnTele { get { lock (_cerrojo) return [.. _enTele]; } }

    /// <summary>De esos, los que además tienen ventanas en otra pantalla: su sonido también se va a la tele (para el aviso de H3).</summary>
    public IReadOnlyCollection<string> TambienEnPc { get { lock (_cerrojo) return [.. _tambienEnPc]; } }

    /// <summary>
    /// Programas que acaban de pasar a la tele (rutas de .exe). Una app que ya estaba sonando sigue por la salida vieja hasta
    /// que pausa y vuelve a reproducir (la política no se aplica en caliente): H3 lo avisa con esta lista.
    /// </summary>
    public event Action<IReadOnlyList<string>>? PasaronATele;

    /// <summary>
    /// Programas que acaban de volver al PC (rutas de .exe). Su sonido seguía yendo a VB-CABLE (sin capturar, así que mudo)
    /// hasta que pausan y vuelven a reproducir: H3 lo avisa con esta lista. Solo los que están sonando en ese momento.
    /// </summary>
    public event Action<IReadOnlyList<string>>? VolvieronAlPc;

    /// <summary>Cambió la lista de <see cref="TambienEnPc"/>.</summary>
    public event Action<IReadOnlyCollection<string>>? CambioTambienEnPc;

    /// <summary>Lo normal: ventanas, procesos y política de Windows de verdad. Lanza si no hay VB-CABLE.</summary>
    public AudioExclusivo(Rectangle monitor)
        : this(monitor, IdDelCable() ?? throw new InvalidOperationException("No está VB-CABLE: no se puede separar el sonido de la tele."),
            new VentanasWindows(), new ProcesosWindows(), new RutasWindows(), new FabricaCapturaWindows(),
            new RutasPersistidas(RutasPersistidas.RutaPorDefecto), new ActividadAudioWindows())
    {
    }

    internal AudioExclusivo(Rectangle monitor, string idCable, IVentanas ventanas, IProcesos procesos, IRutasAudio rutas,
        IFabricaCaptura fabrica, RutasPersistidas guardadas, IActividadAudio? actividad = null)
    {
        _actividad = actividad;
        _monitor = monitor;
        _idCable = idCable;
        _ventanas = ventanas;
        _procesos = procesos;
        _rutas = rutas;
        _fabrica = fabrica;
        _guardadas = guardadas;
        _defectoInicial = rutas.SalidaPorDefecto();
        // Si el programa termina sin pasar por Dispose, que no queden apps mudas. No cubre que maten el proceso: para eso
        // están las rutas anotadas y el rescate.
        AppDomain.CurrentDomain.ProcessExit += AlTerminarElProceso;
    }

    /// <summary>¿Está VB-CABLE instalado? Barato: solo lista las salidas.</summary>
    public static bool HayCable()
    {
        try { return IdDelCable() is not null; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? IdDelCable() => DispositivosAudio.BuscarCable(DispositivosAudio.Listar())?.Id;

    public void Iniciar(Action<byte[]> alTenerPaquete)
    {
        _alTenerPaquete = alTenerPaquete;
        // Un hilo MTA propio (los de .NET que se crean a mano lo son): la captura por proceso lo necesita.
        _hiloMirada = new Thread(BucleMirada) { IsBackground = true, Name = "Audio exclusivo: mirada" };
        _hiloReloj = new Thread(BucleReloj) { IsBackground = true, Name = "Audio exclusivo: mezcla", Priority = ThreadPriority.AboveNormal };
        _hiloMirada.Start();
        _hiloReloj.Start();
    }

    private void BucleMirada()
    {
        while (!_fin.IsCancellationRequested)
        {
            try { Sondear(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Registro.Error("audio exclusivo: error al mirar las ventanas", e);
            }
            _fin.Token.WaitHandle.WaitOne(Mirada);
        }
    }

    private void BucleReloj()
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        long siguiente = 0;
        while (!_fin.IsCancellationRequested)
        {
            siguiente += MsPorPaquete;
            var falta = siguiente - reloj.ElapsedMilliseconds;
            if (falta > 0) _fin.Token.WaitHandle.WaitOne((int)falta);
            else if (falta < -200) siguiente = reloj.ElapsedMilliseconds; // el hilo se atrasó mucho: no soltar una ráfaga
            if (_fin.IsCancellationRequested) break;
            var paquete = _mezclador.TomarPaquete();
            if (paquete is not null) _alTenerPaquete?.Invoke(paquete);
        }
    }

    /// <summary>
    /// Una mirada: qué programas tienen ventana en el monitor y qué hay que cambiar (agregar los que entran, devolver los
    /// que salen, abrir y cerrar capturas). Pública para las pruebas; la llama el hilo de la mirada cada segundo.
    /// </summary>
    internal void Sondear()
    {
        lock (_cerrojo)
        {
            if (_liberado) return;
            var ventanas = _ventanas.Listar();
            var seleccion = SeleccionAudio.Elegir(ventanas, _monitor);
            var diferencia = DiferenciaApps.Calcular(_enTele, seleccion.EnTele);
            // Solo se avisa de los que están sonando: uno en silencio tomará la salida nueva cuando suene, sin que nadie haga nada.
            // Se mide ahora, antes de que el resto de la mirada cambie rutas y capturas. Sin cambios no se gasta nada.
            var (entraronSonando, salieronSonando) = QuienSuena(diferencia);

            foreach (var exe in diferencia.Salen) Registro.Anotar($"audio exclusivo: {DiferenciaApps.Nombre(exe)} sale de la tele");
            foreach (var exe in diferencia.Entran) Registro.Anotar($"audio exclusivo: {DiferenciaApps.Nombre(exe)} pasa a la tele");

            _enTele = new HashSet<string>(seleccion.EnTele, StringComparer.OrdinalIgnoreCase);
            var cambioEnPc = !_tambienEnPc.SetEquals(seleccion.TambienEnPc);
            _tambienEnPc = new HashSet<string>(seleccion.TambienEnPc, StringComparer.OrdinalIgnoreCase);

            var procesos = _procesos.De([.. _enTele]);
            // Un PID que aceptó la lectura pero no la ruta no se vuelve a probar hasta que fallen todos (cada intento son ~2 s).
            _pidsSinRuta.IntersectWith(procesos.Select(p => p.Pid));
            var pidsConVentana = ventanas.Where(v => _enTele.Contains(v.Exe)).Select(v => v.Pid).ToHashSet();
            foreach (var exe in _enTele) AsegurarRuta(exe, procesos, pidsConVentana);
            AbrirYCerrarCapturas(procesos);
            // Lo que ya no está en la tele: devolver su salida (y reintentar lo que quedó pendiente de antes).
            // Un programa que sale pierde su marca: si vuelve, se fija otra vez.
            _exesFijados.RemoveWhere(exe => !_enTele.Contains(exe));
            // Los pendientes (programas cerrados) se reintentan cada 10 miradas, no en cada una.
            if ((diferencia.Salen.Count > 0 || _miradas++ % 10 == 0) && _guardadas.Todas().Any(r => !_enTele.Contains(r.Exe)))
                _guardadas.RestaurarTodo(_rutas, _procesos, _enTele);
            RevisarSalidaPorDefecto();

            if (entraronSonando.Count > 0) PasaronATele?.Invoke(entraronSonando);
            if (salieronSonando.Count > 0) VolvieronAlPc?.Invoke(salieronSonando);
            if (cambioEnPc) CambioTambienEnPc?.Invoke([.. _tambienEnPc]);
        }
    }

    /// <summary>
    /// De los programas que entran y de los que salen, los que tienen audio sonando ahora. Sin <see cref="IActividadAudio"/>
    /// (las pruebas antiguas) no se filtra nada.
    /// </summary>
    private (IReadOnlyList<string> Entraron, IReadOnlyList<string> Salieron) QuienSuena(DiferenciaApps diferencia)
    {
        if (_actividad is null) return (diferencia.Entran, diferencia.Salen);
        if (!diferencia.HayCambios) return ([], []);
        var sonando = _actividad.PidsSonando();
        return (ProgramasSonando.Filtrar(diferencia.Entran, _procesos.De([.. diferencia.Entran]), sonando),
                ProgramasSonando.Filtrar(diferencia.Salen, _procesos.De([.. diferencia.Salen]), sonando));
    }

    /// <summary>Anota lo que había y manda a todos los procesos del programa a VB-CABLE (los que ya estaban fijados no se tocan).</summary>
    private void AsegurarRuta(string exe, IReadOnlyList<ProcesoInfo> procesos, IReadOnlySet<int> pidsConVentana)
    {
        // Primero el proceso con la ventana en el monitor: otro del mismo programa puede no servir (medido en la prueba
        // real de H2: el Edge de fondo de Windows, sin ventana, responde E_INVALIDARG y el de la ventana sí la acepta).
        var propios = procesos.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => pidsConVentana.Contains(p.Pid) ? 0 : 1).ToList();
        if (propios.Count == 0) return; // aún no hay proceso (se está abriendo): la próxima mirada
        if (_exesFijados.Contains(exe)) return;
        // La preferencia es por ejecutable (medido en F4): con un proceso basta, y vale también para los que se abran
        // después. Se prueba con un solo proceso por mirada para no dejar la mirada bloqueada con los reintentos de Fijar.
        // Solo los que Windows acepta (los que ya tienen sesión de audio). Si ninguno, se espera: en cuanto el programa
        // suene, la próxima mirada lo fija (ese primer sonido sale por el PC: «pausa y vuelve a reproducir», H3).
        var p = propios.FirstOrDefault(x => !_pidsSinRuta.Contains(x.Pid) && _rutas.Acepta(x.Pid));
        if (p is null && propios.All(x => _pidsSinRuta.Contains(x.Pid)))
        {
            // Ya fallaron todos (p. ej. uno recién lanzado que aún no valía): otra vuelta desde el primero.
            foreach (var x in propios) _pidsSinRuta.Remove(x.Pid);
            p = propios.FirstOrDefault(x => _rutas.Acepta(x.Pid));
        }
        if (p is null) return;
        if (!_guardadas.Contiene(exe))
        {
            // El «antes» se lee una sola vez, antes de tocar nada, y se anota ANTES de cambiar (si el programa se cae a mitad,
            // se puede devolver). Del mismo proceso aceptado: uno sin sesión de audio leería "" aunque haya una ruta propia.
            try
            {
                var (mm, co) = _rutas.Leer(p.Pid);
                _guardadas.Agregar(new RutaGuardada(exe, mm, co));
            }
            catch (InvalidOperationException e)
            {
                Registro.Anotar($"audio exclusivo: no se pudo leer la salida de {exe}: {e.Message}");
                return;
            }
        }
        try
        {
            _rutas.Fijar(p.Pid, _idCable);
            _exesFijados.Add(exe);
        }
        catch (InvalidOperationException e)
        {
            _pidsSinRuta.Add(p.Pid);
            Registro.Anotar($"audio exclusivo: no se pudo mandar {DiferenciaApps.Nombre(exe)} (pid {p.Pid}) al cable: {e.Message}");
        }
    }

    /// <summary>Una captura por proceso raíz (el que no tiene padre del mismo programa): ya incluye a los hijos, como los de Chrome.</summary>
    private void AbrirYCerrarCapturas(IReadOnlyList<ProcesoInfo> procesos)
    {
        var deseados = new HashSet<int>();
        foreach (var exe in _enTele)
        {
            var propios = procesos.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
            var pids = propios.Select(p => p.Pid).ToHashSet();
            foreach (var p in propios.Where(p => !pids.Contains(p.PidPadre))) deseados.Add(p.Pid);
        }

        foreach (var (pid, captura) in _capturas.ToList())
        {
            if (deseados.Contains(pid) && captura.Viva) continue;
            _capturas.Remove(pid);
            _mezclador.Quitar(pid);
            captura.Dispose();
        }

        foreach (var pid in deseados.Where(pid => !_capturas.ContainsKey(pid)))
        {
            try
            {
                var id = pid;
                _capturas[pid] = _fabrica.Abrir(pid, muestras => _mezclador.Agregar(id, muestras));
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Registro.Anotar($"audio exclusivo: no se pudo capturar el pid {pid}: {e.Message}");
            }
        }
    }

    /// <summary>Con la política por app la salida por defecto no debería cambiar. Si cambiara, se deja constancia: no se toca.</summary>
    private void RevisarSalidaPorDefecto()
    {
        var ahora = _rutas.SalidaPorDefecto();
        if (!string.Equals(ahora, _defectoInicial, StringComparison.Ordinal))
            Registro.Anotar($"audio exclusivo: la salida por defecto de Windows cambió ({_defectoInicial} → {ahora}). Revisa Configuración → Sonido.");
    }

    private void AlTerminarElProceso(object? origen, EventArgs e) => Dispose();

    public void Dispose()
    {
        lock (_cerrojo)
        {
            if (_liberado) return;
            _liberado = true;
        }
        AppDomain.CurrentDomain.ProcessExit -= AlTerminarElProceso;
        _fin.Cancel();
        // No se espera al hilo que mira más de lo razonable: si está abriendo una captura puede tardar unos segundos.
        _hiloMirada?.Join(8000);
        _hiloReloj?.Join(1000);
        lock (_cerrojo)
        {
            foreach (var c in _capturas.Values) c.Dispose();
            _capturas.Clear();
            var r = _guardadas.RestaurarTodo(_rutas, _procesos);
            foreach (var exe in r.Pendientes)
                Registro.Anotar($"audio exclusivo: {exe} no está en marcha; su salida se devuelve la próxima vez (o con --restaurar-audio)");
            _enTele.Clear();
            _tambienEnPc.Clear();
        }
        _fin.Dispose();
    }

    /// <summary>Rescate (y arranque del programa): devuelve las rutas anotadas por una sesión que se cayó. Corre en un hilo MTA.</summary>
    internal static ResultadoRestauracion RestaurarPendientes()
    {
        var guardadas = new RutasPersistidas(RutasPersistidas.RutaPorDefecto);
        if (guardadas.Todas().Count == 0) return new ResultadoRestauracion([], []);
        var resultado = Task.Run(() => guardadas.RestaurarTodo(new RutasWindows(), new ProcesosWindows())).GetAwaiter().GetResult();
        foreach (var exe in resultado.Restauradas) Registro.Anotar($"audio exclusivo: devuelta la salida de {exe}");
        foreach (var exe in resultado.Pendientes) Registro.Anotar($"audio exclusivo: {exe} sigue anotado (no está en marcha)");
        return resultado;
    }
}
