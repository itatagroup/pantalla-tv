using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PantallaTv.Emisor;

/// <summary>
/// Instala y quita el controlador del monitor virtual (Virtual-Display-Driver, ver
/// <c>emisor-windows/controlador/LEEME.md</c>), que va incrustado en el ejecutable.
/// <para>
/// Instalar un controlador pide administrador; el resto de Pantalla TV no. Por eso el programa se relanza
/// a sí mismo elevado solo para esto (<c>--instalar-controlador</c> / <c>--quitar-controlador</c>) y todo lo
/// demás —desconectar el monitor del escritorio, anotar que lo instalamos nosotros— lo hace el proceso
/// normal, que es el del usuario (con UAC «por encima del hombro» el elevado puede ser otro usuario).
/// </para>
/// Sin binarios de terceros: lo que hace <c>devcon install</c> se hace aquí con SetupAPI.
/// </summary>
internal static class Controlador
{
    /// <summary>Hardware id del controlador (de su INF).</summary>
    public const string IdHardware = @"Root\MttVDD";

    /// <summary>Los archivos del paquete, con el nombre del recurso incrustado (ver el <c>.csproj</c>).</summary>
    private static readonly string[] Archivos = ["MttVDD.inf", "MttVDD.dll", "mttvdd.cat"];

    private const string ClaveConfiguracion = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
    private const string CarpetaConfiguracionPorDefecto = @"C:\VirtualDisplayDriver";
    private const string NombreConfiguracion = "vdd_settings.xml";

    /// <summary>Valor (en la clave de desinstalación del programa) que dice que el controlador lo pusimos nosotros.</summary>
    internal const string ValorMarca = "ControladorInstaladoPorNosotros";

    public enum Resultado { Hecho, Rechazado, Fallo }

    /// <param name="Detalle">Para el usuario: por qué falló, o un aviso (p. ej. que hay que reiniciar).</param>
    /// <param name="CreadoPorNosotros">Al instalar: el dispositivo lo creó este instalador (no estaba). Solo entonces es «nuestro».</param>
    public sealed record Respuesta(Resultado Resultado, string? Detalle = null, bool CreadoPorNosotros = false);

    /// <summary>Código de salida del proceso elevado cuando instaló bien pero el dispositivo ya existía.</summary>
    private const int SalidaYaExistia = 2;

    /// <summary>¿Está el monitor virtual disponible en este PC?</summary>
    public static bool Instalado() => MonitorVirtual.Detectar() is not null;

    // ───────────────────────────── proceso normal (usuario) ─────────────────────────────

    /// <summary>
    /// Instala el controlador pidiendo permiso (UAC), lo deja desconectado del escritorio (Windows conecta y
    /// extiende solo un monitor recién instalado) y anota en la instalación <paramref name="id"/> que lo
    /// instalamos nosotros, para ofrecer quitarlo al desinstalar (null: una copia suelta, sin instalación).
    /// </summary>
    public static Respuesta InstalarConPermiso(string? id)
    {
        var r = EjecutarElevado("--instalar-controlador");
        if (r.Resultado != Resultado.Hecho) return r;
        // Un controlador que ya estaba (aunque no funcionara) es del usuario: no se ofrece quitarlo después.
        if (r.CreadoPorNosotros) Marcar(id, true);
        if (!DejarDesconectado(TimeSpan.FromSeconds(30)))
            return r with { Detalle = Unir(r.Detalle, "El monitor virtual quedó conectado al escritorio: se quita solo al terminar la primera transmisión, o en Configuración › Pantalla.") };
        return r;
    }

    /// <summary>Lo quita del escritorio (si lo está) y después quita dispositivo y paquete pidiendo permiso.</summary>
    public static Respuesta QuitarConPermiso(string id)
    {
        if (MonitorVirtual.Detectar() is { } monitor) monitor.Liberar();
        var r = EjecutarElevado("--quitar-controlador");
        if (r.Resultado == Resultado.Hecho) Marcar(id, false);
        return r;
    }

    /// <summary>¿El controlador lo instaló Pantalla TV (instalación <paramref name="id"/>)? Solo entonces se ofrece quitarlo.</summary>
    public static bool LoInstalamosNosotros(string id = Instalacion.IdPorDefecto)
    {
        using var clave = Registry.CurrentUser.OpenSubKey(Instalacion.ClaveDesinstalar + id);
        return clave?.GetValue(ValorMarca) is int v && v == 1;
    }

    /// <summary>
    /// Anota o borra la marca en la clave de desinstalación. Si el programa no está instalado con ese id (una
    /// copia suelta), no se crea la clave: dejaría una entrada falsa en «Aplicaciones instaladas».
    /// </summary>
    private static void Marcar(string? id, bool instalado)
    {
        if (id is null) return;
        using var clave = Registry.CurrentUser.OpenSubKey(Instalacion.ClaveDesinstalar + id, writable: true);
        if (clave is null) return;
        if (instalado) clave.SetValue(ValorMarca, 1, RegistryValueKind.DWord);
        else clave.DeleteValue(ValorMarca, throwOnMissingValue: false);
    }

    /// <summary>
    /// Espera a que el monitor nuevo aparezca y lo desconecta cada vez que Windows lo conecta, hasta que pase
    /// un rato sin que lo vuelva a conectar. Devuelve false si al final sigue conectado.
    /// </summary>
    internal static bool DejarDesconectado(TimeSpan espera)
    {
        var limite = DateTime.UtcNow + espera;
        MonitorVirtual? monitor = null;
        while (monitor is null && DateTime.UtcNow < limite)
        {
            monitor = MonitorVirtual.Detectar();
            if (monitor is null) Thread.Sleep(250);
        }
        // No apareció (p. ej. falta reiniciar): cuando aparezca, Windows lo conectará y nadie lo quitará.
        if (monitor is null) return false;

        var resto = limite - DateTime.UtcNow;
        return monitor.Liberar(resto > TimeSpan.FromSeconds(8) ? resto : TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4));
    }

    /// <summary>Relanza este mismo ejecutable como administrador con <paramref name="orden"/> y espera a que termine.</summary>
    private static Respuesta EjecutarElevado(string orden)
    {
        var informe = Path.Combine(Path.GetTempPath(), $"PantallaTv-controlador-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{orden} --informe \"{informe}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar el proceso.");
            p.WaitForExit();
            var texto = File.Exists(informe) ? File.ReadAllText(informe).Trim() : "";
            return p.ExitCode is 0 or SalidaYaExistia
                ? new Respuesta(Resultado.Hecho, texto.Length > 0 ? texto : null, CreadoPorNosotros: p.ExitCode == 0)
                : new Respuesta(Resultado.Fallo, texto.Length > 0 ? texto : $"El instalador del controlador terminó con el código {p.ExitCode}.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: «No» en el aviso de UAC
        {
            return new Respuesta(Resultado.Rechazado, "No se dio permiso de administrador.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return new Respuesta(Resultado.Fallo, e.Message);
        }
        finally
        {
            try { File.Delete(informe); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Unir(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;

    // ───────────────────────────── proceso elevado ─────────────────────────────

    /// <summary>Atiende <c>--instalar-controlador</c> y <c>--quitar-controlador</c> (proceso ya elevado).</summary>
    public static int EjecutarOrdenElevada(string[] args)
    {
        var informe = Instalador.Texto(args, "--informe");
        try
        {
            if (args.Contains("--instalar-controlador"))
            {
                var (creado, avisoInstalar) = InstalarAqui();
                Informar(informe, avisoInstalar);
                return creado ? 0 : SalidaYaExistia;
            }
            Informar(informe, QuitarAqui());
            return 0;
        }
        catch (Exception e)
        {
            // Cualquier fallo: el proceso del usuario lo muestra; aquí no hay ventana donde hacerlo.
            Informar(informe, e is ControladorException ? e.Message : $"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static void Informar(string? ruta, string? texto)
    {
        if (ruta is null || string.IsNullOrEmpty(texto)) return;
        try { File.WriteAllText(ruta, texto); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Crea el nodo de dispositivo raíz (si no hay uno) y le instala el controlador. Devuelve si el nodo lo
    /// creamos nosotros y un aviso o null.
    /// </summary>
    private static (bool Creado, string? Aviso) InstalarAqui()
    {
        // En la carpeta temporal de Windows y no en la del usuario: ahí un proceso sin privilegios no puede
        // tocar los archivos entre la extracción y la instalación.
        var temporal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp",
            "PantallaTv-controlador-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporal);
        try
        {
            ExtraerArchivos(temporal);
            var configuracionNueva = EscribirConfiguracion();
            var inf = Path.Combine(temporal, Archivos[0]);

            var creado = false;
            if (!ExisteDispositivo()) { CrearDispositivo(inf); creado = true; }
            var error = InstalarControlador(inf, out var reiniciar);
            if (error != 0 && error != SinMejorControlador && !creado)
            {
                // Un nodo que quedó de antes sin funcionar (fantasma): se reemplaza por uno nuevo.
                QuitarDispositivos();
                CrearDispositivo(inf);
                creado = true;
                error = InstalarControlador(inf, out reiniciar);
            }
            if (error != 0 && error != SinMejorControlador)
            {
                if (creado) QuitarDispositivos(); // no dejar un dispositivo sin controlador
                throw new ControladorException($"Windows no instaló el controlador ({new Win32Exception(error).Message}).");
            }
            // Ya estaba: se reinicia para que lea la configuración (y para arreglar uno que quedó a medias).
            if (!creado && !ReiniciarDispositivos()) reiniciar = true;
            if (configuracionNueva) Registro.Anotar("monitor virtual: configuración escrita con los modos de esta versión");
            return (creado, reiniciar ? "Windows pide reiniciar el PC para terminar de instalar el monitor virtual." : null);
        }
        finally
        {
            try { Directory.Delete(temporal, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>ERROR_NO_MORE_ITEMS: el dispositivo ya tiene un controlador igual o mejor. No es un fallo.</summary>
    private const int SinMejorControlador = 259;

    /// <summary>Instala el INF en los dispositivos con nuestro hardware id; devuelve 0 o el error de Windows.</summary>
    private static int InstalarControlador(string inf, out bool reiniciar) =>
        UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, IdHardware, inf, 0, out reiniciar) ? 0 : Marshal.GetLastWin32Error();

    /// <summary>Quita los dispositivos y los paquetes del controlador, y la configuración si es la que escribimos.</summary>
    private static string? QuitarAqui()
    {
        var reiniciar = QuitarDispositivos();
        foreach (var inf in PaquetesInstalados())
        {
            if (SetupUninstallOEMInf(Path.GetFileName(inf), 0, IntPtr.Zero)) continue;
            // El dispositivo se quita del todo al reiniciar; hasta entonces el paquete sigue en uso.
            if (reiniciar)
                return "Reinicia el PC y vuelve a quitar el monitor virtual para borrar también su controlador (el dispositivo ya se quitó).";
            throw new ControladorException($"No se pudo quitar el paquete {Path.GetFileName(inf)} ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
        }
        BorrarConfiguracionSiEsNuestra();
        return reiniciar ? "Windows pide reiniciar el PC para terminar de quitar el monitor virtual." : null;
    }

    private static void ExtraerArchivos(string carpeta)
    {
        var ensamblado = Assembly.GetExecutingAssembly();
        foreach (var nombre in Archivos)
        {
            using var origen = ensamblado.GetManifestResourceStream("controlador/" + nombre)
                               ?? throw new ControladorException($"Este ejecutable no trae el controlador ({nombre}).");
            using var destino = File.Create(Path.Combine(carpeta, nombre));
            origen.CopyTo(destino);
        }
    }

    // ── configuración del controlador (vdd_settings.xml) ──

    /// <summary>Dónde lee el controlador su configuración (lo mismo que hace su <c>Driver.cpp</c>).</summary>
    internal static string RutaConfiguracion()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var clave = hklm.OpenSubKey(ClaveConfiguracion);
        var carpeta = clave?.GetValue("VDDPATH") as string;
        return Path.Combine(string.IsNullOrWhiteSpace(carpeta) ? CarpetaConfiguracionPorDefecto : carpeta, NombreConfiguracion);
    }

    /// <summary>
    /// Los tamaños que ofrece el monitor virtual (en horizontal): los de teles, tabletas y teléfonos comunes, para que
    /// coincida con la pantalla que informa el receptor (1728×1080: una de 16:10 reducida a 1080 líneas, lo que se pide
    /// en automático). Cada uno a 60, 50 y 30 Hz.
    /// </summary>
    internal static readonly (int Ancho, int Alto)[] TamanosPropios =
        [(3840, 2160), (2560, 1600), (2560, 1440), (2400, 1080), (1920, 1200), (1920, 1080), (1728, 1080), (1600, 900), (1366, 768), (1280, 800), (1280, 720)];

    internal static readonly int[] FrecuenciasPropias = [60, 50, 30];

    /// <summary>
    /// La configuración que escribe Pantalla TV: un monitor y los modos de <see cref="TamanosPropios"/> (sin la lista
    /// «global» de frecuencias, que duplicaría los modos). Las opciones, como las trae el controlador.
    /// </summary>
    internal static string ConfiguracionPropia() =>
        Configuracion(TamanosPropios.SelectMany(t => FrecuenciasPropias.Select(hz => (t.Ancho, t.Alto, hz))));

    /// <summary>Las que escribieron versiones anteriores (hasta la 0.10): se reconocen como nuestras y se actualizan.</summary>
    internal static readonly string[] ConfiguracionesAnteriores = [Configuracion([(1920, 1080, 60), (1280, 720, 60)])];

    private static string Configuracion(IEnumerable<(int Ancho, int Alto, int Hz)> modos)
    {
        var resoluciones = string.Concat(modos.Select(m =>
            $"        <resolution>\n            <width>{m.Ancho}</width>\n            <height>{m.Alto}</height>\n            <refresh_rate>{m.Hz}</refresh_rate>\n        </resolution>\n"));
        return ("""
        <?xml version='1.0' encoding='utf-8'?>
        <!-- Escrito por Pantalla TV al instalar el monitor virtual. Si lo cambias, Pantalla TV no lo borra al quitar el controlador. -->
        <vdd_settings>
            <monitors>
                <count>1</count>
            </monitors>
            <gpu>
                <friendlyname>default</friendlyname>
            </gpu>
            <resolutions>

        """ + resoluciones + """
            </resolutions>
            <options>
                <CustomEdid>false</CustomEdid>
                <PreventSpoof>false</PreventSpoof>
                <EdidCeaOverride>false</EdidCeaOverride>
                <HardwareCursor>true</HardwareCursor>
                <SDR10bit>false</SDR10bit>
                <HDRPlus>false</HDRPlus>
                <logging>false</logging>
                <debuglogging>false</debuglogging>
            </options>
        </vdd_settings>
        """).ReplaceLineEndings("\r\n") + "\r\n";
    }

    private static string Normalizar(string texto) => texto.ReplaceLineEndings("\n").Trim();

    /// <summary>
    /// ¿Es exactamente una configuración que escribimos, esta o la de una versión anterior (nadie la cambió)? Solo
    /// entonces se puede borrar o actualizar.
    /// </summary>
    internal static bool EsConfiguracionPropia(string contenido)
    {
        var c = Normalizar(contenido);
        return c == Normalizar(ConfiguracionPropia()) || ConfiguracionesAnteriores.Any(a => c == Normalizar(a));
    }

    /// <summary>¿Es una configuración nuestra de una versión anterior (con menos modos)? Reinstalar la actualiza.</summary>
    internal static bool EsConfiguracionAnterior(string contenido) =>
        EsConfiguracionPropia(contenido) && Normalizar(contenido) != Normalizar(ConfiguracionPropia());

    /// <summary>¿La configuración instalada es una nuestra de una versión anterior?</summary>
    public static bool ConfiguracionDesactualizada()
    {
        try
        {
            var ruta = RutaConfiguracion();
            return File.Exists(ruta) && EsConfiguracionAnterior(File.ReadAllText(ruta));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Escribe la configuración si no hay una o si es una nuestra de una versión anterior: la del usuario no se pisa.
    /// Devuelve si la cambió.
    /// </summary>
    private static bool EscribirConfiguracion()
    {
        var ruta = RutaConfiguracion();
        if (File.Exists(ruta) && !EsConfiguracionAnterior(File.ReadAllText(ruta))) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, ConfiguracionPropia(), new UTF8Encoding(false));
        return true;
    }

    private static void BorrarConfiguracionSiEsNuestra()
    {
        var ruta = RutaConfiguracion();
        if (!File.Exists(ruta) || !EsConfiguracionPropia(File.ReadAllText(ruta))) return;
        File.Delete(ruta);
        var carpeta = Path.GetDirectoryName(ruta)!;
        if (Directory.Exists(carpeta) && !Directory.EnumerateFileSystemEntries(carpeta).Any()) Directory.Delete(carpeta);
    }

    // ── paquetes del controlador en el almacén de Windows ──

    /// <summary>Los <c>oemNN.inf</c> de <c>%windir%\INF</c> que son este controlador (puede haber más de una versión).</summary>
    private static List<string> PaquetesInstalados()
    {
        var carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var lista = new List<string>();
        foreach (var inf in Directory.EnumerateFiles(carpeta, "oem*.inf"))
        {
            try
            {
                if (EsPaqueteDelControlador(File.ReadAllText(inf))) lista.Add(inf);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return lista;
    }

    /// <summary>
    /// ¿Este INF es el del monitor virtual? Por su contenido y no por la salida de <c>pnputil</c>, que viene
    /// traducida al idioma de Windows («Nombre original» en este PC).
    /// </summary>
    internal static bool EsPaqueteDelControlador(string contenidoInf) =>
        contenidoInf.Contains(IdHardware, StringComparison.OrdinalIgnoreCase)
        && contenidoInf.Contains("MttVDD.dll", StringComparison.OrdinalIgnoreCase);

    // ── dispositivos (SetupAPI) ──

    private static bool ExisteDispositivo()
    {
        var hay = false;
        RecorrerDispositivos((_, _) => hay = true);
        return hay;
    }

    /// <summary>Quita todos los nodos con nuestro hardware id (también los fantasma). Devuelve si hay que reiniciar.</summary>
    private static bool QuitarDispositivos()
    {
        var reiniciar = false;
        RecorrerDispositivos((lista, dato) =>
        {
            if (!DiUninstallDevice(IntPtr.Zero, lista, ref dato, 0, out var r))
                throw new ControladorException($"No se pudo quitar el dispositivo ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
            reiniciar |= r;
        });
        return reiniciar;
    }

    /// <summary>
    /// Deshabilita y vuelve a habilitar cada dispositivo presente (DIF_PROPERTYCHANGE, lo que hacen «Deshabilitar» y
    /// «Habilitar» en el Administrador de dispositivos): así el controlador vuelve a leer su configuración. Devuelve
    /// false si alguno no se pudo reiniciar (queda para el próximo reinicio del PC).
    /// </summary>
    private static bool ReiniciarDispositivos()
    {
        var todo = true;
        RecorrerDispositivos((lista, dato) =>
        {
            foreach (var estado in new[] { DicsDisable, DicsEnable })
            {
                var p = new CambioPropiedad { Tamano = 8, Funcion = DifPropertyChange, Estado = estado, Alcance = DicsFlagGlobal };
                if (SetupDiSetClassInstallParams(lista, ref dato, ref p, Marshal.SizeOf<CambioPropiedad>())
                    && SetupDiCallClassInstaller(DifPropertyChange, lista, ref dato)) continue;
                // Un nodo fantasma (no presente) no se puede reiniciar: no importa.
                if (Marshal.GetLastWin32Error() != ErrorNoSuchDevinst) todo = false;
                break;
            }
        });
        return todo;
    }

    private delegate void AccionDispositivo(IntPtr lista, DatosDispositivo dato);

    /// <summary>Llama a <paramref name="accion"/> por cada dispositivo (presente o no) cuyo hardware id es el nuestro.</summary>
    private static void RecorrerDispositivos(AccionDispositivo accion)
    {
        var lista = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, TodasLasClases);
        if (lista == ManejadorInvalido) throw new ControladorException($"No se pudo leer la lista de dispositivos ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
        try
        {
            var encontrados = new List<DatosDispositivo>();
            for (uint i = 0; ; i++)
            {
                var dato = DatosDispositivo.Nuevo();
                if (!SetupDiEnumDeviceInfo(lista, i, ref dato)) break;
                if (IdsDeHardware(lista, dato).Any(id => string.Equals(id, IdHardware, StringComparison.OrdinalIgnoreCase)))
                    encontrados.Add(dato);
            }
            // Fuera del recorrido: quitar un dispositivo mientras se enumera corre los índices.
            foreach (var dato in encontrados) accion(lista, dato);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(lista);
        }
    }

    private static IEnumerable<string> IdsDeHardware(IntPtr lista, DatosDispositivo dato)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryProperty(lista, ref dato, SpdrpHardwareId, out _, buffer, buffer.Length, out var largo))
            return [];
        return LeerMultiSz(buffer.AsSpan(0, Math.Min(largo, buffer.Length)));
    }

    internal static List<string> LeerMultiSz(ReadOnlySpan<byte> datos) =>
        Encoding.Unicode.GetString(datos).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    internal static byte[] MultiSz(params string[] valores) => Encoding.Unicode.GetBytes(string.Join('\0', valores) + "\0\0");

    /// <summary>Lo que hace <c>devcon install</c> antes de instalar el controlador: crear el nodo raíz con el hardware id.</summary>
    private static void CrearDispositivo(string inf)
    {
        var nombreClase = new StringBuilder(64);
        if (!SetupDiGetINFClass(inf, out var clase, nombreClase, nombreClase.Capacity, out _))
            throw new ControladorException($"El INF del controlador no se pudo leer ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");

        var lista = SetupDiCreateDeviceInfoList(ref clase, IntPtr.Zero);
        if (lista == ManejadorInvalido) throw new ControladorException($"SetupDiCreateDeviceInfoList: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        try
        {
            var dato = DatosDispositivo.Nuevo();
            if (!SetupDiCreateDeviceInfo(lista, nombreClase.ToString(), ref clase, null, IntPtr.Zero, GenerarId, ref dato))
                throw new ControladorException($"SetupDiCreateDeviceInfo: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            var ids = MultiSz(IdHardware);
            if (!SetupDiSetDeviceRegistryProperty(lista, ref dato, SpdrpHardwareId, ids, ids.Length))
                throw new ControladorException($"SetupDiSetDeviceRegistryProperty: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            if (!SetupDiCallClassInstaller(DifRegisterDevice, lista, ref dato))
                throw new ControladorException($"No se pudo registrar el dispositivo ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(lista);
        }
    }

    private static readonly IntPtr ManejadorInvalido = new(-1);
    private const int TodasLasClases = 0x4; // DIGCF_ALLCLASSES (sin DIGCF_PRESENT: también los fantasma)
    private const int GenerarId = 0x1; // DICD_GENERATE_ID → ROOT\DISPLAY\000N
    private const int SpdrpHardwareId = 0x1;
    private const int DifRegisterDevice = 0x19;
    private const int DifPropertyChange = 0x12;
    private const int DicsEnable = 1;
    private const int DicsDisable = 2;
    private const int DicsFlagGlobal = 1;
    private const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);

    /// <summary>SP_PROPCHANGE_PARAMS (con su SP_CLASSINSTALL_HEADER al comienzo).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CambioPropiedad
    {
        public int Tamano;
        public int Funcion;
        public int Estado;
        public int Alcance;
        public int Perfil;
    }

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetClassInstallParamsW")]
    private static extern bool SetupDiSetClassInstallParams(IntPtr lista, ref DatosDispositivo dato, ref CambioPropiedad parametros, int tamano);

    [StructLayout(LayoutKind.Sequential)]
    private struct DatosDispositivo // SP_DEVINFO_DATA
    {
        public int Tamano;
        public Guid Clase;
        public int Instancia;
        public IntPtr Reservado;

        public static DatosDispositivo Nuevo() => new() { Tamano = Marshal.SizeOf<DatosDispositivo>() };
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetINFClassW")]
    private static extern bool SetupDiGetINFClass(string inf, out Guid clase, StringBuilder nombreClase, int tamano, out int requerido);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid clase, IntPtr ventana);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoW")]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr lista, string nombre, ref Guid clase, string? descripcion, IntPtr ventana, int banderas, ref DatosDispositivo dato);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr lista, ref DatosDispositivo dato, int propiedad, byte[] valor, int tamano);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr lista, ref DatosDispositivo dato, int propiedad, out int tipo, byte[] valor, int tamano, out int requerido);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int funcion, IntPtr lista, ref DatosDispositivo dato);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr clase, string? enumerador, IntPtr ventana, int banderas);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr lista, uint indice, ref DatosDispositivo dato);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr lista);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupUninstallOEMInfW")]
    private static extern bool SetupUninstallOEMInf(string inf, int banderas, IntPtr reservado);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr ventana, string idHardware, string inf, int banderas, out bool reiniciar);

    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiUninstallDevice(IntPtr ventana, IntPtr lista, ref DatosDispositivo dato, int banderas, out bool reiniciar);
}

/// <summary>Un fallo al instalar o quitar el controlador, con un mensaje para el usuario.</summary>
internal sealed class ControladorException(string mensaje) : Exception(mensaje);
