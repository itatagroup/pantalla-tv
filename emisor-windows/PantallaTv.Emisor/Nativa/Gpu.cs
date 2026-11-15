using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PantallaTv.Emisor.Nativa;

/// <summary>Busca el monitor en DXGI y crea el dispositivo D3D11 del adaptador que lo dibuja.</summary>
internal static class Gpu
{
    /// <summary>
    /// La salida de DXGI de ese monitor, buscada de nuevo cada vez (tras un cambio de modo o una reconexión los
    /// índices y los objetos viejos dejan de valer). Por nombre de Windows (<c>\\.\DISPLAY2</c>); sin nombre, por
    /// índices de adaptador y salida. null si no está conectado al escritorio.
    /// </summary>
    public static (IDXGIAdapter1 Adaptador, IDXGIOutput Salida)? Buscar(string? dispositivo, int adaptador, int salida)
    {
        using var fabrica = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; fabrica.EnumAdapters1(a, out var ad).Success; a++)
        {
            for (uint s = 0; ad.EnumOutputs(s, out var sal).Success; s++)
            {
                var d = sal.Description;
                var es = dispositivo is null
                    ? a == adaptador && s == salida
                    : string.Equals(d.DeviceName, dispositivo, StringComparison.OrdinalIgnoreCase);
                if (es && d.AttachedToDesktop) return (ad, sal);
                sal.Dispose();
            }
            ad.Dispose();
        }
        return null;
    }

    /// <summary>El LUID de DXGI (alto y bajo) en un solo número.</summary>
    public static long ComoLong(Vortice.Luid l) => ((long)l.HighPart << 32) | l.LowPart;

    public static AdaptadorGpu Describir(IDXGIAdapter1 adaptador)
    {
        var d = adaptador.Description1;
        return new AdaptadorGpu(ComoLong(d.Luid), d.Description.Trim(), d.VendorId, MemoriaDedicada(d), (d.Flags & AdapterFlags.Software) != 0);
    }

    /// <summary>
    /// La memoria de vídeo dedicada en bytes. <c>DedicatedVideoMemory</c> es un <c>PointerUSize</c> de SharpGen y un
    /// <c>(long)</c> directo pasa por su conversión a <c>uint</c>, que es <i>checked</i>: con más de 4 GB (una RTX 3050 de 6 GB)
    /// lanzaba <see cref="OverflowException"/> y la tubería nativa se descartaba entera. Se convierte por <c>nuint</c>.
    /// </summary>
    public static long MemoriaDedicada(AdapterDescription1 d) => (long)(ulong)(nuint)d.DedicatedVideoMemory;

    /// <summary>Todas las gráficas que ve DXGI (con o sin pantallas conectadas).</summary>
    public static IReadOnlyList<AdaptadorGpu> Adaptadores()
    {
        var lista = new List<AdaptadorGpu>();
        using var fabrica = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; fabrica.EnumAdapters1(a, out var ad).Success; a++)
            using (ad) lista.Add(Describir(ad));
        return lista;
    }

    /// <summary>El adaptador con ese LUID; quien lo recibe lo libera. null si ya no está.</summary>
    public static IDXGIAdapter1? PorLuid(long luid)
    {
        using var fabrica = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; fabrica.EnumAdapters1(a, out var ad).Success; a++)
        {
            if (ComoLong(ad.Description1.Luid) == luid) return ad;
            ad.Dispose();
        }
        return null;
    }

    /// <summary>Dispositivo D3D11 con soporte de vídeo, protegido para varios hilos (lo exige Media Foundation).</summary>
    public static (ID3D11Device Dispositivo, ID3D11DeviceContext Contexto) Abrir(IDXGIAdapter1 adaptador)
    {
        D3D11.D3D11CreateDevice(adaptador, DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device dispositivo, out ID3D11DeviceContext contexto).CheckError();
        using (var mt = dispositivo!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        return (dispositivo, contexto!);
    }

    /// <summary>
    /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2: sin él <c>DuplicateOutput1</c> devuelve
    /// <c>DXGI_ERROR_UNSUPPORTED</c> (N1, problema 1). La bandeja ya lo es (WinForms, <c>PerMonitorV2</c>); el modo
    /// consola no inicia WinForms. Si el proceso ya tenía un modo, Windows no lo cambia y esto no hace nada.
    /// </summary>
    public static void ConscienteDeDpi() => SetProcessDpiAwarenessContext(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint valor);
}
