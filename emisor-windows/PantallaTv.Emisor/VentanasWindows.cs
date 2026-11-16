using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PantallaTv.Emisor;

/// <summary>Procesos y ventanas de Windows para <see cref="AudioExclusivo"/>: lo que no se puede probar sin un escritorio de verdad.</summary>
internal static class Win32Procesos
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EntradaProceso
    {
        public uint Tamano;
        public uint Uso;
        public uint Pid;
        public IntPtr IdHeap;
        public uint IdModulo;
        public uint Hilos;
        public uint PidPadre;
        public int PrioridadBase;
        public uint Banderas;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint banderas, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr instantanea, ref EntradaProceso entrada);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr instantanea, ref EntradaProceso entrada);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint acceso, bool heredar, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr proceso, uint banderas, StringBuilder ruta, ref uint largo);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr objeto);

    /// <summary>La ruta completa del .exe de un proceso; vacío si no se puede leer (sistema, otro usuario, ya terminó).</summary>
    public static string RutaDe(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var texto = new StringBuilder(1024);
            var largo = (uint)texto.Capacity;
            return QueryFullProcessImageNameW(h, 0, texto, ref largo) ? texto.ToString(0, (int)largo) : "";
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Todos los procesos (pid, padre, nombre del archivo) en una sola llamada.</summary>
    public static List<(int Pid, int PidPadre, string Nombre)> Todos()
    {
        var lista = new List<(int, int, string)>();
        var s = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (s == new IntPtr(-1)) return lista;
        try
        {
            var e = new EntradaProceso { Tamano = (uint)Marshal.SizeOf<EntradaProceso>() };
            for (var ok = Process32FirstW(s, ref e); ok; ok = Process32NextW(s, ref e))
                lista.Add(((int)e.Pid, (int)e.PidPadre, e.Exe));
        }
        finally { CloseHandle(s); }
        return lista;
    }
}

internal sealed class ProcesosWindows : IProcesos
{
    public IReadOnlyList<ProcesoInfo> De(IReadOnlyCollection<string> exes)
    {
        if (exes.Count == 0) return [];
        var nombres = exes.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resultado = new List<ProcesoInfo>();
        foreach (var (pid, padre, nombre) in Win32Procesos.Todos())
        {
            if (!nombres.Contains(nombre)) continue; // la ruta cuesta una llamada por proceso: solo a los que se llaman igual
            var ruta = Win32Procesos.RutaDe(pid);
            if (ruta.Length > 0 && exes.Contains(ruta, StringComparer.OrdinalIgnoreCase)) resultado.Add(new ProcesoInfo(pid, padre, ruta));
        }
        return resultado;
    }
}

/// <summary>
/// Las ventanas que importan para saber en qué pantalla está una app: visibles, no minimizadas, no «cloaked» (las de otros
/// escritorios virtuales y las apps de la tienda suspendidas), no de herramientas. Las ventanas de la tienda (UWP) las
/// dibuja <c>ApplicationFrameHost</c>: el proceso de la app es el de la ventana hija «CoreWindow».
/// </summary>
internal sealed class VentanasWindows : IVentanas
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private const int DwmaExtendedFrameBounds = 9, DwmaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parametro);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Izq, Arriba, Der, Abajo; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr parametro);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr padre, EnumWindowsProc proc, IntPtr parametro);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int indice);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int atributo, out int valor, int tamano);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int atributo, out Rect valor, int tamano);

    public IReadOnlyList<VentanaVisible> Listar()
    {
        var ventanas = new List<VentanaVisible>();
        var rutas = new Dictionary<int, string>(); // una ruta por proceso y mirada
        string RutaCacheada(int pid)
        {
            if (!rutas.TryGetValue(pid, out var r)) rutas[pid] = r = Win32Procesos.RutaDe(pid);
            return r;
        }

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            var estilo = GetWindowLong(hwnd, GwlExStyle);
            if ((estilo & WsExToolWindow) != 0 && (estilo & WsExAppWindow) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var rutaProceso = RutaCacheada((int)pid);
            if (rutaProceso.Length == 0) return true;

            // Una ventana de la tienda: el proceso que suena es el de su ventana hija, no el que la dibuja.
            if (string.Equals(Path.GetFileNameWithoutExtension(rutaProceso), "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                var real = ProcesoDeLaApp(hwnd, pid);
                if (real == 0) return true;
                rutaProceso = RutaCacheada(real);
                pid = (uint)real;
                if (rutaProceso.Length == 0) return true;
            }

            Rect r;
            if (DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out r, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out r)) return true;
            var limites = Rectangle.FromLTRB(r.Izq, r.Arriba, r.Der, r.Abajo);
            if (limites.Width > 0 && limites.Height > 0) ventanas.Add(new VentanaVisible((int)pid, rutaProceso, limites));
            return true;
        }, IntPtr.Zero);
        return ventanas;
    }

    /// <summary>El proceso de la ventana hija de otro proceso (la «CoreWindow» de una app de la tienda); 0 si no hay.</summary>
    private static int ProcesoDeLaApp(IntPtr marco, uint pidMarco)
    {
        var encontrado = 0;
        EnumChildWindows(marco, (hijo, _) =>
        {
            GetWindowThreadProcessId(hijo, out var pid);
            if (pid == pidMarco) return true;
            encontrado = (int)pid;
            return false;
        }, IntPtr.Zero);
        return encontrado;
    }
}
