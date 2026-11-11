using PantallaTv.Emisor;

namespace PantallaTv.Emisor.Tests;

public class AnnexBTests
{
    // Unidades sintéticas con prefijo de 4 bytes: AUD, SPS, PPS, cuadro clave (IDR) y cuadro normal.
    private static readonly byte[] Aud = [0, 0, 0, 1, 0x09, 0xF0];
    private static readonly byte[] Sps = [0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F];
    private static readonly byte[] Pps = [0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80];
    private static readonly byte[] Idr = [0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x10];
    private static readonly byte[] Normal = [0, 0, 0, 1, 0x41, 0x9A, 0x24, 0x6C];

    private static byte[] Juntar(params byte[][] partes) => partes.SelectMany(p => p).ToArray();

    [Fact]
    public void Nals_reconoce_tipos_y_prefijos_de_3_y_4_bytes()
    {
        byte[] flujo = Juntar(Aud, Sps, [0, 0, 1, 0x68, 0x01], Idr);

        var nals = AnnexB.Nals(flujo);

        Assert.Equal([AnnexB.Aud, AnnexB.Sps, AnnexB.Pps, AnnexB.CuadroClave], nals.Select(n => n.Tipo));
        Assert.Equal(0, nals[0].Inicio);
        Assert.Equal(nals[1].Inicio, nals[0].Fin); // los NAL son contiguos: ninguno se pierde
    }

    [Fact]
    public void TieneVideo_distingue_un_cuadro_de_las_cabeceras()
    {
        Assert.False(AnnexB.TieneVideo(Juntar(Aud, Sps, Pps)));
        Assert.True(AnnexB.TieneVideo(Juntar(Aud, Normal)));
        Assert.True(AnnexB.TieneVideo(Juntar(Aud, Sps, Pps, Idr)));
    }

    [Fact]
    public void Separador_entrega_un_cuadro_por_unidad()
    {
        var recibidas = new List<byte[]>();
        var s = new SeparadorUnidades();
        // 3 unidades; la última queda «pendiente» hasta ver la marca AUD siguiente.
        s.Agregar(Juntar(Aud, Sps, Pps, Idr, Aud, Normal, Aud, Normal, Aud), recibidas.Add);

        Assert.Equal(3, recibidas.Count);
        Assert.True(recibidas[0].AsSpan().IndexOf(Sps) >= 0); // la primera trae SPS/PPS
        Assert.Equal(Juntar(Aud, Normal), recibidas[1]);
        Assert.Equal(Juntar(Aud, Normal), recibidas[2]);
    }

    [Fact]
    public void Separador_funciona_aunque_los_datos_lleguen_en_trozos_de_un_byte()
    {
        var flujo = Juntar(Aud, Sps, Pps, Idr, Aud, Normal, Aud, Normal, Aud);
        var recibidas = new List<byte[]>();
        var s = new SeparadorUnidades();

        foreach (var b in flujo) s.Agregar([b], recibidas.Add);

        Assert.Equal(3, recibidas.Count);
        Assert.Equal(Juntar(Aud, Normal), recibidas[2]);
    }

    [Fact]
    public void Separador_une_un_AUD_duplicado_con_el_cuadro_siguiente()
    {
        // El codificador a veces emite su propio AUD y además el insertado: dos marcas por cuadro.
        var recibidas = new List<byte[]>();
        var s = new SeparadorUnidades();

        s.Agregar(Juntar(Aud, Aud, Normal, Aud, Aud, Normal, Aud), recibidas.Add);

        Assert.Equal(2, recibidas.Count); // dos cuadros, no cuatro
        Assert.All(recibidas, au => Assert.True(AnnexB.TieneVideo(au)));
    }

    [Fact]
    public void Separador_descarta_lo_que_haya_antes_de_la_primera_marca()
    {
        var recibidas = new List<byte[]>();
        var s = new SeparadorUnidades();

        s.Agregar(Juntar([0x11, 0x22, 0x33], Aud, Normal, Aud), recibidas.Add);

        Assert.Single(recibidas);
        Assert.Equal(Juntar(Aud, Normal), recibidas[0]);
    }

    [Fact]
    public void Separador_aguanta_un_cuadro_mas_grande_que_su_buffer_inicial()
    {
        var grande = new byte[3 * 1024 * 1024];
        Array.Fill(grande, (byte)0x55); // sin ceros: no puede confundirse con un prefijo de inicio
        byte[] cuadro = [0, 0, 0, 1, 0x65, .. grande];
        var recibidas = new List<byte[]>();
        var s = new SeparadorUnidades();

        s.Agregar(Juntar(Aud, cuadro, Aud), recibidas.Add);

        Assert.Single(recibidas);
        Assert.Equal(Aud.Length + cuadro.Length, recibidas[0].Length);
    }
}

public class TuberiasTests
{
    private static readonly OpcionesEmision Op = new(60, 8000, 1920, 1080, 0, null);

    [Fact]
    public void La_de_GPU_no_copia_el_cuadro_a_memoria()
    {
        var a = Tuberias.Argumentos(Tuberias.AmfGpu, Op);

        Assert.Contains("vsrc_amf=monitor_index=0:framerate=60", a);
        Assert.DoesNotContain(a, x => x.Contains("hwdownload"));
        Assert.Contains("h264_amf", a);
    }

    [Theory]
    [InlineData(Tuberias.AmfGpu)]
    [InlineData(Tuberias.Amf)]
    public void AMF_no_aprieta_el_fotograma_clave(string nombre)
    {
        // Con el HRD exigido el fotograma clave sale del tamaño de un cuadro cualquiera y se ve un pulso.
        var a = Tuberias.Argumentos(nombre, Op);

        Assert.Equal("0", a[a.IndexOf("-enforce_hrd") + 1]);
    }

    [Fact]
    public void El_intervalo_de_claves_y_las_opciones_extra_se_pueden_cambiar_para_experimentar()
    {
        var a = Tuberias.Argumentos(Tuberias.AmfGpu, Op with { SegClave = 10, ExtraCodificador = "-usage  lowlatency" });

        Assert.Equal("600", a[a.IndexOf("-g") + 1]);
        // Van después de las del codificador (ffmpeg se queda con la última) y antes de la salida.
        Assert.Equal("lowlatency", a[a.LastIndexOf("-usage") + 1]);
        Assert.True(a.LastIndexOf("-usage") > a.IndexOf("-enforce_hrd"));
        Assert.Equal("pipe:1", a[^1]);
    }

    [Theory]
    [InlineData(Tuberias.Amf, "h264_amf")]
    [InlineData(Tuberias.Nvenc, "h264_nvenc")]
    [InlineData(Tuberias.Qsv, "h264_qsv")]
    [InlineData(Tuberias.Mf, "h264_mf")]
    [InlineData(Tuberias.X264, "libx264")]
    public void Cada_tuberia_usa_su_codificador_y_el_formato_que_espera_el_separador(string nombre, string codificador)
    {
        var a = Tuberias.Argumentos(nombre, Op);

        Assert.Contains(codificador, a);
        Assert.Contains("8000k", a);
        Assert.Equal((60 * Tuberias.SegundosEntreClaves).ToString(), a[a.IndexOf("-g") + 1]);
        Assert.Contains(a, x => x.Contains("aud=insert")); // sin la marca AUD no se pueden separar los cuadros
        Assert.Equal("pipe:1", a[^1]);
    }

    [Fact]
    public void Una_tuberia_desconocida_da_un_error_que_lista_las_validas()
    {
        var e = Assert.Throws<EmisionException>(() => Tuberias.Argumentos("inventada", Op));

        Assert.Contains("amf-gpu", e.Message);
    }

    [Fact]
    public void Una_tuberia_que_fallo_hace_poco_va_al_final_y_despues_vuelve_a_su_lugar()
    {
        var ahora = new DateTime(2026, 11, 10, 17, 4, 0, DateTimeKind.Utc);
        var fallos = new Dictionary<string, DateTime> { [Tuberias.Nativa] = ahora.AddSeconds(-30) };

        var reciente = Tuberias.Orden(fallos, ahora);
        var despues = Tuberias.Orden(fallos, ahora + Tuberias.CastigoPorFallo);

        // Recién fallada: se prueba al final, pero no se pierde.
        Assert.Equal(Tuberias.AmfGpu, reciente[0]);
        Assert.Equal(Tuberias.Nativa, reciente[^1]);
        Assert.Equal(Tuberias.Todas.Count, reciente.Distinct().Count());
        // Pasado el castigo vuelve a ser la primera: un fallo pasajero no condena a la peor para siempre.
        Assert.Equal(Tuberias.Todas, despues);
        Assert.Equal(Tuberias.Todas, Tuberias.Orden(new Dictionary<string, DateTime>(), ahora));
    }
}

public class RevisionTests
{
    [Fact]
    public void Separador_no_acumula_sin_fin_un_flujo_sin_marcas()
    {
        var s = new SeparadorUnidades();
        var trozo = new byte[1 << 20];
        Array.Fill(trozo, (byte)0x55);
        var entregadas = 0;

        for (var i = 0; i < 40; i++) s.Agregar(trozo, _ => entregadas++); // 40 MB sin ninguna marca AUD

        Assert.Equal(0, entregadas);
        // Y sigue funcionando cuando por fin llegan las marcas.
        byte[] aud = [0, 0, 0, 1, 0x09, 0xF0];
        byte[] cuadro = [0, 0, 0, 1, 0x41, 0x9A];
        s.Agregar([.. aud, .. cuadro, .. aud], _ => entregadas++);
        Assert.Equal(1, entregadas);
    }

    [Fact]
    public async Task Los_valores_fuera_de_rango_se_acotan_antes_del_HELLO()
    {
        var escucha = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((System.Net.IPEndPoint)escucha.LocalEndpoint).Port;
        var saludo = Task.Run(async () =>
        {
            using var cliente = await escucha.AcceptTcpClientAsync();
            var hello = await Protocolo.LeerAsync(cliente.GetStream(), CancellationToken.None);
            await cliente.GetStream().WriteAsync(Protocolo.Armar(Protocolo.HelloErr, 0, "ocupado"u8));
            return hello;
        });
        var op = new OpcionesEmision(300, 8000, 1920, 1080, 70_000, null);

        await Assert.ThrowsAsync<EmisionException>(() => new Emision().EjecutarAsync(
            new Receptor("Falso", System.Net.IPAddress.Loopback, puerto), op, null, CancellationToken.None));

        var hello = await saludo;
        escucha.Stop();
        Assert.NotNull(hello);
        Assert.Equal(120, hello.Carga[5]); // fps: 300 no cabe en un byte
        Assert.Equal(Protocolo.RetardoMaxMs, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hello.Carga.AsSpan(15, 2)));
    }

    [Fact]
    public async Task Un_ffmpeg_que_no_entrega_video_se_da_por_fallido_tras_el_plazo()
    {
        // Un «ffmpeg» falso que se queda vivo sin escribir nada.
        var falso = Path.Combine(Path.GetTempPath(), $"ffmpeg-mudo-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(falso, "@ping -n 30 127.0.0.1 >nul\r\n");
        var escucha = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((System.Net.IPEndPoint)escucha.LocalEndpoint).Port;
        var receptor = Task.Run(async () =>
        {
            using var cliente = await escucha.AcceptTcpClientAsync();
            var red = cliente.GetStream();
            await Protocolo.LeerAsync(red, CancellationToken.None);
            await red.WriteAsync(Protocolo.Armar(Protocolo.HelloOk, 0, []));
            try { while (await Protocolo.LeerAsync(red, CancellationToken.None) is not null) { } } catch (IOException) { }
        });
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, falso, Tuberias.X264);
        var emision = new Emision { PlazoPrimerCuadro = TimeSpan.FromSeconds(1) };
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var e = await Assert.ThrowsAsync<EmisionException>(() => emision.EjecutarAsync(
                new Receptor("Falso", System.Net.IPAddress.Loopback, puerto), op, null, CancellationToken.None));

            Assert.Contains("no entregó vídeo", e.Message);
            Assert.InRange(reloj.Elapsed.TotalSeconds, 0.9, 10);
        }
        finally
        {
            escucha.Stop();
            await receptor;
            File.Delete(falso);
        }
    }
}
