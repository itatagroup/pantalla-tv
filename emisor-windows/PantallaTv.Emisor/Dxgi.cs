using System.Runtime.InteropServices;

namespace PantallaTv.Emisor;

/// <summary>
/// Pregunta a Windows (DXGI) qué salidas de vídeo hay y de qué adaptador cuelga cada una. Hace falta
/// porque ffmpeg captura por «adaptador N, salida M», y un monitor virtual no cuelga de la gráfica
/// principal sino de un adaptador propio: con solo el índice de Windows no se lo encontraría.
/// </summary>
internal static class Dxgi
{
    /// <param name="Adaptador">Índice del adaptador (el que ffmpeg recibe en <c>d3d11va=…:N</c>).</param>
    /// <param name="Indice">Índice de la salida dentro de ese adaptador (el de <c>ddagrab</c>).</param>
    /// <param name="Dispositivo">Nombre de Windows, p. ej. <c>\\.\DISPLAY1</c>: el mismo de <see cref="Screen.DeviceName"/>.</param>
    public sealed record Salida(int Adaptador, int Indice, string Dispositivo, string NombreAdaptador);

    private const int NoEncontrado = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND: no hay más

    /// <summary>Todas las salidas conectadas al escritorio. Vacío si DXGI no responde (no debería).</summary>
    public static IReadOnlyList<Salida> Salidas()
    {
        var salidas = new List<Salida>();
        try
        {
            var iid = typeof(IDXGIFactory).GUID;
            if (CreateDXGIFactory(ref iid, out var fabrica) < 0 || fabrica is null) return salidas;
            try
            {
                for (uint a = 0; fabrica.EnumAdapters(a, out var adaptador) != NoEncontrado && adaptador is not null; a++)
                {
                    try
                    {
                        adaptador.GetDesc(out var descAdaptador);
                        for (uint s = 0; adaptador.EnumOutputs(s, out var salida) != NoEncontrado && salida is not null; s++)
                        {
                            try
                            {
                                salida.GetDesc(out var desc);
                                if (desc.AttachedToDesktop != 0)
                                    salidas.Add(new Salida((int)a, (int)s, desc.DeviceName, descAdaptador.Description));
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(salida);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adaptador);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(fabrica);
            }
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            // Sin DXGI se sigue con el índice de Windows (ver Monitores).
        }
        return salidas;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory? fabrica);

    // Las interfaces COM se declaran con todos sus métodos en orden (aunque no se usen): la posición
    // en la tabla es lo que cuenta. Los cuatro primeros de cada una son los de IDXGIObject.

    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        [PreserveSig] int EnumAdapters(uint indice, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter? adaptador);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        [PreserveSig] int EnumOutputs(uint indice, [MarshalAs(UnmanagedType.Interface)] out IDXGIOutput? salida);
        void GetDesc(out DescAdaptador desc);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void GetDesc(out DescSalida desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DescAdaptador
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DescSalida
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Izquierda;
        public int Arriba;
        public int Derecha;
        public int Abajo;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }
}
