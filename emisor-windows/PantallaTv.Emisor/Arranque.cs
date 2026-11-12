using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PantallaTv.Emisor;

/// <summary>
/// Cómo arranca el programa de bandeja: si muestra la ventana y qué hace una segunda instancia.
/// Abierto desde el menú Inicio (o el asistente) muestra la ventana; desde el inicio de Windows lleva
/// <see cref="ArgOculto"/> y queda solo en la bandeja. Abrirlo con el programa ya abierto no crea otro ícono:
/// le pide al que está corriendo que muestre su ventana.
/// </summary>
internal static partial class Arranque
{
    /// <summary>Lo que lleva el acceso del inicio de Windows (ver <see cref="Instalacion.ComandoInicio"/>).</summary>
    public const string ArgOculto = "--oculto";

    /// <summary>
    /// Solo para desarrollar: <c>PANTALLATV_PERFIL=prueba</c> da otra instancia única y otros ajustes, para poder
    /// abrir una copia de desarrollo con el programa instalado corriendo sin que una cierre a la otra ni
    /// le cambie los ajustes. Letras, números y guiones; cualquier otra cosa se ignora.
    /// </summary>
    public static string? Perfil => PerfilDe(Environment.GetEnvironmentVariable("PANTALLATV_PERFIL"));

    internal static string? PerfilDe(string? valor) =>
        valor is not null && PerfilValido().IsMatch(valor) ? valor.ToLowerInvariant() : null;

    public static bool EmpiezaOculto(string[] args) => args.Contains(ArgOculto, StringComparer.OrdinalIgnoreCase);

    private static string Sufijo => Perfil is { } p ? "." + p : "";

    /// <summary>Nombre del mutex de instancia única (el mismo que antes del 0.7.1 sin perfil).</summary>
    public static string NombreMutex => "PantallaTv.Emisor" + Sufijo;

    private static string NombreMostrar => "PantallaTv.Emisor.Mostrar" + Sufijo;

    /// <summary>
    /// Lo que usa el instalador para pedirle al programa abierto que se cierre (desde el 0.8.0). Lleva la carpeta del
    /// ejecutable: instalar en una carpeta solo cierra al que corre desde esa carpeta, no a otro (una instalación de
    /// prueba con <c>--dir</c> no cierra al instalado de verdad).
    /// </summary>
    internal static string NombreCerrar(string carpeta)
    {
        var normalizada = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta)).ToUpperInvariant();
        var huella = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizada)));
        return "PantallaTv.Emisor.Cerrar." + huella[..16];
    }

    /// <summary>
    /// La segunda instancia le pide a la primera que muestre su ventana. Una versión anterior al 0.7.1 no
    /// escucha: no pasa nada, como antes.
    /// </summary>
    public static void PedirQueSeMuestre()
    {
        if (!EventWaitHandle.TryOpenExisting(NombreMostrar, out var aviso)) return;
        using (aviso)
        {
            // Quien abre el programa es la ventana en primer plano: le cede ese derecho a la otra instancia,
            // si no Windows solo haría parpadear su botón en la barra de tareas.
            AllowSetForegroundWindow(-1);
            aviso.Set();
        }
    }

    /// <summary>La primera instancia escucha los pedidos de <see cref="PedirQueSeMuestre"/>; <paramref name="mostrar"/> corre en un hilo del sistema.</summary>
    public static IDisposable EscucharPedidos(Action mostrar) => Escuchar(NombreMostrar, mostrar);

    /// <summary>
    /// El instalador le pide al programa abierto que se cierre (como «Salir»: detiene la transmisión y desconecta el
    /// monitor virtual), para poder reemplazar el ejecutable. Devuelve false si no hay nadie escuchando (no está
    /// abierto, o es anterior al 0.8.0).
    /// </summary>
    public static bool PedirQueSeCierre(string carpeta)
    {
        if (!EventWaitHandle.TryOpenExisting(NombreCerrar(carpeta), out var aviso)) return false;
        using (aviso) aviso.Set();
        return true;
    }

    /// <summary>
    /// El programa escucha los pedidos de <see cref="PedirQueSeCierre"/> para la carpeta desde la que corre;
    /// <paramref name="cerrar"/> corre en un hilo del sistema.
    /// </summary>
    public static IDisposable EscucharCierre(Action cerrar) =>
        Escuchar(NombreCerrar(Path.GetDirectoryName(Environment.ProcessPath!)!), cerrar);

    private static IDisposable Escuchar(string nombre, Action accion)
    {
        var aviso = new EventWaitHandle(false, EventResetMode.AutoReset, nombre);
        var registro = ThreadPool.RegisterWaitForSingleObject(aviso, (_, _) => accion(), null, Timeout.Infinite, executeOnlyOnce: false);
        return new Escucha(aviso, registro);
    }

    private sealed class Escucha(EventWaitHandle aviso, RegisteredWaitHandle registro) : IDisposable
    {
        public void Dispose()
        {
            registro.Unregister(null);
            aviso.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int idProceso);

    [GeneratedRegex("^[A-Za-z0-9-]{1,32}$")]
    private static partial Regex PerfilValido();
}
