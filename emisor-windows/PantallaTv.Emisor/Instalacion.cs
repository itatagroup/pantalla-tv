using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace PantallaTv.Emisor;

/// <param name="ConFfmpegAdjunto">Instalar también el ffmpeg.exe que esté junto al instalador (ver <see cref="Instalacion.FfmpegAdjunto"/>).</param>
/// <param name="Id">Nombre de las claves del registro. Solo cambia en pruebas, para no pisar una instalación real.</param>
internal sealed record OpcionesInstalacion(
    string Carpeta, bool MenuInicio = true, bool Escritorio = false, bool IniciarConWindows = false,
    string Id = Instalacion.IdPorDefecto, bool ConFfmpegAdjunto = false);

/// <summary>
/// Instala y desinstala el programa **para el usuario actual**, sin permisos de administrador: copia
/// el ejecutable, crea los accesos directos y lo registra en «Aplicaciones instaladas» de Windows.
/// El instalador es este mismo ejecutable (ver <see cref="EsInstalador"/>): así no hay un segundo
/// programa ni una segunda copia del runtime de .NET.
/// </summary>
internal static class Instalacion
{
    public const string IdPorDefecto = "PantallaTv";
    public const string Nombre = "Pantalla TV";
    public const string Ejecutable = "PantallaTv.exe";
    internal const string ClaveDesinstalar = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string ClaveInicio = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string CarpetaPorDefecto => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PantallaTv");

    /// <summary>
    /// ¿Hay que mostrar el instalador? Sí con <c>--instalar</c>, o si el archivo se llama como un
    /// instalador (<c>PantallaTvSetup-0.4.0.exe</c>): es lo que el usuario descarga y abre con doble clic.
    /// </summary>
    public static bool EsInstalador(string[] args, string? rutaExe)
    {
        if (args.Contains("--instalar")) return true;
        var nombre = Path.GetFileNameWithoutExtension(rutaExe ?? "");
        return nombre.Contains("setup", StringComparison.OrdinalIgnoreCase)
               || nombre.Contains("instal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Dónde está instalado, según el registro; null si no lo está.</summary>
    public static string? CarpetaInstalada(string id = IdPorDefecto)
    {
        using var clave = Registry.CurrentUser.OpenSubKey(ClaveDesinstalar + id);
        return clave?.GetValue("InstallLocation") as string;
    }

    /// <summary>El ffmpeg.exe que está en la misma carpeta que el instalador, si lo hay.</summary>
    public static string? FfmpegAdjunto(string rutaInstalador)
    {
        var ruta = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rutaInstalador))!, "ffmpeg.exe");
        return File.Exists(ruta) ? ruta : null;
    }

    /// <summary>¿Este proceso es el programa instalado (y no una copia suelta o el propio instalador)?</summary>
    public static bool CorreDesdeLaInstalacion(string id = IdPorDefecto) =>
        CarpetaInstalada(id) is { } carpeta && Environment.ProcessPath is { } yo
        && string.Equals(Path.GetFullPath(yo), Path.Combine(Path.GetFullPath(carpeta), Ejecutable), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Otros procesos que están usando el ejecutable instalado (el ícono de la bandeja abierto). Mientras
    /// existan no se puede borrar ni reemplazar el archivo.
    /// </summary>
    public static int AbiertosDesde(string carpeta)
    {
        var instalado = Path.Combine(Path.GetFullPath(carpeta), Ejecutable);
        var abiertos = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Ejecutable)))
        {
            using (p)
            {
                try
                {
                    if (p.Id != Environment.ProcessId
                        && string.Equals(p.MainModule?.FileName, instalado, StringComparison.OrdinalIgnoreCase))
                        abiertos++;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // un proceso de otro usuario o que acaba de terminar: no es el nuestro
                }
            }
        }
        return abiertos;
    }

    /// <summary>Lo que se espera a que el programa abierto se cierre: «Salir» espera hasta 10 s a que termine la transmisión.</summary>
    public static readonly TimeSpan EsperaCierre = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Si el programa está abierto desde <paramref name="carpeta"/>, le pide que se cierre (ver
    /// <see cref="Arranque.PedirQueSeCierre"/>) y espera a que suelte el ejecutable. True si ya no queda ninguno
    /// abierto. Una versión anterior al 0.8.0 no escucha el pedido: hay que cerrarla a mano.
    /// </summary>
    public static bool CerrarAbiertos(string carpeta, TimeSpan espera)
    {
        if (AbiertosDesde(carpeta) == 0) return true;
        if (!Arranque.PedirQueSeCierre()) return false;
        var reloj = Stopwatch.StartNew();
        while (reloj.Elapsed < espera)
        {
            Thread.Sleep(250);
            if (AbiertosDesde(carpeta) == 0) return true;
        }
        return false;
    }

    public static string Instalar(OpcionesInstalacion op, string rutaOrigen)
    {
        if (string.IsNullOrWhiteSpace(op.Carpeta)) throw new InstalacionException("Falta la carpeta de instalación.");
        var carpeta = Path.GetFullPath(op.Carpeta);
        var destino = Path.Combine(carpeta, Ejecutable);
        var mismo = string.Equals(Path.GetFullPath(rutaOrigen), destino, StringComparison.OrdinalIgnoreCase);
        // Con el programa abierto el ejecutable está en uso y la copia fallaría: se le pide que se cierre antes.
        if (!mismo && File.Exists(destino) && !CerrarAbiertos(carpeta, EsperaCierre))
            throw new InstalacionException(
                "Pantalla TV sigue abierto y no se cerró solo (las versiones anteriores a la 0.8.0 no saben hacerlo). " +
                "Ciérralo (clic derecho en su ícono junto al reloj → Salir) y vuelve a intentarlo.");
        try
        {
            Directory.CreateDirectory(carpeta);
            if (!mismo) Copiar(rutaOrigen, destino);

            // El ffmpeg.exe que acompaña al instalador solo se instala si se pidió: después es el que
            // el programa ejecuta, y en la carpeta de descargas puede haber uno de cualquier origen.
            var ffmpegDestino = Path.Combine(carpeta, "ffmpeg.exe");
            if (op.ConFfmpegAdjunto && FfmpegAdjunto(rutaOrigen) is { } adjunto
                && !string.Equals(adjunto, ffmpegDestino, StringComparison.OrdinalIgnoreCase))
                File.Copy(adjunto, ffmpegDestino, overwrite: true);
        }
        catch (IOException e)
        {
            throw new InstalacionException(
                $"No se pudo copiar el programa a {carpeta}. Si Pantalla TV está abierto, ciérralo (clic derecho en su ícono → Salir) y vuelve a intentarlo. Detalle: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            throw new InstalacionException($"Sin permiso para escribir en {carpeta}: elige otra carpeta. Detalle: {e.Message}");
        }

        AccesoDirecto(RutaMenuInicio(op.Id), destino, op.MenuInicio);
        AccesoDirecto(RutaEscritorio(op.Id), destino, op.Escritorio);
        IniciarConWindows(op.IniciarConWindows, destino, op.Id);

        using (var clave = Registry.CurrentUser.CreateSubKey(ClaveDesinstalar + op.Id))
        {
            clave.SetValue("DisplayName", Nombre);
            clave.SetValue("DisplayVersion", Version);
            clave.SetValue("Publisher", "Itatagroup");
            clave.SetValue("InstallLocation", carpeta);
            clave.SetValue("DisplayIcon", destino);
            clave.SetValue("UninstallString", $"\"{destino}\" --desinstalar --id {op.Id}");
            clave.SetValue("QuietUninstallString", $"\"{destino}\" --desinstalar --silencioso --id {op.Id}");
            clave.SetValue("NoModify", 1, RegistryValueKind.DWord);
            clave.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            clave.SetValue("EstimatedSize", (int)(new FileInfo(destino).Length / 1024), RegistryValueKind.DWord);
        }
        return destino;
    }

    /// <summary>
    /// Recién cerrado el programa, Windows (o un antivirus que revisa el archivo) puede tardar un instante en soltar el
    /// ejecutable: unos reintentos antes de dar el error.
    /// </summary>
    private static void Copiar(string origen, string destino)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                File.Copy(origen, destino, overwrite: true);
                return;
            }
            catch (IOException) when (intento < 5)
            {
                Thread.Sleep(400);
            }
        }
    }

    /// <summary>¿Existe el acceso del menú Inicio? (al actualizar, el asistente conserva lo que había).</summary>
    public static bool TieneAccesoMenuInicio(string id = IdPorDefecto) => File.Exists(RutaMenuInicio(id));

    public static bool TieneAccesoEscritorio(string id = IdPorDefecto) => File.Exists(RutaEscritorio(id));

    /// <summary>Lo único que el instalador pone en la carpeta, y lo único que la desinstalación borra de ella.</summary>
    private static readonly string[] ArchivosPropios = [Ejecutable, "ffmpeg.exe"];

    /// <summary>
    /// Con el programa abierto el ejecutable no se podría borrar y quedaría corriendo sin figurar ya en
    /// «Aplicaciones instaladas»: se comprueba antes de tocar nada (también antes de quitar el controlador).
    /// </summary>
    public static void ComprobarCerrado(string id = IdPorDefecto)
    {
        if (CarpetaInstalada(id) is { } carpeta && AbiertosDesde(carpeta) > 0)
            throw new InstalacionException("Pantalla TV está abierto. Ciérralo (clic derecho en su ícono junto al reloj → Salir) y vuelve a desinstalar.");
    }

    /// <summary>La orden que borra la carpeta de este mismo proceso; se lanza al salir (<see cref="TerminarBorrado"/>).</summary>
    private static string? _borradoPendiente;

    /// <summary>
    /// Quita accesos, registro y archivos. De la carpeta se borran **solo los archivos propios**, y la
    /// carpeta misma solo si queda vacía: alguien pudo instalar en una carpeta con otras cosas (el
    /// Escritorio, Documentos) y no se le puede borrar nada que no sea nuestro. El ejecutable no se
    /// puede borrar mientras corre: en ese caso el borrado queda pendiente hasta <see cref="TerminarBorrado"/>.
    /// El controlador del monitor virtual no se toca aquí (ver <see cref="Controlador"/>), solo se quita el
    /// monitor del escritorio si quedó conectado.
    /// </summary>
    public static void Desinstalar(string id = IdPorDefecto)
    {
        ComprobarCerrado(id);
        var carpeta = CarpetaInstalada(id);
        if (MonitorVirtual.Detectar() is { } monitor && monitor.EstaConectado()) monitor.Liberar();
        AccesoDirecto(RutaMenuInicio(id), "", crear: false);
        AccesoDirecto(RutaEscritorio(id), "", crear: false);
        IniciarConWindows(false, "", id);
        Registry.CurrentUser.DeleteSubKeyTree(ClaveDesinstalar + id, throwOnMissingSubKey: false);
        if (carpeta is null || !Directory.Exists(carpeta)) return;

        var propia = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (!string.Equals(Path.GetFullPath(propia), Path.GetFullPath(carpeta), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var nombre in ArchivosPropios) File.Delete(Path.Combine(carpeta, nombre));
            if (!Directory.EnumerateFileSystemEntries(carpeta).Any()) Directory.Delete(carpeta);
            return;
        }
        var borrar = string.Join(" & ", ArchivosPropios.Select(n => $"del /f /q \"{Path.Combine(carpeta, n)}\" 2>nul"));
        // Espera ~2 s a que este proceso termine, borra los archivos propios y quita la carpeta si quedó
        // vacía (rmdir sin /s no borra una carpeta con contenido).
        _borradoPendiente = $"/c ping -n 3 127.0.0.1 >nul & {borrar} & rmdir \"{carpeta}\" 2>nul";
    }

    /// <summary>
    /// Lanza el borrado de la carpeta propia, si quedó pendiente. Va justo antes de que el proceso termine:
    /// si se lanzara antes (con el último mensaje todavía en pantalla), el ejecutable seguiría en uso.
    /// </summary>
    public static void TerminarBorrado()
    {
        if (_borradoPendiente is null) return;
        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            // Va en Arguments (texto tal cual) y no en ArgumentList: cmd no entiende las comillas escapadas
            // que genera esta última.
            Arguments = _borradoPendiente,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        _borradoPendiente = null;
    }

    public static bool IniciaConWindows(string id = IdPorDefecto)
    {
        using var clave = Registry.CurrentUser.OpenSubKey(ClaveInicio);
        return clave?.GetValue(id) is string;
    }

    public static void IniciarConWindows(bool activar, string rutaExe, string id = IdPorDefecto)
    {
        using var clave = Registry.CurrentUser.CreateSubKey(ClaveInicio);
        if (activar) clave.SetValue(id, ComandoInicio(rutaExe));
        else clave.DeleteValue(id, throwOnMissingValue: false);
    }

    /// <summary>Lo que se registra para abrir al iniciar Windows: oculto, solo el ícono de la bandeja.</summary>
    public static string ComandoInicio(string rutaExe) => $"\"{rutaExe}\" {Arranque.ArgOculto}";

    /// <summary>
    /// Hasta el 0.7.0 el inicio con Windows no llevaba <see cref="Arranque.ArgOculto"/> (no había ventana): al
    /// actualizar sin pasar por el asistente, la entrada vieja abriría la ventana en cada arranque. Se corrige sola.
    /// </summary>
    public static void ActualizarInicioConWindows(string rutaExe, string id = IdPorDefecto)
    {
        using var clave = Registry.CurrentUser.OpenSubKey(ClaveInicio, writable: true);
        if (clave?.GetValue(id) is string actual && actual != ComandoInicio(rutaExe))
            clave.SetValue(id, ComandoInicio(rutaExe));
    }

    private static string RutaMenuInicio(string id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), NombreAcceso(id));

    private static string RutaEscritorio(string id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), NombreAcceso(id));

    private static string NombreAcceso(string id) => id == IdPorDefecto ? $"{Nombre}.lnk" : $"{Nombre} ({id}).lnk";

    private static void AccesoDirecto(string rutaLnk, string destino, bool crear)
    {
        if (!crear)
        {
            if (File.Exists(rutaLnk)) File.Delete(rutaLnk);
            return;
        }
        // Los .lnk se crean con el objeto COM de Windows; no hay API administrada para eso.
        var tipo = Type.GetTypeFromProgID("WScript.Shell")
                   ?? throw new InstalacionException("Windows no permite crear accesos directos en este equipo.");
        dynamic shell = Activator.CreateInstance(tipo)!;
        dynamic acceso = shell.CreateShortcut(rutaLnk);
        acceso.TargetPath = destino;
        acceso.WorkingDirectory = Path.GetDirectoryName(destino);
        acceso.Description = "Ver y oír el PC en la tele";
        acceso.IconLocation = destino + ",0";
        acceso.Save();
    }
}

/// <summary>Un fallo de instalación con un mensaje que se le muestra al usuario tal cual.</summary>
internal sealed class InstalacionException(string mensaje) : Exception(mensaje);
