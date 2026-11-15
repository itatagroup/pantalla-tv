using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace PantallaTv.Emisor.Tests;

/// <summary>
/// La descarga automática de ffmpeg: versión fijada, tamaño y SHA-256 del código, solo se extrae ffmpeg.exe, y si algo no
/// cuadra no queda nada en disco. Con un servidor falso: <b>nunca</b> se baja ffmpeg de verdad.
/// </summary>
public sealed class DescargadorFfmpegTests : IDisposable
{
    private const string Entrada = "ffmpeg-9.9.9-essentials_build/bin/ffmpeg.exe";
    private static readonly byte[] ExeFalso = [.. "MZ programa de mentira"u8, .. new byte[5000]];

    private readonly string _carpeta = Path.Combine(Directory.CreateTempSubdirectory("pantallatv-ffmpeg-").FullName, "datos");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_carpeta)!, recursive: true);

    private static byte[] Zip(string entrada = Entrada, byte[]? exe = null)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var otra = new StreamWriter(zip.CreateEntry("ffmpeg-9.9.9-essentials_build/LICENSE").Open())) otra.Write("licencia");
            using var e = zip.CreateEntry(entrada).Open();
            e.Write(exe ?? ExeFalso);
        }
        return memoria.ToArray();
    }

    private static string Sha(byte[] datos) => Convert.ToHexStringLower(SHA256.HashData(datos));

    private static OrigenFfmpeg Origen(byte[] zip, string? sha = null, string url = "http://127.0.0.1:5999/ffmpeg.zip", long? tamano = null) =>
        new("9.9.9", new Uri(url), tamano ?? zip.Length, sha ?? Sha(zip), Entrada);

    private DescargadorFfmpeg Descargador(OrigenFfmpeg origen, ManejadorFalso handler, bool http = true) =>
        new(handler, origen, _carpeta, permitirHttpLocal: http);

    private static ManejadorFalso Sirve(byte[] zip, long? anunciado = null) => new(_ =>
    {
        var contenido = new ByteArrayContent(zip);
        if (anunciado is { } a) contenido.Headers.ContentLength = a;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = contenido };
    });

    private bool CarpetaVacia() => !Directory.Exists(_carpeta) || !Directory.EnumerateFileSystemEntries(_carpeta).Any();

    [Fact]
    public async Task Baja_verifica_y_deja_solo_ffmpeg_exe_en_la_carpeta_de_datos()
    {
        var zip = Zip();
        var avances = new List<int>();
        using var d = Descargador(Origen(zip), Sirve(zip));

        var r = await d.DescargarAsync(avances.Add);

        Assert.True(r.Correcto, r.Motivo);
        Assert.Equal(Path.Combine(_carpeta, "ffmpeg.exe"), r.Ruta);
        Assert.Equal(ExeFalso, await File.ReadAllBytesAsync(r.Ruta!));
        Assert.Equal(["ffmpeg.exe"], Directory.EnumerateFileSystemEntries(_carpeta).Select(Path.GetFileName)); // ni el zip, ni la licencia, ni parciales
        Assert.Equal(100, avances[^1]);
        Assert.Equal(avances.Order(), avances); // el avance solo sube
    }

    [Fact]
    public async Task Con_otra_huella_no_queda_nada_en_disco()
    {
        var zip = Zip();
        var otro = (byte[])zip.Clone();
        otro[otro.Length / 2] ^= 0xFF; // mismo tamaño, otro contenido
        using var d = Descargador(Origen(zip), Sirve(otro));

        var r = await d.DescargarAsync();

        Assert.False(r.Correcto);
        Assert.Contains("SHA-256", r.Motivo);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public async Task Un_tamano_distinto_al_fijado_se_rechaza()
    {
        var zip = Zip();
        using var d1 = Descargador(Origen(zip), Sirve(zip, anunciado: zip.Length + 10));
        Assert.False((await d1.DescargarAsync()).Correcto); // el servidor anuncia otro tamaño

        using var d2 = Descargador(Origen(zip, tamano: zip.Length - 1), Sirve(zip)); // llega más de lo esperado
        Assert.False((await d2.DescargarAsync()).Correcto);

        using var d3 = Descargador(Origen(zip, tamano: zip.Length + 1), Sirve(zip)); // llega menos
        var r = await d3.DescargarAsync();
        Assert.False(r.Correcto);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public async Task Mientras_la_huella_este_pendiente_se_niega_sin_pedir_nada()
    {
        var zip = Zip();
        var handler = Sirve(zip);
        using var d = Descargador(Origen(zip, sha: "PENDIENTE"), handler);

        var r = await d.DescargarAsync();

        Assert.False(r.Correcto);
        Assert.Contains("huella", r.Motivo);
        Assert.Equal(0, handler.Pedidos);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public async Task El_zip_sin_ffmpeg_exe_en_la_ruta_esperada_se_rechaza()
    {
        var zip = Zip(entrada: "otra-carpeta/bin/ffmpeg.exe"); // huella correcta, pero no es lo que se espera
        using var d = Descargador(Origen(zip), Sirve(zip));

        var r = await d.DescargarAsync();

        Assert.False(r.Correcto);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public async Task Exige_https_salvo_el_servidor_de_prueba_local()
    {
        var zip = Zip();
        using var d1 = Descargador(Origen(zip, url: "http://ejemplo.invalid/ffmpeg.zip"), Sirve(zip), http: true); // http, pero no es local
        Assert.False((await d1.DescargarAsync()).Correcto);

        using var d2 = Descargador(Origen(zip, url: "https://ejemplo.invalid/ffmpeg.zip"), new ManejadorFalso(p => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://ejemplo.invalid/redirigido"), // una redirección que baja a http
        }), http: false);
        var r = await d2.DescargarAsync();
        Assert.False(r.Correcto);
        Assert.Contains("https", r.Motivo);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public async Task Un_error_del_servidor_se_cuenta_en_palabras()
    {
        var zip = Zip();
        using var d = Descargador(Origen(zip), new ManejadorFalso(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var r = await d.DescargarAsync();
        Assert.False(r.Correcto);
        Assert.StartsWith("No se pudo descargar ffmpeg", r.Motivo);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public async Task Cancelar_corta_la_descarga_y_no_deja_nada()
    {
        var zip = Zip();
        using var cts = new CancellationTokenSource();
        var handler = new ManejadorFalso(_ =>
        {
            var c = new StreamContent(new SeColgaDespuesDe(zip, zip.Length / 2, cts));
            c.Headers.ContentLength = zip.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = c };
        });
        using var d = Descargador(Origen(zip), handler);

        var r = await d.DescargarAsync(ct: cts.Token);

        Assert.True(r.Cancelada);
        Assert.False(r.Correcto);
        Assert.True(CarpetaVacia());
    }

    [Fact]
    public void La_version_fijada_trae_una_huella_valida_y_viene_por_https()
    {
        var o = DescargadorFfmpeg.Fijado;
        Assert.True(o.HuellaValida);
        Assert.Equal(Uri.UriSchemeHttps, o.Url.Scheme);
        Assert.StartsWith("ffmpeg-" + o.Version, o.EntradaZip);
        Assert.EndsWith("/bin/ffmpeg.exe", o.EntradaZip);
        Assert.True(o.Tamano > 1 << 20);
    }

    [Theory]
    [InlineData("PENDIENTE", false)]
    [InlineData("", false)]
    [InlineData("60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA", false)] // en minúsculas, como se compara
    [InlineData("60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047b", false)] // 63
    [InlineData("60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", true)]
    public void Solo_una_huella_de_64_hexadecimales_en_minusculas_vale(string sha, bool vale) =>
        Assert.Equal(vale, new OrigenFfmpeg("1", new Uri("https://x.invalid/a.zip"), 1, sha, "a").HuellaValida);

    [Fact]
    public async Task El_mando_baja_ffmpeg_avisa_y_deja_de_ofrecerlo()
    {
        var zip = Zip();
        var handler = Sirve(zip);
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta + "-ajustes", "ajustes.json"));
        ajustes.ConAudio = false;
        using var mando = new Mando(ajustes, new ContextoInmediato(), () => []);
        mando.CrearDescargadorFfmpeg = () => Descargador(Origen(zip), handler);
        var ruta = Path.Combine(_carpeta, "ffmpeg.exe");
        mando.BuscarFfmpeg = () => File.Exists(ruta) ? ruta : null;
        var avisos = new List<string>();
        mando.Aviso += (t, _) => avisos.Add(t);

        Assert.True(mando.PuedeDescargarFfmpeg);
        await mando.DescargarFfmpegAsync();

        Assert.Null(mando.AvanceFfmpeg);
        Assert.False(mando.PuedeDescargarFfmpeg); // ya está
        Assert.Contains("quedó instalado", mando.EstadoFfmpeg);
        Assert.Contains(avisos, a => a.Contains("verificado"));
        Assert.Equal(1, handler.Pedidos);
    }

    [Fact]
    public async Task El_mando_cuenta_por_que_fallo_y_se_puede_volver_a_intentar()
    {
        var zip = Zip();
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta + "-ajustes", "ajustes.json"));
        ajustes.ConAudio = false;
        using var mando = new Mando(ajustes, new ContextoInmediato(), () => []);
        mando.CrearDescargadorFfmpeg = () => Descargador(Origen(zip, sha: new string('0', 64)), Sirve(zip));
        mando.BuscarFfmpeg = () => null;

        await mando.DescargarFfmpegAsync();

        Assert.Null(mando.AvanceFfmpeg);
        Assert.Contains("SHA-256", mando.EstadoFfmpeg);
        Assert.True(mando.PuedeDescargarFfmpeg);
    }

    [Fact]
    public void La_confirmacion_dice_de_donde_que_version_y_cuanto_pesa()
    {
        var texto = Mando.TextoConfirmacionFfmpeg();
        Assert.Contains(DescargadorFfmpeg.Fijado.Version, texto);
        Assert.Contains(DescargadorFfmpeg.Fijado.Url.Host, texto);
        Assert.Contains("SHA-256", texto);
        Assert.Contains("MB", texto);
    }

    /// <summary>Entrega la primera parte y, al pedir más, cancela y se queda esperando (una conexión que se cae a medias).</summary>
    private sealed class SeColgaDespuesDe(byte[] datos, int hasta, CancellationTokenSource cancelar) : Stream
    {
        private int _leidos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _leidos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_leidos >= hasta)
            {
                await cancelar.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }
            var n = Math.Min(Math.Min(buffer.Length, 1000), hasta - _leidos);
            datos.AsMemory(_leidos, n).CopyTo(buffer);
            _leidos += n;
            return n;
        }
    }
}
