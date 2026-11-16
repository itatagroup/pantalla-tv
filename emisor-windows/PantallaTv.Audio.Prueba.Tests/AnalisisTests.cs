using PantallaTv.Audio.Prueba;

namespace PantallaTv.Audio.Prueba.Tests;

public class AnalisisTests
{
    private const int Fm = 48000;

    private static float[] Seno(double hz, double amplitud, double seg = 1) =>
        Enumerable.Range(0, (int)(Fm * seg)).Select(i => (float)(amplitud * Math.Sin(2 * Math.PI * hz * i / Fm))).ToArray();

    [Fact]
    public void RmsDeSenoidalPlenaEsMenosTresDb() =>
        Assert.Equal(-3.01, Analisis.RmsDbfs(Seno(440, 1.0)), 1);

    [Fact]
    public void RmsDeSilencioEsElPiso()
    {
        Assert.Equal(Analisis.PisoDb, Analisis.RmsDbfs(new float[4800]));
        Assert.Equal(Analisis.PisoDb, Analisis.RmsDbfs([]));
    }

    [Fact]
    public void GoertzelEstimaLaAmplitud() =>
        Assert.Equal(0.05, Analisis.Amplitud(Seno(440, 0.05), 440, Fm), 3);

    [Theory]
    [InlineData(440)]
    [InlineData(880)]
    [InlineData(1000)]
    public void FrecuenciaDominanteEncuentraElTono(double hz) =>
        Assert.Equal(hz, Analisis.FrecuenciaDominante(Seno(hz, 0.05), Fm).Hz, 5);

    [Fact]
    public void SeparaDosTonosMezclados()
    {
        var a = Seno(440, 0.05);
        var b = Seno(880, 0.05);
        var mezcla = a.Zip(b, (x, y) => x + y).ToArray();
        Assert.Equal(0.05, Analisis.Amplitud(mezcla, 440, Fm), 3);
        Assert.Equal(0.05, Analisis.Amplitud(mezcla, 880, Fm), 3);
        // Un tono solo: la frecuencia ajena queda muy por debajo.
        var sep = Analisis.SeparacionDb(Analisis.Amplitud(a, 440, Fm), Analisis.Amplitud(a, 880, Fm));
        Assert.True(sep < -60, $"separación {sep:F1} dB");
    }

    [Fact]
    public void FormatoFlotante32EsWaveFormatEx()
    {
        var b = Analisis.FormatoFlotante32(48000, 2);
        Assert.Equal(18, b.Length);
        Assert.Equal(3, BitConverter.ToUInt16(b, 0));      // IEEE float
        Assert.Equal(2, BitConverter.ToUInt16(b, 2));      // canales
        Assert.Equal(48000u, BitConverter.ToUInt32(b, 4));
        Assert.Equal(384000u, BitConverter.ToUInt32(b, 8)); // bytes por segundo
        Assert.Equal(8, BitConverter.ToUInt16(b, 12));     // alineación de bloque
        Assert.Equal(32, BitConverter.ToUInt16(b, 14));
        Assert.Equal(0, BitConverter.ToUInt16(b, 16));
    }

    [Fact]
    public void ParametrosDeActivacionLlevanTipoPidYModo()
    {
        var incluir = Analisis.ParametrosActivacion(1234, false);
        var excluir = Analisis.ParametrosActivacion(1234, true);
        Assert.Equal(12, incluir.Length);
        Assert.Equal(1, BitConverter.ToInt32(incluir, 0));
        Assert.Equal(1234u, BitConverter.ToUInt32(incluir, 4));
        Assert.Equal(0, BitConverter.ToInt32(incluir, 8));
        Assert.Equal(1, BitConverter.ToInt32(excluir, 8));
    }
}
