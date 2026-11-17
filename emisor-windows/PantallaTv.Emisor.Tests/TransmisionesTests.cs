using System.Net;

namespace PantallaTv.Emisor.Tests;

/// <summary>Una emisión que no toca la red ni la pantalla: espera a que la cancelen o la terminen y deja ver con qué opciones se la llamó.</summary>
internal sealed class EmisionFalsa : IEmision
{
    private readonly TaskCompletionSource _fin = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _empezo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<EstadoEmision>? CambioDeEstado;
    public event Action<Estadisticas>? NuevasEstadisticas;
    public event Action<CambioAudio>? CambioDeAudio;

    /// <summary>Lo que se le pidió mandar a la tele (mensajes AVISO).</summary>
    public List<string> AvisosATele { get; } = [];
    public void AvisarATele(string texto) => AvisosATele.Add(texto);
    public void Audio(TipoCambioAudio tipo, params string[] exes) => CambioDeAudio?.Invoke(new CambioAudio(tipo, exes));

    public OpcionesEmision? Opciones { get; private set; }
    public Task Empezo => _empezo.Task;
    public bool Cancelada { get; private set; }

    public async Task EjecutarAsync(Receptor receptor, OpcionesEmision op, Telemetria? telemetria, CancellationToken ct)
    {
        Opciones = op;
        _empezo.TrySetResult();
        using var _ = ct.Register(() => { Cancelada = true; _fin.TrySetResult(); });
        await _fin.Task;
    }

    public void Estado(Fase fase, string detalle) => CambioDeEstado?.Invoke(new EstadoEmision(fase, detalle));
    public void Numeros(Estadisticas e) => NuevasEstadisticas?.Invoke(e);
    public void Terminar() => _fin.TrySetResult();
}

/// <summary>Hasta 4 transmisiones a la vez (F3b): reglas de TV, pantalla, sonido y detención, con emisiones y pantallas falsas.</summary>
public sealed class TransmisionesTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-transmisiones-").FullName;
    private readonly Queue<EmisionFalsa> _falsas = new();
    private readonly List<string> _avisos = [];
    private readonly List<string?> _pantallasPreparadas = [];

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private static Receptor Tv(string nombre, int n) => new(nombre, IPAddress.Parse($"10.0.0.{n}"), Protocolo.PuertoTcp);

    private static readonly Receptor Sala = Tv("Sala", 20), Pieza = Tv("Pieza", 30), Cocina = Tv("Cocina", 40), Estudio = Tv("Estudio", 50), Patio = Tv("Patio", 60);

    private Mando Nuevo(bool conAudio = false, Receptor[]? tvs = null)
    {
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta, "ajustes.json"));
        ajustes.ConAudio = conAudio; // las emisiones son falsas: no se captura ni se silencia nada
        ajustes.Reconectar = false;
        var vistas = tvs ?? [Sala, Pieza, Cocina, Estudio, Patio];
        var mando = new Mando(ajustes, new ContextoInmediato(), () => vistas)
        {
            FabricaEmision = () => { var e = new EmisionFalsa(); _falsas.Enqueue(e); return e; },
            Preparador = (nombre, _, _) =>
            {
                _pantallasPreparadas.Add(nombre);
                return new Monitores.Preparado(new Monitor(0, nombre ?? @"\\.\DISPLAYPRINCIPAL", 1920, 1080, true, 0, 0, "AMD"), () => { });
            },
        };
        mando.Aviso += (t, _) => _avisos.Add(t);
        return mando;
    }

    /// <summary>Inicia una transmisión y espera a que su emisión falsa arranque; devuelve esa emisión.</summary>
    private async Task<EmisionFalsa> Iniciar(Mando mando, Receptor tv, string? pantalla)
    {
        Assert.True(mando.Iniciar(tv, pantalla), string.Join(" | ", _avisos));
        var emision = _falsas.ToArray()[^1];
        await emision.Empezo.WaitAsync(TimeSpan.FromSeconds(10));
        return emision;
    }

    private static async Task Esperar(Func<bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicion())
        {
            Assert.True(DateTime.UtcNow < limite, "no pasó: " + que);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Una_TV_solo_puede_estar_en_una_transmision()
    {
        using var mando = Nuevo();
        await Iniciar(mando, Sala, "virtual");

        Assert.False(mando.Iniciar(Sala, "virtual2"));

        Assert.Single(mando.Transmisiones);
        Assert.Contains("Sala (10.0.0.20) ya está recibiendo una transmisión. Elige otra TV o detén esa.", _avisos);
        mando.Detener();
    }

    [Fact]
    public async Task Una_pantalla_solo_puede_estar_en_una_transmision()
    {
        using var mando = Nuevo();
        await Iniciar(mando, Sala, "virtual2");

        Assert.False(mando.Iniciar(Pieza, "virtual2"));
        Assert.False(mando.Iniciar(Pieza, "VIRTUAL2")); // sin distinguir mayúsculas

        Assert.Single(mando.Transmisiones);
        Assert.Contains("Esa pantalla ya se transmite a Sala (10.0.0.20). Elige otra pantalla o detén esa transmisión.", _avisos);
        // Otra pantalla sí.
        await Iniciar(mando, Pieza, "virtual");
        Assert.Equal(2, mando.Transmisiones.Count);
        mando.Detener();
    }

    [Fact]
    public async Task Sin_TV_libre_a_la_vista_se_puede_sumar_otra_por_IP()
    {
        using var mando = Nuevo(tvs: [Sala]);
        await Iniciar(mando, Sala, "virtual");
        Assert.Empty(mando.DestinosLibres());

        Assert.Null(mando.MotivoNoOtra()); // la ventana y la bandeja dejan «Escribir IP…»
        Assert.True(mando.IniciarConIp("10.0.0.30:5602"));
        Assert.Equal(2, mando.Transmisiones.Count);
        mando.Detener();
    }

    [Fact]
    public async Task Hay_un_tope_de_cuatro_transmisiones()
    {
        using var mando = Nuevo();
        await Iniciar(mando, Sala, "virtual");
        await Iniciar(mando, Pieza, "virtual2");
        await Iniciar(mando, Cocina, "virtual3");
        await Iniciar(mando, Estudio, "virtual4");
        Assert.Equal(Mando.MaxTransmisiones, mando.Transmisiones.Count);

        Assert.False(mando.Iniciar(Patio, @"\\.\DISPLAY1"));

        Assert.Equal(4, mando.Transmisiones.Count);
        Assert.Contains("Ya hay 4 transmisiones a la vez, que es el máximo. Detén una para empezar otra.", _avisos);
        Assert.NotNull(mando.MotivoNoOtra());
        Assert.Equal("4 transmisiones", mando.Estado.Split(',')[0]);

        // Al detener una vuelve a haber lugar.
        mando.Detener(mando.Transmisiones[0]);
        await Esperar(() => mando.Transmisiones.Count == 3, "se fue una");
        await Iniciar(mando, Patio, "virtual");
        Assert.Equal(4, mando.Transmisiones.Count);
        mando.Detener();
    }

    [Fact]
    public async Task Solo_la_primera_transmision_lleva_sonido()
    {
        using var mando = Nuevo(conAudio: true);
        mando.Cambiar(a => a.SilenciarPc = true);
        var primera = await Iniciar(mando, Sala, "virtual");
        var segunda = await Iniciar(mando, Pieza, "virtual2");
        var tercera = await Iniciar(mando, Cocina, "virtual3");

        Assert.True(primera.Opciones!.ConAudio);
        Assert.True(primera.Opciones.SilenciarPc);
        Assert.False(segunda.Opciones!.ConAudio);
        Assert.False(segunda.Opciones.SilenciarPc); // solo la del sonido silencia el PC
        Assert.False(tercera.Opciones!.ConAudio);
        Assert.Equal("con sonido", mando.NotaFila(mando.Transmisiones[0]));
        Assert.Equal("sin sonido (solo una transmisión lleva sonido)", mando.NotaFila(mando.Transmisiones[1]));

        // Se detiene la que llevaba el sonido: las otras siguen sin él y una nueva tampoco lo recibe.
        mando.Detener(mando.Transmisiones[0]);
        await Esperar(() => mando.Transmisiones.Count == 2, "se fue la primera");
        var nueva = await Iniciar(mando, Estudio, "virtual");
        Assert.False(nueva.Opciones!.ConAudio);
        Assert.False(segunda.Opciones.ConAudio);

        // Sin ninguna en curso, la que empieza es otra vez la primera.
        mando.Detener();
        await Esperar(() => mando.Transmisiones.Count == 0, "terminaron todas");
        var sola = await Iniciar(mando, Sala, "virtual");
        Assert.True(sola.Opciones!.ConAudio);
        mando.Detener();
    }

    [Fact]
    public async Task Sin_la_opcion_de_sonido_ninguna_lo_lleva_ni_lo_dice()
    {
        using var mando = Nuevo(conAudio: false);
        var primera = await Iniciar(mando, Sala, "virtual");
        await Iniciar(mando, Pieza, "virtual2");

        Assert.False(primera.Opciones!.ConAudio);
        Assert.Equal("", mando.NotaFila(mando.Transmisiones[1]));
        mando.Detener();
    }

    [Fact]
    public async Task Detener_una_no_detiene_las_otras()
    {
        using var mando = Nuevo();
        var a = await Iniciar(mando, Sala, "virtual");
        var b = await Iniciar(mando, Pieza, "virtual2");

        mando.Detener(mando.Transmisiones[0]);

        await Esperar(() => mando.Transmisiones.Count == 1, "terminó la primera");
        Assert.True(a.Cancelada);
        Assert.False(b.Cancelada);
        Assert.True(mando.Transmitiendo);
        Assert.False(mando.PuedeCambiarAjustes); // sigue habiendo una
        Assert.Equal(Pieza, mando.Transmisiones[0].Receptor);
        mando.Detener();
    }

    [Fact]
    public async Task Detener_sin_argumento_las_detiene_todas()
    {
        using var mando = Nuevo();
        var emisiones = new[] { await Iniciar(mando, Sala, "virtual"), await Iniciar(mando, Pieza, "virtual2"), await Iniciar(mando, Cocina, "virtual3") };
        Assert.True(mando.Transmitiendo);

        mando.Detener();

        await Esperar(() => !mando.Transmitiendo && mando.Transmisiones.Count == 0, "terminaron todas");
        Assert.All(emisiones, e => Assert.True(e.Cancelada));
        Assert.True(mando.PuedeCambiarAjustes);
        Assert.Equal(Fase.Inactivo, mando.Fase);
        Assert.Equal(Mando.SinTransmitir, mando.Estado);
        Assert.Null(mando.EnVivo);
    }

    [Fact]
    public async Task Si_una_falla_las_otras_siguen_y_el_aviso_dice_a_cual_TV_se_refiere()
    {
        using var mando = Nuevo();
        var sala = await Iniciar(mando, Sala, "virtual");
        var pieza = await Iniciar(mando, Pieza, "virtual2");

        pieza.Estado(Fase.Error, "La TV usa otra versión del protocolo. Actualiza la app «Pantalla» de la TV.");
        pieza.Terminar();

        // La que falló se queda en su fila con el motivo (con otras en curso, si no, se borraría al instante) y se puede quitar.
        await Esperar(() => mando.Transmisiones.Count(t => t.EnCurso) == 1, "terminó la que falló");
        Assert.Equal("Pieza (10.0.0.30): La TV usa otra versión del protocolo. Actualiza la app «Pantalla» de la TV.", _avisos[^1]);
        Assert.False(sala.Cancelada);
        Assert.True(mando.Transmitiendo);
        var fallida = Assert.Single(mando.Transmisiones, t => !t.EnCurso);
        Assert.Equal(Fase.Error, fallida.Fase);
        Assert.Contains("otra versión del protocolo", Mando.EstadoFila(fallida));
        Assert.Equal("Quitar", Mando.TextoBotonFila(fallida));
        Assert.Equal(2, mando.Transmisiones.Count);
        mando.Detener(fallida); // «Quitar»
        Assert.Equal(Sala, Assert.Single(mando.Transmisiones).Receptor);
        mando.Detener();
    }

    [Fact]
    public async Task La_fila_con_error_deja_su_lugar_al_volver_a_empezar_a_esa_TV()
    {
        using var mando = Nuevo();
        await Iniciar(mando, Sala, "virtual");
        var pieza = await Iniciar(mando, Pieza, "virtual2");
        pieza.Estado(Fase.Error, "Se cortó la conexión con el receptor.");
        pieza.Terminar();
        await Esperar(() => mando.Transmisiones.Count(t => t.EnCurso) == 1, "terminó la que falló");

        await Iniciar(mando, Pieza, "virtual2"); // la misma TV y pantalla: no se rechaza y la fila vieja sale
        Assert.Equal(2, mando.Transmisiones.Count);
        Assert.All(mando.Transmisiones, t => Assert.True(t.EnCurso));
        mando.Detener();
    }

    [Fact]
    public async Task Cada_aviso_de_una_transmision_empieza_por_su_TV()
    {
        using var mando = Nuevo();
        var sala = await Iniciar(mando, Sala, "virtual");
        await Iniciar(mando, Pieza, "virtual2");

        sala.Estado(Fase.Transmitiendo, "Transmitiendo a Sala");
        sala.Estado(Fase.Reconectando, "Reconectando…");

        Assert.Equal("Sala (10.0.0.20): Se cortó la transmisión. Reintentando…", _avisos[^1]);
        Assert.True(Mando.ParaGlobo(_avisos[^1]).Length <= Mando.LargoGlobo);
        mando.Detener();
    }

    [Fact]
    public async Task El_estado_junta_a_todas_y_cada_fila_dice_el_suyo()
    {
        using var mando = Nuevo();
        var sala = await Iniciar(mando, Sala, "virtual");
        var pieza = await Iniciar(mando, Pieza, "virtual2");

        Assert.Equal(Fase.Conectando, mando.Fase);
        sala.Estado(Fase.Transmitiendo, "Transmitiendo a Sala (nativa)");
        Assert.Equal(Fase.Transmitiendo, mando.Fase);
        sala.Numeros(new Estadisticas(59.6, 4.25, 0, 3, Tuberia: Tuberias.Nativa));
        var fila = mando.Transmisiones[0];
        Assert.StartsWith("Transmitiendo · 60 fps · 4,3 Mb/s", Mando.EstadoFila(fila));
        Assert.StartsWith("Conectando", Mando.EstadoFila(mando.Transmisiones[1]));
        Assert.StartsWith("Sala (10.0.0.20) ← ", Mando.TituloFila(fila));

        pieza.Estado(Fase.Reconectando, "Reconectando… (se cortó)");
        Assert.Equal(Fase.Reconectando, mando.Fase); // la más urgente
        Assert.Equal("2 transmisiones, 1 reconectando", mando.Estado);
        Assert.Equal("Reconectando… (se cortó)", Mando.EstadoFila(mando.Transmisiones[1]));
        mando.Detener();
    }

    [Fact]
    public async Task Con_una_sola_todo_sigue_como_antes()
    {
        using var mando = Nuevo();
        var avisos = _avisos;
        var emision = await Iniciar(mando, Sala, null);

        Assert.True(mando.Transmitiendo);
        Assert.False(mando.PuedeCambiarAjustes);
        Assert.NotNull(mando.EnVivo);
        Assert.Equal("Sala (10.0.0.20)", mando.EnVivo.Tv);
        Assert.Equal(Fase.Conectando, mando.Fase);
        Assert.Equal("Conectando con Sala…", mando.Estado);
        Assert.Equal("10.0.0.20", Ajustes.Cargar(mando.Ajustes.Ruta).UltimaIp);

        // Los números solo cuentan transmitiendo.
        emision.Numeros(new Estadisticas(60, 4, 0, 3));
        Assert.Null(mando.EnVivo.Ultimas);
        emision.Estado(Fase.Transmitiendo, "Transmitiendo a Sala (nativa)");
        var desde = mando.EnVivo.Desde;
        Assert.NotNull(desde);
        emision.Numeros(new Estadisticas(59.6, 4.25, 0, 3, Tuberia: Tuberias.Nativa));
        Assert.Equal("60", Mando.TextoFps(mando.EnVivo.Ultimas));
        Assert.Equal("4,3", Mando.TextoMbps(mando.EnVivo.Ultimas));
        Assert.Equal("Propia, sin ffmpeg (nativa)", Mando.TextoForma(mando.EnVivo.Ultimas));

        // Se corta: los números viejos se borran, el tiempo sigue contando desde la primera vez y se avisa una sola vez.
        emision.Estado(Fase.Reconectando, "Reconectando… (se cortó)");
        emision.Estado(Fase.Reconectando, "Reconectando… (otra vez)");
        Assert.Null(mando.EnVivo.Ultimas);
        Assert.Single(avisos, a => a.Contains("Se cortó la transmisión"));
        emision.Estado(Fase.Transmitiendo, "Transmitiendo a Sala (nativa)");
        Assert.Equal(desde, mando.EnVivo.Desde);

        // Terminó bien: vuelve a «Sin transmitir».
        mando.Detener();
        await Esperar(() => mando.Transmisiones.Count == 0, "terminó");
        Assert.Null(mando.EnVivo);
        Assert.Equal(Fase.Inactivo, mando.Fase);
        Assert.Equal(Mando.SinTransmitir, mando.Estado);

        // Terminó con error: el mensaje (con su TV) se queda a la vista.
        var otra = await Iniciar(mando, Sala, null);
        Assert.Equal(Fase.Conectando, mando.Fase); // el error de antes ya no está
        otra.Estado(Fase.Error, "La TV usa otra versión.");
        otra.Terminar();
        await Esperar(() => mando.Transmisiones.Count == 0, "terminó con error");
        Assert.Equal(Fase.Error, mando.Fase);
        Assert.Equal("Sala (10.0.0.20): La TV usa otra versión.", mando.Estado);
        Assert.Equal("Sala (10.0.0.20): La TV usa otra versión.", avisos[^1]);
    }

    [Fact]
    public async Task Iniciar_con_la_pantalla_de_los_ajustes_y_sin_pantallas_listadas_respeta_el_ajuste()
    {
        using var mando = Nuevo();
        mando.Cambiar(a => a.MonitorNombre = "virtual3");

        await Iniciar(mando, Sala, mando.Ajustes.MonitorNombre);
        Assert.Equal("virtual3", Assert.Single(_pantallasPreparadas));
        // La misma pantalla por defecto (Iniciar con una sola TV y sin elegir otra) se rechaza: ya está en uso.
        Assert.False(mando.Iniciar(Pieza));
        mando.Detener();
    }

    [Fact]
    public async Task Elegir_pantalla_se_puede_mientras_se_transmite_pero_los_ajustes_siguen_bloqueados()
    {
        using var mando = Nuevo();
        await Iniciar(mando, Sala, "virtual");

        mando.ElegirPantalla("virtual2");
        mando.Cambiar(a => a.Fps = 30);

        Assert.Equal("virtual2", mando.Ajustes.MonitorNombre);
        Assert.NotEqual(30, mando.Ajustes.Fps);
        mando.Detener();
    }

    // ── La lista de pantallas ────────────────────────────────────────────────────────────────────

    private static Monitor M(int i, string nombre, bool principal = false, string descripcion = "") =>
        new(i, nombre, 1920, 1080, principal, 0, i, "AMD", descripcion);

    [Fact]
    public void Solo_se_ofrece_el_monitor_virtual_1_y_sus_salidas_escondidas_no_salen_como_pantallas_reales()
    {
        // Windows lista las cuatro salidas del controlador, conectadas o no: ninguna es una pantalla «real». Mientras dos teles
        // a la vez estén en pausa se ofrece solo el virtual 1 (Mando.VirtualesALaVista).
        var todos = new[]
        {
            M(0, @"\\.\DISPLAY1", principal: true), M(1, @"\\.\DISPLAY2"),
            M(2, @"\\.\DISPLAY6", descripcion: "VDD by MTT"), M(3, @"\\.\DISPLAY7", descripcion: "VDD by MTT"),
            M(4, @"\\.\DISPLAY8", descripcion: "VDD by MTT"), M(5, @"\\.\DISPLAY9", descripcion: "VDD by MTT"),
        };
        (int, string)[] virtuales = [(1, @"\\.\DISPLAY6"), (2, @"\\.\DISPLAY7"), (3, @"\\.\DISPLAY8"), (4, @"\\.\DISPLAY9")];

        var p = Mando.ArmarPantallas(todos, virtuales, null);

        Assert.Equal(1, Mando.VirtualesALaVista);
        Assert.True(p.HayVirtual);
        Assert.Equal(new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2", "virtual" }, p.Opciones.Select(o => o.Valor));
        Assert.StartsWith("Monitor virtual (", p.Opciones[2].Texto);
        Assert.DoesNotContain(p.Opciones, o => o.Valor is { } v && virtuales.Any(x => x.Item2 == v)); // ni su nombre de Windows
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(p.Opciones, o => o.Elegida).Valor);

        // Guardado el virtual 3 (de antes): se propone el 1.
        Assert.Equal("virtual", Assert.Single(Mando.ArmarPantallas(todos, virtuales, "virtual3").Opciones, o => o.Elegida).Valor);
    }

    [Fact]
    public void Las_pantallas_en_uso_se_ven_pero_no_se_pueden_elegir_y_se_propone_una_libre()
    {
        var todos = new[] { M(0, @"\\.\DISPLAY1", principal: true), M(1, @"\\.\DISPLAY6", descripcion: "VDD by MTT"), M(2, @"\\.\DISPLAY7", descripcion: "VDD by MTT") };
        (int, string)[] virtuales = [(1, @"\\.\DISPLAY6"), (2, @"\\.\DISPLAY7")];
        var ocupadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "virtual" };

        var p = Mando.ArmarPantallas(todos, virtuales, "virtual", ocupadas);

        var enUso = p.Opciones.Single(o => o.Valor == "virtual");
        Assert.False(enUso.Disponible);
        Assert.False(enUso.Elegida);
        Assert.EndsWith(Mando.TextoEnUso, enUso.Texto);
        Assert.All(p.Opciones.Where(o => o.Valor != "virtual"), o => Assert.True(o.Disponible));
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(p.Opciones, o => o.Elegida).Valor); // la primera libre

        // Con todas en uso no hay nada que proponer.
        var todasOcupadas = Mando.ArmarPantallas(todos, virtuales, null, new HashSet<string>(["virtual", "virtual2", @"\\.\DISPLAY1"], StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(todasOcupadas.Opciones, o => o.Disponible);
    }
}
