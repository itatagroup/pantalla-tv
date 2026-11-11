using PantallaTv.Emisor;
using Vortice.DXGI;

namespace PantallaTv.Captura.Prueba;

/// <summary>Una salida de vídeo (monitor) vista por DXGI.</summary>
internal sealed record SalidaDxgi(int Indice, int Adaptador, int Salida, string Dispositivo, string Monitor,
    int X, int Y, int Ancho, int Alto, string NombreAdaptador);

/// <summary>Enumera monitores para elegir uno por índice o «virtual». Abrirlos lo hace <c>Emisor.Nativa.Gpu</c>.</summary>
internal static class ListaMonitores
{
    public static List<SalidaDxgi> Salidas()
    {
        var r = new List<SalidaDxgi>();
        using var fabrica = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; fabrica.EnumAdapters1(a, out var adaptador).Success; a++)
        {
            using (adaptador)
            {
                for (uint s = 0; adaptador.EnumOutputs(s, out var salida).Success; s++)
                {
                    using (salida)
                    {
                        var d = salida.Description;
                        if (!d.AttachedToDesktop) continue;
                        var c = d.DesktopCoordinates;
                        r.Add(new SalidaDxgi(r.Count, (int)a, (int)s, d.DeviceName, Monitores.DescripcionDe(d.DeviceName),
                            c.Left, c.Top, c.Right - c.Left, c.Bottom - c.Top, adaptador.Description1.Description));
                    }
                }
            }
        }
        return r;
    }

    /// <summary>Elige por índice, por nombre de Windows o «virtual» (el que detecta el emisor, <c>MonitorVirtual.Detectar</c>).</summary>
    public static SalidaDxgi Elegir(IReadOnlyList<SalidaDxgi> salidas, string pedido)
    {
        SalidaDxgi? s = pedido switch
        {
            "virtual" => MonitorVirtual.Detectar() is { } v
                ? salidas.FirstOrDefault(x => string.Equals(x.Dispositivo, v.Dispositivo, StringComparison.OrdinalIgnoreCase)) : null,
            _ when int.TryParse(pedido, out var i) => salidas.FirstOrDefault(x => x.Indice == i),
            _ => salidas.FirstOrDefault(x => string.Equals(x.Dispositivo, pedido, StringComparison.OrdinalIgnoreCase)),
        };
        return s ?? throw new ArgumentException($"No hay ningún monitor «{pedido}» conectado (ver --listar).");
    }
}
