using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PantallaTv.Emisor;

/// <summary>Cómo reparte Windows las pantallas (lo que cambia Win+P). Los valores son los de <c>DISPLAYCONFIG_TOPOLOGY_ID</c>.</summary>
internal enum TopologiaPantalla
{
    Desconocida = 0,
    SoloPrincipal = 0x1,  // «Solo pantalla del PC»
    Duplicar = 0x2,
    Extender = 0x4,
    SoloSegunda = 0x8,    // «Solo segunda pantalla»
}

/// <summary>
/// La topología de pantallas de Windows (Win+P): consultarla y cambiarla con <c>SetDisplayConfig</c>, la misma API que usa
/// Win+P. Hace falta porque con las pantallas en «Duplicar» Windows rechaza conectar el monitor virtual
/// (<c>ChangeDisplaySettingsEx</c> devuelve -1): hay que pasar a «Extender» antes y volver al terminar.
/// </summary>
internal static class Topologia
{
    private const uint ConsultarBaseDeDatos = 0x4;   // QDC_DATABASE_CURRENT: la que Windows tiene guardada ahora
    private const uint Aplicar = 0x80;               // SDC_APPLY
    private const uint BufferChico = 122;            // ERROR_INSUFFICIENT_BUFFER
    private const int BytesRuta = 72, BytesModo = 64; // DISPLAYCONFIG_PATH_INFO y DISPLAYCONFIG_MODE_INFO

    /// <summary>Con «Duplicar» no se puede conectar el monitor virtual; con las otras, sí.</summary>
    public static bool HayQuePasarAExtender(TopologiaPantalla actual) => actual == TopologiaPantalla.Duplicar;

    /// <summary>Del valor que devuelve Windows al enumerado; lo que no se conoce es <see cref="TopologiaPantalla.Desconocida"/>.</summary>
    public static TopologiaPantalla Interpretar(uint id) =>
        id is 0x1 or 0x2 or 0x4 or 0x8 ? (TopologiaPantalla)id : TopologiaPantalla.Desconocida;

    /// <summary>La topología actual; <see cref="TopologiaPantalla.Desconocida"/> si Windows no la dice (nunca lanza).</summary>
    public static TopologiaPantalla Actual()
    {
        try
        {
            for (var intento = 0; intento < 3; intento++)
            {
                if (GetDisplayConfigBufferSizes(ConsultarBaseDeDatos, out var rutas, out var modos) != 0) return TopologiaPantalla.Desconocida;
                var bufRutas = new byte[Math.Max(1, (int)rutas) * BytesRuta];
                var bufModos = new byte[Math.Max(1, (int)modos) * BytesModo];
                var r = QueryDisplayConfig(ConsultarBaseDeDatos, ref rutas, bufRutas, ref modos, bufModos, out var id);
                if (r == 0) return Interpretar(id);
                if (r != BufferChico) return TopologiaPantalla.Desconocida; // Windows cambió la lista entre las dos llamadas: se repite
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return TopologiaPantalla.Desconocida;
    }

    /// <summary>Pasa a esa topología (como Win+P). Devuelve 0 si Windows la aplicó, o su código de error.</summary>
    public static int PasarA(TopologiaPantalla destino)
    {
        if (destino == TopologiaPantalla.Desconocida) return 87; // ERROR_INVALID_PARAMETER
        try { return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, (uint)destino | Aplicar); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 120; } // ERROR_CALL_NOT_IMPLEMENTED
    }

    /// <summary>El código de error de Windows en palabras, para que quien lo lea no tenga que buscarlo.</summary>
    public static string Explicar(int codigo) => $"{new Win32Exception(codigo).Message.TrimEnd('.')} (error {codigo})";

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint banderas, out uint rutas, out uint modos);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint banderas, ref uint rutas, byte[] infoRutas, ref uint modos, byte[] infoModos, out uint topologia);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint rutas, IntPtr infoRutas, uint modos, IntPtr infoModos, uint banderas);
}
