using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PantallaTv.Emisor;

/// <summary>
/// Los colores de la ventana: la paleta oliva. En claro, fondo `#ECEDE6`, superficies blancas y primario verde oliva
/// `#3F6212`; en oscuro, grafito `#22251E` con primario lima `#B5D67A`. Estados y acento elegidos para contraste AA.
/// </summary>
internal sealed record Paleta(
    bool Oscura,
    Color Fondo, Color Superficie, Color SuperficieSuave, Color SuperficieApagada,
    Color Texto, Color TextoFuerte, Color Secundario,
    Color Linea, Color LineaFuerte,
    Color Primario, Color PrimarioFuerte, Color SobrePrimario,
    Color Correcto, Color Error, Color Advertencia)
{
    public static readonly Paleta Clara = new(
        Oscura: false,
        Fondo: Hex(0xECEDE6), Superficie: Hex(0xFFFFFF), SuperficieSuave: Hex(0xF5F5F1), SuperficieApagada: Hex(0xE4E6DC),
        Texto: Hex(0x1F2416), TextoFuerte: Hex(0x151A0D), Secundario: Hex(0x52574A),
        Linea: Hex(0xDCDED4), LineaFuerte: Hex(0xC4C8B8),
        Primario: Hex(0x3F6212), PrimarioFuerte: Hex(0x365314), SobrePrimario: Hex(0xFFFFFF),
        Correcto: Hex(0x336B2F), Error: Hex(0x93203A), Advertencia: Hex(0xA8380A));

    public static readonly Paleta OscuraGrafito = new(
        Oscura: true,
        Fondo: Hex(0x22251E), Superficie: Hex(0x2B2F26), SuperficieSuave: Hex(0x262A21), SuperficieApagada: Hex(0x343930),
        Texto: Hex(0xECEEE6), TextoFuerte: Hex(0xF7F8F3), Secundario: Hex(0xB3B8A9),
        Linea: Hex(0x3C4135), LineaFuerte: Hex(0x4E5446),
        Primario: Hex(0xB5D67A), PrimarioFuerte: Hex(0xCBE59B), SobrePrimario: Hex(0x1E2216),
        Correcto: Hex(0x9ED69A), Error: Hex(0xF3A0B0), Advertencia: Hex(0xFDA874));

    /// <summary>
    /// El borde de un control que se puede usar (casilla, opción, lista). Los de la web (<see cref="LineaFuerte"/>)
    /// no llegan al 3:1 que pide AA para lo que no es texto: se usa el secundario, que sí.
    /// </summary>
    public Color BordeControl => Secundario;

    /// <summary>Color del punto y del texto del estado. Nunca va solo: siempre acompaña al texto del estado.</summary>
    public Color DeFase(Fase fase) => fase switch
    {
        Fase.Transmitiendo => Correcto,
        Fase.Conectando or Fase.Reconectando => Advertencia,
        Fase.Error => Error,
        _ => Secundario,
    };

    private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>Contraste WCAG 2 entre dos colores opacos (1 a 21).</summary>
    public static double Contraste(Color a, Color b)
    {
        double la = Luminancia(a), lb = Luminancia(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminancia(Color c)
    {
        static double Canal(int v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Canal(c.R) + 0.7152 * Canal(c.G) + 0.0722 * Canal(c.B);
    }
}

/// <summary>Qué paleta toca: la de las aplicaciones de Windows (Configuración → Personalización → Colores).</summary>
internal static class Tema
{
    /// <summary>
    /// Solo para desarrollar y sacar capturas sin cambiar el tema de Windows: <c>PANTALLATV_TEMA=claro|oscuro</c>.
    /// </summary>
    public static Paleta Actual()
    {
        var forzado = Environment.GetEnvironmentVariable("PANTALLATV_TEMA");
        if (string.Equals(forzado, "oscuro", StringComparison.OrdinalIgnoreCase)) return Paleta.OscuraGrafito;
        if (string.Equals(forzado, "claro", StringComparison.OrdinalIgnoreCase)) return Paleta.Clara;
        return WindowsUsaOscuro() ? Paleta.OscuraGrafito : Paleta.Clara;
    }

    private static bool WindowsUsaOscuro()
    {
        try
        {
            using var clave = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return clave?.GetValue("AppsUseLightTheme") is int claro && claro == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>La barra de título oscura de Windows 10 (20H1) y 11, para que no quede blanca sobre la ventana oscura.</summary>
    public static void BarraDeTitulo(IntPtr ventana, bool oscura)
    {
        var valor = oscura ? 1 : 0;
        _ = DwmSetWindowAttribute(ventana, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref valor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamano);
}
