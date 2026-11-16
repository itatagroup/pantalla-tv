using PantallaTv.Audio.Prueba;

namespace PantallaTv.Audio.Prueba.Tests;

public class DispositivosTests
{
    private const string Mm = "{0.0.0.00000000}.{aaaaaaaa-1111-2222-3333-444444444444}";
    private static readonly InfoDispositivo Realtek = new("{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}", "Altavoces (Realtek(R) Audio)", true);
    private static readonly InfoDispositivo Cable = new(Mm, "CABLE Input (VB-Audio Virtual Cable)", false);
    private static readonly InfoDispositivo Hdmi = new("{0.0.0.00000000}.{22222222-2222-2222-2222-222222222222}", "Monitor HDMI", false);

    [Fact]
    public void EnvolverArmaLaFormaDeLaPolitica() =>
        Assert.Equal(@"\\?\SWD#MMDEVAPI#" + Mm + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}", Dispositivos.Envolver(Mm));

    [Fact]
    public void EnvolverEsIdempotenteYDesenvolverEsLaInversa()
    {
        var env = Dispositivos.Envolver(Mm);
        Assert.Equal(env, Dispositivos.Envolver(env));
        Assert.Equal(Mm, Dispositivos.Desenvolver(env));
        Assert.Equal(Mm, Dispositivos.Desenvolver(Mm));
        Assert.Equal("", Dispositivos.Desenvolver(null));
        Assert.Equal("", Dispositivos.Envolver(" "));
    }

    [Fact]
    public void ElegirPorNombreIdOIdEnvuelto()
    {
        var lista = new[] { Realtek, Cable, Hdmi };
        Assert.Same(Cable, Dispositivos.Elegir(lista, "cable").Elegido);
        Assert.Same(Cable, Dispositivos.Elegir(lista, Mm).Elegido);
        Assert.Same(Cable, Dispositivos.Elegir(lista, Dispositivos.Envolver(Mm)).Elegido);
        Assert.Same(Cable, Dispositivos.Elegir(lista, "aaaaaaaa").Elegido);
    }

    [Fact]
    public void ElegirInformaNingunoOAmbiguo()
    {
        var lista = new[] { Realtek, Cable, Hdmi };
        Assert.NotNull(Dispositivos.Elegir(lista, "zzz").Error);
        Assert.NotNull(Dispositivos.Elegir(lista, "").Error);
        var amb = Dispositivos.Elegir(lista, "o");
        Assert.Null(amb.Elegido);
        Assert.Contains("ambiguo", amb.Error);
    }

    [Fact]
    public void SegundaSalidaPrefiereElCableYNuncaLaPorDefecto()
    {
        Assert.Same(Cable, Dispositivos.SegundaSalida([Realtek, Hdmi, Cable]));
        Assert.Same(Hdmi, Dispositivos.SegundaSalida([Realtek, Hdmi]));
        Assert.Null(Dispositivos.SegundaSalida([Realtek]));
    }
}
