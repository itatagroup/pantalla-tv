using System.Buffers.Binary;
using System.Drawing;
using System.Net;
using System.Text;

namespace PantallaTv.Emisor.Tests;

/// <summary>El mensaje AVISO (0x50): armado y recorte a 500 bytes sin partir un carácter.</summary>
public sealed class ProtocoloAvisoTests
{
    [Fact]
    public void ArmarAviso_pone_el_tipo_0x50_y_el_texto_en_UTF8()
    {
        var m = Protocolo.ArmarAviso("Edge pasó a la tele", 123UL);

        Assert.Equal(0x50, m[0]);
        var carga = Encoding.UTF8.GetBytes("Edge pasó a la tele");
        Assert.Equal((uint)carga.Length, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(1, 4)));
        Assert.Equal(123UL, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(5, 8)));
        Assert.Equal(carga, m[Protocolo.LargoCabecera..]);
    }

    [Fact]
    public void Un_texto_corto_no_se_recorta()
    {
        Assert.Equal("hola ñandú", Encoding.UTF8.GetString(Protocolo.CargaDeAviso("hola ñandú")));
    }

    [Theory]
    [InlineData("a", 700)] // 1 byte por carácter
    [InlineData("ñ", 700)] // 2 bytes
    [InlineData("€", 700)] // 3 bytes
    [InlineData("😀", 700)] // 4 bytes (par sustituto)
    public void Un_texto_largo_se_recorta_a_500_bytes_en_un_limite_de_caracter(string unidad, int veces)
    {
        var texto = "x" + string.Concat(Enumerable.Repeat(unidad, veces)); // el «x» desalinea: el corte cae a mitad de un carácter

        var carga = Protocolo.CargaDeAviso(texto);

        Assert.InRange(carga.Length, 497, Protocolo.MaxAviso);
        // Decodifica sin error ni caracteres de reemplazo: no se partió ninguno.
        var estricto = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var leido = estricto.GetString(carga);
        Assert.StartsWith("x", leido);
        Assert.StartsWith(leido, texto, StringComparison.Ordinal);
    }
}

/// <summary>Los nombres de los programas y los textos de los avisos (plural incluido).</summary>
public sealed class AvisosAudioTextosTests
{
    private const string Edge = @"C:\Edge\msedge.exe";
    private const string Vlc = @"C:\VLC\vlc.exe";
    private const string Kodi = @"C:\Kodi\kodi.exe";

    private static AvisosAudio Con(Dictionary<string, string?> descripciones) =>
        new(ruta => descripciones.TryGetValue(ruta, out var d) ? d : throw new FileNotFoundException(ruta));

    [Fact]
    public void El_nombre_es_la_descripcion_del_archivo()
    {
        var a = Con(new() { [Edge] = "Microsoft Edge" });
        Assert.Equal("Microsoft Edge", a.Nombre(Edge));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_descripcion_el_nombre_es_el_archivo_sin_extension(string? descripcion)
    {
        var a = Con(new() { [Vlc] = descripcion });
        Assert.Equal("vlc", a.Nombre(Vlc));
    }

    [Fact]
    public void Si_leer_el_archivo_falla_el_nombre_es_el_archivo_sin_extension()
    {
        var a = Con([]); // lanza FileNotFoundException
        Assert.Equal("kodi", a.Nombre(Kodi));
    }

    [Fact]
    public void El_nombre_se_lee_una_sola_vez_por_ruta()
    {
        var lecturas = 0;
        var a = new AvisosAudio(_ => { lecturas++; return "Edge"; });

        a.Nombre(Edge);
        a.Nombre(Edge.ToUpperInvariant());
        a.Nombre(Edge);

        Assert.Equal(1, lecturas); // la caché es por ruta y no distingue mayúsculas
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "A" }, "A")]
    [InlineData(new[] { "A", "B" }, "A y B")]
    [InlineData(new[] { "A", "B", "C" }, "A, B y C")]
    public void Unir_pone_la_y_antes_del_ultimo(string[] nombres, string esperado) =>
        Assert.Equal(esperado, AvisosAudio.Unir(nombres));

    [Fact]
    public void Textos_de_un_programa_en_singular()
    {
        var a = Con(new() { [Edge] = "Microsoft Edge" });

        Assert.Equal("Microsoft Edge pasó a la tele: pausa y vuelve a reproducir para que suene allá.", a.PasaronATele([Edge]));
        Assert.Equal("Microsoft Edge volvió a tu pantalla: pausa y vuelve a reproducir para que suene en el PC.", a.VolvieronAlPc([Edge]));
        Assert.Equal("Microsoft Edge también tiene ventanas en tu pantalla: su sonido sale por la tele", a.TambienEnPc([Edge]));
    }

    [Fact]
    public void Textos_de_varios_programas_en_plural_y_un_solo_aviso()
    {
        var a = Con(new() { [Edge] = "Microsoft Edge", [Vlc] = "VLC media player", [Kodi] = "Kodi" });

        Assert.Equal("Microsoft Edge y VLC media player pasaron a la tele: pausa y vuelve a reproducir para que suenen allá.",
            a.PasaronATele([Vlc, Edge]));
        Assert.Equal("Kodi, Microsoft Edge y VLC media player volvieron a tu pantalla: pausa y vuelve a reproducir para que suenen en el PC.",
            a.VolvieronAlPc([Vlc, Edge, Kodi]));
        Assert.Equal("Kodi y VLC media player también tienen ventanas en tu pantalla: su sonido sale por la tele", a.TambienEnPc([Vlc, Kodi]));
    }
}

/// <summary>Qué programas se avisan: solo los que tienen audio sonando.</summary>
public sealed class AvisosAudioSondeoTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-avisos-").FullName;
    private static readonly Rectangle Tele = new(1920, 0, 1920, 1080);
    private const string Chrome = @"C:\Chrome\chrome.exe";
    private const string Vlc = @"C:\VLC\vlc.exe";
    private const string Cable = "{0.0.0.00000000}.{cable}";

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private sealed class ActividadFalsa : IActividadAudio
    {
        public HashSet<int> Pids { get; } = [];
        public IReadOnlySet<int> PidsSonando() => new HashSet<int>(Pids);
    }

    private AudioExclusivo Nuevo(SistemaFalso s, ActividadFalsa a) =>
        new(Tele, Cable, s, s, s, s, new RutasPersistidas(Path.Combine(_carpeta, "rutas-audio.json")), a);

    private static void VentanaEnTele(SistemaFalso s, string exe, int pid) =>
        s.Ventanas.Add(new VentanaVisible(pid, exe, new Rectangle(2000, 100, 900, 600)));

    [Fact]
    public void Solo_los_que_suenan_pasan_a_la_lista_de_avisos_de_la_tele()
    {
        var s = new SistemaFalso();
        var a = new ActividadFalsa();
        s.Procesos.AddRange([new ProcesoInfo(10, 1, Chrome), new ProcesoInfo(20, 1, Vlc)]);
        VentanaEnTele(s, Chrome, 10);
        VentanaEnTele(s, Vlc, 20);
        a.Pids.Add(10); // solo Chrome suena
        using var audio = Nuevo(s, a);
        var avisos = new List<IReadOnlyList<string>>();
        audio.PasaronATele += avisos.Add;

        audio.Sondear();

        Assert.Equal([Chrome], Assert.Single(avisos));
        Assert.Equal([Chrome, Vlc], audio.EnTele.Order()); // los dos pasaron igual a la tele
    }

    [Fact]
    public void El_sonido_de_un_proceso_hijo_cuenta_para_el_programa()
    {
        var s = new SistemaFalso();
        var a = new ActividadFalsa();
        s.Procesos.AddRange([new ProcesoInfo(10, 1, Chrome), new ProcesoInfo(11, 10, Chrome)]);
        VentanaEnTele(s, Chrome, 10);
        a.Pids.Add(11); // el audio lo hace el hijo
        using var audio = Nuevo(s, a);
        IReadOnlyList<string>? llegaron = null;
        audio.PasaronATele += l => llegaron = l;

        audio.Sondear();

        Assert.Equal([Chrome], llegaron);
    }

    [Fact]
    public void Si_nada_suena_no_se_avisa_de_que_pasaron_a_la_tele()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s, new ActividadFalsa());
        var llamadas = 0;
        audio.PasaronATele += _ => llamadas++;

        audio.Sondear();

        Assert.Equal(0, llamadas);
        Assert.Equal([Chrome], audio.EnTele);
    }

    [Fact]
    public void Un_programa_que_suena_y_sale_de_la_tele_avisa_que_volvio_al_pc()
    {
        var s = new SistemaFalso();
        var a = new ActividadFalsa();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s, a);
        var volvieron = new List<IReadOnlyList<string>>();
        audio.VolvieronAlPc += volvieron.Add;
        audio.Sondear();
        s.Ventanas.Clear(); // la ventana sale del monitor
        a.Pids.Add(10);

        audio.Sondear();

        Assert.Equal([Chrome], Assert.Single(volvieron));
        Assert.Empty(audio.EnTele);
    }

    [Fact]
    public void Un_programa_en_silencio_que_sale_de_la_tele_no_avisa()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s, new ActividadFalsa());
        var llamadas = 0;
        audio.VolvieronAlPc += _ => llamadas++;
        audio.Sondear();
        s.Ventanas.Clear();

        audio.Sondear();

        Assert.Equal(0, llamadas);
    }

    [Fact]
    public void Sin_cambios_no_se_consulta_el_audio()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        var a = new ActividadConConteo();
        using var audio = new AudioExclusivo(Tele, Cable, s, s, s, s, new RutasPersistidas(Path.Combine(_carpeta, "rutas-audio.json")), a);

        audio.Sondear();
        var tras = a.Consultas;
        audio.Sondear();
        audio.Sondear();

        Assert.Equal(1, tras);
        Assert.Equal(1, a.Consultas);
    }

    private sealed class ActividadConConteo : IActividadAudio
    {
        public int Consultas { get; private set; }
        public IReadOnlySet<int> PidsSonando() { Consultas++; return new HashSet<int>(); }
    }

    [Fact]
    public void ProgramasSonando_mira_todos_los_procesos_del_programa_y_no_confunde_mayusculas()
    {
        var procesos = new[] { new ProcesoInfo(1, 0, Chrome), new ProcesoInfo(2, 1, Chrome), new ProcesoInfo(3, 0, Vlc) };

        var r = ProgramasSonando.Filtrar([Chrome.ToUpperInvariant(), Vlc], procesos, new HashSet<int> { 2 });

        Assert.Equal([Chrome.ToUpperInvariant()], r);
    }
}

/// <summary>Mando: qué se avisa en el PC y en la tele, y cuándo se repite.</summary>
public sealed class AvisosAudioMandoTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-avisos-mando-").FullName;
    private readonly List<string> _globos = [];
    private readonly List<EmisionFalsa> _falsas = [];
    private long _ms, _paso = 10_000; // por defecto cada aviso sale «mucho después» del anterior
    private const string Edge = @"C:\Edge\msedge.exe";
    private const string Vlc = @"C:\VLC\vlc.exe";
    private static readonly Receptor Sala = new("Sala", IPAddress.Parse("10.0.0.20"), Protocolo.PuertoTcp);

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private async Task<(Mando Mando, Transmision T, EmisionFalsa E)> Empezar()
    {
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta, "ajustes.json"));
        ajustes.ConAudio = false;
        ajustes.Reconectar = false;
        var mando = new Mando(ajustes, new ContextoInmediato(), () => [Sala])
        {
            FabricaEmision = () => { var e = new EmisionFalsa(); _falsas.Add(e); return e; },
            Preparador = (nombre, _, _) => new Monitores.Preparado(new Monitor(0, nombre ?? @"\\.\DISPLAYPRINCIPAL", 1920, 1080, true, 0, 0, "AMD"), () => { }),
            Avisos = new AvisosAudio(ruta => ruta == Edge ? "Microsoft Edge" : ruta == Vlc ? "VLC media player" : null),
            Reloj = () => _ms += _paso,
        };
        mando.Aviso += (texto, icono) => { Assert.Equal(System.Windows.Forms.ToolTipIcon.Info, icono); _globos.Add(texto); };
        Assert.True(mando.Iniciar(Sala, "virtual"));
        var emision = _falsas[^1];
        await emision.Empezo.WaitAsync(TimeSpan.FromSeconds(10));
        emision.Estado(Fase.Transmitiendo, ""); // la nota «también…» solo se muestra transmitiendo
        return (mando, mando.Transmisiones[0], emision);
    }

    [Fact]
    public async Task Pasar_a_la_tele_avisa_en_el_PC_y_en_la_tele_cada_vez()
    {
        var (mando, _, e) = await Empezar();
        using var _ = mando;

        e.Audio(TipoCambioAudio.PasaronATele, Edge);
        e.Audio(TipoCambioAudio.PasaronATele, Edge); // sin tope: cada vez que pasa

        const string texto = "Microsoft Edge pasó a la tele: pausa y vuelve a reproducir para que suene allá.";
        Assert.Equal([$"Sala (10.0.0.20): {texto}", $"Sala (10.0.0.20): {texto}"], _globos);
        Assert.Equal([texto, texto], e.AvisosATele);
        mando.Detener();
    }

    [Fact]
    public async Task Varios_programas_a_la_vez_van_en_un_solo_aviso_en_plural()
    {
        var (mando, _, e) = await Empezar();
        using var _ = mando;

        e.Audio(TipoCambioAudio.VolvieronAlPc, Vlc, Edge);

        Assert.Equal("Microsoft Edge y VLC media player volvieron a tu pantalla: pausa y vuelve a reproducir para que suenen en el PC.",
            Assert.Single(e.AvisosATele));
        Assert.Single(_globos);
        mando.Detener();
    }

    [Fact]
    public async Task Un_aviso_sin_programas_no_dice_nada()
    {
        var (mando, _, e) = await Empezar();
        using var _ = mando;

        e.Audio(TipoCambioAudio.PasaronATele);
        e.Audio(TipoCambioAudio.VolvieronAlPc);

        Assert.Empty(_globos);
        Assert.Empty(e.AvisosATele);
        mando.Detener();
    }

    [Fact]
    public async Task Tambien_tiene_ventanas_se_avisa_una_sola_vez_por_programa_y_la_nota_sigue_mientras_este()
    {
        var (mando, t, e) = await Empezar();
        using var _ = mando;
        var cambios = 0;
        mando.Cambio += () => cambios++;

        e.Audio(TipoCambioAudio.TambienEnPc, Edge);
        Assert.Equal("Sala (10.0.0.20): Microsoft Edge también tiene ventanas en tu pantalla: su sonido sale por la tele.", Assert.Single(_globos));
        Assert.Contains("Microsoft Edge también tiene ventanas en tu pantalla: su sonido sale por la tele", mando.NotaFila(t));
        Assert.True(cambios > 0);

        e.Audio(TipoCambioAudio.TambienEnPc); // deja de estar: la línea desaparece
        Assert.DoesNotContain("también tiene", mando.NotaFila(t));

        e.Audio(TipoCambioAudio.TambienEnPc, Edge); // vuelve: la nota vuelve, el aviso no
        Assert.Single(_globos);
        Assert.Single(e.AvisosATele);
        Assert.Contains("Microsoft Edge también tiene", mando.NotaFila(t));

        e.Audio(TipoCambioAudio.TambienEnPc, Edge, Vlc); // uno nuevo: se avisa solo ese
        Assert.Equal(2, _globos.Count);
        Assert.EndsWith("VLC media player también tiene ventanas en tu pantalla: su sonido sale por la tele.", _globos[^1]);
        Assert.Contains("Microsoft Edge y VLC media player también tienen ventanas", mando.NotaFila(t));
        mando.Detener();
    }

    [Fact]
    public async Task Avisos_de_la_misma_mirada_se_juntan_para_no_taparse()
    {
        var (mando, _, e) = await Empezar();
        using var _ = mando;
        _paso = 100; // llegan juntos, como en una misma mirada

        e.Audio(TipoCambioAudio.PasaronATele, Edge);
        e.Audio(TipoCambioAudio.TambienEnPc, Edge);

        const string juntos = "Microsoft Edge pasó a la tele: pausa y vuelve a reproducir para que suene allá. "
                              + "Microsoft Edge también tiene ventanas en tu pantalla: su sonido sale por la tele.";
        Assert.Equal(juntos, e.AvisosATele[^1]);
        Assert.Equal("Sala (10.0.0.20): " + juntos, _globos[^1]);

        _paso = Mando.MsParaJuntarAvisos; // ya pasó el plazo: sale solo
        e.Audio(TipoCambioAudio.VolvieronAlPc, Edge);
        Assert.Equal("Microsoft Edge volvió a tu pantalla: pausa y vuelve a reproducir para que suene en el PC.", e.AvisosATele[^1]);
        mando.Detener();
    }

    [Fact]
    public async Task Una_transmision_nueva_vuelve_a_avisar_el_tambien()
    {
        var (mando, t, e) = await Empezar();
        using var _ = mando;
        e.Audio(TipoCambioAudio.TambienEnPc, Edge);
        mando.Detener();
        await Task.Run(() => { while (t.EnCurso) Thread.Sleep(10); });
        mando.Detener(t); // la quita de la lista
        _globos.Clear();

        Assert.True(mando.Iniciar(Sala, "virtual"));
        var e2 = _falsas[^1];
        await e2.Empezo.WaitAsync(TimeSpan.FromSeconds(10));
        e2.Audio(TipoCambioAudio.TambienEnPc, Edge);

        Assert.Single(_globos);
        mando.Detener();
    }
}
