using System.Globalization;

namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// Coeficientes RGB → YCbCr de rango limitado (16–235 / 16–240), listos para el sombreador.
/// Por omisión BT.601, que es lo que hoy produce <c>format=nv12</c> de ffmpeg (la tubería <c>amf</c>) y lo que
/// Sunshine usa por omisión: así la tele ve los mismos colores que con la tubería actual.
/// </summary>
internal sealed record MatrizColor(double Kr, double Kb)
{
    public static readonly MatrizColor Bt601 = new(0.299, 0.114);
    public static readonly MatrizColor Bt709 = new(0.2126, 0.0722);

    private double Kg => 1 - Kr - Kb;

    /// <summary>Fila de Y: (r, g, b, desplazamiento), con la entrada en 0–1.</summary>
    public (double R, double G, double B, double D) FilaY =>
        (219.0 / 255 * Kr, 219.0 / 255 * Kg, 219.0 / 255 * Kb, 16.0 / 255);

    /// <summary>Fila de Cb (U).</summary>
    public (double R, double G, double B, double D) FilaU
    {
        get
        {
            var e = 224.0 / 255 / (2 * (1 - Kb));
            return (-Kr * e, -Kg * e, (1 - Kb) * e, 128.0 / 255);
        }
    }

    /// <summary>Fila de Cr (V).</summary>
    public (double R, double G, double B, double D) FilaV
    {
        get
        {
            var e = 224.0 / 255 / (2 * (1 - Kr));
            return ((1 - Kr) * e, -Kg * e, -Kb * e, 128.0 / 255);
        }
    }

    /// <summary>Una fila como <c>float4(…)</c> de HLSL.</summary>
    public static string Hlsl((double R, double G, double B, double D) f) =>
        string.Create(CultureInfo.InvariantCulture, $"float4({f.R:R}, {f.G:R}, {f.B:R}, {f.D:R})");

    /// <summary>Aplica la matriz a un color (para pruebas): devuelve Y, U, V en 0–255.</summary>
    public (double Y, double U, double V) Aplicar(byte r, byte g, byte b)
    {
        double F((double R, double G, double B, double D) f) => 255 * (f.R * r / 255 + f.G * g / 255 + f.B * b / 255 + f.D);
        return (F(FilaY), F(FilaU), F(FilaV));
    }
}
