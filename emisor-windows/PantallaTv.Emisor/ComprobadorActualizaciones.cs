using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PantallaTv.Emisor;

/// <summary>Una versión más nueva del emisor, ya validada contra el manifiesto firmado.</summary>
/// <param name="Texto">La versión tal como viene en el manifiesto («0.8.1»).</param>
internal sealed record Novedad(Version Version, string Texto, Uri Url, long Tamano, string Sha256, string Notas);

/// <summary>Por qué una búsqueda no ofrece nada. <see cref="NoEsNueva"/> es el caso normal: ya está al día.</summary>
internal enum Rechazo
{
    Ninguno,
    /// <summary>No se pudo descargar el manifiesto o su firma (sin internet, 404, tiempo agotado, demasiado grande, no https).</summary>
    Red,
    Firma,
    /// <summary>JSON inválido o <c>formato</c> distinto de 1.</summary>
    Formato,
    Repo,
    /// <summary>El manifiesto no trae una entrada para esta app.</summary>
    SinEntrada,
    /// <summary>Versión, tamaño o huella con un valor que no corresponde.</summary>
    Datos,
    NoEsNueva,
    /// <summary>La descarga no apunta a los releases del repo.</summary>
    Url,
}

/// <summary>El resultado de revisar un manifiesto: la novedad, o por qué no hay.</summary>
internal sealed record Revision(Novedad? Novedad, Rechazo Rechazo, string Detalle);

/// <summary>
/// Busca y descarga versiones nuevas del emisor según el contrato de <c>docs/actualizaciones.md</c>: un GET del
/// <c>version.json</c> del último release de GitHub y su firma (ECDSA P-256), sin identificadores ni cookies.
/// Solo ofrece algo si la firma es válida, el repo es el esperado, la versión es mayor que la propia y la descarga
/// apunta a los releases del repo; la descarga se acepta solo con el tamaño y el SHA-256 exactos. Todo lo que pasa
/// queda en <see cref="Registro"/>; nada se le muestra al usuario como error.
/// No instala nada: eso lo decide <see cref="Mando"/> con el usuario.
/// </summary>
internal sealed partial class ComprobadorActualizaciones : IDisposable
{
    public const string Repo = "itatagroup/pantalla-tv";
    public const string App = "emisor-windows";

    /// <summary>La clave pública de las publicaciones (SubjectPublicKeyInfo DER en base64). La privada no está en el repositorio.</summary>
    public const string ClavePublica =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENdrCrDU1A0B87/zG/2dD80YPGfx8+gK2CclanFT9+8hPOlWFc9Q4zYXJqMEdQEd6bdsQ2eMgjqwhea0vcj95zA==";

    /// <summary>GitHub redirige esta dirección al <c>version.json</c> del release más nuevo.</summary>
    public static readonly Uri ManifiestoPorDefecto = new($"https://github.com/{Repo}/releases/latest/download/version.json");

    public static string PrefijoDescargas => $"https://github.com/{Repo}/releases/download/";

    /// <summary>Tope para el manifiesto y para la firma: los dos son de pocos cientos de bytes.</summary>
    public const int LimiteManifiesto = 64 * 1024;
    public static readonly TimeSpan TiempoManifiesto = TimeSpan.FromSeconds(10);
    /// <summary>Tope para la descarga del instalador (~50 MB): holgado para una conexión lenta.</summary>
    public static readonly TimeSpan TiempoDescarga = TimeSpan.FromMinutes(30);
    /// <summary>Un instalador de más de esto no es nuestro (el actual pesa ~50 MB).</summary>
    public const long TamanoMaximo = 1L << 30;

    /// <summary>Carpeta propia de las descargas: se vacía antes de cada descarga y al abrir el programa.</summary>
    public static string CarpetaPorDefecto => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PantallaTv", "actualizaciones");

    private readonly HttpClient _http;
    private readonly byte[] _clave;
    private readonly string _prefijo;
    private readonly bool _permitirHttpLocal;

    /// <param name="handler">El que hace los pedidos (las pruebas ponen uno propio).</param>
    /// <param name="clavePublica">SubjectPublicKeyInfo DER de la clave que firma los manifiestos.</param>
    /// <param name="actual">La versión de este programa.</param>
    /// <param name="manifiesto">Dónde está el <c>version.json</c>; su firma está en la misma dirección más <c>.sig</c>.</param>
    /// <param name="carpeta">Dónde se descargan los instaladores.</param>
    /// <param name="prefijoDescargas">Solo para pruebas: las URL de descarga aceptadas. Por defecto, los releases del repo.</param>
    /// <param name="permitirHttpLocal">Solo para pruebas: aceptar <c>http</c> a la propia máquina (un servidor de prueba local).</param>
    public ComprobadorActualizaciones(HttpMessageHandler handler, byte[] clavePublica, string actual, Uri manifiesto, string carpeta,
        string? prefijoDescargas = null, bool permitirHttpLocal = false)
    {
        _clave = clavePublica;
        // Un sufijo de compilación («0.8.0-prueba») no cuenta para comparar.
        Actual = LeerVersion(actual.Split('-')[0]) ?? throw new ArgumentException($"Versión inválida: {actual}", nameof(actual));
        Manifiesto = manifiesto;
        Carpeta = carpeta;
        _prefijo = prefijoDescargas ?? PrefijoDescargas;
        _permitirHttpLocal = permitirHttpLocal;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        // Lo único que se manda: un User-Agent genérico (sin datos del equipo ni del uso).
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PantallaTv/{actual}");
    }

    /// <summary>El de verdad: GitHub (o <paramref name="manifiesto"/>), sin cookies, siguiendo redirecciones (https no baja a http).</summary>
    public static ComprobadorActualizaciones Crear(Uri? manifiesto = null) => new(
        new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 10 },
        Convert.FromBase64String(ClavePublica), Instalacion.Version, manifiesto ?? ManifiestoPorDefecto, CarpetaPorDefecto);

    public Version Actual { get; }
    public Uri Manifiesto { get; }
    public string Carpeta { get; }

    // ── Buscar ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Descarga el manifiesto y su firma y los revisa. Nunca lanza: un fallo es un <see cref="Rechazo"/>.</summary>
    public async Task<Revision> BuscarAsync(CancellationToken ct = default)
    {
        Revision r;
        try
        {
            r = await DescargarYRevisarAsync(ct).ConfigureAwait(false);
            // Son dos pedidos a «latest»: si justo entre uno y otro se publicó un release, el manifiesto y la firma
            // son de releases distintos. Un segundo intento lo resuelve (una firma falsa vuelve a fallar igual).
            if (r.Rechazo == Rechazo.Firma) r = await DescargarYRevisarAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or ActualizacionException)
        {
            r = new Revision(null, Rechazo.Red, e is OperationCanceledException && !ct.IsCancellationRequested ? "tiempo agotado" : e.Message);
        }
        Registro.Anotar(r.Novedad is { } n
            ? $"actualizaciones: hay una versión nueva {n.Texto} (tengo {Actual}), {n.Tamano} bytes"
            : $"actualizaciones: nada que ofrecer ({r.Rechazo}): {r.Detalle}");
        return r;
    }

    private async Task<Revision> DescargarYRevisarAsync(CancellationToken ct)
    {
        var manifiesto = await LeerAsync(Manifiesto, ct).ConfigureAwait(false);
        var firma = await LeerAsync(ConSufijo(Manifiesto, ".sig"), ct).ConfigureAwait(false);
        return Revisar(manifiesto, firma, _clave, Actual, Repo, App, _prefijo);
    }

    /// <summary>
    /// Las reglas del contrato, en orden, sobre los bytes ya descargados. Pura: no toca la red ni el disco.
    /// </summary>
    internal static Revision Revisar(byte[] manifiesto, byte[] firmaBase64, byte[] clavePublica, Version actual, string repo, string app, string prefijo)
    {
        // 1. La firma, antes de mirar nada del contenido.
        if (!FirmaValida(manifiesto, firmaBase64, clavePublica))
            return new Revision(null, Rechazo.Firma, "la firma del manifiesto no es válida");

        // 2. Formato, repo y la entrada de esta app.
        JsonElement entrada;
        try
        {
            using var doc = JsonDocument.Parse(manifiesto);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object
                || !raiz.TryGetProperty("formato", out var formato) || formato.ValueKind != JsonValueKind.Number
                || !formato.TryGetInt32(out var f) || f != 1)
                return new Revision(null, Rechazo.Formato, "formato desconocido");
            if (!raiz.TryGetProperty("repo", out var r) || r.ValueKind != JsonValueKind.String || r.GetString() != repo)
                return new Revision(null, Rechazo.Repo, "el manifiesto es de otro repositorio");
            if (!raiz.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Object
                || !apps.TryGetProperty(app, out var e) || e.ValueKind != JsonValueKind.Object)
                return new Revision(null, Rechazo.SinEntrada, $"el manifiesto no trae «{app}»");
            entrada = e.Clone();
        }
        catch (JsonException e)
        {
            return new Revision(null, Rechazo.Formato, "JSON inválido: " + e.Message);
        }

        // 3. Solo una versión mayor.
        var texto = Cadena(entrada, "version");
        if (texto is null || LeerVersion(texto) is not { } version)
            return new Revision(null, Rechazo.Datos, "versión inválida");
        if (version <= Normalizar(actual))
            return new Revision(null, Rechazo.NoEsNueva, $"la publicada es {texto}");

        // 4. La descarga, solo de los releases del repo. Se mira el texto y la URL ya normalizada («..» incluidos).
        var url = Cadena(entrada, "url");
        if (url is null || !url.StartsWith(prefijo, StringComparison.Ordinal)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.AbsoluteUri.StartsWith(prefijo, StringComparison.Ordinal))
            return new Revision(null, Rechazo.Url, "la descarga no es de los releases del repo: " + url);

        // 5. Tamaño y huella con forma válida (se comprueban de verdad al descargar).
        if (!entrada.TryGetProperty("tamano", out var t) || t.ValueKind != JsonValueKind.Number
            || !t.TryGetInt64(out var tamano) || tamano <= 0 || tamano > TamanoMaximo)
            return new Revision(null, Rechazo.Datos, "tamaño inválido");
        var sha = Cadena(entrada, "sha256");
        if (sha is null || !HuellaValida().IsMatch(sha))
            return new Revision(null, Rechazo.Datos, "huella SHA-256 inválida");

        return new Revision(new Novedad(version, texto, uri, tamano, sha, Notas(Cadena(entrada, "notas"))), Rechazo.Ninguno, "");
    }

    internal static bool FirmaValida(byte[] datos, byte[] firmaBase64, byte[] clavePublica)
    {
        try
        {
            var firma = Convert.FromBase64String(Encoding.ASCII.GetString(firmaBase64).Trim());
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(clavePublica, out _);
            return ec.VerifyData(datos, firma, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>«0.8» y «0.8.0» son la misma: las partes que faltan cuentan como 0. Null si no es una versión.</summary>
    internal static Version? LeerVersion(string texto) =>
        Version.TryParse(texto.Trim(), out var v) ? Normalizar(v) : null;

    private static Version Normalizar(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static string? Cadena(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>
    /// Las notas se muestran en un globo y una etiqueta: una línea, corta, sin caracteres de control ni de formato
    /// (los de dirección del texto, como U+202E, podrían dar vuelta lo que se lee).
    /// </summary>
    internal static string Notas(string? notas)
    {
        if (string.IsNullOrWhiteSpace(notas)) return "";
        var limpio = string.Join(' ', new string([.. notas.Select(c =>
                char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c)])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (limpio.Length <= 200) return limpio;
        var corte = char.IsHighSurrogate(limpio[196]) ? 196 : 197; // sin partir un emoji
        return limpio[..corte] + "…";
    }

    // ── Descargar ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Descarga el instalador a <see cref="Carpeta"/> y lo deja solo si tiene exactamente el tamaño y el SHA-256 del
    /// manifiesto. Devuelve su ruta, o null (y nada en disco) si algo falló. <paramref name="avance"/> recibe el
    /// porcentaje (0-100) desde el hilo de la descarga.
    /// </summary>
    public async Task<string?> DescargarAsync(Novedad n, Action<int>? avance = null, CancellationToken ct = default)
    {
        Limpiar();
        // El nombre lo pone el programa (la versión ya se validó como número): «Setup» hace que abra el asistente.
        var final = Path.Combine(Carpeta, $"PantallaTvSetup-{n.Version.ToString(3)}.exe");
        var parcial = final + ".parcial";
        try
        {
            Directory.CreateDirectory(Carpeta);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TiempoDescarga);
            using var resp = await _http.GetAsync(n.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            ComprobarSegura(resp);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is { } anunciado && anunciado > n.Tamano)
                throw new ActualizacionException($"el servidor anuncia {anunciado} bytes y se esperaban {n.Tamano}");

            using var huella = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var ultimo = -1;
            await using (var origen = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var destino = new FileStream(parcial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int leidos;
                while ((leidos = await origen.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                {
                    total += leidos;
                    if (total > n.Tamano) throw new ActualizacionException($"la descarga pasa de los {n.Tamano} bytes anunciados");
                    huella.AppendData(buf, 0, leidos);
                    await destino.WriteAsync(buf.AsMemory(0, leidos), cts.Token).ConfigureAwait(false);
                    var porcentaje = (int)(total * 100 / n.Tamano);
                    if (porcentaje != ultimo)
                    {
                        ultimo = porcentaje;
                        avance?.Invoke(porcentaje);
                    }
                }
            }
            if (total != n.Tamano) throw new ActualizacionException($"llegaron {total} bytes y se esperaban {n.Tamano}");
            var sha = Convert.ToHexStringLower(huella.GetHashAndReset());
            if (sha != n.Sha256) throw new ActualizacionException($"el SHA-256 no coincide ({sha})");

            File.Move(parcial, final, overwrite: true);
            Registro.Anotar($"actualizaciones: descargada y verificada la {n.Texto} en {final}");
            return final;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException
                                      or UnauthorizedAccessException or ActualizacionException)
        {
            Registro.Anotar($"actualizaciones: descarga de la {n.Texto} rechazada: " +
                            (e is OperationCanceledException && !ct.IsCancellationRequested ? "tiempo agotado" : e.Message));
            Borrar(final);
            return null;
        }
        finally
        {
            Borrar(parcial);
        }
    }

    /// <summary>Vacía la carpeta de descargas. Lo que esté en uso (el instalador recién abierto) queda para la próxima.</summary>
    public void Limpiar()
    {
        if (!Directory.Exists(Carpeta)) return;
        foreach (var archivo in Directory.EnumerateFiles(Carpeta)) Borrar(archivo);
    }

    private static void Borrar(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // en uso: se borra la próxima vez
        }
    }

    // ── Red ─────────────────────────────────────────────────────────────────────────────────────

    private bool EsSegura(Uri? u) =>
        u is not null && (u.Scheme == Uri.UriSchemeHttps || (_permitirHttpLocal && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    /// <summary>La dirección pedida y la final (tras las redirecciones) tienen que ser https.</summary>
    private void ComprobarSegura(HttpResponseMessage resp)
    {
        if (!EsSegura(resp.RequestMessage?.RequestUri))
            throw new ActualizacionException("la respuesta no llegó por https: " + resp.RequestMessage?.RequestUri);
    }

    /// <summary>Un archivo chico (manifiesto o firma): https, 10 s y como máximo <see cref="LimiteManifiesto"/>.</summary>
    private async Task<byte[]> LeerAsync(Uri url, CancellationToken ct)
    {
        if (!EsSegura(url)) throw new ActualizacionException("solo se consulta por https: " + url);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TiempoManifiesto);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        ComprobarSegura(resp);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > LimiteManifiesto)
            throw new ActualizacionException($"{url} pasa de {LimiteManifiesto} bytes");
        await using var origen = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var memoria = new MemoryStream();
        var buf = new byte[8192];
        int leidos;
        while ((leidos = await origen.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (memoria.Length + leidos > LimiteManifiesto) throw new ActualizacionException($"{url} pasa de {LimiteManifiesto} bytes");
            memoria.Write(buf, 0, leidos);
        }
        return memoria.ToArray();
    }

    /// <summary>La misma dirección con algo agregado a la ruta (sin tocar una consulta, si la hubiera).</summary>
    internal static Uri ConSufijo(Uri url, string sufijo)
    {
        var b = new UriBuilder(url);
        b.Path += sufijo;
        return b.Uri;
    }

    public void Dispose() => _http.Dispose();

    // \z y no $: en .NET, $ también acepta un salto de línea al final.
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex HuellaValida();
}

/// <summary>Algo de una actualización no cuadra (tamaño, huella, https). Solo se registra.</summary>
internal sealed class ActualizacionException(string mensaje) : Exception(mensaje);
