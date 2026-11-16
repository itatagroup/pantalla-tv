using System.Buffers.Binary;
using System.Drawing;
using System.Text.Json;

namespace PantallaTv.Emisor;

/// <summary>Una ventana de nivel superior visible, con el proceso que la dibuja y el programa (ruta del .exe) de ese proceso.</summary>
internal sealed record VentanaVisible(int Pid, string Exe, Rectangle Limites);

/// <summary>Un proceso en marcha: su padre sirve para saber cuáles son «raíz» de un mismo programa (el árbol entero se captura junto).</summary>
internal sealed record ProcesoInfo(int Pid, int PidPadre, string Exe);

/// <summary>Las ventanas visibles del escritorio ahora mismo (una versión falsa en las pruebas).</summary>
internal interface IVentanas
{
    IReadOnlyList<VentanaVisible> Listar();
}

/// <summary>Los procesos de unos programas dados (una versión falsa en las pruebas).</summary>
internal interface IProcesos
{
    /// <summary>Todos los procesos vivos cuyo ejecutable es alguno de <paramref name="exes"/> (rutas completas).</summary>
    IReadOnlyList<ProcesoInfo> De(IReadOnlyCollection<string> exes);
}

/// <summary>El resultado de mirar qué programas tienen ventana en la tele.</summary>
/// <param name="EnTele">Programas (ruta del .exe) con alguna ventana mayoritariamente en el monitor transmitido.</param>
/// <param name="TambienEnPc">De esos, los que además tienen ventanas visibles en otra pantalla: su sonido va a la tele igual.</param>
internal sealed record SeleccionAudio(IReadOnlySet<string> EnTele, IReadOnlySet<string> TambienEnPc)
{
    public static readonly SeleccionAudio Vacia = new(new HashSet<string>(), new HashSet<string>());

    /// <summary>Programas del sistema y de la propia herramienta: su sonido nunca se mueve (sin extensión, sin mayúsculas).</summary>
    internal static readonly IReadOnlySet<string> Excluidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PantallaTv", "explorer", "dwm", "csrss", "winlogon", "fontdrvhost", "sihost", "taskhostw", "RuntimeBroker",
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "LockApp",
        "ApplicationFrameHost", "SystemSettingsBroker", "ctfmon", "SecurityHealthSystray", "WidgetService", "Widgets",
        "CompPkgSrv", "PhoneExperienceHost", "ShellHost", "svchost", "conhost",
    };

    public static bool EsExcluido(string exe) =>
        Excluidos.Contains(Path.GetFileNameWithoutExtension(exe));

    /// <summary>
    /// Qué programas van a la tele: los que tienen una ventana con más de la mitad de su área dentro de
    /// <paramref name="monitor"/>. Los que además tienen otra ventana fuera de él quedan en <see cref="TambienEnPc"/>.
    /// </summary>
    public static SeleccionAudio Elegir(IEnumerable<VentanaVisible> ventanas, Rectangle monitor)
    {
        var enTele = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fuera = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in ventanas)
        {
            if (v.Exe.Length == 0 || EsExcluido(v.Exe)) continue;
            var area = (long)v.Limites.Width * v.Limites.Height;
            if (area <= 0) continue;
            var comun = Rectangle.Intersect(v.Limites, monitor);
            var dentro = comun.IsEmpty ? 0 : (long)comun.Width * comun.Height;
            if (dentro * 2 > area) enTele.Add(v.Exe);
            else fuera.Add(v.Exe);
        }
        fuera.IntersectWith(enTele);
        return new SeleccionAudio(enTele, fuera);
    }
}

/// <summary>Qué cambió entre dos miradas (una por segundo).</summary>
internal sealed record DiferenciaApps(IReadOnlyList<string> Entran, IReadOnlyList<string> Salen)
{
    public static DiferenciaApps Calcular(IReadOnlySet<string> antes, IReadOnlySet<string> ahora) =>
        new([.. ahora.Where(e => !antes.Contains(e)).OrderBy(e => e, StringComparer.OrdinalIgnoreCase)],
            [.. antes.Where(e => !ahora.Contains(e)).OrderBy(e => e, StringComparer.OrdinalIgnoreCase)]);

    public bool HayCambios => Entran.Count > 0 || Salen.Count > 0;

    /// <summary>El nombre para mostrar: «chrome» en vez de «C:\Program Files\Google\Chrome\Application\chrome.exe».</summary>
    public static string Nombre(string exe) => Path.GetFileNameWithoutExtension(exe);
}

/// <summary>
/// Junta lo que captura cada programa en un solo flujo: PCM de 16 bits, estéreo, 48000 Hz, en paquetes de 20 ms
/// (la misma forma que entrega <see cref="CapturaAudio"/>). Cada fuente tiene un colchón de un paquete para que
/// las llegadas irregulares no dejen huecos; lo que se pasa de 500 ms se tira (sin acumular retraso).
/// Si ninguna fuente tiene sonido, no hay paquete (como la captura de todo el PC: en silencio no se manda nada).
/// </summary>
internal sealed class MezcladorPcm
{
    public const int Tasa = 48000;
    public const int Canales = 2;
    public const int MsPorPaquete = 20;
    public const int MuestrasPorPaquete = Tasa * MsPorPaquete / 1000 * Canales;
    private const int MuestrasMaximas = MuestrasPorPaquete * 25;

    private sealed class Fuente
    {
        public readonly List<float> Pendiente = [];
        public bool Cebada;
        public bool LlegoEsteTurno;
        public int TurnosSinLlegar;
    }

    private readonly Dictionary<int, Fuente> _fuentes = [];
    private readonly object _cerrojo = new();

    public void Agregar(int fuente, ReadOnlySpan<float> muestras)
    {
        if (muestras.Length == 0) return;
        lock (_cerrojo)
        {
            if (!_fuentes.TryGetValue(fuente, out var f)) _fuentes[fuente] = f = new Fuente();
            f.Pendiente.AddRange(muestras);
            f.LlegoEsteTurno = true;
            if (f.Pendiente.Count > MuestrasMaximas) f.Pendiente.RemoveRange(0, f.Pendiente.Count - MuestrasMaximas);
            if (f.Pendiente.Count >= MuestrasPorPaquete) f.Cebada = true;
        }
    }

    public void Quitar(int fuente)
    {
        lock (_cerrojo) _fuentes.Remove(fuente);
    }

    /// <summary>El siguiente paquete de 20 ms, o null si ninguna fuente tiene sonido. Se llama cada 20 ms.</summary>
    public byte[]? TomarPaquete()
    {
        lock (_cerrojo)
        {
            var aportes = new List<float[]>();
            foreach (var f in _fuentes.Values)
            {
                if (f.LlegoEsteTurno) f.TurnosSinLlegar = 0; else f.TurnosSinLlegar++;
                f.LlegoEsteTurno = false;
                if (f.Pendiente.Count == 0) { f.Cebada = false; continue; }
                var completo = f.Pendiente.Count >= MuestrasPorPaquete;
                // Sin colchón no se toca (se espera a que junte un paquete); pero lo que queda de una fuente que dejó de
                // llegar —el final de un sonido— sale igual, con ceros, para que no se oiga después como un resto viejo.
                if (!f.Cebada && !completo && f.TurnosSinLlegar < 2) continue;
                var n = Math.Min(f.Pendiente.Count, MuestrasPorPaquete);
                var trozo = new float[MuestrasPorPaquete];
                f.Pendiente.CopyTo(0, trozo, 0, n);
                f.Pendiente.RemoveRange(0, n);
                if (n < MuestrasPorPaquete || f.Pendiente.Count == 0) f.Cebada = false;
                aportes.Add(trozo);
            }
            return aportes.Count == 0 ? null : Sumar(aportes);
        }
    }

    /// <summary>Suma las fuentes muestra a muestra y lo pasa a PCM de 16 bits: lo que se sale de rango se satura, no se desborda.</summary>
    public static byte[] Sumar(IReadOnlyList<float[]> fuentes)
    {
        var largo = fuentes.Count == 0 ? 0 : fuentes.Max(f => f.Length);
        var salida = new byte[largo * 2];
        for (var i = 0; i < largo; i++)
        {
            var suma = 0f;
            foreach (var f in fuentes)
                if (i < f.Length && !float.IsNaN(f[i])) suma += f[i];
            BinaryPrimitives.WriteInt16LittleEndian(salida.AsSpan(i * 2, 2), (short)Math.Round(Math.Clamp(suma, -1f, 1f) * short.MaxValue));
        }
        return salida;
    }
}

/// <summary>Lo que había guardado para la salida de un programa antes de mandarlo a la tele (ver <see cref="RutasPersistidas"/>).</summary>
/// <param name="Exe">Ruta completa del .exe: la preferencia de Windows es por programa, no por proceso.</param>
/// <param name="Multimedia">Salida guardada para el rol multimedia; vacío = ninguna.</param>
/// <param name="Consola">Salida guardada para el rol consola; vacío = ninguna.</param>
internal sealed record RutaGuardada(string Exe, string Multimedia, string Consola);

/// <summary>
/// Las rutas de audio que cambiamos, en <c>%AppData%\PantallaTv\[perfil-X\]rutas-audio.json</c>. Se escribe ANTES de
/// tocar Windows: la preferencia es persistente, y si el programa se cae sin devolverla, esa app seguiría sonando
/// en el cable (o sea, en silencio) para siempre. Al abrir el programa, o con <c>--restaurar-audio</c>, se devuelve.
/// </summary>
internal sealed class RutasPersistidas(string ruta)
{
    private readonly object _cerrojo = new();

    public static string RutaPorDefecto => Path.Combine(Path.GetDirectoryName(Ajustes.RutaPorDefecto)!, "rutas-audio.json");

    public string Ruta { get; } = ruta;

    public IReadOnlyList<RutaGuardada> Todas()
    {
        lock (_cerrojo) return [.. Leer()];
    }

    public bool Contiene(string exe)
    {
        lock (_cerrojo) return Leer().Any(r => Igual(r.Exe, exe));
    }

    public RutaGuardada? De(string exe)
    {
        lock (_cerrojo) return Leer().FirstOrDefault(r => Igual(r.Exe, exe));
    }

    /// <summary>Anota lo que había. Si ese programa ya estaba anotado no se pisa: lo primero que se guardó es lo original.</summary>
    public void Agregar(RutaGuardada r)
    {
        lock (_cerrojo)
        {
            var lista = Leer();
            if (lista.Any(x => Igual(x.Exe, r.Exe))) return;
            lista.Add(r);
            Escribir(lista);
        }
    }

    public void Quitar(string exe)
    {
        lock (_cerrojo)
        {
            var lista = Leer();
            if (lista.RemoveAll(x => Igual(x.Exe, exe)) > 0) Escribir(lista);
        }
    }

    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private List<RutaGuardada> Leer()
    {
        try
        {
            if (!File.Exists(Ruta)) return [];
            return JsonSerializer.Deserialize<List<RutaGuardada>>(File.ReadAllText(Ruta)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return []; // un archivo dañado no debe impedir transmitir
        }
    }

    private void Escribir(List<RutaGuardada> lista)
    {
        try
        {
            if (lista.Count == 0)
            {
                if (File.Exists(Ruta)) File.Delete(Ruta);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
            File.WriteAllText(Ruta, JsonSerializer.Serialize(lista, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Registro.Anotar($"audio exclusivo: no se pudo guardar {Ruta}: {e.Message}");
        }
    }

    /// <summary>
    /// Devuelve a Windows lo que había para cada programa anotado y lo borra del archivo. Un programa que no está en marcha
    /// no se puede tocar (la API pide un proceso vivo): queda anotado y se reintenta la próxima vez.
    /// </summary>
    /// <param name="excepto">Programas que se siguen usando ahora mismo: no se tocan.</param>
    public ResultadoRestauracion RestaurarTodo(IRutasAudio rutas, IProcesos procesos, IReadOnlySet<string>? excepto = null)
    {
        var pedidas = Todas().Where(r => excepto is null || !excepto.Contains(r.Exe)).ToList();
        if (pedidas.Count == 0) return new ResultadoRestauracion([], []);
        var vivos = procesos.De([.. pedidas.Select(r => r.Exe)]);
        List<string> hechas = [], pendientes = [];
        foreach (var r in pedidas)
        {
            var pids = vivos.Where(p => Igual(p.Exe, r.Exe)).Select(p => p.Pid).ToList();
            var ok = false;
            foreach (var pid in pids)
            {
                try
                {
                    rutas.Restaurar(pid, r.Multimedia, r.Consola);
                    ok = true;
                    break; // es por programa: con un proceso basta
                }
                catch (InvalidOperationException e)
                {
                    Registro.Anotar($"audio exclusivo: no se pudo devolver la salida de {r.Exe} (pid {pid}): {e.Message}");
                }
            }
            if (ok) { Quitar(r.Exe); hechas.Add(r.Exe); }
            else pendientes.Add(r.Exe);
        }
        return new ResultadoRestauracion(hechas, pendientes);
    }
}

/// <summary>Qué se devolvió y qué quedó pendiente por no estar el programa en marcha.</summary>
internal sealed record ResultadoRestauracion(IReadOnlyList<string> Restauradas, IReadOnlyList<string> Pendientes);
