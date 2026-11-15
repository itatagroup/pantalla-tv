using System.Drawing;
using PantallaTv.Emisor;

namespace PantallaTv.Emisor.Tests;

public class MonitorVirtualTests
{
    [Fact]
    public void Elige_1080p60_si_esta()
    {
        var modos = new[] { (800, 600, 30), (1280, 720, 60), (1920, 1080, 60), (1920, 1080, 30) };
        Assert.Equal((1920, 1080, 60), MonitorVirtual.ElegirModo(modos));
    }

    [Fact]
    public void Sin_1080p60_elige_el_de_mayor_area_a_60_Hz()
    {
        var modos = new[] { (1920, 1080, 30), (1280, 720, 60), (1600, 900, 60), (800, 600, 60) };
        Assert.Equal((1600, 900, 60), MonitorVirtual.ElegirModo(modos));
    }

    [Fact]
    public void Sin_ningun_modo_a_60_Hz_no_elige_nada()
    {
        Assert.Null(MonitorVirtual.ElegirModo([(1920, 1080, 30), (1280, 720, 144)]));
        Assert.Null(MonitorVirtual.ElegirModo([]));
    }

    [Fact]
    public void Ignora_los_modos_mayores_a_1080p()
    {
        Assert.Equal((1280, 720, 60), MonitorVirtual.ElegirModo([(3840, 2160, 60), (2560, 1440, 60), (1280, 720, 60)]));
        Assert.Null(MonitorVirtual.ElegirModo([(3840, 2160, 60)]));
    }

    private static readonly (int, int, int)[] Ofrecidos =
    [
        (3840, 2160, 60), (3840, 2160, 30), (2560, 1440, 60), (1920, 1200, 60), (1920, 1080, 60), (1920, 1080, 50),
        (1920, 1080, 30), (1280, 800, 60), (1280, 720, 60), (1280, 720, 50),
    ];

    [Theory]
    [InlineData(1280, 720, 60, 1280, 720, 60)]     // la TV Box: exacto
    [InlineData(1920, 1080, 50, 1920, 1080, 50)]   // una tele europea a 50 Hz
    [InlineData(1080, 1920, 60, 1920, 1080, 60)]   // una pantalla «de pie»: el monitor virtual va horizontal
    [InlineData(2000, 1200, 60, 1920, 1200, 60)]   // tableta 2000×1200: la misma forma más grande que cabe
    [InlineData(1366, 768, 60, 1280, 720, 60)]     // sin el exacto: la misma forma (16:9) que cabe
    [InlineData(3840, 2160, 30, 3840, 2160, 30)]   // 4K a 30 Hz
    [InlineData(1920, 1080, 24, 1920, 1080, 60)]   // nada a 24 Hz o menos: el de siempre (1080p60)
    public void Con_la_pantalla_de_la_tele_elige_el_modo_mas_cercano_sin_pasarse(int ancho, int alto, int hz, int eAncho, int eAlto, int eHz)
    {
        Assert.Equal((eAncho, eAlto, eHz), MonitorVirtual.ElegirModo(Ofrecidos, (ancho, alto, hz)));
    }

    [Fact]
    public void Si_nada_cabe_vuelve_a_1080p60()
    {
        Assert.Equal((1920, 1080, 60), MonitorVirtual.ElegirModo(Ofrecidos, (640, 480, 60)));
        Assert.Equal((1920, 1080, 60), MonitorVirtual.ElegirModo(Ofrecidos, null));
    }

    [Theory]
    [InlineData("PANTALLA-TV-PANTALLA 1 5600 1280 720 60 tv", 5600, 1280, 720, 60, "tv")]
    [InlineData("PANTALLA-TV-PANTALLA 1 5600 1200 2000 60 tableta", 5600, 1200, 2000, 60, "tableta")]
    [InlineData("PANTALLA-TV-PANTALLA 1 5600 1080 2400 120 algo", 5600, 1080, 2400, 120, "tv")]
    public void Lee_el_aviso_de_la_pantalla(string texto, int puerto, int ancho, int alto, int hz, string tipo)
    {
        var r = Protocolo.InterpretarPantalla(texto);
        Assert.NotNull(r);
        Assert.Equal(puerto, r.Value.Puerto);
        Assert.Equal(new PantallaTele(ancho, alto, hz, tipo), r.Value.Pantalla);
    }

    [Theory]
    [InlineData("PANTALLA-TV 1 Tele 5600")]
    [InlineData("PANTALLA-TV-PANTALLA 2 5600 1280 720 60 tv")]
    [InlineData("PANTALLA-TV-PANTALLA 1 5600 0 720 60 tv")]
    [InlineData("PANTALLA-TV-PANTALLA 1 5600 1280 720 9999 tv")]
    [InlineData("PANTALLA-TV-PANTALLA 1 5600 1280")]
    public void Descarta_avisos_de_pantalla_raros(string texto) => Assert.Null(Protocolo.InterpretarPantalla(texto));

    [Fact]
    public void Un_emisor_viejo_no_confunde_el_aviso_de_pantalla_con_una_tele() =>
        Assert.Null(Protocolo.InterpretarAnuncio("PANTALLA-TV-PANTALLA 1 5600 1280 720 60 tv", System.Net.IPAddress.Loopback));

    [Fact]
    public void Sin_pantallas_va_al_origen()
    {
        Assert.Equal((0, 0), MonitorVirtual.PosicionADerecha([]));
    }

    [Fact]
    public void Con_una_pantalla_va_pegado_a_su_borde_derecho()
    {
        Assert.Equal((1920, 0), MonitorVirtual.PosicionADerecha([new Rectangle(0, 0, 1920, 1080)]));
    }

    [Fact]
    public void Con_una_pantalla_a_la_izquierda_en_negativo_va_a_la_derecha_de_la_ultima()
    {
        var pantallas = new[] { new Rectangle(-1280, 100, 1280, 1024), new Rectangle(0, 0, 1920, 1080) };
        Assert.Equal((1920, 0), MonitorVirtual.PosicionADerecha(pantallas));
        // aunque la de la izquierda sea la única: su borde derecho es 0, no un valor negativo
        Assert.Equal((0, 0), MonitorVirtual.PosicionADerecha([new Rectangle(-1280, 0, 1280, 1024)]));
    }

    [Theory]
    [InlineData("virtual", 1)]
    [InlineData("virtual2", 2)]
    [InlineData("virtual3", 3)]
    [InlineData("virtual4", 4)]
    [InlineData("Virtual3", 3)]
    [InlineData(" virtual2 ", 2)]
    public void Reconoce_los_nombres_de_monitor_virtual(string nombre, int numero) =>
        Assert.Equal(numero, MonitorVirtual.NumeroDeNombre(nombre));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\.\DISPLAY2")]
    [InlineData("virtual0")]
    [InlineData("virtual1")]
    [InlineData("virtual5")]
    [InlineData("virtual10")]
    [InlineData("virtual2x")]
    [InlineData("virtualx")]
    [InlineData("2")]
    public void Lo_que_no_es_un_monitor_virtual_no_tiene_numero(string? nombre) =>
        Assert.Null(MonitorVirtual.NumeroDeNombre(nombre));

    [Fact]
    public void El_nombre_de_cada_numero_vuelve_al_mismo_numero()
    {
        Assert.Equal("virtual", MonitorVirtual.NombreDeNumero(1));
        Assert.Equal("virtual3", MonitorVirtual.NombreDeNumero(3));
        for (var n = 1; n <= Controlador.MonitoresVirtuales; n++)
            Assert.Equal(n, MonitorVirtual.NumeroDeNombre(MonitorVirtual.NombreDeNumero(n)));
    }

    [Fact]
    public void Un_numero_que_no_existe_explica_que_hay_que_reinstalar()
    {
        var texto = MonitorVirtual.TextoNoExiste(3);
        Assert.Equal("El monitor virtual 3 no existe: reinstala el monitor virtual en «Avanzado» para tener hasta 4.", texto);
    }

    [Fact]
    public void Las_marcas_son_una_por_monitor_y_la_primera_conserva_su_nombre()
    {
        Assert.Equal("monitor-virtual-conectado.marca", MonitorVirtual.NombreDeMarca(1));
        Assert.Equal("monitor-virtual-conectado-2.marca", MonitorVirtual.NombreDeMarca(2));
        Assert.Equal("monitor-virtual-conectado-4.marca", MonitorVirtual.NombreDeMarca(4));
        Assert.Equal(MonitorVirtual.RutaMarca, MonitorVirtual.RutaMarcaDe(1));
        var rutas = Enumerable.Range(1, Controlador.MonitoresVirtuales).Select(MonitorVirtual.RutaMarcaDe).ToList();
        Assert.Equal(rutas.Count, rutas.Distinct().Count());
    }

    [Fact]
    public void Las_salidas_del_controlador_se_numeran_en_el_orden_de_Windows_y_ignoran_las_demas()
    {
        var salidas = new[]
        {
            (@"\\.\DISPLAY1", @"PCI\VEN_1002&DEV_1638"),
            (@"\\.\DISPLAY9", @"ROOT\DISPLAY\0000\MttVDD"),
            (@"\\.\DISPLAY3", @"ROOT\DISPLAY\0000\MttVDD"),
            (@"\\.\DISPLAY2", @"ROOT\DISPLAY\0000\mttvdd"),
        };
        // El orden es el de la enumeración, no el del número de DISPLAYn.
        Assert.Equal([@"\\.\DISPLAY9", @"\\.\DISPLAY3", @"\\.\DISPLAY2"], MonitorVirtual.SalidasDelControlador(salidas));
        Assert.Empty(MonitorVirtual.SalidasDelControlador([]));
        var seis = Enumerable.Range(1, 6).Select(i => ($@"\\.\DISPLAY{i}", @"ROOT\DISPLAY\0000\MttVDD"));
        Assert.Equal(Controlador.MonitoresVirtuales, MonitorVirtual.SalidasDelControlador(seis).Count);
    }

    [Fact]
    public void Un_virtual_nuevo_va_a_la_derecha_de_los_otros_virtuales_ya_conectados()
    {
        var real = new Rectangle(0, 0, 1920, 1080);
        var virtual1 = new Rectangle(1920, 0, 1920, 1080);
        var virtual2 = new Rectangle(3840, 0, 1280, 720);
        Assert.Equal((3840, 0), MonitorVirtual.PosicionADerecha([real, virtual1]));
        Assert.Equal((5120, 0), MonitorVirtual.PosicionADerecha([real, virtual1, virtual2]));
        Assert.Equal((5120, 0), MonitorVirtual.PosicionADerecha([virtual2, real, virtual1])); // el orden no importa
    }

    [Fact]
    public void El_estado_lista_todos_los_monitores_virtuales()
    {
        Assert.Equal(["monitor virtual: no instalado"], Cli.LineasMonitorVirtual([]));
        var lineas = Cli.LineasMonitorVirtual([(1, @"\\.\DISPLAY6", false), (2, @"\\.\DISPLAY7", true)]);
        Assert.Equal(3, lineas.Count);
        Assert.Contains("2 salidas", lineas[0]);
        Assert.Equal(@"monitor virtual 1: \\.\DISPLAY6, desconectado", lineas[1]);
        Assert.Equal(@"monitor virtual 2: \\.\DISPLAY7, conectado al escritorio", lineas[2]);
    }

    [Fact]
    public void Una_configuracion_de_un_monitor_se_actualiza_a_cuatro_y_conserva_la_grafica()
    {
        const string amd = "AMD Radeon(TM) Graphics";
        var deUno = Controlador.ConfiguracionesAnteriores.Last().Replace("<friendlyname>default</friendlyname>", $"<friendlyname>{amd}</friendlyname>");
        Assert.Contains("<count>1</count>", deUno);
        // Sin otra gráfica pedida: se queda con la que tenía.
        var nueva = Controlador.ConfiguracionAEscribir(deUno, null);
        Assert.NotNull(nueva);
        Assert.Contains("<count>4</count>", nueva);
        Assert.Equal(amd, Controlador.GpuDeConfiguracion(nueva));
        // Con otra pedida: la nueva.
        Assert.Equal("NVIDIA", Controlador.GpuDeConfiguracion(Controlador.ConfiguracionAEscribir(deUno, "NVIDIA")!));
        // Ya al día, o del usuario: no se toca. Sin archivo: se escribe la de ahora.
        Assert.Null(Controlador.ConfiguracionAEscribir(nueva, null));
        Assert.Null(Controlador.ConfiguracionAEscribir(deUno.Replace("<HardwareCursor>true<", "<HardwareCursor>false<"), null));
        Assert.Equal(Controlador.ConfiguracionPropia(), Controlador.ConfiguracionAEscribir(null, null));
    }}
