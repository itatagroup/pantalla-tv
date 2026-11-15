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

    internal static IReadOnlyList<(int Numero, string Dispositivo)> V(string? dispositivo) => dispositivo is null ? [] : [(1, dispositivo)];

    private static Monitor M(int i, string nombre, bool principal = false, string descripcion = "") =>
        new(i, nombre, 1920, 1080, principal, 0, i, "AMD", descripcion);

    [Fact]
    public void Cambiar_guarda_en_disco_y_avisa_a_los_dos_lados()
    {
        using var mando = Nuevo();
        var avisos = 0;
        mando.Cambio += () => avisos++;

        mando.Cambiar(a => a.Fps = 30);

        Assert.Equal(1, avisos);
        Assert.Equal(30, Ajustes.Cargar(mando.Ajustes.Ruta).Fps);
    }

    [Fact]
    public void Los_textos_de_las_opciones_son_los_del_menu()
    {
        using var mando = Nuevo();
        var formas = mando.OpcionesForma();
        Assert.Equal("Automática (recomendada)", formas[0].Texto);
        Assert.True(formas[0].Elegida);
        Assert.Equal(Tuberias.Todas.Count + 1, formas.Count);

        mando.Cambiar(a => a.Tuberia = Tuberias.Nativa);
        Assert.Single(mando.OpcionesForma(), o => o.Elegida && o.Valor == Tuberias.Nativa);

    }

    [Fact]
    public void Las_pantallas_dejan_el_virtual_aparte_y_marcan_la_elegida()
    {
        var todos = new[] { M(0, @"\\.\DISPLAY1", principal: true), M(1, @"\\.\DISPLAY2"), M(2, @"\\.\DISPLAY6", descripcion: "VDD by MTT") };

        // Sin elegir: el principal. La salida del virtual (conectado) no sale como un monitor más.
        var p = Mando.ArmarPantallas(todos, V(@"\\.\DISPLAY6"), null);
        Assert.True(p.HayVirtual);
        Assert.Equal(3, p.Opciones.Count);
        Assert.Equal(Monitores.ElMonitorVirtual, p.Opciones[^1].Valor);
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(p.Opciones, o => o.Elegida).Valor);

        // El virtual elegido.
        Assert.Equal(Monitores.ElMonitorVirtual, Assert.Single(Mando.ArmarPantallas(todos, V(@"\\.\DISPLAY6"), Monitores.ElMonitorVirtual).Opciones, o => o.Elegida).Valor);

        // Un monitor que ya no está: el principal (como al transmitir).
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(Mando.ArmarPantallas(todos, V(null), @"\\.\DISPLAY9").Opciones, o => o.Elegida).Valor);

        // Sin el controlador: no hay virtual (se ofrece instalarlo) y «virtual» guardado cae en el principal.
        var sin = Mando.ArmarPantallas(todos[..2], V(null), Monitores.ElMonitorVirtual);
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

        Assert.Equal("Esa no es una dirección IP válida: escribe cuatro números separados por puntos (por ejemplo 10.0.0.20).", aviso);
        Assert.False(mando.Transmitiendo);
        Assert.Null(mando.Ajustes.UltimaIp);
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
    [InlineData("10.0.0.20", "10.0.0.20", 5600)]
    [InlineData("127.0.0.1:5602", "127.0.0.1", 5602)] // un emulador detrás de adb forward
    [InlineData(" 10.0.0.7:5600 ", "10.0.0.7", 5600)]
    [InlineData("fe80::1", "fe80::1", 5600)]
    public void La_ip_escrita_a_mano_puede_traer_puerto(string texto, string ip, int puerto)
    {
        var d = Mando.LeerDestino(texto);
        Assert.NotNull(d);
        Assert.Equal(ip, d.Value.Direccion.ToString());
        Assert.Equal(puerto, d.Value.Puerto);
    }

    [Theory]
    [InlineData("10.0.0.20:")]
    [InlineData("10.0.0.20:0")]
    [InlineData("10.0.0.20:70000")]
    [InlineData("10.0.0:5602")]
    public void Un_puerto_invalido_no_vale(string texto) => Assert.Null(Mando.LeerDestino(texto));

    [Fact]
    public void Un_receptor_con_otro_puerto_lo_muestra() =>
        Assert.Equal("TV (127.0.0.1:5602)", new Receptor("TV", System.Net.IPAddress.Loopback, 5602).ToString());

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
            mando.Cambiar(a => { a.Reconectar = false; a.Fps = 60; });
            var receptor = new Receptor("Falso", IPAddress.Loopback, ((IPEndPoint)escucha.LocalEndpoint).Port);
            var aceptada = escucha.AcceptTcpClientAsync();

            mando.Iniciar(receptor);
            using var cliente = await aceptada.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(mando.Transmitiendo);
            Assert.False(mando.PuedeCambiarAjustes);
            Assert.False(mando.PuedeDiagnosticar);
            Assert.Equal(Fase.Conectando, mando.Fase);
            Assert.NotNull(mando.EnVivo);
            Assert.StartsWith("Falso (127.0.0.1:", mando.EnVivo.Tv); // el receptor falso usa un puerto suelto: se muestra
            Assert.Equal("127.0.0.1", Ajustes.Cargar(mando.Ajustes.Ruta).UltimaIp);
            mando.Cambiar(a => a.Fps = 30);
            Assert.Equal(60, mando.Ajustes.Fps); // no hizo nada
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
