using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PantallaTv.Emisor;

/// <summary>
/// De dónde se baja VB-CABLE: un paquete <b>fijado</b> (tamaño y SHA-256 en el código) y la huella del certificado con que viene
/// firmado su instalador. Ver <c>THIRD-PARTY.md</c> (VB-Audio Software, donationware: se ofrece con créditos y para donar).
/// </summary>
internal sealed record OrigenVbCable(string Version, Uri Url, long Tamano, string Sha256, string Instalador, string HuellaCertificado);

/// <summary>Cómo terminó ofrecer VB-CABLE: hecho, la persona dijo «No» al permiso, o el motivo del fallo.</summary>
internal sealed record ResultadoVbCable(bool Hecho, string? Motivo, bool Rechazado = false);

/// <summary>Lo que necesita del sistema (en las pruebas, uno falso): firma, ejecutar elevado y la salida de audio por defecto.</summary>
internal interface ISistemaVbCable
{
    /// <summary>¿El archivo tiene una firma Authenticode válida y su certificado tiene esa huella (SHA-1, hexadecimal)?</summary>
    bool FirmaValida(string ruta, string huella);

    /// <summary>Ejecuta como administrador y espera; devuelve el código de salida. Lanza <see cref="Win32Exception"/> 1223 si se rechaza el permiso.</summary>
    int EjecutarElevado(string ruta, string argumentos);

    string? SalidaPorDefecto();

    void PonerSalidaPorDefecto(string id);
}

/// <summary>
/// Ofrece VB-CABLE (la salida «muda» del audio exclusivo de la tele) solo cuando la persona lo pide: baja el paquete oficial de la
/// versión fijada por https, comprueba tamaño, SHA-256 y la firma del instalador, lo ejecuta con permiso de administrador
/// (<c>-i -h</c>) y devuelve la salida de audio por defecto que había (VB-CABLE se pone como predeterminada al instalarse).
/// Deja el paquete en <see cref="Carpeta"/> y una marca, para poder quitarlo al desinstalar Pantalla TV.
/// </summary>
internal sealed class DescargadorVbCable : IDisposable
{
    /// <summary>
    /// VB-CABLE Driver Pack 46, leído el 16/11/2026 de <c>download.vb-audio.com</c>: tamaño y SHA-256 del zip, y el certificado
    /// de <c>VBCABLE_Setup_x64.exe</c> (2.1.5.8), «BUREL VINCENT Entrepreneur individuel». Si VB-Audio cambia el paquete con el
    /// mismo nombre, la huella deja de coincidir y la descarga se niega: hay que actualizar esto.
    /// </summary>
    public static readonly OrigenVbCable Fijado = new(
        "Pack 46",
        new Uri("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip"),
        1_324_680,
        "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90",
        "VBCABLE_Setup_x64.exe",
        "A77952D93229D0EC36E2543081EEA7D125732B9C");

    /// <summary>Para donar a VB-Audio (la licencia pide que la persona tenga esa opción).</summary>
    public const string UrlDonar = "https://vb-audio.com/Cable/";

    /// <summary>Lo que se dice de VB-CABLE en la ventana y en el instalador.</summary>
    public const string Creditos = "VB-CABLE es de VB-Audio Software (Vincent Burel): gratuito, con opción de donar.";

    public static readonly TimeSpan TiempoDescarga = TimeSpan.FromMinutes(5);
    /// <summary>Ningún archivo del paquete pesa más de ~1 MB.</summary>
    public const long TamanoMaximoArchivo = 8L << 20;
    public const string NombreMarca = "instalado-por-pantallatv";

    /// <summary>Dónde queda el paquete extraído y la marca.</summary>
    public static string Carpeta => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PantallaTv", "vbcable");

    private readonly HttpClient _http;
    private readonly OrigenVbCable _origen;
    private readonly string _carpeta;
    private readonly ISistemaVbCable _sistema;
    private readonly bool _permitirHttpLocal;

    public DescargadorVbCable(HttpMessageHandler handler, OrigenVbCable origen, string carpeta, ISistemaVbCable sistema, bool permitirHttpLocal = false)
    {
        _origen = origen;
        _carpeta = carpeta;
        _sistema = sistema;
        _permitirHttpLocal = permitirHttpLocal;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PantallaTv/{Instalacion.Version}");
    }

    public static DescargadorVbCable Crear() => new(
        new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }, Fijado, Carpeta, new SistemaWindows());

    private string CarpetaPaquete => Path.Combine(_carpeta, "paquete");
    private string RutaInstalador => Path.Combine(CarpetaPaquete, _origen.Instalador);
    private string RutaMarca => Path.Combine(_carpeta, NombreMarca);

    /// <summary>¿Lo instaló Pantalla TV? (entonces, al desinstalar, se ofrece quitarlo).</summary>
    public static bool LoInstalamosNosotros(string? carpeta = null) => File.Exists(Path.Combine(carpeta ?? Carpeta, NombreMarca));

    /// <summary>
    /// Baja, verifica, instala con UAC y devuelve la salida por defecto. Nunca lanza: un fallo es un resultado con su motivo.
    /// <paramref name="avance"/> recibe 0-100 (hasta 90 la descarga).
    /// </summary>
    public async Task<ResultadoVbCable> InstalarAsync(Action<int>? avance = null, CancellationToken ct = default)
    {
        var zip = Path.Combine(_carpeta, "vbcable.zip.parcial");
        try
        {
            Directory.CreateDirectory(_carpeta);
            Borrar(zip);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TiempoDescarga);
                await DescargadorFfmpeg.BajarVerificadoAsync(_http, _origen.Url, _origen.Tamano, _origen.Sha256, zip, EsSegura, avance, cts.Token)
                    .ConfigureAwait(false);
            }
            Extraer(zip);
            avance?.Invoke(95);
            if (!_sistema.FirmaValida(RutaInstalador, _origen.HuellaCertificado))
                throw new DescargaVerificadaException($"la firma de {_origen.Instalador} no es la de VB-Audio");

            var defecto = _sistema.SalidaPorDefecto();
            var codigo = _sistema.EjecutarElevado(RutaInstalador, "-i -h");
            DevolverSalida(defecto);
            if (codigo != 0)
            {
                Registro.Error($"VB-CABLE: el instalador terminó con el código {codigo}");
                return new ResultadoVbCable(false, $"El instalador de VB-CABLE terminó con el código {codigo}.");
            }
            File.WriteAllText(RutaMarca, $"VB-CABLE {_origen.Version} instalado por Pantalla TV el {DateTime.Now:yyyy-MM-dd HH:mm}");
            avance?.Invoke(100);
            Registro.Anotar($"VB-CABLE {_origen.Version}: descargado, verificado (SHA-256 y firma) e instalado");
            return new ResultadoVbCable(true, null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // «No» en el aviso de UAC
        {
            return new ResultadoVbCable(false, "No se dio permiso de administrador.", Rechazado: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ResultadoVbCable(false, "Descarga cancelada.");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException
                                      or InvalidDataException or DescargaVerificadaException or Win32Exception)
        {
            var motivo = e is OperationCanceledException ? "se agotó el tiempo" : e.Message;
            Registro.Error($"VB-CABLE: no se instaló: {motivo}");
            return new ResultadoVbCable(false, "No se pudo instalar VB-CABLE: " + motivo);
        }
        finally
        {
            Borrar(zip);
        }
    }

    /// <summary>Quita VB-CABLE con su propio instalador (<c>-u -h</c>, UAC), solo si lo instaló Pantalla TV y el paquete sigue ahí.</summary>
    public ResultadoVbCable Quitar()
    {
        if (!File.Exists(RutaMarca)) return new ResultadoVbCable(false, "VB-CABLE no lo instaló Pantalla TV: se deja.");
        try
        {
            if (!File.Exists(RutaInstalador) || !_sistema.FirmaValida(RutaInstalador, _origen.HuellaCertificado))
                return new ResultadoVbCable(false, "No está el instalador de VB-CABLE para quitarlo: quítalo desde su programa de instalación.");
            var defecto = _sistema.SalidaPorDefecto();
            var codigo = _sistema.EjecutarElevado(RutaInstalador, "-u -h");
            DevolverSalida(defecto);
            if (codigo != 0) return new ResultadoVbCable(false, $"El instalador de VB-CABLE terminó con el código {codigo}.");
            Borrar(RutaMarca);
            Registro.Anotar("VB-CABLE quitado");
            return new ResultadoVbCable(true, null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new ResultadoVbCable(false, "No se dio permiso de administrador.", Rechazado: true);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new ResultadoVbCable(false, "No se pudo quitar VB-CABLE: " + e.Message);
        }
    }

    /// <summary>VB-CABLE se pone como salida por defecto al instalarse: se devuelve la que había (y el PC no queda mudo).</summary>
    private void DevolverSalida(string? antes)
    {
        if (string.IsNullOrEmpty(antes)) return;
        try
        {
            var ahora = _sistema.SalidaPorDefecto();
            if (string.Equals(ahora, antes, StringComparison.Ordinal)) return;
            _sistema.PonerSalidaPorDefecto(antes);
            Registro.Anotar("VB-CABLE: se devolvió la salida de audio por defecto que había");
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException)
        {
            Registro.Error($"VB-CABLE: no se pudo devolver la salida por defecto ({e.Message}): revisa Configuración › Sonido");
        }
    }

    /// <summary>Extrae el paquete (ya verificado) a su carpeta: solo archivos sueltos, sin rutas dentro del zip.</summary>
    private void Extraer(string zip)
    {
        var destino = CarpetaPaquete;
        if (Directory.Exists(destino)) Directory.Delete(destino, recursive: true);
        Directory.CreateDirectory(destino);
        using var archivo = ZipFile.OpenRead(zip);
        foreach (var entrada in archivo.Entries)
        {
            if (entrada.FullName.EndsWith('/')) continue; // carpeta
            if (entrada.Name.Length == 0 || entrada.FullName != entrada.Name || entrada.Name.Contains('\\') || entrada.Name.Contains(".."))
                throw new DescargaVerificadaException($"el paquete trae una ruta inesperada: {entrada.FullName}");
            if (entrada.Length is < 0 or > TamanoMaximoArchivo)
                throw new DescargaVerificadaException($"{entrada.Name} pesa {entrada.Length} bytes, algo no cuadra");
            entrada.ExtractToFile(Path.Combine(destino, entrada.Name), overwrite: true);
        }
        if (!File.Exists(RutaInstalador)) throw new DescargaVerificadaException($"el paquete no trae {_origen.Instalador}");
    }

    private bool EsSegura(Uri? u) =>
        u is not null && (u.Scheme == Uri.UriSchemeHttps || (_permitirHttpLocal && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    private static void Borrar(string ruta)
    {
        try { File.Delete(ruta); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>El de verdad: WinVerifyTrust, «runas» y la política de audio de Windows.</summary>
    private sealed class SistemaWindows : ISistemaVbCable
    {
        public bool FirmaValida(string ruta, string huella) => Firmas.Valida(ruta, huella);

        public int EjecutarElevado(string ruta, string argumentos)
        {
            var psi = new ProcessStartInfo(ruta, argumentos)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(ruta)!,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi) ?? throw new Win32Exception("No se pudo iniciar el instalador de VB-CABLE.");
            if (!p.WaitForExit(TimeSpan.FromMinutes(5))) throw new Win32Exception("El instalador de VB-CABLE no terminó en 5 minutos.");
            return p.ExitCode;
        }

        public string? SalidaPorDefecto() => DispositivosAudio.IdPorDefecto();

        public void PonerSalidaPorDefecto(string id) => DispositivosAudio.PonerPorDefecto(id);
    }
}

/// <summary>Firmas Authenticode (WinVerifyTrust) con el certificado esperado.</summary>
internal static class Firmas
{
    /// <summary>¿Firma válida según Windows (cadena y sello) y certificado con esa huella SHA-1?</summary>
    public static bool Valida(string ruta, string huella)
    {
        if (!VerificarConWindows(ruta)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // leer el certificado del firmante de un archivo firmado: no hay reemplazo en X509CertificateLoader
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(ruta));
#pragma warning restore SYSLIB0057
            return string.Equals(cert.Thumbprint, huella, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static bool VerificarConWindows(string ruta)
    {
        var archivo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = ruta };
        var pArchivo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(archivo, pArchivo, false);
            var datos = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = pArchivo,
                dwStateAction = 0,
                dwProvFlags = 0x1000, // WTD_CACHE_ONLY_URL_RETRIEVAL: sin salir a internet a revisar revocaciones
            };
            var accion = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref accion, ref datos) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pArchivo);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid accion, ref WINTRUST_DATA datos);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
