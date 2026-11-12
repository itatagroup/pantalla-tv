using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PantallaTv.Emisor;

/// <summary>
/// Modo de consola para probar y automatizar sin la bandeja.
/// <code>
/// PantallaTv.exe --descubrir [--seg 6]
/// PantallaTv.exe --cli --ip 10.0.0.20 [--retardo 2000] [--fps 60] [--kbps 8000] [--seg 30] [--telemetria] [--sin-audio] [--no-silenciar] [--tuberia nativa|amf-gpu|amf|nvenc|qsv|mf|x264] (sin --tuberia: la nativa y, si falla, las de ffmpeg si está) [--clave 4] [--codec-extra "-enforce_hrd 0"] [--monitor 1|virtual] [--reconectar]
///     (solo con la nativa:) [--latido 30] [--sin-cursor]; --kbps es el tope y --clave, una clave de seguridad (0 = solo a pedido)
/// PantallaTv.exe --monitores | --audio-info | --quitar-silencio
/// PantallaTv.exe --diagnostico   (informe para mandar si algo no funciona; queda en %LOCALAPPDATA%\PantallaTv\)
/// PantallaTv.exe --monitor-virtual estado|conectar|desconectar
/// PantallaTv.exe --manifiesto-url https://servidor/version.json
/// </code>
/// <c>--manifiesto-url</c> (para probar las actualizaciones) abre el programa de bandeja normal, pero lee el
/// <c>version.json</c> de esa dirección https (y la firma de la misma más <c>.sig</c>) en vez del último release de GitHub.
/// No abre ningún hueco: la firma con la clave del proyecto se exige igual, y la descarga tiene que seguir siendo de
/// <c>https://github.com/itatagroup/pantalla-tv/releases/download/</c>. Si el programa ya estaba abierto no cambia nada
/// (la segunda instancia solo le pide que muestre su ventana): ciérralo antes o usa <c>PANTALLATV_PERFIL</c>.
/// </summary>
internal static class Cli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    public static bool Pedido(string[] args) => args.Contains("--cli") || args.Contains("--diagnostico") || args.Contains("--descubrir") || args.Contains("--audio-info") || args.Contains("--quitar-silencio") || args.Contains("--monitores") || args.Contains("--monitor-virtual");

    public static async Task<int> EjecutarAsync(string[] args)
    {
        var conConsola = AttachConsole(-1); // la consola de quien nos lanzó (el programa es de ventanas)
        if (args.Contains("--diagnostico")) return Diagnosticar(conConsola);
        if (args.Contains("--quitar-silencio")) return QuitarSilencio();
        if (args.Contains("--audio-info")) return AudioInfo();
        if (args.Contains("--monitor-virtual")) return ComandoMonitorVirtual(Texto(args, "--monitor-virtual"));
        if (args.Contains("--monitores"))
        {
            foreach (var m in Monitores.Listar())
                Console.WriteLine($"{m.Indice}\t{m.Ancho}x{m.Alto}\t{(m.Principal ? "principal" : "")}\t{m.Nombre}\t{m.Descripcion}\tadaptador {m.Adaptador} ({m.NombreAdaptador}), salida {m.Salida}");
            Console.WriteLine($"monitor virtual: {EstadoMonitorVirtual()}");
            return 0;
        }
        var seg = Numero(args, "--seg", args.Contains("--descubrir") ? 6 : 0);
        return args.Contains("--descubrir") ? await DescubrirAsync(seg) : await TransmitirAsync(args, seg);
    }

    /// <summary>Sin consola (lanzado desde un acceso directo o con doble clic) el informe se abre en el Bloc de notas.</summary>
    private static int Diagnosticar(bool conConsola)
    {
        var ruta = Diagnostico.Generar(conConsola ? Console.WriteLine : null);
        if (conConsola) Console.WriteLine($"{Environment.NewLine}Guardado en {ruta}");
        else Diagnostico.Abrir(ruta);
        return 0;
    }

    /// <summary>El dispositivo de salida por defecto, su formato y si está en silencio. Lanza si no hay.</summary>
    internal static string DescribirAudio()
    {
        using var dispositivos = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var d = dispositivos.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        var f = d.AudioClient.MixFormat;
        return $"{d.FriendlyName}: {f.SampleRate} Hz, {f.Channels} canales, {f.BitsPerSample} bits; silenciado={d.AudioEndpointVolume.Mute}; volumen={d.AudioEndpointVolume.MasterVolumeLevelScalar:P0}";
    }

    internal static string EstadoMonitorVirtual() => MonitorVirtual.Detectar() is { } v
        ? $"instalado ({v.Dispositivo}), {(v.EstaConectado() ? "conectado al escritorio" : "desconectado")}"
        : "no instalado";

    /// <summary>Diagnóstico del monitor virtual: <c>estado</c>, <c>conectar</c> o <c>desconectar</c>.</summary>
    private static int ComandoMonitorVirtual(string? orden)
    {
        if (orden is not ("estado" or "conectar" or "desconectar"))
        {
            Console.Error.WriteLine("Uso: --monitor-virtual estado|conectar|desconectar");
            return 1;
        }
        var v = MonitorVirtual.Detectar();
        if (v is null)
        {
            Console.WriteLine("monitor virtual: no instalado");
            return orden == "estado" ? 0 : 2;
        }
        if (orden == "conectar")
        {
            var r = v.Conectar(TimeSpan.FromSeconds(20));
            Console.WriteLine($"conectar: {r}");
            if (r == MonitorVirtual.Resultado.Fallo) return 3;
            MonitorVirtual.OlvidarMarca(); // conectado a mano: que el próximo arranque no lo quite
        }
        else if (orden == "desconectar")
        {
            var ok = v.Liberar();
            Console.WriteLine($"desconectar: {(ok ? "listo" : "no se pudo")}");
            if (!ok) return 3;
        }
        Console.WriteLine($"monitor virtual: {EstadoMonitorVirtual()}");
        return 0;
    }

    /// <summary>Rescate: si el programa se cerró de golpe mientras tenía silenciado el PC.</summary>
    private static int QuitarSilencio()
    {
        using (var dispositivos = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        using (var d = dispositivos.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
            d.AudioEndpointVolume.Mute = false;
        return AudioInfo();
    }

    /// <summary>Diagnóstico: el dispositivo de salida por defecto, su formato y si está en silencio.</summary>
    private static int AudioInfo()
    {
        try
        {
            Console.WriteLine(DescribirAudio());
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Sin dispositivo de sonido: " + e.Message);
            return 1;
        }
    }

    private static async Task<int> DescubrirAsync(int seg)
    {
        using var d = new Descubridor();
        d.Iniciar();
        if (d.Problema is { } p)
        {
            Console.Error.WriteLine(p);
            return 1;
        }
        await Task.Delay(TimeSpan.FromSeconds(seg));
        var tvs = d.Vigentes();
        foreach (var tv in tvs) Console.WriteLine($"{tv.Nombre}\t{tv.Direccion}\t{tv.Puerto}");
        if (tvs.Count == 0) Console.WriteLine("No se vio ningún receptor.");
        return tvs.Count > 0 ? 0 : 2;
    }

    private static async Task<int> TransmitirAsync(string[] args, int seg)
    {
        var ip = Texto(args, "--ip");
        if (ip is null || Mando.LeerIp(ip) is not { } dir)
        {
            Console.Error.WriteLine("Falta --ip <dirección de la TV>.");
            return 1;
        }

        var eleccion = Texto(args, "--monitor");
        Monitores.Preparado preparado;
        try
        {
            preparado = eleccion == Monitores.ElMonitorVirtual
                ? Monitores.Preparar(eleccion, Console.WriteLine)
                : new Monitores.Preparado(Monitores.Elegir(Numero(args, "--monitor", 0)), () => { });
        }
        catch (EmisionException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
        try
        {
            return await TransmitirAsync(args, seg, dir, preparado.Monitor);
        }
        finally
        {
            preparado.Liberar(); // desconecta el monitor virtual si lo conectamos nosotros
        }
    }

    private static async Task<int> TransmitirAsync(string[] args, int seg, IPAddress dir, Monitor pantalla)
    {
        var op = new OpcionesEmision(Numero(args, "--fps", 60), Numero(args, "--kbps", 8000),
            pantalla.Ancho, pantalla.Alto,
            Monitores.RetardoPara(Monitores.EsElVirtual(pantalla), args.Contains("--retardo") ? Numero(args, "--retardo", 500) : null, 500), Texto(args, "--ffmpeg"), Texto(args, "--tuberia"), !args.Contains("--sin-audio"), !args.Contains("--no-silenciar"),
            Numero(args, "--clave", 0), Texto(args, "--codec-extra"), pantalla.Salida, pantalla.Adaptador, pantalla.Nombre,
            Numero(args, "--latido", Nativa.TuberiaNativa.LatidoPorDefecto), !args.Contains("--sin-cursor"));
        using var telemetria = args.Contains("--telemetria") ? new Telemetria(Ajustes.CarpetaTelemetria) : null;
        if (telemetria is not null) Console.WriteLine($"Telemetría en {telemetria.Ruta}");

        void AlEstado(EstadoEmision e) => Console.WriteLine($"[{e.Fase}] {e.Detalle}");
        void AlMedir(Estadisticas e) => Console.WriteLine(
            $"{e.Fps,5:F1} fps  {e.Mbps,5:F2} Mb/s  tirados {e.Tirados}  audio {e.AudioPaqS,2:F0} paq/s  RTT {(e.RttMs is { } r ? $"{r:F0} ms" : "n/d")}"
            + (e.Nativa is { } n ? $"  | nuevos {n.Nuevos} ratón {n.SoloRaton} latidos {n.Latidos} perdidos {n.Perdidos} claves {n.Claves}"
                + $" captura {Ms(n.LatCapturaMs)} codif {Ms(n.LatCodifMs)} (máx {Ms(n.LatCodifMaxMs)}) reinicios {n.Reinicios}" : ""));
        static string Ms(double? v) => v is { } x ? $"{x:F1} ms" : "n/d";
        var receptor = new Receptor("TV", dir, Protocolo.PuertoTcp);
        Func<CancellationToken, Task> ejecutar;
        if (args.Contains("--reconectar"))
        {
            var persistente = new EmisionPersistente();
            persistente.CambioDeEstado += AlEstado;
            persistente.NuevasEstadisticas += AlMedir;
            ejecutar = ct => persistente.EjecutarAsync(receptor, op, telemetria, ct);
        }
        else
        {
            var emision = new Emision();
            emision.CambioDeEstado += AlEstado;
            emision.NuevasEstadisticas += AlMedir;
            ejecutar = ct => emision.EjecutarAsync(receptor, op, telemetria, ct);
        }

        using var cts = seg > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(seg)) : new CancellationTokenSource();
        Console.CancelKeyPress += (_, a) => { a.Cancel = true; cts.Cancel(); };
        try
        {
            await ejecutar(cts.Token);
            return 0;
        }
        catch (EmisionException)
        {
            return 3; // el mensaje ya salió por CambioDeEstado
        }
    }

    /// <summary>La dirección de <c>--manifiesto-url</c>, si es https absoluta; si no, se ignora (y queda en el registro).</summary>
    internal static Uri? UrlManifiesto(string[] args)
    {
        if (Texto(args, "--manifiesto-url") is not { } texto) return null;
        if (Uri.TryCreate(texto, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps) return url;
        Registro.Anotar($"actualizaciones: --manifiesto-url ignorado (no es una dirección https): {texto}");
        return null;
    }

    private static string? Texto(string[] args, string nombre)
    {
        var i = Array.IndexOf(args, nombre);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Numero(string[] args, string nombre, int porDefecto) =>
        int.TryParse(Texto(args, nombre), out var n) ? n : porDefecto;
}
