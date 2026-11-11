using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PantallaTv.Emisor;

/// <summary>Escucha los avisos UDP de los receptores («PANTALLA-TV 1 nombre puerto», cada 2 s).</summary>
internal sealed class Descubridor : IDisposable
{
    /// <summary>Un receptor deja de listarse si no avisa en este tiempo (el aviso es cada 2 s).</summary>
    private static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(7);

    private readonly ConcurrentDictionary<string, (Receptor Receptor, DateTime Visto)> _vistos = new();
    private readonly CancellationTokenSource _fin = new();
    private UdpClient? _udp;

    /// <summary>Texto del último problema al abrir el puerto (p. ej. el firewall o un puerto ocupado).</summary>
    public string? Problema { get; private set; }

    public void Iniciar()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocolo.PuertoUdp));
        }
        catch (SocketException e)
        {
            Problema = $"No se pudo escuchar el puerto UDP {Protocolo.PuertoUdp}: {e.Message}";
            _udp?.Dispose();
            _udp = null;
            return;
        }
        _ = Task.Run(() => EscucharAsync(_udp, _fin.Token));
    }

    public IReadOnlyList<Receptor> Vigentes()
    {
        var limite = DateTime.UtcNow - Vigencia;
        return _vistos.Values.Where(v => v.Visto >= limite).Select(v => v.Receptor)
            .OrderBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task EscucharAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                var anuncio = Protocolo.InterpretarAnuncio(Encoding.UTF8.GetString(r.Buffer), r.RemoteEndPoint.Address);
                if (anuncio is not null)
                    _vistos[$"{anuncio.Direccion}:{anuncio.Puerto}"] = (anuncio, DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // un datagrama roto no debe tumbar la escucha
            }
        }
    }

    public void Dispose()
    {
        _fin.Cancel();
        _udp?.Dispose();
        _fin.Dispose();
    }
}
