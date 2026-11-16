using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PantallaTv.Audio.Prueba;

/// <summary>Sesiones de audio del dispositivo de salida por defecto y ventanas por monitor. Solo lectura.</summary>
internal static class Sesiones
{
    public sealed record VentanaMon(string Titulo, string Monitor);

    public static int Listar()
    {
        using var enumerador = new MMDeviceEnumerator();
        using var disp = enumerador.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Console.WriteLine($"Dispositivo de salida: {disp.FriendlyName}  (endpoint mute={disp.AudioEndpointVolume.Mute}, volumen={disp.AudioEndpointVolume.MasterVolumeLevelScalar:F2})");

        var mgr = disp.AudioSessionManager;
        mgr.RefreshSessions();
        var ventanas = VentanasPorProceso();
        var activos = new List<int>();
        var inactivas = 0;

        Console.WriteLine("Sesiones activas (PID, proceso, volumen de sesión, mute):");
        for (var i = 0; i < mgr.Sessions.Count; i++)
        {
            var s = mgr.Sessions[i];
            if (s.State != AudioSessionState.AudioSessionStateActive) { inactivas++; continue; }
            var pid = (int)s.GetProcessID;
            activos.Add(pid);
            Console.WriteLine($"  PID {pid,6}  {NombreProceso(pid),-24} vol={s.SimpleAudioVolume.Volume:F2} mute={s.SimpleAudioVolume.Mute}" +
                              (s.IsSystemSoundsSession ? "  (sonidos del sistema)" : ""));
            if (ventanas.TryGetValue(pid, out var propias)) ImprimirVentanas(propias, "      ");
            else
            {
                var padre = AncestroConVentanas(pid, ventanas);
                if (padre is not null)
                {
                    Console.WriteLine($"      sin ventanas propias; ancestro con ventanas: PID {padre} ({NombreProceso(padre.Value)})");
                    ImprimirVentanas(ventanas[padre.Value], "      ");
                }
                else Console.WriteLine("      sin ventanas visibles (ni en sus ancestros)");
            }
        }
        Console.WriteLine($"({inactivas} sesión(es) inactiva(s) no listadas)");

        Console.WriteLine();
        Console.WriteLine("Procesos con ventanas visibles y su monitor:");
        foreach (var (pid, lista) in ventanas.OrderBy(k => k.Key))
        {
            Console.WriteLine($"  PID {pid,6}  {NombreProceso(pid),-24}{(activos.Contains(pid) ? "  [con audio]" : "")}");
            ImprimirVentanas(lista, "      ");
        }
        return 0;
    }

    private static void ImprimirVentanas(List<VentanaMon> lista, string sangria)
    {
        foreach (var g in lista.GroupBy(v => v.Monitor))
        {
            var titulos = string.Join(" | ", g.Select(v => Cortar(v.Titulo, 40)).Take(3));
            Console.WriteLine($"{sangria}en {g.Key}: {g.Count()} ventana(s): {titulos}");
        }
    }

    private static string Cortar(string t, int max) => t.Length <= max ? t : t[..max] + "...";

    public static string NombreProceso(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "(salió)"; }
    }

    private static int? AncestroConVentanas(int pid, Dictionary<int, List<VentanaMon>> ventanas)
    {
        var actual = pid;
        for (var i = 0; i < 8; i++)
        {
            var padre = PadreDe(actual);
            if (padre is null or 0) return null;
            if (ventanas.ContainsKey(padre.Value)) return padre;
            actual = padre.Value;
        }
        return null;
    }

    // ---- P/Invoke ----
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref InfoMonitor info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? dispositivo, uint n, ref DispositivoPantalla d, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int valor, int tam);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint acceso, bool heredar, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int clase, ref InfoProceso info, int tam, out int ret);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InfoMonitor
    {
        public int Tam;
        public Rect Monitor, Trabajo;
        public uint Banderas;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Dispositivo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DispositivoPantalla
    {
        public int Tam;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Nombre;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Descripcion;
        public uint Estado;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Clave;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoProceso
    {
        public IntPtr Salida, Peb, Afinidad, Prioridad, Pid, PadrePid;
    }

    private static string NombreMonitor(IntPtr mon)
    {
        var info = new InfoMonitor { Tam = Marshal.SizeOf<InfoMonitor>(), Dispositivo = "" };
        if (!GetMonitorInfo(mon, ref info)) return "(monitor desconocido)";
        var d = new DispositivoPantalla { Tam = Marshal.SizeOf<DispositivoPantalla>(), Nombre = "", Descripcion = "", Id = "", Clave = "" };
        var amigable = EnumDisplayDevices(info.Dispositivo, 0, ref d, 0) ? d.Descripcion : "?";
        var ancho = info.Monitor.R - info.Monitor.L;
        var alto = info.Monitor.B - info.Monitor.T;
        return $"{info.Dispositivo} ({amigable}, {ancho}x{alto} en {info.Monitor.L},{info.Monitor.T})";
    }

    public static int? PadreDe(int pid)
    {
        var h = OpenProcess(0x1000, false, (uint)pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var info = new InfoProceso();
            return NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf<InfoProceso>(), out _) == 0 ? (int)info.PadrePid : null;
        }
        finally { CloseHandle(h); }
    }

    public static Dictionary<int, List<VentanaMon>> VentanasPorProceso()
    {
        var resultado = new Dictionary<int, List<VentanaMon>>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out var oculta, 4) == 0 && oculta != 0) return true; // DWMWA_CLOAKED
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            if (!GetWindowRect(hwnd, out var r) || r.R - r.L < 50 || r.B - r.T < 50) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var nombre = NombreMonitor(MonitorFromWindow(hwnd, 2)) + (IsIconic(hwnd) ? " [minimizada]" : "");
            if (!resultado.TryGetValue((int)pid, out var lista)) resultado[(int)pid] = lista = [];
            lista.Add(new VentanaMon(sb.ToString(), nombre));
            return true;
        }, IntPtr.Zero);
        return resultado;
    }
}
