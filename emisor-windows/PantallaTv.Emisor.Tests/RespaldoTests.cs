using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using PantallaTv.Emisor.Nativa;

namespace PantallaTv.Emisor.Tests;

internal static class SinRegistro
{
    /// <summary>Las pruebas no escriben en el registro real del usuario.</summary>
    [ModuleInitializer]
    internal static void Apagar() => Registro.Ruta = null;
}

/// <summary>Una fuente de vídeo de mentira: corre lo que se le dé y cuenta lo que entrega.</summary>
internal sealed class FuenteFalsa(string nombre, Func<FuenteFalsa, Action<byte[]>, CancellationToken, Task> correr, bool listaAlEmpezar = true) : IFuenteVideo
{
    /// <summary>SPS + PPS + IDR: lo mínimo para que la emisión lo mande como clave.</summary>
    public static readonly byte[] Clave = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 9, 9];

    private readonly TaskCompletionSource _lista = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _unidades;

    public string Nombre => nombre;
    public string? Detalle => null;
    public long Unidades => Interlocked.Read(ref _unidades);
    public Task Lista => _lista.Task;
    public int ClavesPedidas;
    public void MarcarLista() => _lista.TrySetResult();
    public void PedirClave() => Interlocked.Increment(ref ClavesPedidas);
    public EstadisticasNativa? Tomar() => null;
    public void Dispose() { }

    public Task EjecutarAsync(Action<byte[]> enviar, CancellationToken ct)
    {
        if (listaAlEmpezar) MarcarLista();
        return correr(this, au => { Interlocked.Increment(ref _unidades); enviar(au); }, ct);
    }

    /// <summary>Manda una clave cada 20 ms hasta que se cancela (o hasta <paramref name="cuantas"/>).</summary>
    public static async Task Transmitir(Action<byte[]> enviar, CancellationToken ct, int cuantas = int.MaxValue)
    {
        for (var i = 0; i < cuantas; i++)
        {
            enviar(Clave);
            await Task.Delay(20, ct);
        }
    }
}

/// <summary>
/// El respaldo automático entre tuberías (N3): si la primera (la nativa) no entrega vídeo o deja de servir, se pasa a la
/// siguiente sin cortar la conexión con la TV.
/// </summary>
public class RespaldoTests
{
    /// <summary>Un receptor que acepta una conexión, contesta HELLO_OK y cuenta los VIDEO que le llegan.</summary>
    private sealed class ReceptorFalso : IDisposable
    {
        private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
        public int Conexiones;
        public int Videos;
        /// <summary>Contestar el primer VIDEO con un PEDIR_CLAVE, como la TV cuando salta.</summary>
        public bool PedirClave { get; init; }
        public Receptor Receptor { get; }
        public Task Servidor { get; }

        public ReceptorFalso()
        {
            _escucha.Start();
            Receptor = new Receptor("Falso", IPAddress.Loopback, ((IPEndPoint)_escucha.LocalEndpoint).Port);
            Servidor = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient cliente;
                    try { cliente = await _escucha.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref Conexiones);
                    using (cliente)
                    {
                        var red = cliente.GetStream();
                        await Protocolo.LeerAsync(red, CancellationToken.None);
                        await red.WriteAsync(Protocolo.Armar(Protocolo.HelloOk, 0, []));
                        try
                        {
                            while (await Protocolo.LeerAsync(red, CancellationToken.None) is { } m)
                                if (m.Tipo == Protocolo.Video && Interlocked.Increment(ref Videos) == 1 && PedirClave)
                                    await red.WriteAsync(Protocolo.Armar(Protocolo.PedirClave, 0, []));
                        }
                        catch (IOException) { }
                    }
                }
            });
        }

        public void Dispose() => _escucha.Stop();
    }

    private static OpcionesEmision Op(string tuberias) => new(60, 8000, 1920, 1080, 0, null, tuberias, ConAudio: false);

    private static async Task<List<EstadoEmision>> TransmitirHasta(Emision emision, ReceptorFalso tv, OpcionesEmision op, Func<List<EstadoEmision>, bool> listo)
    {
        var estados = new List<EstadoEmision>();
        emision.CambioDeEstado += e => { lock (estados) estados.Add(e); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tarea = emision.EjecutarAsync(tv.Receptor, op, null, cts.Token);
        while (!cts.IsCancellationRequested)
        {
            lock (estados) if (listo(estados)) break;
            if (tarea.IsCompleted) await tarea; // propaga el error
            await Task.Delay(20);
        }
        cts.Cancel();
        await tarea;
        return estados;
    }

    private static bool Transmite(List<EstadoEmision> estados, string tuberia) =>
        estados.Any(e => e.Fase == Fase.Transmitiendo && e.Detalle.EndsWith($"({tuberia})"));

    [Fact]
    public async Task Si_la_primera_no_entrega_video_a_tiempo_se_pasa_a_la_siguiente_sin_cortar()
    {
        using var tv = new ReceptorFalso();
        var emision = new Emision
        {
            PlazoPrimerCuadro = TimeSpan.FromMilliseconds(300),
            Fuentes = n => n == "muda"
                ? new FuenteFalsa(n, (_, _, ct) => Task.Delay(Timeout.Infinite, ct))
                : new FuenteFalsa(n, (_, enviar, ct) => FuenteFalsa.Transmitir(enviar, ct)),
        };

        var estados = await TransmitirHasta(emision, tv, Op("muda,buena"), e => Transmite(e, "buena") && tv.Videos > 3);

        Assert.DoesNotContain(estados, e => e.Fase == Fase.Transmitiendo && e.Detalle.EndsWith("(muda)"));
        Assert.Equal(1, tv.Conexiones);
        Assert.True(tv.Videos > 3);
    }

    [Fact]
    public async Task Si_la_primera_deja_de_servir_transmitiendo_sigue_la_siguiente_en_la_misma_conexion()
    {
        using var tv = new ReceptorFalso();
        var emision = new Emision
        {
            Fuentes = n => n == "fragil"
                ? new FuenteFalsa(n, async (_, enviar, ct) =>
                {
                    await FuenteFalsa.Transmitir(enviar, ct, cuantas: 5);
                    throw new NativaNoDisponibleException("falló 4 veces en 60 s: dispositivo perdido");
                })
                : new FuenteFalsa(n, (_, enviar, ct) => FuenteFalsa.Transmitir(enviar, ct)),
        };

        var estados = await TransmitirHasta(emision, tv, Op("fragil,buena"), e => Transmite(e, "buena"));

        Assert.True(Transmite(estados, "fragil"));
        Assert.Contains(estados, e => e.Detalle.Contains("«fragil» falló") && e.Detalle.Contains("se sigue con otra forma"));
        Assert.Equal(1, tv.Conexiones);
        Assert.DoesNotContain(estados, e => e.Fase == Fase.Error);
    }

    [Fact]
    public async Task Si_la_ultima_deja_de_servir_corta_para_reconectar()
    {
        using var tv = new ReceptorFalso();
        var emision = new Emision
        {
            Fuentes = n => new FuenteFalsa(n, async (_, enviar, ct) =>
            {
                await FuenteFalsa.Transmitir(enviar, ct, cuantas: 3);
                throw new NativaNoDisponibleException("se perdió la gráfica");
            }),
        };

        var error = await Assert.ThrowsAsync<EmisionException>(() =>
            TransmitirHasta(emision, tv, Op("unica"), _ => false));

        Assert.True(error.Reintentable); // EmisionPersistente vuelve a intentarlo
        Assert.Contains("se perdió la gráfica", error.Message);
    }

    [Fact]
    public async Task El_plazo_del_primer_cuadro_corre_desde_que_la_fuente_esta_lista()
    {
        // La nativa espera al monitor antes de estar lista (hasta 15 s): esa espera no la condena.
        using var tv = new ReceptorFalso();
        var emision = new Emision
        {
            PlazoPrimerCuadro = TimeSpan.FromMilliseconds(300),
            Fuentes = n => new FuenteFalsa(n, async (f, enviar, ct) =>
            {
                await Task.Delay(600, ct); // «buscando el monitor»: más que el plazo
                f.MarcarLista();
                await Task.Delay(100, ct);
                await FuenteFalsa.Transmitir(enviar, ct);
            }, listaAlEmpezar: false),
        };

        var estados = await TransmitirHasta(emision, tv, Op("lenta,otra"), e => Transmite(e, "lenta") || Transmite(e, "otra"));

        Assert.True(Transmite(estados, "lenta"));
    }

    [Fact]
    public async Task Un_pedido_de_clave_de_la_TV_llega_a_la_fuente()
    {
        using var tv = new ReceptorFalso { PedirClave = true };
        FuenteFalsa? fuente = null;
        var emision = new Emision { Fuentes = n => fuente = new FuenteFalsa(n, (_, enviar, ct) => FuenteFalsa.Transmitir(enviar, ct)) };

        await TransmitirHasta(emision, tv, Op("una"), _ => fuente is { ClavesPedidas: > 0 });

        Assert.True(fuente!.ClavesPedidas > 0);
    }

    [Fact]
    public async Task Elegir_amf_gpu_para_otro_monitor_se_rechaza_antes_de_conectar()
    {
        // Regresión: con el monitor virtual, vsrc_amf muestra el escritorio principal sin avisar.
        using var tv = new ReceptorFalso();
        var op = Op(Tuberias.AmfGpu) with { Monitor = 1 };

        var e = await Assert.ThrowsAsync<EmisionException>(() => new Emision().EjecutarAsync(tv.Receptor, op, null, CancellationToken.None));

        Assert.Contains("monitor principal", e.Message);
        Assert.Equal(0, tv.Conexiones);
    }
}
