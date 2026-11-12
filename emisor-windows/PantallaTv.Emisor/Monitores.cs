namespace PantallaTv.Emisor;

/// <summary>Un monitor conectado al PC (real o virtual) y cómo lo encuentra ffmpeg.</summary>
/// <param name="Indice">Posición en la lista (para el menú y <c>--monitor N</c>).</param>
/// <param name="Nombre">Nombre de Windows (<c>\\.\DISPLAY2</c>); es lo que se guarda en los ajustes.</param>
/// <param name="Adaptador">Adaptador de vídeo del que cuelga (índice de DXGI).</param>
/// <param name="Salida">Salida dentro de ese adaptador (índice de DXGI).</param>
/// <param name="Descripcion">Cómo llama Windows al monitor («Generic Monitor (VDD by MTT)»); sirve para reconocer el virtual.</param>
internal sealed record Monitor(
    int Indice, string Nombre, int Ancho, int Alto, bool Principal,
    int Adaptador, int Salida, string NombreAdaptador, string Descripcion = "")
{
    public override string ToString() =>
        $"Monitor {Indice + 1} · {Ancho}×{Alto}{(Principal ? " (principal)" : "")}"
        + (Descripcion.Length > 0 ? $" · {Descripcion}" : "")
        + (Adaptador != 0 ? $" · {NombreAdaptador}" : "");
}

/// <summary>
/// Los monitores que se pueden transmitir. Con un controlador de pantalla virtual instalado, el
/// monitor virtual aparece aquí como uno más: transmitirlo es «proyectar» un escritorio extendido a la
/// tele mientras el PC sigue libre (docs/monitor-virtual.md).
/// </summary>
/// <summary>Lo poco del EDID (los datos con que un monitor se identifica) que hace falta aquí.</summary>
internal static class Edid
{
    /// <summary>El nombre del monitor: el descriptor de tipo 0xFC, uno de los cuatro bloques de 18 bytes desde el 54.</summary>
    public static string Nombre(ReadOnlySpan<byte> edid)
    {
        for (var i = 54; i + 18 <= Math.Min(edid.Length, 126); i += 18)
        {
            var b = edid.Slice(i, 18);
            if (b[0] != 0 || b[1] != 0 || b[2] != 0 || b[3] != 0xFC) continue;
            var texto = b[5..];
            var fin = texto.IndexOf((byte)0x0A); // el nombre termina en salto de línea y se rellena con espacios
            return System.Text.Encoding.ASCII.GetString(fin >= 0 ? texto[..fin] : texto).Trim();
        }
        return "";
    }
}

internal static class Monitores
{
    public static IReadOnlyList<Monitor> Listar() => Combinar(
            Pantallas().Select(p => (p.Nombre, p.Limites.Width, p.Limites.Height, p.Principal)).ToList(),
            Dxgi.Salidas())
        .Select(m => m with { Descripcion = DescripcionDe(m.Nombre) })
        .ToList();

    private const int ConectadaAlEscritorio = 0x1, EsLaPrincipal = 0x4; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP / _PRIMARY_DEVICE

    /// <summary>
    /// Las pantallas conectadas al escritorio ahora mismo, preguntándole a Windows en cada llamada.
    /// No usa <c>Screen.AllScreens</c>: WinForms lo guarda en caché y solo lo refresca con un evento del
    /// sistema, que fuera de la bandeja (modo consola, hilos de tarea) puede no llegar nunca.
    /// </summary>
    internal static IReadOnlyList<(string Nombre, System.Drawing.Rectangle Limites, bool Principal)> Pantallas()
    {
        var r = new List<(string, System.Drawing.Rectangle, bool)>();
        for (uint i = 0; i < 64; i++)
        {
            var d = NuevoDispositivo();
            if (!EnumDisplayDevices(null, i, ref d, 0)) break;
            if ((d.Estado & ConectadaAlEscritorio) == 0) continue;
            if (MonitorVirtual.LimitesActuales(d.Nombre) is { } limites)
                r.Add((d.Nombre, limites, (d.Estado & EsLaPrincipal) != 0));
        }
        return r;
    }

    private const uint ConRutaDeInterfaz = 1; // EDD_GET_DEVICE_INTERFACE_NAME

    /// <summary>
    /// El nombre que el propio monitor declara en su EDID («VDD by MTT», «C24X3F»). Windows suele
    /// llamarlos a todos «Generic PnP Monitor», que no sirve para distinguir el virtual. Vacío si el
    /// monitor no declara nombre (pasa con la pantalla de un portátil) o no se puede leer.
    /// </summary>
    internal static string DescripcionDe(string dispositivo)
    {
        try
        {
            var d = new DispositivoDePantalla { Tamano = System.Runtime.InteropServices.Marshal.SizeOf<DispositivoDePantalla>() };
            if (!EnumDisplayDevices(dispositivo, 0, ref d, ConRutaDeInterfaz) || string.IsNullOrEmpty(d.Id)) return "";
            // \\?\DISPLAY#MTT1337#1&28a6823a&0&UID256#{clase}  →  …\Enum\DISPLAY\MTT1337\1&28a6823a&0&UID256
            var partes = d.Id.Split('#');
            if (partes.Length < 3) return "";
            using var clave = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{partes[1]}\{partes[2]}\Device Parameters");
            return clave?.GetValue("EDID") is byte[] edid ? Edid.Nombre(edid) : "";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? dispositivo, uint indice, ref DispositivoDePantalla d, uint banderas);

    internal static DispositivoDePantalla NuevoDispositivo() =>
        new() { Tamano = System.Runtime.InteropServices.Marshal.SizeOf<DispositivoDePantalla>() };

    internal static bool EnumerarDispositivo(string? dispositivo, uint indice, ref DispositivoDePantalla d, uint banderas) =>
        EnumDisplayDevices(dispositivo, indice, ref d, banderas);

    /// <summary>DISPLAY_DEVICEW de Windows.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal struct DispositivoDePantalla
    {
        public int Tamano;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string Nombre;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string Descripcion;
        public int Estado;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string Clave;
    }

    /// <summary>
    /// Une lo que dice Windows de cada pantalla con el adaptador y la salida que le da DXGI, por nombre
    /// de dispositivo. Si DXGI no conoce una pantalla (no debería), se supone la gráfica principal y la
    /// misma posición: es lo que se hacía antes de consultar DXGI.
    /// </summary>
    internal static IReadOnlyList<Monitor> Combinar(
        IReadOnlyList<(string Nombre, int Ancho, int Alto, bool Principal)> pantallas,
        IReadOnlyList<Dxgi.Salida> salidas)
    {
        var r = new List<Monitor>(pantallas.Count);
        for (var i = 0; i < pantallas.Count; i++)
        {
            var p = pantallas[i];
            var s = salidas.FirstOrDefault(x => string.Equals(x.Dispositivo, p.Nombre, StringComparison.OrdinalIgnoreCase));
            r.Add(new Monitor(i, p.Nombre, p.Ancho, p.Alto, p.Principal,
                s?.Adaptador ?? 0, s?.Indice ?? i, s?.NombreAdaptador ?? ""));
        }
        return r;
    }

    /// <summary>Lo que se pone en los ajustes cuando se elige el monitor virtual: no es un nombre de Windows.</summary>
    public const string ElMonitorVirtual = "virtual";

    /// <summary>Un monitor listo para transmitir y, si hubo que conectar el virtual, cómo quitarlo al terminar.</summary>
    /// <param name="Hz">Los Hz del monitor virtual recién conectado (para no mandar más cuadros que eso); null si no se sabe.</param>
    internal sealed record Preparado(Monitor Monitor, Action Liberar, int? Hz = null);

    /// <summary>
    /// ¿Es el monitor virtual? Se usa como segundo monitor de trabajo, donde medio segundo se nota en el ratón: la
    /// sesión va como interactiva y el receptor la muestra sin búfer (ver protocolo.md, «Búfer»).
    /// </summary>
    public static bool EsElVirtual(Monitor monitor) =>
        MonitorVirtual.Detectar() is { } v && string.Equals(v.Dispositivo, monitor.Nombre, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deja listo el monitor elegido. Con el virtual lo conecta al escritorio (si no lo estaba) y
    /// <see cref="Preparado.Liberar"/> lo quita al terminar; si ya estaba conectado, no lo toca.
    /// Lanza <see cref="EmisionException"/> si no se pudo.
    /// </summary>
    public static Preparado Preparar(string? elegido, Action<string>? avisar = null, (int Ancho, int Alto, int Hz)? modoVirtual = null)
    {
        if (elegido != ElMonitorVirtual) return new Preparado(Elegir(elegido), () => { });

        var virtualMonitor = MonitorVirtual.Detectar();
        if (virtualMonitor is null)
        {
            // Como un monitor desenchufado: se transmite el principal en vez de fallar.
            avisar?.Invoke("El monitor virtual no está instalado; se transmite la pantalla principal.");
            return new Preparado(Elegir(null), () => { });
        }
        avisar?.Invoke("Conectando el monitor virtual…");
        var resultado = virtualMonitor.Conectar(TimeSpan.FromSeconds(20), modoVirtual);
        if (resultado == MonitorVirtual.Resultado.Fallo)
            throw new EmisionException("No se pudo conectar el monitor virtual a Windows. Pruébalo en Configuración → Pantalla.");
        var monitor = Listar().FirstOrDefault(m => string.Equals(m.Nombre, virtualMonitor.Dispositivo, StringComparison.OrdinalIgnoreCase));
        if (monitor is null)
        {
            if (resultado == MonitorVirtual.Resultado.Conectado) virtualMonitor.Liberar();
            throw new EmisionException("Windows conectó el monitor virtual pero no aparece en la lista de pantallas.");
        }
        return new Preparado(monitor, () =>
        {
            if (resultado == MonitorVirtual.Resultado.Conectado) virtualMonitor.Liberar();
        }, virtualMonitor.ModoConectado?.Hz);
    }

    /// <summary>El monitor con ese nombre de Windows o, si ya no existe (se desconectó), el principal.</summary>
    public static Monitor Elegir(string? nombre) => Elegir(Listar(), m => m.Nombre == nombre);

    /// <summary>El monitor en esa posición de la lista o, si no existe, el principal.</summary>
    public static Monitor Elegir(int indice) => Elegir(Listar(), m => m.Indice == indice);

    internal static Monitor Elegir(IReadOnlyList<Monitor> todos, Func<Monitor, bool> esEl) =>
        todos.FirstOrDefault(esEl) ?? todos.FirstOrDefault(m => m.Principal) ?? todos[0];
}
