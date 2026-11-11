using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace PantallaTv.Emisor;

internal enum Fase { Inactivo, Conectando, Transmitiendo, Reconectando, Error }

internal sealed record EstadoEmision(Fase Fase, string Detalle);

/// <param name="AudioPaqS">Paquetes de audio por segundo (50 cuando suena algo; 0 en silencio o sin audio).</param>
/// <param name="Nativa">Con la tubería nativa: lo que pasó dentro de la captura y el codificador.</param>
/// <param name="Tuberia">La forma de capturar que produjo estos números (una de <see cref="Tuberias.Todas"/>).</param>
internal sealed record Estadisticas(double Fps, double Mbps, int Tirados, double? RttMs, double AudioPaqS = 0,
    Nativa.EstadisticasNativa? Nativa = null, string Tuberia = "");

/// <param name="Tuberia">Fuerza una tubería de <see cref="Tuberias"/>; null = probarlas en orden.</param>
/// <param name="Monitor">Salida a capturar dentro del adaptador (<see cref="Emisor.Monitor.Salida"/>); 0 = la primera.</param>
/// <param name="Adaptador">Adaptador de vídeo del que cuelga el monitor (<see cref="Emisor.Monitor.Adaptador"/>); 0 = la gráfica principal.</param>
/// <param name="SegClave">Segundos entre fotogramas clave; 0 = el valor de <see cref="Tuberias.SegundosEntreClaves"/>.</param>
/// <param name="ExtraCodificador">Solo para experimentar: opciones de ffmpeg que se agregan después de las del codificador (las pisan).</param>
/// <param name="MonitorNombre">Nombre de Windows del monitor (<c>\\.\DISPLAY2</c>): la tubería nativa lo vuelve a buscar por nombre tras un cambio.</param>
/// <param name="LatidoFps">Tubería nativa: con la pantalla quieta, cuadros por segundo que se recodifican (ver <see cref="Nativa.TuberiaNativa"/>).</param>
/// <param name="Cursor">Tubería nativa: dibujar el puntero (ffmpeg lo dibuja siempre).</param>
internal sealed record OpcionesEmision(
    int Fps, int Kbps, int Ancho, int Alto, int RetardoMs, string? RutaFfmpeg, string? Tuberia = null,
    bool ConAudio = false, bool SilenciarPc = false, int SegClave = 0, string? ExtraCodificador = null,
    int Monitor = 0, int Adaptador = 0, string? MonitorNombre = null,
    int LatidoFps = Nativa.TuberiaNativa.LatidoPorDefecto, bool Cursor = true)
{
    public Nativa.OpcionesNativa ParaNativa() =>
        new(MonitorNombre, Adaptador, Monitor, Ancho, Alto, Fps, Kbps, LatidoFps, SegClave, Cursor);
}

/// <summary>Un fallo esperable con un mensaje que se le puede mostrar al usuario tal cual.</summary>
/// <param name="reintentable">
/// true si volver a intentarlo puede funcionar sin que el usuario haga nada (se cortó la red, la TV
/// cerró la app); false si hay que arreglar algo antes (falta ffmpeg, otra versión del protocolo).
/// </param>
internal sealed class EmisionException(string mensaje, bool reintentable = false) : Exception(mensaje)
{
    public bool Reintentable { get; } = reintentable;
}

/// <summary>
/// Una transmisión a un receptor: conecta, saluda, captura la pantalla con una de las <see cref="Tuberias"/> (una
/// <see cref="IFuenteVideo"/>: la nativa o ffmpeg) y manda los cuadros. Dura hasta que se cancela o algo falla.
/// </summary>
/// <summary>Lo común de <see cref="Emision"/> y <see cref="EmisionPersistente"/>: el mando las usa igual.</summary>
internal interface IEmision
{
    event Action<EstadoEmision>? CambioDeEstado;
    event Action<Estadisticas>? NuevasEstadisticas;
    Task EjecutarAsync(Receptor receptor, OpcionesEmision op, Telemetria? telemetria, CancellationToken ct);
}

internal sealed class Emision : IEmision
{
    /// <summary>
    /// Mensajes esperando salir. Si la red no da abasto se tiran cuadros en vez de acumular retraso.
    /// Sin retardo la cola es corta (~200 ms a 60 fps): lo que se acumula ahí es latencia pura. Con
    /// retardo a propósito el receptor ya tiene buffer, así que se tolera más (~2 s) antes de tirar.
    /// </summary>
    internal const int ColaBajaLatencia = 12;
    internal const int ColaConRetardo = 120;

    /// <summary>Cuándo falló por última vez cada tubería en este PC (ver <see cref="Tuberias.Orden"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> s_fallos = new();

    /// <summary>Si una tubería no entrega su primer cuadro en este tiempo, se prueba la siguiente.</summary>
    internal TimeSpan PlazoPrimerCuadro { get; init; } = TimeSpan.FromSeconds(8);

    private readonly Stopwatch _reloj = Stopwatch.StartNew();
    private Channel<byte[]> _cola = Channel.CreateBounded<byte[]>(ColaBajaLatencia);
    /// <summary>
    /// El audio va por su propia cola y sale antes que el vídeo: son paquetes chicos y un corte de
    /// sonido se nota mucho más que un cuadro perdido. Si se llena (~0,5 s) se descarta lo más viejo.
    /// </summary>
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(25) { FullMode = BoundedChannelFullMode.DropOldest });
    /// <summary>Cuenta los mensajes encolados (audio o vídeo) para despertar al que escribe en la red.</summary>
    private readonly SemaphoreSlim _hay = new(0);
    private Telemetria? _telemetria;
    private int _paquetesAudio;

    // Contadores que se leen y reinician cada 2 s.
    private int _cuadros;
    private long _bytes;
    private int _tirados;
    /// <summary>Último RTT en microsegundos; -1 = todavía no hay. Un long se lee y escribe de forma atómica.</summary>
    private long _rttUs = -1;

    private byte[]? _config;
    private volatile bool _esperandoClave;
    private volatile string _tuberia = "";
    /// <summary>La fuente en curso (atiende PEDIR_CLAVE y da sus estadísticas); null entre una y otra.</summary>
    private volatile IFuenteVideo? _fuente;

    /// <summary>Para las pruebas: crea la fuente de cada tubería (sin buscar ffmpeg).</summary>
    internal Func<string, IFuenteVideo>? Fuentes { get; init; }

    public event Action<EstadoEmision>? CambioDeEstado;
    public event Action<Estadisticas>? NuevasEstadisticas;

    private ulong AhoraUs => (ulong)(_reloj.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);

    public async Task EjecutarAsync(Receptor receptor, OpcionesEmision op, Telemetria? telemetria, CancellationToken ct)
    {
        _telemetria = telemetria;
        // Los valores vienen de la línea de comandos o de ajustes.json: se acotan a lo que cabe en el
        // HELLO (fps en 1 byte, retardo en 2) y a lo que el receptor acepta.
        op = op with
        {
            Fps = Math.Clamp(op.Fps, 1, 120),
            Kbps = Math.Clamp(op.Kbps, 500, 50_000),
            RetardoMs = Math.Clamp(op.RetardoMs, 0, Protocolo.RetardoMaxMs),
        };
        _cola = Channel.CreateBounded<byte[]>(op.RetardoMs > 0 ? ColaConRetardo : ColaBajaLatencia);
        Notificar(Fase.Conectando, $"Conectando con {receptor.Nombre}…");
        try
        {
            // Forzada puede ser una lista separada por comas (para probar el salto de una a otra). También se filtra por
            // monitor: «amf-gpu» elegida en la bandeja con el monitor virtual mostraría el principal.
            IReadOnlyList<string> candidatas = Tuberias.Candidatas(op.Tuberia is { } forzada
                ? forzada.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Tuberias.Orden(s_fallos, DateTime.UtcNow), op);
            if (candidatas.Count == 0)
                throw new EmisionException($"«{Tuberias.AmfGpu}» solo sirve para el monitor principal: elige otra forma de capturar.");
            // Antes de conectar: ffmpeg es opcional (solo lo usan las tuberías de respaldo). Sin él quedan las que no lo
            // necesitan; si no queda ninguna, no tiene sentido ocupar el receptor.
            string? rutaFfmpeg = null;
            if (Fuentes is null && candidatas.Any(c => c != Tuberias.Nativa))
            {
                try { rutaFfmpeg = Ffmpeg.Buscar(op.RutaFfmpeg); }
                catch (EmisionException) when (candidatas.Contains(Tuberias.Nativa)) { }
                if (rutaFfmpeg is null)
                {
                    candidatas = [.. candidatas.Where(c => c == Tuberias.Nativa)];
                    if (candidatas.Count == 0) throw new EmisionException(Ffmpeg.NoEsta);
                }
            }
            using var cliente = new TcpClient { NoDelay = true };
            await ConectarAsync(cliente, receptor, ct);
            var red = cliente.GetStream();
            using var audio = op.ConAudio ? AbrirAudio(op.SilenciarPc) : null;
            await SaludarAsync(red, op, audio?.Tasa ?? 0, audio?.Canales ?? 2, ct);

            using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tareas = new List<Task>
            {
                Task.Run(() => EscribirAsync(red, fin.Token), CancellationToken.None),
                Task.Run(() => LeerAsync(red, fin.Token), CancellationToken.None),
                Task.Run(() => PingsAsync(fin.Token), CancellationToken.None),
                Task.Run(() => EstadisticasAsync(fin.Token), CancellationToken.None),
                Task.Run(() => CapturarAsync(candidatas, rutaFfmpeg, op, receptor, fin.Token), CancellationToken.None),
            };

            audio?.Iniciar(EncolarAudio);

            try
            {
                var primera = await Task.WhenAny(tareas);
                await fin.CancelAsync();
                await primera; // propaga el error de quien terminó primero
            }
            finally
            {
                await fin.CancelAsync();
                try { await Task.WhenAll(tareas); } catch { /* ya se informó el primero */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // detenida por el usuario
        }
        catch (EmisionException e)
        {
            Notificar(Fase.Error, e.Message);
            throw;
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException)
        {
            var msg = "Se cortó la conexión con el receptor: " + e.Message;
            Notificar(Fase.Error, msg);
            throw new EmisionException(msg, reintentable: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Lo imprevisto también se le dice al usuario: quien llama solo espera EmisionException.
            var msg = $"Error inesperado al transmitir: {e.Message}";
            Notificar(Fase.Error, msg);
            throw new EmisionException(msg);
        }
        Notificar(Fase.Inactivo, "Detenida");
    }

    private static async Task ConectarAsync(TcpClient cliente, Receptor receptor, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await cliente.ConnectAsync(receptor.Direccion, receptor.Puerto, limite.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmisionException($"No hubo respuesta de {receptor}. ¿Está abierta la app «Pantalla» en la TV?", reintentable: true);
        }
        catch (SocketException e)
        {
            throw new EmisionException($"No se pudo conectar con {receptor}: {e.Message}", reintentable: true);
        }
    }

    /// <summary>Si no hay dispositivo de sonido (o falla), se transmite solo vídeo.</summary>
    private CapturaAudio? AbrirAudio(bool silenciarPc)
    {
        try
        {
            return new CapturaAudio(silenciarPc);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Notificar(Fase.Conectando, $"Sin sonido ({e.Message}); se transmite solo vídeo…");
            return null;
        }
    }

    /// <summary>Llamado desde el hilo de captura de audio. La marca de tiempo es el inicio del paquete.</summary>
    private void EncolarAudio(byte[] pcm)
    {
        var ahora = AhoraUs;
        var duracion = (ulong)CapturaAudio.MsPorPaquete * 1000;
        _audio.Writer.TryWrite(Protocolo.Armar(Protocolo.Audio, ahora > duracion ? ahora - duracion : 0, pcm));
        _hay.Release();
        Interlocked.Increment(ref _paquetesAudio);
    }

    private bool EncolarVideo(byte[] mensaje)
    {
        if (!_cola.Writer.TryWrite(mensaje)) return false;
        _hay.Release();
        return true;
    }

    /// <param name="tasaAudio">0 = sin audio.</param>
    private static async Task SaludarAsync(NetworkStream red, OpcionesEmision op, int tasaAudio, int canales, CancellationToken ct)
    {
        await red.WriteAsync(Protocolo.ArmarHello(op.Ancho, op.Alto, op.Fps, tasaAudio, canales, "0000", op.RetardoMs), ct);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(5));
        Mensaje? respuesta;
        try
        {
            respuesta = await Protocolo.LeerAsync(red, limite.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmisionException("El receptor no contestó al saludo.", reintentable: true);
        }

        if (respuesta is null) throw new EmisionException("El receptor cerró la conexión al saludar.", reintentable: true);
        if (respuesta.Tipo == Protocolo.HelloErr)
        {
            var motivo = System.Text.Encoding.UTF8.GetString(respuesta.Carga);
            // «ocupado» y «sin pantalla» pueden pasarse solos (termina el otro emisor, se vuelve a la app).
            throw motivo switch
            {
                "ocupado" => new EmisionException("La TV ya está recibiendo de otro emisor.", reintentable: true),
                "version" => new EmisionException("El receptor usa otra versión del protocolo: actualiza la app de la TV y este programa."),
                "sin pantalla" => new EmisionException("La app «Pantalla» no está a la vista en la TV.", reintentable: true),
                _ => new EmisionException($"El receptor rechazó la conexión: {motivo}"),
            };
        }
        if (respuesta.Tipo != Protocolo.HelloOk) throw new EmisionException("Respuesta inesperada del receptor.");
    }

    /// <summary>
    /// Prueba las tuberías en orden con <see cref="ProbarAsync"/>: la que no sirve en este PC se anota (castigo de
    /// <see cref="Tuberias.CastigoPorFallo"/>) y se pasa a la siguiente sin cortar la conexión.
    /// </summary>
    private async Task CapturarAsync(IReadOnlyList<string> candidatas, string? rutaFfmpeg, OpcionesEmision op, Receptor receptor, CancellationToken ct)
    {
        var fallos = new List<string>();
        for (var i = 0; i < candidatas.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var nombre = candidatas[i];
            using var fuente = CrearFuente(nombre, rutaFfmpeg, op);
            var motivo = await ProbarAsync(fuente, receptor, quedanOtras: i + 1 < candidatas.Count, ct);
            fallos.Add($"{nombre}: {motivo}");
            s_fallos[nombre] = DateTime.UtcNow;
            Registro.Anotar($"tubería {nombre} descartada: {motivo}");
        }
        if (rutaFfmpeg is null && Fuentes is null && op.Tuberia is null)
            fallos.Add("las de ffmpeg (respaldo): no está instalado");
        throw new EmisionException("Ninguna forma de capturar la pantalla funcionó en este PC. " + string.Join(" · ", fallos));
    }

    private IFuenteVideo CrearFuente(string nombre, string? rutaFfmpeg, OpcionesEmision op) =>
        Fuentes is { } crear ? crear(nombre)
        : nombre == Tuberias.Nativa ? new Nativa.TuberiaNativa(op.ParaNativa(), () => AhoraUs)
        : new FuenteFfmpeg(nombre, rutaFfmpeg!, op, _telemetria);

    /// <summary>
    /// Transmite con una fuente hasta que se cancela. Devuelve por qué no sirvió si hay que probar la siguiente: no
    /// entregó vídeo en <see cref="PlazoPrimerCuadro"/> (contado desde <see cref="IFuenteVideo.Lista"/>) o lanzó
    /// <see cref="FuenteNoSirveException"/>. Si ya transmitía y no quedan otras, lanza para reconectar.
    /// </summary>
    private async Task<string> ProbarAsync(IFuenteVideo fuente, Receptor receptor, bool quedanOtras, CancellationToken ct)
    {
        var nombre = fuente.Nombre;
        var primera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tuberia = nombre;
        _fuente = fuente;
        _esperandoClave = true; // si venía otra tubería, lo suyo no sirve hasta la primera clave de esta
        var tarea = Task.Run(() => fuente.EjecutarAsync(au =>
        {
            primera.TrySetResult();
            EnviarUnidad(au);
        }, fin.Token), CancellationToken.None);
        try
        {
            await Task.WhenAny(fuente.Lista, tarea);
            var plazo = Task.Delay(PlazoPrimerCuadro, fin.Token);
            var cual = await Task.WhenAny(primera.Task, tarea, plazo);
            ct.ThrowIfCancellationRequested();
            if (cual == plazo) return $"no entregó vídeo en {PlazoPrimerCuadro.TotalSeconds:0} s";
            if (cual == primera.Task)
            {
                s_fallos.TryRemove(nombre, out _);
                Registro.Anotar($"transmitiendo con {nombre}" + (fuente.Detalle is { } d ? $" ({d})" : ""));
                Notificar(Fase.Transmitiendo, $"Transmitiendo a {receptor.Nombre} ({nombre})");
            }
            await tarea; // hasta que se cancele o falle
            ct.ThrowIfCancellationRequested();
            return "terminó sin producir vídeo";
        }
        catch (FuenteNoSirveException e) when (!ct.IsCancellationRequested)
        {
            if (fuente.Unidades > 0 && !quedanOtras)
                throw new EmisionException($"La captura «{nombre}» dejó de funcionar: {e.Message}", reintentable: true);
            if (fuente.Unidades > 0)
                Notificar(Fase.Transmitiendo, $"La captura «{nombre}» falló ({e.Message}); se sigue con otra forma…");
            return e.Message;
        }
        finally
        {
            await fin.CancelAsync();
            _fuente = null;
            try { await tarea; } catch { /* ya se informó arriba o se canceló */ }
        }
    }

    /// <summary>
    /// Manda una unidad de acceso. El pts es la hora en que sale del codificador (con ffmpeg y con la nativa): el
    /// receptor mide el atraso desde ahí (ver <see cref="Nativa.TuberiaNativa"/>, AlSalir).
    /// </summary>
    private void EnviarUnidad(byte[] au)
    {
        var nals = AnnexB.Nals(au);
        var clave = false;
        AnnexB.Nal? sps = null, pps = null;
        foreach (var n in nals)
        {
            if (n.Tipo == AnnexB.CuadroClave) clave = true;
            else if (n.Tipo == AnnexB.Sps) sps ??= n;
            else if (n.Tipo == AnnexB.Pps) pps ??= n;
        }

        var pts = AhoraUs;
        if (sps is { } s && pps is { } p)
        {
            byte[] cfg = [.. au.AsSpan(s.Inicio, s.Fin - s.Inicio), .. au.AsSpan(p.Inicio, p.Fin - p.Inicio)];
            if ((_config is null || !cfg.AsSpan().SequenceEqual(_config))
                && EncolarVideo(Protocolo.Armar(Protocolo.VideoConfig, pts, cfg)))
                _config = cfg;
        }
        if (_config is null) return; // sin SPS/PPS no se puede decodificar

        if (_esperandoClave && !clave)
        {
            Interlocked.Increment(ref _tirados);
            return;
        }

        var carga = new byte[1 + au.Length];
        carga[0] = (byte)(clave ? 1 : 0);
        au.CopyTo(carga.AsSpan(1));
        if (EncolarVideo(Protocolo.Armar(Protocolo.Video, pts, carga)))
        {
            if (clave) _esperandoClave = false;
            Interlocked.Increment(ref _cuadros);
            Interlocked.Add(ref _bytes, carga.Length);
        }
        else
        {
            // La red no da abasto: se tira hasta el siguiente fotograma clave (mejor un salto que un retraso).
            // La tubería nativa lo da enseguida; con ffmpeg, se espera el periódico.
            _esperandoClave = true;
            Interlocked.Increment(ref _tirados);
            _fuente?.PedirClave();
        }
    }

    private async Task EscribirAsync(NetworkStream red, CancellationToken ct)
    {
        while (true)
        {
            await _hay.WaitAsync(ct);
            // Primero el audio. Puede no haber nada: la cola de audio descarta lo viejo sin avisar.
            if (_audio.Reader.TryRead(out var m) || _cola.Reader.TryRead(out m))
                await red.WriteAsync(m, ct);
        }
    }

    private async Task LeerAsync(NetworkStream red, CancellationToken ct)
    {
        while (true)
        {
            var m = await Protocolo.LeerAsync(red, ct)
                    ?? throw new EmisionException("El receptor cerró la conexión.", reintentable: true);
            switch (m.Tipo)
            {
                case Protocolo.Pong:
                    Interlocked.Exchange(ref _rttUs, (long)(AhoraUs - m.PtsUs));
                    break;
                case Protocolo.PedirClave:
                    // La nativa lo da al momento; ffmpeg no puede (manda uno cada Tuberias.SegundosEntreClaves).
                    _fuente?.PedirClave();
                    break;
            }
        }
    }

    private async Task PingsAsync(CancellationToken ct)
    {
        var relleno = new byte[8];
        while (true)
        {
            await Task.Delay(2000, ct);
            BinaryPrimitives.WriteUInt64BigEndian(relleno, AhoraUs);
            await _cola.Writer.WriteAsync(Protocolo.Armar(Protocolo.Ping, AhoraUs, relleno), ct);
            _hay.Release();
        }
    }

    private async Task EstadisticasAsync(CancellationToken ct)
    {
        var ultimo = _reloj.Elapsed;
        while (true)
        {
            await Task.Delay(2000, ct);
            // Se divide por el tiempo real transcurrido, no por 2 s fijos: el temporizador no es exacto.
            var ahora = _reloj.Elapsed;
            var segs = Math.Max(0.001, (ahora - ultimo).TotalSeconds);
            ultimo = ahora;
            var e = new Estadisticas(
                Interlocked.Exchange(ref _cuadros, 0) / segs,
                Interlocked.Exchange(ref _bytes, 0) * 8 / segs / 1e6,
                Interlocked.Exchange(ref _tirados, 0),
                Interlocked.Read(ref _rttUs) is var rtt and >= 0 ? rtt / 1000.0 : null,
                Interlocked.Exchange(ref _paquetesAudio, 0) / segs,
                _fuente?.Tomar(),
                _tuberia);
            _telemetria?.Anotar(e, _cola.Reader.Count, _tuberia);
            NuevasEstadisticas?.Invoke(e);
        }
    }

    private void Notificar(Fase fase, string detalle) => CambioDeEstado?.Invoke(new EstadoEmision(fase, detalle));
}
