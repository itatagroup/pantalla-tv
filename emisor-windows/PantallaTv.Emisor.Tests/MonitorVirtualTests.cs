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
