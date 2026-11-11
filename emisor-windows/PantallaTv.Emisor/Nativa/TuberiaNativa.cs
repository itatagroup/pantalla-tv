using System.Diagnostics;
using SharpGen.Runtime;

namespace PantallaTv.Emisor.Nativa;

/// <param name="Dispositivo">Nombre de Windows del monitor (<c>\\.\DISPLAY2</c>); null = por <paramref name="Adaptador"/> y <paramref name="Salida"/>.</param>
/// <param name="Ancho">Tamaño del vídeo (el anunciado en el <c>HELLO</c>); si la captura cambia de tamaño, se encaja en este.</param>
/// <param name="Kbps">Tope de la tasa (VBR con tope: con la pantalla quieta gasta casi nada).</param>
/// <param name="LatidoFps">Tras un cambio, cuántas veces por segundo se recodifica el último cuadro durante <see cref="Ritmo.VentanaLatidoUs"/>; 0 = ninguna (solo el empujón).</param>
/// <param name="SegClave">Fotograma clave de seguridad cada tantos segundos; 0 = solo al empezar y a pedido.</param>
/// <param name="Cursor">Dibujar el puntero del ratón.</param>
internal sealed record OpcionesNativa(
    string? Dispositivo, int Adaptador, int Salida, int Ancho, int Alto, int Fps, int Kbps,
    int LatidoFps = TuberiaNativa.LatidoPorDefecto, int SegClave = 0, bool Cursor = true);

/// <summary>Lo ocurrido en la tubería nativa desde la última consulta (ver <see cref="TuberiaNativa.Tomar"/>).</summary>
/// <param name="Nuevos">Cuadros con la imagen cambiada.</param>
/// <param name="SoloRaton">Cuadros que solo movieron el puntero.</param>
/// <param name="Latidos">Recodificaciones del último cuadro con la pantalla quieta (o para dar una clave a pedido).</param>
/// <param name="Perdidos">Presentaciones de Windows que la captura juntó con otras (no llegó a tiempo).</param>
/// <param name="LatCapturaMs">Media de presentación en Windows → cuadro en manos del emisor.</param>
/// <param name="LatCodifMs">Media de cuadro capturado → unidad codificada (puntero, conversión y codificador).</param>
/// <param name="Reinicios">Duplicaciones o dispositivos que hubo que volver a crear.</param>
internal sealed record EstadisticasNativa(
    int Nuevos, int SoloRaton, int Latidos, int Perdidos, int Claves,
    double? LatCapturaMs, double? LatCodifMs, double? LatCodifMaxMs, int Reinicios)
{
    /// <summary>Columnas de la telemetría (<c>Telemetria</c>), en el orden de <see cref="Csv"/>.</summary>
    public const string CabeceraCsv =
        "nat_nuevos;nat_raton;nat_latidos;nat_perdidos;nat_claves;nat_lat_captura_ms;nat_lat_codif_ms;nat_lat_codif_max_ms;nat_reinicios";

    /// <summary>Los valores para esas columnas; vacíos si no hay (tubería de ffmpeg).</summary>
    public static string Csv(EstadisticasNativa? e)
    {
        if (e is null) return new string(';', 8);
        var c = System.Globalization.CultureInfo.InvariantCulture;
        static string Ms(double? v, IFormatProvider c) => v?.ToString("F2", c) ?? "";
        return string.Join(';', e.Nuevos.ToString(c), e.SoloRaton.ToString(c), e.Latidos.ToString(c), e.Perdidos.ToString(c),
            e.Claves.ToString(c), Ms(e.LatCapturaMs, c), Ms(e.LatCodifMs, c), Ms(e.LatCodifMaxMs, c), e.Reinicios.ToString(c));
    }
}

/// <summary>La tubería nativa no puede seguir en este PC (no hay codificador, falla una y otra vez): se pasa a la siguiente.</summary>
internal sealed class NativaNoDisponibleException(string mensaje, Exception? causa = null) : FuenteNoSirveException(mensaje, causa);

/// <summary>
/// La tubería «nativa» (captura propia, sin ffmpeg): duplicación de escritorio → NV12 en la GPU
/// con sombreadores → H.264 de Media Foundation en VBR con tope, todo dentro del proceso y sin ffmpeg.
/// <list type="bullet">
/// <item>Codifica <b>solo cuando algo cambia</b> (imagen o puntero), con un tope de <see cref="OpcionesNativa.Fps"/>. Tras
/// el último cambio recodifica el mismo cuadro unas veces («latido», ver <see cref="Ritmo"/>) y después no manda nada
/// hasta el siguiente cambio: con la pantalla quieta no hay tráfico de vídeo.</item>
/// <item>Fotograma clave al empezar y cuando se pide (<see cref="PedirClave"/>: la TV manda <c>PEDIR_CLAVE</c> o la cola de
/// envío se llenó). Sin claves periódicas salvo <see cref="OpcionesNativa.SegClave"/>.</item>
/// <item>Se recupera sola: si Windows quita la duplicación (cambio de resolución, pantalla de UAC, bloqueo) la vuelve a
/// crear sin cortar la transmisión (la TV se queda con la última imagen); si se pierde el dispositivo o falla el
/// codificador, rehace todo. Si eso falla seguido, lanza <see cref="NativaNoDisponibleException"/>.</item>
/// </list>
/// Corre en un hilo propio (<see cref="Ejecutar"/> bloquea hasta que se cancela; <see cref="EjecutarAsync"/> lo lanza).
/// </summary>
internal sealed class TuberiaNativa : IFuenteVideo
{
    public const int LatidoPorDefecto = 30;

    /// <summary>
    /// Solo para probar el respaldo a ffmpeg en un PC donde la nativa funciona: <c>inicio</c> la hace fallar antes del
    /// primer cuadro; <c>seguido</c>, cada 2 s después de empezar (como una gráfica que se pierde una y otra vez).
    /// </summary>
    internal const string VariableFallo = "PANTALLATV_FALLO_NATIVA";

    /// <summary>Si el monitor no aparece en este tiempo, se corta la transmisión (se desconectó).</summary>
    internal static readonly TimeSpan PlazoMonitor = TimeSpan.FromSeconds(15);
    /// <summary>Más reconstrucciones completas que esto en <see cref="VentanaReconstrucciones"/> y se da por perdida.</summary>
    internal const int ReconstruccionesMax = 3;
    internal static readonly TimeSpan VentanaReconstrucciones = TimeSpan.FromSeconds(60);
    /// <summary>Dos claves a pedido no se dan más seguido que esto (una tanda de pedidos cuesta una sola clave).</summary>
    internal const long ClaveMinimaUs = 200_000;

    // HRESULT con los que hay que esperar y volver a crear la duplicación (no son fallos del dispositivo).
    private const int AccesoPerdido = unchecked((int)0x887A0026);      // DXGI_ERROR_ACCESS_LOST
    private const int NoDisponibleAhora = unchecked((int)0x887A0022);  // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
    private const int SesionDesconectada = unchecked((int)0x887A0028); // DXGI_ERROR_SESSION_DISCONNECTED
    private const int AccesoDenegado = unchecked((int)0x80070005);     // E_ACCESSDENIED (escritorio seguro: UAC, bloqueo)
    private const int ArgumentoInvalido = unchecked((int)0x80070057);  // E_INVALIDARG (salida a medio cambiar de modo)
    private const int NoSoportado = unchecked((int)0x887A0004);        // DXGI_ERROR_UNSUPPORTED

    private readonly OpcionesNativa _op;
    private readonly Func<ulong> _ahoraUs;
    private readonly string? _falloProvocado = Environment.GetEnvironmentVariable(VariableFallo);
    private readonly TaskCompletionSource _lista = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<byte[]> _enviar = _ => { };
    private int _clavePedida;
    private long _unidades;
    private volatile bool _claveAPedido = true;
    private volatile string? _detalle;
    private volatile string? _retencion;

    // Estadísticas (bajo _cerrojoStats).
    private readonly object _cerrojoStats = new();
    private int _nuevos, _soloRaton, _latidos, _perdidos, _claves, _reinicios;
    private double _sumaCaptura, _sumaCodif, _maxCodif;
    private int _nCaptura, _nCodif;
    /// <summary>Tiempo (100 ns) de la última unidad que salió del codificador (ver <see cref="DetectorRetencion"/>).</summary>
    private long _ultimoSalido = -1;

    /// <param name="ahoraUs">El reloj de la transmisión (el de los <c>pts</c>).</param>
    public TuberiaNativa(OpcionesNativa op, Func<ulong> ahoraUs)
    {
        _op = op with
        {
            Fps = Math.Clamp(op.Fps, 1, 120),
            LatidoFps = Math.Clamp(op.LatidoFps, 0, Math.Clamp(op.Fps, 1, 120)),
            Ancho = Math.Max(2, op.Ancho & ~1),
            Alto = Math.Max(2, op.Alto & ~1),
        };
        _ahoraUs = ahoraUs;
    }

    public string Nombre => Tuberias.Nativa;

    /// <summary>El codificador elegido y si retiene un cuadro (lo que se ve en el registro y en <c>--diagnostico</c>).</summary>
    public string? Detalle => _detalle is { } d ? d + (_retencion is { } r ? " · " + r : "") : null;

    /// <summary>Se completa al encontrar el monitor (ver <see cref="IFuenteVideo.Lista"/>).</summary>
    public Task Lista => _lista.Task;

    /// <summary>Unidades entregadas desde que empezó.</summary>
    public long Unidades => Interlocked.Read(ref _unidades);

    /// <summary>Pide un fotograma clave lo antes posible. Se puede llamar desde cualquier hilo.</summary>
    public void PedirClave() => Volatile.Write(ref _clavePedida, 1);

    public Task EjecutarAsync(Action<byte[]> enviar, CancellationToken ct) =>
        Task.Factory.StartNew(() => Ejecutar(enviar, ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose() { } // lo suyo se libera al terminar Ejecutar

    /// <summary>Devuelve lo ocurrido desde la última llamada y reinicia los contadores.</summary>
    public EstadisticasNativa Tomar()
    {
        lock (_cerrojoStats)
        {
            var e = new EstadisticasNativa(_nuevos, _soloRaton, _latidos, _perdidos, _claves,
                _nCaptura > 0 ? _sumaCaptura / _nCaptura : null,
                _nCodif > 0 ? _sumaCodif / _nCodif : null,
                _nCodif > 0 ? _maxCodif : null, _reinicios);
            _nuevos = _soloRaton = _latidos = _perdidos = _claves = _reinicios = _nCaptura = _nCodif = 0;
            _sumaCaptura = _sumaCodif = _maxCodif = 0;
            return e;
        }
    }

    /// <summary>Captura y codifica hasta que se cancela. Bloquea: correrlo en un hilo propio.</summary>
    /// <param name="enviar">Cada unidad de acceso lista (SPS/PPS en cada clave), apenas sale. Se llama desde el hilo del codificador.</param>
    public void Ejecutar(Action<byte[]> enviar, CancellationToken ct)
    {
        _enviar = enviar;
        Gpu.ConscienteDeDpi();
        var reconstrucciones = new Queue<DateTime>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sesion(ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not (EmisionException or NativaNoDisponibleException or OperationCanceledException))
            {
                // Antes del primer cuadro no se insiste: lo más probable es que este PC no pueda (sin codificador
                // por hardware, DXGI sin duplicación). Que pruebe la tubería siguiente.
                if (Unidades == 0) throw new NativaNoDisponibleException(Explicar(e), e);
                var ahora = DateTime.UtcNow;
                while (reconstrucciones.Count > 0 && ahora - reconstrucciones.Peek() > VentanaReconstrucciones) reconstrucciones.Dequeue();
                reconstrucciones.Enqueue(ahora);
                if (reconstrucciones.Count > ReconstruccionesMax)
                    throw new NativaNoDisponibleException($"falló {reconstrucciones.Count} veces en {VentanaReconstrucciones.TotalSeconds:0} s: {Explicar(e)}", e);
                lock (_cerrojoStats) _reinicios++;
                Registro.Anotar($"nativa: se rehace todo tras «{Explicar(e)}»");
                ct.WaitHandle.WaitOne(300);
            }
        }
    }

    private static string Explicar(Exception e) =>
        e is SharpGenException s ? $"{s.Message.Trim()} (0x{s.HResult:X8})" : e.Message;

    /// <summary>Un dispositivo, un codificador y las duplicaciones que hagan falta. Sale por excepción o al cancelar.</summary>
    private void Sesion(CancellationToken ct)
    {
        var encontrado = EsperarMonitor(ct) ?? throw new OperationCanceledException(ct);
        _lista.TrySetResult();
        var (adaptador, salida) = encontrado;
        var luid = adaptador.Description1.Luid;
        using var _a = adaptador;
        using var _s = salida;
        if (_falloProvocado == "inicio") throw new InvalidOperationException($"fallo provocado ({VariableFallo}=inicio)");
        var (dev, ctx) = Gpu.Abrir(adaptador);
        using var _d = dev;
        using var _c = ctx;
        using var cursor = _op.Cursor ? new Puntero(dev, ctx) : null;
        using var conversor = new ConversorSombreador(dev, ctx, _op.Ancho, _op.Alto, MatrizColor.Bt601);
        var parametros = new ParametrosH264();
        var enVuelo = new EnVuelo();
        using var codificador = new CodificadorMf(dev, _op.Ancho, _op.Alto, _op.Fps, _op.Kbps, vbr: true,
            (unidad, tiempo) => AlSalir(parametros, enVuelo, unidad, tiempo), luid);
        _claveAPedido = codificador.ClaveAPedido;
        if (_detalle != codificador.Descripcion)
        {
            _detalle = codificador.Descripcion;
            Registro.Anotar($"nativa: {adaptador.Description1.Description.Trim()} · {codificador.Descripcion}");
        }
        var sesionDesde = (long)_ahoraUs();

        // Si se empieza con el escritorio seguro (UAC, bloqueo) o a medio cambio de modo, no es que este PC no
        // pueda: se sigue sin captura y el bucle la vuelve a intentar (Reabrir).
        CapturaEscritorio? captura;
        try { captura = new CapturaEscritorio(salida, dev, ctx, cursor); }
        catch (SharpGenException e) when (EsDeEsperar(e.HResult)) { captura = null; }

        var intervaloUs = 1_000_000L / _op.Fps;
        var minimoUs = Math.Max(0, intervaloUs - 2_000); // tolerancia: 60 Hz con algo de vaivén no debe perder cuadros
        var claveCadaUs = _op.SegClave > 0 ? _op.SegClave * 1_000_000L : Ritmo.Nunca;
        var ritmo = new Ritmo(intervaloUs, _op.LatidoFps, claveCadaUs);
        // Hasta saber si este codificador retiene un cuadro se empuja (lo seguro); después, solo si retiene.
        var retencion = new DetectorRetencion(intervaloUs * 3 / 4);
        if (_retencion is not null) ritmo.Empujon = _retencion == DetectorRetencion.Retiene;
        DestinoNv12? ultimo = null;
        long ultimoTiempo = 0;
        var pendiente = false;           // hay un cambio capturado sin codificar
        long pendienteAdquiridoUs = 0;
        DateTime? sinMonitorDesde = null;
        var siguienteIntento = 0L;

        void Codificar(DestinoNv12 d, bool real, long adquiridoUs)
        {
            var ahora = (long)_ahoraUs();
            var clave = ultimo is null || ClavePendiente(ritmo, ahora) || ahora - ritmo.ClaveUs >= claveCadaUs;
            if (clave) Volatile.Write(ref _clavePedida, 0);
            // El tiempo de la muestra solo identifica el cuadro al salir (y tiene que crecer siempre). El pts que va a
            // la TV es la hora de salida del codificador (ver AlSalir).
            var tiempo = Math.Max(ultimoTiempo + 1, ahora);
            ultimoTiempo = tiempo;
            enVuelo.Anotar(tiempo * 10, real ? adquiridoUs : -1);
            if (_retencion is null && retencion.AntesDeEntregar(ahora, Interlocked.Read(ref _ultimoSalido)) is { } retiene)
            {
                ritmo.Empujon = retiene;
                _retencion = retiene ? DetectorRetencion.Retiene : DetectorRetencion.NoRetiene;
                Registro.Anotar($"nativa: el codificador {_retencion}");
            }
            retencion.Entregado(ahora, tiempo * 10);
            codificador.Codificar(d, tiempo * 10, clave);
            ultimo = d;
            ritmo.Codificado(ahora, real, clave);
            if (!real) lock (_cerrojoStats) _latidos++;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ahora = (long)_ahoraUs();
                if (_falloProvocado == "seguido" && Unidades > 0 && ahora - sesionDesde > 2_000_000)
                    throw new InvalidOperationException($"fallo provocado ({VariableFallo}=seguido)");
                if (pendiente && captura is not null && ahora - ritmo.UltimaUs >= minimoUs)
                {
                    var d = codificador.SiguienteDestino();
                    conversor.Convertir(captura.Vista, captura.Ancho, captura.Alto, d);
                    Codificar(d, real: true, pendienteAdquiridoUs);
                    pendiente = false;
                    continue;
                }
                if (!pendiente && ultimo is { } u && (ClavePendiente(ritmo, ahora) || ahora >= ritmo.Proximo))
                {
                    Codificar(u, real: false, 0);
                    continue;
                }

                if (captura is null)
                {
                    if (ahora >= siguienteIntento)
                    {
                        captura = Reabrir(dev, ctx, cursor, luid, ref sinMonitorDesde);
                        siguienteIntento = ahora + 100_000;
                        if (captura is not null) lock (_cerrojoStats) _reinicios++;
                    }
                    if (captura is null) ct.WaitHandle.WaitOne(20);
                    continue;
                }

                var hasta = pendiente ? ritmo.UltimaUs + minimoUs - ahora : ultimo is null ? 100_000 : ritmo.Proximo - ahora;
                // Redondeado hacia arriba (con menos de 1 ms por delante, 0 haría girar el bucle en vacío) y como mucho
                // 10 ms: un PEDIR_CLAVE no puede despertar a AcquireNextFrame y no debe esperar más.
                var esperaMs = (int)Math.Clamp((hasta + 999) / 1000, 0, 10);
                CapturaEscritorio.Resultado r;
                try { r = captura.Esperar(esperaMs); }
                catch (SharpGenException e) when (EsDeEsperar(e.HResult))
                {
                    // Cambio de modo, UAC, bloqueo: hay que crear otra duplicación (lo de antes ya no vale).
                    captura.Dispose();
                    captura = null;
                    pendiente = false;
                    continue;
                }
                if (r == CapturaEscritorio.Resultado.NadaNuevo) continue;
                var adquirido = (long)_ahoraUs();
                lock (_cerrojoStats)
                {
                    if (r == CapturaEscritorio.Resultado.Nuevo)
                    {
                        _nuevos++;
                        _perdidos += captura.Acumulados - 1;
                        var lat = HaceUs(captura.CuandoQpc); // de la presentación en Windows hasta aquí
                        if (lat is >= 0 and < 1_000_000) { _sumaCaptura += lat / 1000.0; _nCaptura++; }
                    }
                    else _soloRaton++;
                }
                // Si ya había uno pendiente, este lo reemplaza (es más nuevo); la latencia se cuenta desde el primero.
                if (!pendiente) pendienteAdquiridoUs = adquirido;
                pendiente = true;
            }
        }
        finally
        {
            captura?.Dispose();
        }
    }

    /// <summary>¿Hay una clave pedida y ya pasó lo mínimo desde la anterior?</summary>
    private bool ClavePendiente(Ritmo ritmo, long ahoraUs) =>
        _claveAPedido && Volatile.Read(ref _clavePedida) == 1 && ahoraUs - ritmo.ClaveUs >= ClaveMinimaUs;

    /// <summary>Busca el monitor; si no está, reintenta hasta <see cref="PlazoMonitor"/>. null = cancelado.</summary>
    private (Vortice.DXGI.IDXGIAdapter1, Vortice.DXGI.IDXGIOutput)? EsperarMonitor(CancellationToken ct)
    {
        DateTime? desde = null;
        while (!ct.IsCancellationRequested)
        {
            if (BuscarMonitor(ref desde) is { } s) return s;
            ct.WaitHandle.WaitOne(250);
        }
        return null;
    }

    /// <summary>
    /// El monitor, o null si no está ahora. <paramref name="sinMonitorDesde"/> lleva desde cuándo falta: pasado
    /// <see cref="PlazoMonitor"/>, se da por desconectado y se corta la transmisión.
    /// </summary>
    private (Vortice.DXGI.IDXGIAdapter1, Vortice.DXGI.IDXGIOutput)? BuscarMonitor(ref DateTime? sinMonitorDesde)
    {
        if (Gpu.Buscar(_op.Dispositivo, _op.Adaptador, _op.Salida) is { } s)
        {
            sinMonitorDesde = null;
            return s;
        }
        sinMonitorDesde ??= DateTime.UtcNow;
        if (DateTime.UtcNow - sinMonitorDesde > PlazoMonitor)
            throw new EmisionException($"El monitor {_op.Dispositivo ?? "elegido"} ya no está conectado.", reintentable: true);
        return null;
    }

    /// <summary>
    /// Vuelve a crear la duplicación del mismo monitor. null si todavía no se puede (escritorio seguro, cambio de
    /// modo a medias): se reintenta. Si el monitor pasó a otro adaptador, hay que rehacer todo (lanza).
    /// </summary>
    private CapturaEscritorio? Reabrir(Vortice.Direct3D11.ID3D11Device dev, Vortice.Direct3D11.ID3D11DeviceContext ctx,
        Puntero? cursor, Vortice.Luid luid, ref DateTime? sinMonitorDesde)
    {
        if (BuscarMonitor(ref sinMonitorDesde) is not { } s) return null;
        var (adaptador, salida) = s;
        using (adaptador)
        using (salida)
        {
            var otro = adaptador.Description1.Luid;
            if (otro.LowPart != luid.LowPart || otro.HighPart != luid.HighPart)
                throw new InvalidOperationException("el monitor pasó a otro adaptador de vídeo");
            try { return new CapturaEscritorio(salida, dev, ctx, cursor); }
            catch (SharpGenException e) when (EsDeEsperar(e.HResult) || e.HResult == NoSoportado) { return null; }
        }
    }

    private static bool EsDeEsperar(int hr) =>
        hr is AccesoPerdido or NoDisponibleAhora or SesionDesconectada or AccesoDenegado or ArgumentoInvalido;

    /// <summary>Hilo del codificador: completa la unidad (SPS/PPS en cada clave), mide y la entrega.</summary>
    private void AlSalir(ParametrosH264 parametros, EnVuelo enVuelo, byte[] unidad, long tiempo100ns)
    {
        var (u, clave) = parametros.Procesar(unidad);
        Interlocked.Exchange(ref _ultimoSalido, tiempo100ns);
        var adquirido = enVuelo.Sacar(tiempo100ns);
        var ahora = (long)_ahoraUs();
        lock (_cerrojoStats)
        {
            if (clave) _claves++;
            if (adquirido >= 0)
            {
                var ms = (ahora - adquirido) / 1000.0;
                _sumaCodif += ms;
                _nCodif++;
                _maxCodif = Math.Max(_maxCodif, ms);
            }
        }
        Interlocked.Increment(ref _unidades);
        // Quien envía le pone pts = ahora, como con ffmpeg: el receptor 0.4.0 mide el atraso desde el pts y deja de
        // dibujar lo que pasa de 90 ms. Con la hora de captura (probado en N2) la captura y el codificador (~20 ms)
        // contaban como atraso y la TV dejó de dibujar 186 cuadros en 60 s. La latencia interna se mide aparte.
        _enviar(u);
    }

    /// <summary>Microsegundos desde una hora del contador de rendimiento (QPC, el reloj de DXGI).</summary>
    private static long HaceUs(long qpc) => (Stopwatch.GetTimestamp() - qpc) * 1_000_000L / Stopwatch.Frequency;

    /// <summary>Cuadros entregados al codificador y aún sin salir: de cada uno, cuándo se capturó (-1 = latido).</summary>
    private sealed class EnVuelo
    {
        private readonly (long Tiempo, long Adquirido)[] _ring = new (long, long)[16];
        private int _siguiente;

        public void Anotar(long tiempo100ns, long adquiridoUs)
        {
            lock (_ring)
            {
                _ring[_siguiente] = (tiempo100ns, adquiridoUs);
                _siguiente = (_siguiente + 1) % _ring.Length;
            }
        }

        public long Sacar(long tiempo100ns)
        {
            lock (_ring)
            {
                for (var i = 0; i < _ring.Length; i++)
                    if (_ring[i].Tiempo == tiempo100ns) return _ring[i].Adquirido;
                return -1;
            }
        }
    }
}

/// <summary>
/// Cuándo recodificar el último cuadro sin que haya cambiado nada. Recibe la hora (no la lee): se prueba sin GPU.
/// <list type="number">
/// <item><b>Empujón</b>: tras un cuadro con cambios (o una clave), uno más un poco después de cuando habría llegado el
/// siguiente (un cuadro y 3 ms). El codificador de AMD retiene un cuadro (ver <see cref="CodificadorMf"/>): sin esto, el
/// último cambio de la pantalla saldría recién con el próximo. Con movimiento continuo a 60 no se usa: lo empuja el
/// siguiente. Con contenido más lento (un vídeo a 24 o 30 fps) sí sale tras cada cuadro: se codifica y se manda el
/// doble, con la mitad repetidos (casi sin bits). Es el precio de no sumar un cuadro de latencia.</item>
/// <item><b>Latido</b>: durante <see cref="VentanaLatidoUs"/> tras el último cambio, <c>latidoFps</c> por segundo; deja que
/// el codificador afine la imagen quieta y que el decodificador de la TV suelte el suyo. Después, nada: medido en N2,
/// recodificar a 30 fps un escritorio con foto quieto gastaba 4 Mb/s fijos (repite el error de cuantización).</item>
/// <item>La clave de seguridad, si se pidió (<c>claveCadaUs</c>).</item>
/// </list>
/// </summary>
internal sealed class Ritmo(long intervaloUs, int latidoFps, long claveCadaUs)
{
    /// <summary>«Nunca», sin desbordar al sumarle una hora.</summary>
    public const long Nunca = long.MaxValue / 4;
    /// <summary>Cuánto dura el latido tras el último cambio.</summary>
    public const long VentanaLatidoUs = 500_000;

    private readonly long _empujonUs = intervaloUs + 3_000;

    /// <summary>false = el codificador no retiene cuadros (<see cref="DetectorRetencion"/>): no hace falta empujar.</summary>
    public bool Empujon { get; set; } = true;
    private readonly long _latidoUs = latidoFps > 0 ? 1_000_000L / latidoFps : Nunca;
    private long _cambioUs = -Nunca;
    private bool _empujar;

    /// <summary>Cuándo se codificó el último cuadro (µs).</summary>
    public long UltimaUs { get; private set; }
    /// <summary>Cuándo se codificó la última clave (µs); muy en el pasado si no hubo ninguna.</summary>
    public long ClaveUs { get; private set; } = -Nunca;

    /// <summary>Hora (µs) del próximo cuadro repetido; <see cref="Nunca"/> si no toca ninguno.</summary>
    public long Proximo
    {
        get
        {
            var repetir = _empujar && Empujon ? UltimaUs + _empujonUs
                : UltimaUs + _latidoUs - _cambioUs < VentanaLatidoUs ? UltimaUs + _latidoUs
                : Nunca;
            return Math.Min(repetir, claveCadaUs >= Nunca ? Nunca : ClaveUs + claveCadaUs);
        }
    }

    /// <summary>Se acaba de codificar un cuadro: <paramref name="real"/> si traía cambios.</summary>
    public void Codificado(long ahoraUs, bool real, bool clave)
    {
        UltimaUs = ahoraUs;
        // Una clave también se retiene en el codificador: se empuja como un cambio.
        _empujar = real || clave;
        if (real || clave) _cambioUs = ahoraUs;
        if (clave) ClaveUs = ahoraUs;
    }
}

/// <summary>
/// ¿El codificador retiene un cuadro? La MFT de AMD sí (la salida del cuadro N llega recién al entregarle el N+1, ver
/// <see cref="CodificadorMf"/>); NVIDIA o Intel quizá no, y entonces el empujón de <see cref="Ritmo"/> solo manda cuadros
/// repetidos. Se mira justo antes de entregar cada cuadro: si el anterior se entregó hace al menos <c>holguraUs</c>
/// (tiempo de sobra para codificarlo) y todavía no salió, lo retuvo. Decide por mayoría tras <see cref="Muestras"/> (un empate cuenta como «retiene»).
/// Recibe las horas (no las lee): se prueba sin GPU.
/// </summary>
internal sealed class DetectorRetencion(long holguraUs)
{
    public const int Muestras = 8;
    public const string Retiene = "retiene un cuadro (se empuja)";
    public const string NoRetiene = "no retiene cuadros (sin empujón)";

    private long _anteriorUs = -1, _anteriorTiempo = -1;
    private int _retuvo, _salio;

    /// <summary>Justo antes de entregar un cuadro. Devuelve la decisión cuando la hay (una sola vez); si no, null.</summary>
    /// <param name="ultimoSalido">Tiempo (100 ns) de la última unidad que salió; -1 si ninguna.</param>
    public bool? AntesDeEntregar(long ahoraUs, long ultimoSalido)
    {
        if (_anteriorTiempo < 0 || ahoraUs - _anteriorUs < holguraUs) return null;
        if (ultimoSalido >= _anteriorTiempo) _salio++; else _retuvo++;
        return _retuvo + _salio == Muestras ? _retuvo >= _salio : null; // en un empate, lo seguro: empujar
    }

    public void Entregado(long ahoraUs, long tiempo100ns)
    {
        _anteriorUs = ahoraUs;
        _anteriorTiempo = tiempo100ns;
    }
}
