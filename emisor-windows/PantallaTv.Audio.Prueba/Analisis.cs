using System.Buffers.Binary;

namespace PantallaTv.Audio.Prueba;

/// <summary>Lógica pura de medición: nivel, detección de frecuencia y armado del formato de captura.</summary>
internal static class Analisis
{
    public const double PisoDb = -120.0;

    /// <summary>Nivel RMS en dBFS (una senoidal de amplitud pico 1,0 da -3,01).</summary>
    public static double RmsDbfs(ReadOnlySpan<float> x)
    {
        if (x.Length == 0) return PisoDb;
        double suma = 0;
        foreach (var v in x) suma += (double)v * v;
        return ADb(Math.Sqrt(suma / x.Length));
    }

    public static double ADb(double amplitud) => amplitud <= 1e-6 ? PisoDb : Math.Max(PisoDb, 20 * Math.Log10(amplitud));

    /// <summary>
    /// Amplitud estimada de la senoidal de frecuencia <paramref name="hz"/> (Goertzel con ventana de Hann,
    /// que baja la fuga espectral entre 440 y 880 Hz). Una senoidal de amplitud A devuelve ~A.
    /// </summary>
    public static double Amplitud(ReadOnlySpan<float> x, double hz, int frecuenciaMuestreo)
    {
        if (x.Length < 8) return 0;
        return AmplitudEnventanada(Enventanar(x), hz, frecuenciaMuestreo);
    }

    /// <summary>La señal multiplicada por una ventana de Hann (se calcula una vez para barrer muchas frecuencias).</summary>
    private static double[] Enventanar(ReadOnlySpan<float> x)
    {
        var n = x.Length;
        var y = new double[n];
        for (var i = 0; i < n; i++) y[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        return y;
    }

    private static double AmplitudEnventanada(double[] y, double hz, int frecuenciaMuestreo)
    {
        var n = y.Length;
        var coef = 2 * Math.Cos(2 * Math.PI * hz / frecuenciaMuestreo);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < n; i++)
        {
            var s0 = y[i] + coef * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var potencia = s1 * s1 + s2 * s2 - coef * s1 * s2;
        return 4 * Math.Sqrt(Math.Max(0, potencia)) / n;
    }

    /// <summary>Barre desde..hasta cada paso Hz y devuelve la frecuencia de mayor amplitud.</summary>
    public static (double Hz, double Amplitud) FrecuenciaDominante(
        ReadOnlySpan<float> x, int frecuenciaMuestreo, double desde = 100, double hasta = 2000, double paso = 5)
    {
        var mejorHz = 0.0;
        var mejorA = 0.0;
        if (x.Length < 8) return (mejorHz, mejorA);
        var y = Enventanar(x);
        for (var f = desde; f <= hasta; f += paso)
        {
            var a = AmplitudEnventanada(y, f, frecuenciaMuestreo);
            if (a > mejorA) { mejorA = a; mejorHz = f; }
        }
        return (mejorHz, mejorA);
    }

    /// <summary>Cuántos dB está "otra" por debajo de "esperada" (negativo = bien separada).</summary>
    public static double SeparacionDb(double esperada, double otra) => ADb(otra) - ADb(esperada);

    /// <summary>
    /// WAVEFORMATEX de 18 bytes para PCM flotante de 32 bits. Con process loopback no sirve GetMixFormat
    /// (falla): hay que pasar un formato fijo.
    /// </summary>
    public static byte[] FormatoFlotante32(int frecuencia, int canales)
    {
        var b = new byte[18];
        var blockAlign = (ushort)(canales * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), 3); // WAVE_FORMAT_IEEE_FLOAT
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)canales);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)frecuencia);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(frecuencia * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 0); // cbSize
        return b;
    }

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS: tipo (1 = process loopback), PID, modo (0 = incluir árbol, 1 = excluir árbol).</summary>
    public static byte[] ParametrosActivacion(uint pid, bool excluir)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), pid);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), excluir ? 1 : 0);
        return b;
    }
}
