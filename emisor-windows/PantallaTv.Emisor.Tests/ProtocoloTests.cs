using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PantallaTv.Emisor;

namespace PantallaTv.Emisor.Tests;

public class ProtocoloTests
{
    [Fact]
    public void Armar_pone_cabecera_en_big_endian()
    {
        var m = Protocolo.Armar(Protocolo.Video, 0x0102030405060708UL, new byte[] { 9, 8, 7 });

        Assert.Equal(13 + 3, m.Length);
        Assert.Equal(Protocolo.Video, m[0]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(1, 4)));
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(5, 8)));
        Assert.Equal(new byte[] { 9, 8, 7 }, m[13..]);
    }

    [Fact]
    public void Hello_lleva_17_bytes_de_carga_con_el_retardo_al_final()
    {
        var m = Protocolo.ArmarHello(1920, 1080, 60, 48000, 2, "12", 2000);
        var c = m.AsSpan(13);

        Assert.Equal(17, c.Length);
        Assert.Equal(1, c[0]);
        Assert.Equal(1920, BinaryPrimitives.ReadUInt16BigEndian(c.Slice(1, 2)));
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(c.Slice(3, 2)));
        Assert.Equal(60, c[5]);
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32BigEndian(c.Slice(6, 4)));
        Assert.Equal(2, c[10]);
        Assert.Equal("0012", Encoding.ASCII.GetString(c.Slice(11, 4))); // el PIN se rellena con ceros
        Assert.Equal(2000, BinaryPrimitives.ReadUInt16BigEndian(c.Slice(15, 2)));
    }

    [Fact]
    public async Task LeerAsync_recupera_lo_que_Armar_escribio()
    {
        var m = Protocolo.Armar(Protocolo.Pong, 77, new byte[] { 1, 2, 3, 4 });
        using var flujo = new MemoryStream(m);

        var leido = await Protocolo.LeerAsync(flujo, CancellationToken.None);

        Assert.NotNull(leido);
        Assert.Equal(Protocolo.Pong, leido.Tipo);
        Assert.Equal(77UL, leido.PtsUs);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, leido.Carga);
    }

    [Fact]
    public async Task LeerAsync_devuelve_null_si_la_conexion_se_cierra_a_medias()
    {
        var m = Protocolo.Armar(Protocolo.Video, 1, new byte[100]);
        using var flujo = new MemoryStream(m[..50]);

        Assert.Null(await Protocolo.LeerAsync(flujo, CancellationToken.None));
    }

    [Fact]
    public async Task LeerAsync_rechaza_una_carga_absurda()
    {
        var cab = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(cab.AsSpan(1, 4), (uint)Protocolo.MaxCarga + 1);
        using var flujo = new MemoryStream(cab);

        await Assert.ThrowsAsync<InvalidDataException>(() => Protocolo.LeerAsync(flujo, CancellationToken.None));
    }

    [Theory]
    [InlineData("PANTALLA-TV 1 TV Stick Mi 5600", "TV Stick Mi", 5600)]
    [InlineData("PANTALLA-TV 1 Sala 5601", "Sala", 5601)]
    [InlineData("  PANTALLA-TV 1 Sala 5600\n", "Sala", 5600)]
    public void InterpretarAnuncio_entiende_nombres_con_espacios(string texto, string nombre, int puerto)
    {
        var r = Protocolo.InterpretarAnuncio(texto, IPAddress.Parse("10.0.0.20"));

        Assert.NotNull(r);
        Assert.Equal(nombre, r.Nombre);
        Assert.Equal(puerto, r.Puerto);
        Assert.Equal(IPAddress.Parse("10.0.0.20"), r.Direccion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hola")]
    [InlineData("PANTALLA-TV 2 Sala 5600")] // otra versión
    [InlineData("PANTALLA-TV 1 Sala")] // sin puerto
    [InlineData("PANTALLA-TV 1 Sala 99999")] // puerto fuera de rango
    [InlineData("OTRA-COSA 1 Sala 5600")]
    public void InterpretarAnuncio_ignora_lo_que_no_es_un_anuncio(string texto)
    {
        Assert.Null(Protocolo.InterpretarAnuncio(texto, IPAddress.Loopback));
    }
}

public class EmisionTests
{
    /// <summary>Levanta un «receptor» falso que lee el HELLO y contesta lo que se le pida.</summary>
    private static async Task<(Receptor Receptor, Task<Mensaje?> Saludo)> ReceptorFalsoAsync(byte tipoRespuesta, string cargaRespuesta)
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((IPEndPoint)escucha.LocalEndpoint).Port;
        var saludo = Task.Run(async () =>
        {
            using var cliente = await escucha.AcceptTcpClientAsync();
            var red = cliente.GetStream();
            var hello = await Protocolo.LeerAsync(red, CancellationToken.None);
            await red.WriteAsync(Protocolo.Armar(tipoRespuesta, 0, Encoding.UTF8.GetBytes(cargaRespuesta)));
            if (tipoRespuesta == Protocolo.HelloOk)
            {
                // Un receptor real sigue conectado: se espera a que el emisor cierre, si no el error
                // «el receptor cerró la conexión» le gana la carrera a cualquier otro.
                try
                {
                    while (await Protocolo.LeerAsync(red, CancellationToken.None) is not null) { }
                }
                catch (IOException)
                {
                    // el emisor cerró de golpe
                }
            }
            escucha.Stop();
            return hello;
        });
        await Task.Yield();
        return (new Receptor("Falso", IPAddress.Loopback, puerto), saludo);
    }

    [Fact]
    public async Task Un_receptor_ocupado_da_un_mensaje_claro_y_recibio_un_HELLO_correcto()
    {
        var (receptor, saludo) = await ReceptorFalsoAsync(Protocolo.HelloErr, "ocupado");
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 2000, null);

        var e = await Assert.ThrowsAsync<EmisionException>(
            () => new Emision().EjecutarAsync(receptor, op, null, CancellationToken.None));

        Assert.Contains("otro emisor", e.Message);
        var hello = await saludo;
        Assert.NotNull(hello);
        Assert.Equal(Protocolo.Hello, hello.Tipo);
        Assert.Equal(17, hello.Carga.Length);
        Assert.Equal(2000, BinaryPrimitives.ReadUInt16BigEndian(hello.Carga.AsSpan(15, 2)));
    }

    [Fact]
    public async Task Sin_receptor_escuchando_falla_con_un_mensaje_claro()
    {
        var libre = new TcpListener(IPAddress.Loopback, 0);
        libre.Start();
        var puerto = ((IPEndPoint)libre.LocalEndpoint).Port;
        libre.Stop(); // el puerto queda cerrado
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, null);

        var e = await Assert.ThrowsAsync<EmisionException>(() =>
            new Emision().EjecutarAsync(new Receptor("Nadie", IPAddress.Loopback, puerto), op, null, CancellationToken.None));

        Assert.Contains("No se pudo conectar", e.Message);
    }

    [Fact]
    public async Task Si_ffmpeg_no_existe_el_error_lo_dice()
    {
        // Solo con tuberías de ffmpeg: desde el 0.7.0 es opcional y, sin elegir, quedaría la nativa. Se comprueba antes
        // de conectar: el receptor ni siquiera llega a recibir el saludo.
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var receptor = new Receptor("Falso", IPAddress.Loopback, ((IPEndPoint)escucha.LocalEndpoint).Port);
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, @"C:\no\existe\ffmpeg.exe", Tuberia: "amf,x264");

        var e = await Assert.ThrowsAsync<EmisionException>(
            () => new Emision().EjecutarAsync(receptor, op, null, CancellationToken.None));

        Assert.Contains("ffmpeg", e.Message);
        Assert.False(escucha.Pending());
        escucha.Stop();
    }
}
