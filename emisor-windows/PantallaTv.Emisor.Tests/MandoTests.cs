using System.Net;
using System.Net.Sockets;

namespace PantallaTv.Emisor.Tests;

/// <summary>Corre lo que se le «postea» en el mismo hilo: en las pruebas no hay bucle de mensajes.</summary>
internal sealed class ContextoInmediato : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);
}

/// <summary>
/// La lógica que comparten el menú de la bandeja y la ventana principal (<see cref="Mando"/>): si algo cambia en
/// un lado, el otro se entera por <see cref="Mando.Cambio"/> y lee lo mismo.
/// </summary>
public sealed class MandoTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-mando-").FullName;

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private Mando Nuevo(IReadOnlyList<Receptor>? tvs = null)
    {
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta, "ajustes.json"));
        ajustes.ConAudio = false; // en pruebas automáticas, nunca el audio (silenciaría el PC)
        return new Mando(ajustes, new ContextoInmediato(), () => tvs ?? []);
    }

    private static Monitor M(int i, string nombre, bool principal = false, string descripcion = "") =>
        new(i, nombre, 1920, 1080, principal, 0, i, "AMD", descripcion);

    [Fact]
    public void Cambiar_guarda_en_disco_y_avisa_a_los_dos_lados()
    {
        using var mando = Nuevo();
        var avisos = 0;
        mando.Cambio += () => avisos++;

        mando.Cambiar(a => a.RetardoMs = 2000);

        Assert.Equal(1, avisos);
        Assert.Equal(2000, Ajustes.Cargar(mando.Ajustes.Ruta).RetardoMs);
        Assert.Contains(mando.OpcionesRetardo(), o => o is { Valor: 2000, Elegida: true });
        Assert.Single(mando.OpcionesRetardo(), o => o.Elegida);
    }

    [Fact]
    public void Los_textos_de_las_opciones_son_los_del_menu()
    {
        using var mando = Nuevo();
        Assert.Equal(["Sin retardo (mínima latencia)", "0,5 s", "1 s", "2 s"], mando.OpcionesRetardo().Select(o => o.Texto));
        var formas = mando.OpcionesForma();
        Assert.Equal("Automática (recomendada)", formas[0].Texto);
        Assert.True(formas[0].Elegida);
        Assert.Equal(Tuberias.Todas.Count + 1, formas.Count);

        mando.Cambiar(a => a.Tuberia = Tuberias.Nativa);
        Assert.Single(mando.OpcionesForma(), o => o.Elegida && o.Valor == Tuberias.Nativa);

        // Un retardo editado a mano en ajustes.json aparece (marcado) en vez de mostrarse otro.
        mando.Cambiar(a => a.RetardoMs = 300);
        Assert.Equal([0, 300, 500, 1000, 2000], mando.OpcionesRetardo().Select(o => o.Valor));
        Assert.Equal("0,3 s", Assert.Single(mando.OpcionesRetardo(), o => o.Elegida).Texto);
    }

    [Fact]
    public void Las_pantallas_dejan_el_virtual_aparte_y_marcan_la_elegida()
    {
        var todos = new[] { M(0, @"\\.\DISPLAY1", principal: true), M(1, @"\\.\DISPLAY2"), M(2, @"\\.\DISPLAY6", descripcion: "VDD by MTT") };

        // Sin elegir: el principal. La salida del virtual (conectado) no sale como un monitor más.
        var p = Mando.ArmarPantallas(todos, @"\\.\DISPLAY6", null);
        Assert.True(p.HayVirtual);
        Assert.Equal(3, p.Opciones.Count);
        Assert.Equal(Monitores.ElMonitorVirtual, p.Opciones[^1].Valor);
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(p.Opciones, o => o.Elegida).Valor);

        // El virtual elegido.
        Assert.Equal(Monitores.ElMonitorVirtual, Assert.Single(Mando.ArmarPantallas(todos, @"\\.\DISPLAY6", Monitores.ElMonitorVirtual).Opciones, o => o.Elegida).Valor);

        // Un monitor que ya no está: el principal (como al transmitir).
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(Mando.ArmarPantallas(todos, null, @"\\.\DISPLAY9").Opciones, o => o.Elegida).Valor);

        // Sin el controlador: no hay virtual (se ofrece instalarlo) y «virtual» guardado cae en el principal.
        var sin = Mando.ArmarPantallas(todos[..2], null, Monitores.ElMonitorVirtual);
        Assert.False(sin.HayVirtual);
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(sin.Opciones, o => o.Elegida).Valor);
    }

    [Fact]
    public void Los_destinos_suman_la_ultima_ip_si_no_se_anuncia()
    {
        var sala = new Receptor("TV Box sala", IPAddress.Parse("10.0.0.20"), Protocolo.PuertoTcp);
        var pieza = new Receptor("Pieza", IPAddress.Parse("10.0.0.30"), Protocolo.PuertoTcp);

        // La última IP es de una TV que se ve: no se repite.
        var d = Mando.ArmarDestinos([pieza, sala], "10.0.0.20");
        Assert.Equal(2, d.Count);
        Assert.Equal(sala, Mando.Preferido(d, "10.0.0.20")!.Receptor);
        Assert.Equal("Transmitir a TV Box sala (10.0.0.20)", d[1].TextoMenu);

        // No se ve (el firewall tapa los anuncios): queda la última IP para transmitir igual.
        d = Mando.ArmarDestinos([pieza], "10.0.0.20");
        Assert.Equal(2, d.Count);
        Assert.True(d[1].UltimaIp);
        Assert.Equal("Última IP usada (10.0.0.20)", d[1].TextoLista);
        Assert.Equal(d[1], Mando.Preferido(d, "10.0.0.20"));

        Assert.False(Mando.HayTvAnunciada(Mando.ArmarDestinos([], "10.0.0.20"))); // solo la última IP: el menú y la ventana dicen que no se ve ninguna
        Assert.True(Mando.HayTvAnunciada(Mando.ArmarDestinos([pieza], null)));

        Assert.Empty(Mando.ArmarDestinos([], null));
        Assert.Empty(Mando.ArmarDestinos([], "no es una ip"));
        Assert.Null(Mando.Preferido([], null));
        Assert.Equal(pieza, Mando.Preferido(Mando.ArmarDestinos([pieza], null), null)!.Receptor);
    }

    [Fact]
    public void Una_ip_mal_escrita_avisa_y_no_transmite()
    {
        using var mando = Nuevo();
        string? aviso = null;
        mando.Aviso += (t, _) => aviso = t;

        Assert.False(mando.IniciarConIp("10.0.0"));

        Assert.Equal("Esa no es una dirección IP válida.", aviso);
        Assert.False(mando.Transmitiendo);
        Assert.Null(mando.Ajustes.UltimaIp);
    }

    [Fact]
    public void El_estado_y_los_numeros_en_vivo_siguen_a_la_emision()
    {
        using var mando = Nuevo();
        var avisos = new List<string>();
        mando.Aviso += (t, _) => avisos.Add(t);
        mando.EnVivo = new EnVivo { Tv = "TV (10.0.0.20)", FpsPedidos = 60 };

        // Los números solo cuentan transmitiendo.
        mando.AlEstadisticas(new Estadisticas(60, 4, 0, 3));
        Assert.Null(mando.EnVivo.Ultimas);

        mando.AlCambiarEstado(new EstadoEmision(Fase.Transmitiendo, "Transmitiendo a TV (nativa)"));
        var desde = mando.EnVivo.Desde;
        Assert.NotNull(desde);
        mando.AlEstadisticas(new Estadisticas(59.6, 4.25, 0, 3, Tuberia: Tuberias.Nativa));
        Assert.Equal("60", Mando.TextoFps(mando.EnVivo.Ultimas));
        Assert.Equal("4,3", Mando.TextoMbps(mando.EnVivo.Ultimas));
        Assert.Equal("Propia, sin ffmpeg (nativa)", Mando.TextoForma(mando.EnVivo.Ultimas));

        // Se corta: los números viejos se borran, el tiempo sigue contando desde la primera vez y se avisa una sola vez.
        mando.AlCambiarEstado(new EstadoEmision(Fase.Reconectando, "Reconectando… (se cortó)"));
        mando.AlCambiarEstado(new EstadoEmision(Fase.Reconectando, "Reconectando… (otra vez)"));
        Assert.Null(mando.EnVivo.Ultimas);
        Assert.Single(avisos);
        mando.AlCambiarEstado(new EstadoEmision(Fase.Transmitiendo, "Transmitiendo a TV (nativa)"));
        Assert.Equal(desde, mando.EnVivo.Desde);

        // Terminó bien: vuelve a «Sin transmitir».
        mando.Reposar();
        Assert.Null(mando.EnVivo);
        Assert.Equal(Fase.Inactivo, mando.Fase);
        Assert.Equal(Mando.SinTransmitir, mando.Estado);

        // Terminó con error: el mensaje se queda a la vista.
        mando.AlCambiarEstado(new EstadoEmision(Fase.Error, "La TV usa otra versión."));
        mando.Reposar();
        Assert.Equal(Fase.Error, mando.Fase);
        Assert.Equal("La TV usa otra versión.", mando.Estado);
        Assert.Equal("La TV usa otra versión.", avisos[^1]);
    }

    [Theory]
    [InlineData("10.0.0.20", "10.0.0.20")]
    [InlineData("  10.0.0.7 ", "10.0.0.7")]
    [InlineData("10.0.0", null)]
    [InlineData("3232235877", null)]
    [InlineData("tele", null)]
    [InlineData("", null)]
    [InlineData("fe80::1", "fe80::1")]
    public void La_ip_escrita_a_mano_trae_sus_cuatro_numeros(string texto, string? esperada) =>
        Assert.Equal(esperada, Mando.LeerIp(texto)?.ToString());

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3725, "1:02:05")]
    public void El_tiempo_se_lee_como_un_reloj(int segundos, string esperado) =>
        Assert.Equal(esperado, Mando.TextoTiempo(TimeSpan.FromSeconds(segundos)));

    [Fact]
    public void Sin_numeros_se_muestra_una_raya()
    {
        Assert.Equal("—", Mando.TextoFps(null));
        Assert.Equal("—", Mando.TextoMbps(null));
        Assert.Equal("—", Mando.TextoForma(null));
        Assert.Equal("—", Mando.TextoForma(new Estadisticas(60, 4, 0, null)));
    }

    [Fact]
    public void Silenciar_el_pc_solo_con_sonido_y_el_diagnostico_sin_transmitir()
    {
        using var mando = Nuevo(); // sin audio (ver Nuevo)
        Assert.False(mando.PuedeSilenciar);
        mando.Cambiar(a => a.ConAudio = true);
        Assert.True(mando.PuedeSilenciar);
        Assert.True(mando.PuedeDiagnosticar);
        Assert.Equal("Instalar el monitor virtual…", mando.TextoInstalarVirtual);
    }

    [Fact]
    public void El_aviso_de_la_bandeja_sale_solo_la_primera_vez()
    {
        using var mando = Nuevo();
        Assert.True(mando.MarcarAvisoBandeja());
        Assert.False(mando.MarcarAvisoBandeja());
        Assert.True(Ajustes.Cargar(mando.Ajustes.Ruta).AvisoBandejaVisto);
    }

    /// <summary>
    /// Una transmisión de verdad contra un receptor falso que acepta la conexión y no contesta el saludo: mientras
    /// tanto los ajustes quedan bloqueados (en el menú y en la ventana) y «Detener» la termina. Sin audio y sin
    /// llegar a capturar (eso empieza después del saludo).
    /// </summary>
    [Fact]
    public async Task Mientras_transmite_los_ajustes_se_bloquean_y_detener_la_termina()
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        try
        {
            using var mando = Nuevo();
            mando.Cambiar(a => { a.Reconectar = false; a.RetardoMs = 500; });
            var receptor = new Receptor("Falso", IPAddress.Loopback, ((IPEndPoint)escucha.LocalEndpoint).Port);
            var aceptada = escucha.AcceptTcpClientAsync();

            mando.Iniciar(receptor);
            using var cliente = await aceptada.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(mando.Transmitiendo);
            Assert.False(mando.PuedeCambiarAjustes);
            Assert.False(mando.PuedeDiagnosticar);
            Assert.Equal(Fase.Conectando, mando.Fase);
            Assert.NotNull(mando.EnVivo);
            Assert.Equal("Falso (127.0.0.1)", mando.EnVivo.Tv);
            Assert.Equal("127.0.0.1", Ajustes.Cargar(mando.Ajustes.Ruta).UltimaIp);
            mando.Cambiar(a => a.RetardoMs = 2000);
            Assert.Equal(500, mando.Ajustes.RetardoMs); // no hizo nada
            mando.Iniciar(receptor); // tampoco: ya hay una

            mando.Detener();
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (mando.Transmitiendo || mando.EnVivo is not null)
            {
                Assert.True(DateTime.UtcNow < limite, "no terminó al detener");
                await Task.Delay(50);
            }
            Assert.True(mando.PuedeCambiarAjustes);
            Assert.Equal(Fase.Inactivo, mando.Fase);
        }
        finally
        {
            escucha.Stop();
        }
    }
}

public class ArranqueTests
{
    [Fact]
    public void Desde_el_inicio_de_windows_arranca_oculto()
    {
        Assert.True(Arranque.EmpiezaOculto(["--oculto"]));
        Assert.False(Arranque.EmpiezaOculto([]));
        Assert.Equal("\"C:\\Programas\\PantallaTv\\PantallaTv.exe\" --oculto", Instalacion.ComandoInicio(@"C:\Programas\PantallaTv\PantallaTv.exe"));
    }

    [Theory]
    [InlineData("prueba", "prueba")]
    [InlineData("Dev-2", "dev-2")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("..\\otra", null)]
    [InlineData("con espacio", null)]
    public void El_perfil_de_desarrollo_solo_admite_un_nombre_simple(string? valor, string? esperado) =>
        Assert.Equal(esperado, Arranque.PerfilDe(valor));
}

/// <summary>El contraste de la paleta oliva medido con la fórmula de WCAG 2 (AA: 4,5:1 texto, 3:1 lo demás).</summary>
public class PaletaTests
{
    public static TheoryData<string> Paletas => ["claro", "oscuro"];

    private static Paleta De(string nombre) => nombre == "claro" ? Paleta.Clara : Paleta.OscuraGrafito;

    [Theory]
    [MemberData(nameof(Paletas))]
    public void Todo_el_texto_llega_a_AA(string nombre)
    {
        var p = De(nombre);
        var pares = new (string Que, Color Texto, Color Fondo)[]
        {
            ("texto en tarjeta", p.Texto, p.Superficie),
            ("texto en fondo", p.Texto, p.Fondo),
            ("nota en tarjeta", p.Secundario, p.Superficie),
            ("versión en fondo", p.Secundario, p.Fondo),
            ("etiqueta del panel en vivo", p.Secundario, p.SuperficieSuave),
            ("valor del panel en vivo", p.TextoFuerte, p.SuperficieSuave),
            ("bloqueado", p.Secundario, p.SuperficieApagada),
            ("botón principal", p.SobrePrimario, p.Primario),
            ("botón principal (encima)", p.SobrePrimario, p.PrimarioFuerte),
            ("botón Detener", p.SobrePrimario, p.Error),
            ("lista: elegido", p.SobrePrimario, p.Primario),
            ("botón secundario (encima)", p.Texto, p.SuperficieApagada),
        };
        foreach (var fase in Enum.GetValues<Fase>())
            pares = [.. pares, ($"estado {fase}", p.DeFase(fase), p.Fondo)];
        foreach (var (que, texto, fondo) in pares)
            Assert.True(Paleta.Contraste(texto, fondo) >= 4.5, $"{nombre}, {que}: {Paleta.Contraste(texto, fondo):0.00}:1");
    }

    [Theory]
    [MemberData(nameof(Paletas))]
    public void Bordes_marcas_y_foco_llegan_a_3_a_1(string nombre)
    {
        var p = De(nombre);
        foreach (var (que, color, fondo) in new (string, Color, Color)[]
        {
            ("borde de casillas y listas", p.BordeControl, p.Superficie),
            ("casilla marcada", p.Primario, p.Superficie),
            ("botón principal", p.Primario, p.Superficie),
            ("anillo de foco en tarjeta", p.Texto, p.Superficie),
            ("anillo de foco en fondo", p.Texto, p.Fondo),
            ("punto del estado", p.DeFase(Fase.Inactivo), p.Fondo),
        })
            Assert.True(Paleta.Contraste(color, fondo) >= 3, $"{nombre}, {que}: {Paleta.Contraste(color, fondo):0.00}:1");
    }

    [Fact]
    public void La_formula_de_contraste_es_la_de_WCAG() =>
        Assert.Equal(21, Paleta.Contraste(Color.Black, Color.White), 2);
}
