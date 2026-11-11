using System.Buffers.Binary;
using PantallaTv.Emisor;

namespace PantallaTv.Emisor.Tests;

public class ConversorPcmTests
{
    private static byte[] Flotantes(params float[] v)
    {
        var b = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4, 4), v[i]);
        return b;
    }

    private static short[] Muestras(byte[] pcm)
    {
        var r = new short[pcm.Length / 2];
        for (var i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
        return r;
    }

    [Fact]
    public void Flotante_estereo_pasa_a_16_bits_y_se_recorta_lo_que_se_sale()
    {
        var entrada = Flotantes(0f, 1f, -1f, 0.5f, 2f, -3f, float.NaN, 0f);

        var salida = Muestras(ConversorPcm.A16Bits(entrada, new FormatoPcm(32, true, 2), 2));

        Assert.Equal(new short[] { 0, 32767, -32767, 16384, 32767, -32767, 0, 0 }, salida);
    }

    [Fact]
    public void De_un_dispositivo_5_1_se_toman_los_dos_primeros_canales()
    {
        // Un cuadro de 6 canales: izquierda, derecha y cuatro más.
        var entrada = Flotantes(0.25f, -0.25f, 0.9f, 0.9f, 0.9f, 0.9f);

        var salida = Muestras(ConversorPcm.A16Bits(entrada, new FormatoPcm(32, true, 6), 2));

        Assert.Equal(new short[] { 8192, -8192 }, salida);
    }

    [Fact]
    public void Mono_se_duplica_si_se_piden_dos_canales()
    {
        var salida = Muestras(ConversorPcm.A16Bits(Flotantes(0.5f, -0.5f), new FormatoPcm(32, true, 1), 2));

        Assert.Equal(new short[] { 16384, 16384, -16384, -16384 }, salida);
    }

    [Fact]
    public void Enteros_de_16_24_y_32_bits_conservan_los_bits_altos()
    {
        byte[] de16 = [0x34, 0x12];
        byte[] de24 = [0xFF, 0x34, 0x12];
        byte[] de32 = [0xFF, 0xFF, 0x34, 0x12];

        Assert.Equal(0x1234, Muestras(ConversorPcm.A16Bits(de16, new FormatoPcm(16, false, 1), 1))[0]);
        Assert.Equal(0x1234, Muestras(ConversorPcm.A16Bits(de24, new FormatoPcm(24, false, 1), 1))[0]);
        Assert.Equal(0x1234, Muestras(ConversorPcm.A16Bits(de32, new FormatoPcm(32, false, 1), 1))[0]);
    }

    [Fact]
    public void Un_cuadro_incompleto_al_final_se_ignora()
    {
        var entrada = Flotantes(0.5f, 0.5f, 0.5f); // un cuadro estéreo y medio

        Assert.Equal(4, ConversorPcm.A16Bits(entrada, new FormatoPcm(32, true, 2), 2).Length);
    }
}
