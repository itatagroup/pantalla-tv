using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PantallaTv.Emisor.Tests;

/// <summary>Responde con lo que diga la función y cuenta los pedidos. Sin red de verdad.</summary>
internal sealed class ManejadorFalso(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int Pedidos;
    public readonly ConcurrentQueue<HttpRequestMessage> Recibidos = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Pedidos);
        Recibidos.Enqueue(request);
        var r = responder(request);
        r.RequestMessage ??= request;
        return Task.FromResult(r);
    }
}

/// <summary>
/// Las reglas de <c>docs/actualizaciones.md</c>: firma, repo, versión, URL, tamaño y huella; y lo que hace
/// <see cref="Mando"/> con ellas (solo busca cuando se le pide, no ofrece mientras transmite).
/// </summary>
public sealed class ActualizacionesTests : IDisposable
{
    private const string Prefijo = "https://github.com/itatagroup/pantalla-tv/releases/download/";
    private static readonly Uri UrlManifiesto = ComprobadorActualizaciones.ManifiestoPorDefecto;
    private static readonly Version Actual = new(0, 8, 0, 0);

    private readonly ECDsa _clave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-actualizaciones-").FullName;

    public void Dispose()
    {
        _clave.Dispose();
        Directory.Delete(_carpeta, recursive: true);
    }

    private byte[] Publica => _clave.ExportSubjectPublicKeyInfo();

    private static byte[] Firmar(ECDsa clave, byte[] datos) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(clave.SignData(datos, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

    private static byte[] Manifiesto(string version = "0.8.1", string? url = null, long tamano = 3, string? sha = null,
        string repo = "itatagroup/pantalla-tv", int formato = 1, string app = "emisor-windows", string notas = "Arreglos") =>
        Encoding.UTF8.GetBytes($$"""
        {
          "formato": {{formato}},
          "repo": "{{repo}}",
          "apps": {
            "{{app}}": { "version": "{{version}}", "url": "{{url ?? Prefijo + $"v{version}/PantallaTvSetup-{version}.exe"}}", "tamano": {{tamano}}, "sha256": "{{sha ?? Sha("abc"u8.ToArray())}}", "notas": "{{notas}}" },
            "receptor-android": { "version": "0.5.0", "versionCode": 7, "url": "{{Prefijo}}v0.8.1/pantalla-receptor-0.5.0.apk", "tamano": 30000, "sha256": "{{new string('a', 64)}}", "notas": "Otra app" }
          }
        }
        """);

    private static string Sha(byte[] datos) => Convert.ToHexStringLower(SHA256.HashData(datos));

    private Revision Revisar(byte[] manifiesto, byte[]? firma = null) =>
        ComprobadorActualizaciones.Revisar(manifiesto, firma ?? Firmar(_clave, manifiesto), Publica, Actual,
            ComprobadorActualizaciones.Repo, ComprobadorActualizaciones.App, Prefijo);

    // ── Reglas sobre el manifiesto ─────────────────────────────────────────────────────────────

    [Fact]
    public void Con_firma_valida_y_version_mayor_ofrece_la_novedad()
    {
        var r = Revisar(Manifiesto());
        Assert.Equal(Rechazo.Ninguno, r.Rechazo);
        Assert.NotNull(r.Novedad);
        Assert.Equal(new Version(0, 8, 1, 0), r.Novedad.Version);
        Assert.Equal("0.8.1", r.Novedad.Texto);
        Assert.Equal(3, r.Novedad.Tamano);
        Assert.Equal("Arreglos", r.Novedad.Notas);
        Assert.Equal(Prefijo + "v0.8.1/PantallaTvSetup-0.8.1.exe", r.Novedad.Url.AbsoluteUri);
    }

    [Fact]
    public void Firmado_con_otra_clave_se_rechaza()
    {
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var m = Manifiesto();
        Assert.Equal(Rechazo.Firma, Revisar(m, Firmar(otra, m)).Rechazo);
    }

    [Fact]
    public void Un_byte_cambiado_en_el_manifiesto_invalida_la_firma()
    {
        var m = Manifiesto();
        var firma = Firmar(_clave, m);
        var alterado = (byte[])m.Clone();
        alterado[Encoding.ASCII.GetString(m).IndexOf("0.8.1", StringComparison.Ordinal) + 4] = (byte)'9'; // 0.8.1 → 0.8.9
        var r = Revisar(alterado, firma);
        Assert.Equal(Rechazo.Firma, r.Rechazo);
        Assert.Null(r.Novedad);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es base64 ###")]
    [InlineData("AAAA")]
    public void Una_firma_vacia_o_invalida_se_rechaza(string firma) =>
        Assert.Equal(Rechazo.Firma, Revisar(Manifiesto(), Encoding.ASCII.GetBytes(firma)).Rechazo);

    [Fact]
    public void La_firma_con_salto_de_linea_al_final_sirve() =>
        Assert.Equal(Rechazo.Ninguno, Revisar(Manifiesto(), [.. Firmar(_clave, Manifiesto()), (byte)'\n']).Rechazo);

    [Fact]
    public void Un_manifiesto_de_otro_repo_se_rechaza_aunque_este_bien_firmado() =>
        Assert.Equal(Rechazo.Repo, Revisar(Manifiesto(repo: "itatagroup/launcher-tv")).Rechazo);

    [Fact]
    public void Otro_formato_o_sin_la_entrada_de_esta_app_no_ofrece_nada()
    {
        Assert.Equal(Rechazo.Formato, Revisar(Manifiesto(formato: 2)).Rechazo);
        Assert.Equal(Rechazo.SinEntrada, Revisar(Manifiesto(app: "launcher-android")).Rechazo);
        Assert.Equal(Rechazo.Formato, Revisar("no es json"u8.ToArray()).Rechazo);
    }

    [Theory]
    [InlineData("0.8.0", nameof(Rechazo.NoEsNueva))]
    [InlineData("0.8", nameof(Rechazo.NoEsNueva))] // «0.8» es la 0.8.0
    [InlineData("0.7.9", nameof(Rechazo.NoEsNueva))]
    [InlineData("0.8.0.1", nameof(Rechazo.Ninguno))]
    [InlineData("0.8.1", nameof(Rechazo.Ninguno))]
    [InlineData("1.0.0", nameof(Rechazo.Ninguno))]
    [InlineData("ocho", nameof(Rechazo.Datos))]
    public void Solo_se_ofrece_una_version_mayor(string version, string esperado) =>
        Assert.Equal(Enum.Parse<Rechazo>(esperado), Revisar(Manifiesto(version: version)).Rechazo);

    [Theory]
    [InlineData("https://github.com/otro/pantalla-tv/releases/download/v0.8.1/PantallaTvSetup-0.8.1.exe")]
    [InlineData("https://github.com/itatagroup/pantalla-tv-falso/releases/download/v0.8.1/x.exe")]
    [InlineData("http://github.com/itatagroup/pantalla-tv/releases/download/v0.8.1/PantallaTvSetup-0.8.1.exe")]
    [InlineData("https://ejemplo.com/itatagroup/pantalla-tv/releases/download/x.exe")]
    [InlineData("https://github.com/itatagroup/pantalla-tv/releases/download/../../../otro/x.exe")]
    [InlineData("https://github.com.ejemplo.com/itatagroup/pantalla-tv/releases/download/x.exe")]
    public void La_descarga_tiene_que_ser_de_los_releases_del_repo(string url) =>
        Assert.Equal(Rechazo.Url, Revisar(Manifiesto(url: url)).Rechazo);

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(3, "ABC")]
    [InlineData(3, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\\n")] // un «\n» del JSON al final
    [InlineData(3, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")] // en mayúsculas no: el contrato dice minúsculas
    public void Tamano_y_huella_con_forma_invalida_se_rechazan(long tamano, string? sha) =>
        Assert.Equal(Rechazo.Datos, Revisar(Manifiesto(tamano: tamano, sha: sha)).Rechazo);

    [Fact]
    public void Las_notas_se_limpian_para_el_globo()
    {
        Assert.Equal("Uno dos", ComprobadorActualizaciones.Notas("Uno\n\tdos  "));
        Assert.Equal("", ComprobadorActualizaciones.Notas(null));
        Assert.Equal("al revés", ComprobadorActualizaciones.Notas("al‮revés")); // sin caracteres de dirección
        Assert.Equal(198, ComprobadorActualizaciones.Notas(new string('x', 500)).Length);
    }

    /// <summary>La clave pública del contrato es una P-256 válida (si estuviera mal copiada, nada se podría verificar).</summary>
    [Fact]
    public void La_clave_publica_del_proyecto_es_una_P256_valida()
    {
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ComprobadorActualizaciones.ClavePublica), out var leidos);
        Assert.Equal(91, leidos);
        Assert.Equal(256, ec.KeySize);
    }

    /// <summary>
    /// Un manifiesto firmado por <c>herramientas/firmar-manifiesto.mjs</c> (Node, con una clave de prueba descartada)
    /// se verifica aquí: los dos lados hablan el mismo formato de firma (DER).
    /// </summary>
    [Fact]
    public void Una_firma_hecha_con_el_script_de_Node_se_verifica_en_NET()
    {
        var publica = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAETrzAMCto7SKOUy3u+kCZKqTcL7Wlq4wiR/l88kAGFHImWbEj0aqU0dTZ8CguCEuy0HEDfVGhh7TFe5QHVxp1ag==");
        var manifiesto = Encoding.UTF8.GetBytes("""{"formato":1,"repo":"itatagroup/pantalla-tv","apps":{"emisor-windows":{"version":"9.9.9","url":"https://github.com/itatagroup/pantalla-tv/releases/download/v9.9.9/PantallaTvSetup-9.9.9.exe","tamano":3,"sha256":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","notas":"Vector de prueba firmado con Node"}}}""");
        var firma = "MEUCIQCl19Q/upCmatZ2/KEPGvWqp1lbo4KS4helCq/qxQ++GgIgC88mYVxfCfhKXF3spp4TDEwRjO9DbL07lYkt+Ej+o8k="u8.ToArray();
        var r = ComprobadorActualizaciones.Revisar(manifiesto, firma, publica, Actual, ComprobadorActualizaciones.Repo, ComprobadorActualizaciones.App, Prefijo);
        Assert.Equal(Rechazo.Ninguno, r.Rechazo);
        Assert.Equal("9.9.9", r.Novedad!.Texto);
    }

    // ── Red y descarga (con un manejador falso) ────────────────────────────────────────────────

    private ComprobadorActualizaciones Comprobador(HttpMessageHandler handler, Uri? manifiesto = null) =>
        new(handler, Publica, "0.8.0", manifiesto ?? UrlManifiesto, Path.Combine(_carpeta, "descargas"));

    private static HttpResponseMessage Ok(byte[] cuerpo) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(cuerpo) };

    /// <summary>Sirve el manifiesto, su firma y el instalador (<paramref name="instalador"/>) como lo haría GitHub.</summary>
    private ManejadorFalso Servidor(byte[] manifiesto, byte[] instalador, byte[]? firma = null) => new(req =>
    {
        var ruta = req.RequestUri!.AbsolutePath;
        if (ruta.EndsWith("/emisor-windows.json")) return Ok(manifiesto);
        if (ruta.EndsWith("/emisor-windows.json.sig")) return Ok(firma ?? Firmar(_clave, manifiesto));
        if (ruta.EndsWith(".exe")) return Ok(instalador);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task Busca_con_un_GET_sin_cookies_ni_datos_y_con_User_Agent_generico()
    {
        var handler = Servidor(Manifiesto(), "abc"u8.ToArray());
        using var c = Comprobador(handler);
        var r = await c.BuscarAsync();
        Assert.NotNull(r.Novedad);
        Assert.Equal(2, handler.Pedidos);
        Assert.All(handler.Recibidos, p =>
        {
            Assert.Equal(HttpMethod.Get, p.Method);
            Assert.Null(p.Content);
            Assert.Equal("PantallaTv/0.8.0", p.Headers.UserAgent.ToString());
            Assert.False(p.Headers.Contains("Cookie"));
            Assert.True(string.IsNullOrEmpty(p.RequestUri!.Query));
        });
        Assert.Equal(UrlManifiesto, handler.Recibidos.First().RequestUri);
        Assert.Equal(UrlManifiesto.AbsoluteUri + ".sig", handler.Recibidos.Last().RequestUri!.AbsoluteUri);
    }

    /// <summary>Si justo entre el manifiesto y la firma se publicó otro release, el segundo intento los empareja.</summary>
    [Fact]
    public async Task Una_firma_de_otro_release_se_reintenta_una_vez()
    {
        var m = Manifiesto();
        var firmas = 0;
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new ManejadorFalso(req => req.RequestUri!.AbsolutePath.EndsWith(".sig")
            ? Ok(++firmas == 1 ? Firmar(otra, m) : Firmar(_clave, m))
            : Ok(m));
        using var c = Comprobador(handler);
        Assert.NotNull((await c.BuscarAsync()).Novedad);
        Assert.Equal(4, handler.Pedidos);
    }

    [Fact]
    public async Task Un_manifiesto_de_mas_de_64_KB_no_se_lee()
    {
        var grande = new byte[ComprobadorActualizaciones.LimiteManifiesto + 1];
        using var c = Comprobador(new ManejadorFalso(_ => Ok(grande)));
        Assert.Equal(Rechazo.Red, (await c.BuscarAsync()).Rechazo);
    }

    [Fact]
    public async Task Un_manifiesto_por_http_ni_se_pide()
    {
        using var c = Comprobador(new ManejadorFalso(_ => throw new InvalidOperationException("no debió pedirse")),
            new Uri("http://github.com/itatagroup/pantalla-tv/releases/download/actualizaciones/emisor-windows.json"));
        Assert.Equal(Rechazo.Red, (await c.BuscarAsync()).Rechazo);
    }

    [Fact]
    public async Task Sin_conexion_no_ofrece_nada_ni_lanza()
    {
        using var c = Comprobador(new ManejadorFalso(_ => throw new HttpRequestException("sin red")));
        var r = await c.BuscarAsync();
        Assert.Equal(Rechazo.Red, r.Rechazo);
        Assert.Null(r.Novedad);
    }

    [Fact]
    public async Task Descarga_con_tamano_y_huella_exactos_queda_lista()
    {
        var instalador = "abc"u8.ToArray();
        using var c = Comprobador(Servidor(Manifiesto(), instalador));
        var n = (await c.BuscarAsync()).Novedad!;
        var avances = new List<int>();
        var ruta = await c.DescargarAsync(n, avances.Add);
        Assert.NotNull(ruta);
        Assert.Equal("PantallaTvSetup-0.8.1.exe", Path.GetFileName(ruta));
        Assert.Equal(instalador, File.ReadAllBytes(ruta));
        Assert.Equal(100, avances.Last());
        Assert.Single(Directory.GetFiles(c.Carpeta)); // sin el .parcial
    }

    [Theory]
    [InlineData("abcd")] // de más
    [InlineData("ab")] // de menos
    [InlineData("xyz")] // mismo tamaño, otra huella
    public async Task Descarga_que_no_cuadra_se_borra(string contenido)
    {
        using var c = Comprobador(Servidor(Manifiesto(), Encoding.ASCII.GetBytes(contenido)));
        var n = (await c.BuscarAsync()).Novedad!;
        Assert.Null(await c.DescargarAsync(n));
        Assert.Empty(Directory.GetFiles(c.Carpeta));
    }

    [Fact]
    public async Task Una_redireccion_que_baja_a_http_se_rechaza()
    {
        var instalador = "abc"u8.ToArray();
        using var c = Comprobador(new ManejadorFalso(req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith(".exe")) return servidorResponder(req);
            // Como si el handler hubiera seguido una redirección a http.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(instalador),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://objects.githubusercontent.com/x.exe"),
            };
        }));
        var n = (await c.BuscarAsync()).Novedad!;
        Assert.Null(await c.DescargarAsync(n));
        Assert.Empty(Directory.GetFiles(c.Carpeta));

        HttpResponseMessage servidorResponder(HttpRequestMessage req)
        {
            var ruta = req.RequestUri!.AbsolutePath;
            return ruta.EndsWith(".sig") ? Ok(Firmar(_clave, Manifiesto())) : Ok(Manifiesto());
        }
    }

    // ── Lo que hace el Mando ───────────────────────────────────────────────────────────────────

    private Mando NuevoMando(HttpMessageHandler handler)
    {
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta, "ajustes.json"));
        ajustes.ConAudio = false; // en pruebas automáticas, nunca el audio
        return new Mando(ajustes, new ContextoInmediato(), () => [], comprobador: Comprobador(handler))
        {
            LanzarInstalador = _ => throw new InvalidOperationException("no debió abrirse ningún instalador"),
        };
    }

    [Fact]
    public void El_manifiesto_es_el_propio_del_emisor_en_el_release_fijo() =>
        Assert.Equal("https://github.com/itatagroup/pantalla-tv/releases/download/actualizaciones/emisor-windows.json",
            ComprobadorActualizaciones.ManifiestoPorDefecto.AbsoluteUri);

    [Fact]
    public void Al_abrir_no_se_conecta_a_nada()
    {
        var handler = new ManejadorFalso(_ => throw new InvalidOperationException("no debió conectarse solo"));
        using var mando = NuevoMando(handler);
        mando.IniciarActualizaciones();
        Assert.Equal(0, handler.Pedidos);
        Assert.True(mando.PuedeBuscarActualizaciones);
        Assert.Null(mando.NovedadOfrecida);
    }

    [Fact]
    public async Task Ofrece_la_version_nueva_y_Despues_la_esconde_hasta_la_proxima_busqueda()
    {
        var handler = Servidor(Manifiesto(), "abc"u8.ToArray());
        using var mando = NuevoMando(handler);
        var avisos = new List<string>();
        mando.Aviso += (t, _) => avisos.Add(t);

        await mando.ComprobarAsync();
        Assert.Equal("0.8.1", mando.NovedadOfrecida?.Texto);
        Assert.Single(avisos, a => a.Contains("Hay una versión nueva (0.8.1): Arreglos"));

        mando.PosponerActualizacion();
        Assert.Null(mando.NovedadOfrecida);

        await mando.ComprobarAsync(); // buscar de nuevo la vuelve a ofrecer
        Assert.NotNull(mando.NovedadOfrecida);
    }

    [Fact]
    public async Task Una_firma_mala_no_ofrece_nada_ni_avisa_con_error()
    {
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var m = Manifiesto();
        using var mando = NuevoMando(Servidor(m, "abc"u8.ToArray(), Firmar(otra, m)));
        var avisos = new List<(string Texto, ToolTipIcon Icono)>();
        mando.Aviso += (t, i) => avisos.Add((t, i));
        await mando.ComprobarAsync();
        Assert.Null(mando.NovedadOfrecida);
        var aviso = Assert.Single(avisos);
        Assert.Equal(ToolTipIcon.Info, aviso.Icono);
        Assert.StartsWith("No se pudo comprobar", aviso.Texto);
    }

    [Fact]
    public void El_pedido_de_cierre_es_de_una_carpeta()
    {
        Assert.Equal(Arranque.NombreCerrar(@"C:\Programas\Pantalla TV"), Arranque.NombreCerrar(@"c:\programas\pantalla tv\"));
        Assert.NotEqual(Arranque.NombreCerrar(@"C:\Programas\Pantalla TV"), Arranque.NombreCerrar(@"C:\prueba"));
    }

    /// <summary>
    /// Mientras se transmite (una transmisión de verdad contra un receptor falso que no contesta el saludo) no se
    /// ofrece ni se descarga nada; al detener, queda ofrecida.
    /// </summary>
    [Fact]
    public async Task No_ofrece_ni_instala_mientras_transmite_y_la_ofrece_al_terminar()
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        try
        {
            var handler = Servidor(Manifiesto(), "abc"u8.ToArray());
            using var mando = NuevoMando(handler);
            mando.Cambiar(a => a.Reconectar = false);
            var avisos = new ConcurrentQueue<string>();
            mando.Aviso += (t, _) => avisos.Enqueue(t);
            var aceptada = escucha.AcceptTcpClientAsync();
            mando.Iniciar(new Receptor("Falso", IPAddress.Loopback, ((IPEndPoint)escucha.LocalEndpoint).Port));
            using var cliente = await aceptada.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(mando.Transmitiendo);

            await mando.ComprobarAsync();
            Assert.Null(mando.NovedadOfrecida);
            Assert.Contains(avisos, a => a.Contains("se ofrece al terminar de transmitir"));
            await mando.ActualizarAhoraAsync(); // no hace nada: no hay nada ofrecido
            Assert.Equal(2, handler.Pedidos); // solo el manifiesto y la firma

            mando.Detener();
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (mando.Transmitiendo || mando.EnVivo is not null)
            {
                Assert.True(DateTime.UtcNow < limite, "no terminó al detener");
                await Task.Delay(50);
            }
            Assert.Equal("0.8.1", mando.NovedadOfrecida?.Texto);
        }
        finally
        {
            escucha.Stop();
        }
    }

    // ── De punta a punta, con un servidor HTTP local ───────────────────────────────────────────

    /// <summary>
    /// Un <see cref="HttpListener"/> en 127.0.0.1 sirve el manifiesto, la firma (con una clave generada aquí) y el
    /// instalador; el mando lo encuentra, lo descarga, lo verifica y llama a abrir el instalador con el archivo listo.
    /// No se ejecuta nada: el instalador de prueba son bytes al azar.
    /// </summary>
    [Fact]
    public async Task De_punta_a_punta_queda_listo_para_instalar()
    {
        int puerto;
        using (var libre = new TcpListener(IPAddress.Loopback, 0))
        {
            libre.Start();
            puerto = ((IPEndPoint)libre.LocalEndpoint).Port;
            libre.Stop();
        }
        var raiz = $"http://127.0.0.1:{puerto}/";
        var instalador = RandomNumberGenerator.GetBytes(300_000);
        var manifiesto = Encoding.UTF8.GetBytes($$$"""
            {"formato":1,"repo":"itatagroup/pantalla-tv","apps":{"emisor-windows":{"version":"0.9.0","url":"{{{raiz}}}releases/download/v0.9.0/PantallaTvSetup-0.9.0.exe","tamano":{{{instalador.Length}}},"sha256":"{{{Sha(instalador)}}}","notas":"Prueba local"} } }
            """);
        var archivos = new Dictionary<string, byte[]>
        {
            ["/latest/version.json"] = manifiesto,
            ["/latest/version.json.sig"] = Firmar(_clave, manifiesto),
            ["/releases/download/v0.9.0/PantallaTvSetup-0.9.0.exe"] = instalador,
        };

        using var servidor = new HttpListener();
        servidor.Prefixes.Add(raiz);
        servidor.Start();
        using var parar = new CancellationTokenSource();
        var sirviendo = Task.Run(async () =>
        {
            while (!parar.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await servidor.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                using var resp = ctx.Response;
                if (ctx.Request.Headers["Cookie"] is not null || ctx.Request.HttpMethod != "GET") resp.StatusCode = 400;
                else if (archivos.TryGetValue(ctx.Request.Url!.AbsolutePath, out var cuerpo))
                {
                    resp.ContentLength64 = cuerpo.Length;
                    await resp.OutputStream.WriteAsync(cuerpo);
                }
                else resp.StatusCode = 404;
            }
        });

        try
        {
            var ajustes = Ajustes.Cargar(Path.Combine(_carpeta, "ajustes.json"));
            ajustes.ConAudio = false;
            var comprobador = new ComprobadorActualizaciones(new SocketsHttpHandler { UseCookies = false }, Publica, "0.8.0",
                new Uri(raiz + "latest/version.json"), Path.Combine(_carpeta, "descargas"),
                prefijoDescargas: raiz + "releases/download/", permitirHttpLocal: true);
            string? lanzado = null;
            using var mando = new Mando(ajustes, new ContextoInmediato(), () => [], comprobador: comprobador)
            {
                LanzarInstalador = ruta => lanzado = ruta,
            };
            var avances = new ConcurrentQueue<int?>();
            mando.Cambio += () => avances.Enqueue(mando.AvanceDescarga);

            await mando.ComprobarAsync();
            Assert.Equal("0.9.0", mando.NovedadOfrecida?.Texto);
            Assert.Equal("Prueba local", mando.NovedadOfrecida?.Notas);

            await mando.ActualizarAhoraAsync();
            Assert.NotNull(lanzado);
            Assert.Equal(Path.Combine(_carpeta, "descargas", "PantallaTvSetup-0.9.0.exe"), lanzado);
            Assert.Equal(Sha(instalador), Sha(File.ReadAllBytes(lanzado)));
            Assert.Contains(avances, a => a == 0);
            Assert.Null(mando.AvanceDescarga);
            Assert.Null(mando.NovedadOfrecida); // ya se abrió el instalador
            Assert.Contains("Se abrió el instalador", mando.EstadoActualizaciones);
        }
        finally
        {
            parar.Cancel();
            servidor.Stop();
            await sirviendo.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Manifiesto_url_solo_acepta_https()
    {
        Assert.Equal(new Uri("https://ejemplo.com/v.json"), Cli.UrlManifiesto(["--manifiesto-url", "https://ejemplo.com/v.json"]));
        Assert.Null(Cli.UrlManifiesto(["--manifiesto-url", "http://ejemplo.com/v.json"]));
        Assert.Null(Cli.UrlManifiesto(["--manifiesto-url", "no es url"]));
        Assert.Null(Cli.UrlManifiesto([]));
    }
}
