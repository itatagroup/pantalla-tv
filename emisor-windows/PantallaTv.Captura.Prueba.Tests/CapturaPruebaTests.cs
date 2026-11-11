using PantallaTv.Captura.Prueba;

namespace PantallaTv.Captura.Prueba.Tests;

public class OpcionesTests
{
    [Fact]
    public void Por_omision_mf_cbr_sombreador()
    {
        var o = Opciones.Leer([]);
        Assert.Equal(Candidato.MediaFoundation, o.Codificador);
        Assert.Equal(ModoTasa.Cbr, o.Tasa);
        Assert.Equal(Conversion.Sombreador, o.Conversion);
        Assert.Equal(30, o.LatidoFps);
    }

    [Fact]
    public void Lee_todas_las_opciones()
    {
        var o = Opciones.Leer(["--monitor", "virtual", "--codificador", "av", "--tasa", "vbr", "--conversion", "vp",
            "--kbps", "6000", "--fps", "30", "--seg", "5", "--latido", "0", "--salida", "x.h264", "--clave-cada", "2"]);
        Assert.Equal(("virtual", Candidato.Libavcodec, ModoTasa.Vbr, Conversion.ProcesadorVideo), (o.Monitor, o.Codificador, o.Tasa, o.Conversion));
        Assert.Equal((6000, 30, 5, 0, "x.h264", 2), (o.Kbps, o.Fps, o.Segundos, o.LatidoFps, o.Salida, o.ClaveCadaSeg));
    }

    [Theory]
    [InlineData("--codificador", "x264")]
    [InlineData("--kbps", "abc")]
    [InlineData("--kbps", "10")]
    [InlineData("--nada", "1")]
    public void Rechaza_lo_que_no_cuadra(string opcion, string valor) =>
        Assert.Throws<ArgumentException>(() => Opciones.Leer([opcion, valor]));

    [Fact]
    public void Latido_no_mayor_que_fps() =>
        Assert.Throws<ArgumentException>(() => Opciones.Leer(["--fps", "30", "--latido", "60"]));

    [Fact]
    public void Falta_valor() => Assert.Throws<ArgumentException>(() => Opciones.Leer(["--monitor"]));
}

public class MedidorTests
{
    [Fact]
    public void Resumen_sin_primer_ni_ultimo_segundo()
    {
        var m = new Medidor();
        int[] capturados = [10, 60, 58, 60, 3];
        foreach (var c in capturados)
        {
            for (var i = 0; i < c; i++) { m.Capturado(); m.Codificado(1000, i == 0); }
            m.SoloRaton();
            m.Cerrar(0.25);
        }
        var r = m.Resumir();
        Assert.Equal(3, r.Segundos);
        Assert.Equal(59.3, r.CapturadosMedio);
        Assert.Equal(58, r.CapturadosMinimo);
        Assert.Equal(0.25, r.CpuMedia);
        Assert.Equal(5, r.Claves);     // las claves y el ratón cuentan todos los segundos
        Assert.Equal(5, r.SoloRaton);
        Assert.Equal(Math.Round((60 + 58 + 60) * 1000 * 8 / 3.0 / 1e6, 2), r.MbpsMedio);
    }

    [Fact]
    public void Sin_segundos_no_falla() => Assert.Equal(0, new Medidor().Resumir().Segundos);
}
