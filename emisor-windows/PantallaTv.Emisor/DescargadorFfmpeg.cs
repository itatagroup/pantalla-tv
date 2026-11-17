using System.IO.Compression;
using System.Security.Cryptography;

namespace PantallaTv.Emisor;

/// <summary>
/// De dónde se baja ffmpeg: una versión <b>fijada</b>, con el tamaño y el SHA-256 del zip puestos en el código (no se leen
/// de un archivo que se baje del mismo sitio). Ver <c>THIRD-PARTY.md</c> para la fuente y la licencia.
/// </summary>
/// <param name="Sha256">Huella del zip, 64 caracteres hexadecimales en minúsculas. Mientras no sea una huella válida, la descarga se niega.</param>
/// <param name="EntradaZip">La ruta de <c>ffmpeg.exe</c> dentro del zip; es lo único que se extrae.</param>
internal sealed record OrigenFfmpeg(string Version, Uri Url, long Tamano, string Sha256, string EntradaZip)
{
    /// <summary>¿La huella fijada es una huella de verdad (y no un marcador de «pendiente»)?</summary>
    public bool HuellaValida => Sha256.Length == 64 && Sha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>Cómo terminó una descarga de ffmpeg: la ruta del <c>ffmpeg.exe</c> si salió bien; si no, el porqué (para la persona).</summary>
internal sealed record ResultadoFfmpeg(string? Ruta, string? Motivo, bool Cancelada = false)
{
    public bool Correcto => Ruta is not null;
}

/// <summary>
/// Baja ffmpeg (el respaldo de la captura propia) con la confirmación de la persona: descarga el zip de la versión fijada
/// por https, comprueba que tenga exactamente el tamaño y el SHA-256 del código, extrae <b>solo</b> <c>ffmpeg.exe</c> a la
/// carpeta de datos del emisor (<see cref="Ffmpeg.CarpetaDatos"/>, nunca junto al exe: puede estar en Archivos de programa) y
/// borra todo lo demás. Si algo no cuadra no deja nada en disco. Es la segunda conexión a internet del programa, después de
/// buscar actualizaciones, y solo ocurre cuando la persona la confirma.
/// </summary>
internal sealed class DescargadorFfmpeg : IDisposable
{
    /// <summary>
    /// ffmpeg 9.0.2, «essentials» de Gyan Doshi, publicado en el repositorio de GitHub <c>GyanD/codexffmpeg</c> (GPL v3: con x264
    /// para la tubería por CPU). Huella y tamaño leídos de la API de releases de GitHub (<c>digest</c>) y confirmados con el
    /// <c>.sha256</c> que publica gyan.dev, sin bajar el archivo.
    /// </summary>
    public static readonly OrigenFfmpeg Fijado = new(
        "9.0.2",
        new Uri("https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip"),
        114_768_076,
        "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba",
        "ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe");

    /// <summary>Tope para la descarga completa (~115 MB): holgado para una conexión lenta.</summary>
    public static readonly TimeSpan TiempoDescarga = TimeSpan.FromMinutes(30);
    /// <summary>Un ffmpeg.exe de más de esto no es el que se espera (el de 9.0.2 pesa ~110 MB).</summary>
    public const long TamanoMaximoExe = 400L << 20;

    private readonly HttpClient _http;
    private readonly OrigenFfmpeg _origen;
    private readonly string _carpeta;
    private readonly bool _permitirHttpLocal;

    /// <param name="handler">El que hace los pedidos (las pruebas ponen uno propio).</param>
    /// <param name="carpeta">Dónde queda <c>ffmpeg.exe</c>.</param>
    /// <param name="permitirHttpLocal">Solo para pruebas: aceptar <c>http</c> a la propia máquina.</param>
    public DescargadorFfmpeg(HttpMessageHandler handler, OrigenFfmpeg origen, string carpeta, bool permitirHttpLocal = false)
    {
        _origen = origen;
        _carpeta = carpeta;
        _permitirHttpLocal = permitirHttpLocal;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        // Lo único que se manda: un User-Agent genérico, sin datos del equipo.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PantallaTv/{Instalacion.Version}");
    }

    /// <summary>El de verdad: la versión fijada, a la carpeta de datos, sin cookies y siguiendo redirecciones (https no baja a http).</summary>
    public static DescargadorFfmpeg Crear() => new(
        new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }, Fijado, Ffmpeg.CarpetaDatos);

    public string RutaFinal => Path.Combine(_carpeta, "ffmpeg.exe");

    /// <summary>
    /// Baja, verifica y extrae. Nunca lanza por la red ni por el disco: un fallo es un <see cref="ResultadoFfmpeg"/> con su motivo
    /// (y, con la verificación fallida, el zip ya borrado). <paramref name="avance"/> recibe 0-100 desde el hilo de la descarga:
    /// hasta 90 es lo que se baja y el resto, la extracción.
    /// </summary>
    public async Task<ResultadoFfmpeg> DescargarAsync(Action<int>? avance = null, CancellationToken ct = default)
    {
        if (!_origen.HuellaValida)
        {
            Registro.Error("ffmpeg: la huella SHA-256 de la versión fijada está pendiente; no se descarga nada");
            return new ResultadoFfmpeg(null, "Esta versión del programa todavía no tiene fijada la huella de ffmpeg, así que no lo descarga. Instala ffmpeg a mano (winget install Gyan.FFmpeg).");
        }
        var zip = Path.Combine(_carpeta, "ffmpeg.zip.parcial");
        var exeParcial = RutaFinal + ".parcial";
        try
        {
            Directory.CreateDirectory(_carpeta);
            Borrar(zip);
            Borrar(exeParcial);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TiempoDescarga);

            await BajarAsync(zip, avance, cts.Token).ConfigureAwait(false);
            await ExtraerAsync(zip, exeParcial, avance, cts.Token).ConfigureAwait(false);
            File.Move(exeParcial, RutaFinal, overwrite: true);
            avance?.Invoke(100);
            Registro.Anotar($"ffmpeg: {_origen.Version} descargado, verificado (SHA-256) y extraído en {RutaFinal}");
            return new ResultadoFfmpeg(RutaFinal, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Registro.Anotar("ffmpeg: descarga cancelada");
            return new ResultadoFfmpeg(null, "Descarga cancelada.", Cancelada: true);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException
                                      or InvalidDataException or DescargaVerificadaException)
        {
            var motivo = e is OperationCanceledException ? "se agotó el tiempo" : e.Message;
            Registro.Error($"ffmpeg: descarga rechazada: {motivo}");
            return new ResultadoFfmpeg(null, "No se pudo descargar ffmpeg: " + motivo);
        }
        finally
        {
            // Pase lo que pase, no queda ni el zip ni un ffmpeg a medias (el bueno, si hubo, ya se movió).
            Borrar(zip);
            Borrar(exeParcial);
        }
    }

    private Task BajarAsync(string destino, Action<int>? avance, CancellationToken ct) =>
        BajarVerificadoAsync(_http, _origen.Url, _origen.Tamano, _origen.Sha256, destino, EsSegura, avance, ct);

    /// <summary>
    /// Baja <paramref name="url"/> a <paramref name="destino"/> comprobando que llegue por https (también tras las redirecciones),
    /// con exactamente <paramref name="tamano"/> bytes y el SHA-256 <paramref name="sha256"/>. Lanza
    /// <see cref="DescargaVerificadaException"/> si algo no cuadra (el que llama borra el archivo). Avance de 0 a 90.
    /// Lo usan ffmpeg y VB-CABLE.
    /// </summary>
    internal static async Task BajarVerificadoAsync(HttpClient http, Uri url, long tamano, string sha256, string destino,
        Func<Uri?, bool> esSegura, Action<int>? avance, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!esSegura(resp.RequestMessage?.RequestUri))
            throw new DescargaVerificadaException("la respuesta no llegó por https: " + resp.RequestMessage?.RequestUri);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } anunciado && anunciado != tamano)
            throw new DescargaVerificadaException($"el servidor anuncia {anunciado} bytes y se esperaban {tamano}");

        using var huella = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var ultimo = -1;
        await using (var origen = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var archivo = new FileStream(destino, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buf = new byte[81920];
            int leidos;
            while ((leidos = await origen.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += leidos;
                if (total > tamano) throw new DescargaVerificadaException($"la descarga pasa de los {tamano} bytes esperados");
                huella.AppendData(buf, 0, leidos);
                await archivo.WriteAsync(buf.AsMemory(0, leidos), ct).ConfigureAwait(false);
                var porcentaje = (int)(total * 90 / tamano);
                if (porcentaje != ultimo)
                {
                    ultimo = porcentaje;
                    avance?.Invoke(porcentaje);
                }
            }
        }
        if (total != tamano) throw new DescargaVerificadaException($"llegaron {total} bytes y se esperaban {tamano}");
        var sha = Convert.ToHexStringLower(huella.GetHashAndReset());
        if (sha != sha256) throw new DescargaVerificadaException($"la huella SHA-256 no coincide (llegó {sha}); se borró lo descargado");
    }

    /// <summary>Saca del zip (ya verificado) solo <c>ffmpeg.exe</c>, sin fiarse de ninguna otra entrada ni de rutas de dentro del zip.</summary>
    private async Task ExtraerAsync(string zip, string destino, Action<int>? avance, CancellationToken ct)
    {
        using var archivo = ZipFile.OpenRead(zip);
        var entrada = archivo.Entries.FirstOrDefault(e => string.Equals(e.FullName, _origen.EntradaZip, StringComparison.OrdinalIgnoreCase))
                      ?? throw new DescargaVerificadaException($"el zip no trae {_origen.EntradaZip}");
        if (entrada.Length is <= 0 or > TamanoMaximoExe)
            throw new DescargaVerificadaException($"{entrada.Name} pesa {entrada.Length} bytes, algo no cuadra");
        long total = 0;
        var ultimo = -1;
        await using (var origen = entrada.Open())
        await using (var salida = new FileStream(destino, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buf = new byte[81920];
            int leidos;
            while ((leidos = await origen.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += leidos;
                if (total > entrada.Length) throw new DescargaVerificadaException("el contenido pasa de lo que dice el zip");
                await salida.WriteAsync(buf.AsMemory(0, leidos), ct).ConfigureAwait(false);
                var porcentaje = 90 + (int)(total * 10 / entrada.Length);
                if (porcentaje != ultimo)
                {
                    ultimo = porcentaje;
                    avance?.Invoke(Math.Min(porcentaje, 99));
                }
            }
        }
        if (total != entrada.Length) throw new DescargaVerificadaException("el contenido es más corto de lo que dice el zip");
    }

    private bool EsSegura(Uri? u) =>
        u is not null && (u.Scheme == Uri.UriSchemeHttps || (_permitirHttpLocal && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    private static void Borrar(string ruta)
    {
        try { File.Delete(ruta); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Lo descargado no es lo esperado (tamaño, huella, https, contenido del zip): se descarta.</summary>
internal sealed class DescargaVerificadaException(string mensaje) : Exception(mensaje);
