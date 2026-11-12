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
}
