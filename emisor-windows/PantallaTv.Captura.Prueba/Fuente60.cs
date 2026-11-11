using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PantallaTv.Captura.Prueba;

/// <summary>
/// Una fuente fiable de 60 cuadros por segundo (N1 dejó pendiente: la ventana de <c>ffplay</c> presentaba entre 50 y 60).
/// Ventana sin bordes que cubre el monitor pedido y presenta con D3D11 (<c>FlipDiscard</c>, sincronía vertical) un
/// cuadro distinto en cada refresco: fondo que cambia de color y franjas que se mueven. Imprime cuántas
/// presentaciones hizo cada segundo, para comprobar que la fuente misma llegó a 60.
/// </summary>
internal static class Fuente60
{
    public static int Ejecutar(SalidaDxgi monitor, int segundos)
    {
        using var forma = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Left = monitor.X, Top = monitor.Y, Width = monitor.Ancho, Height = monitor.Alto,
            TopMost = true, ShowInTaskbar = false, Text = "Fuente 60",
        };
        forma.Show();
        Application.DoEvents();

        // En la gráfica del monitor elegido (con la de por omisión habría una copia entre adaptadores).
        var (adaptador, salida) = PantallaTv.Emisor.Nativa.Gpu.Buscar(monitor.Dispositivo, 0, 0)
            ?? throw new InvalidOperationException($"{monitor.Dispositivo} desapareció");
        salida.Dispose();
        using var _a = adaptador;
        var (dev, ctx) = PantallaTv.Emisor.Nativa.Gpu.Abrir(adaptador);
        using var _d = dev;
        using var _c = ctx;
        using var ctx1 = ctx.QueryInterface<ID3D11DeviceContext1>();
        using var fabrica = adaptador.GetParent<IDXGIFactory2>();
        using var cadena = fabrica.CreateSwapChainForHwnd(dev, forma.Handle, new SwapChainDescription1
        {
            Width = (uint)monitor.Ancho, Height = (uint)monitor.Alto, Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
        });
        using var fondo = cadena.GetBuffer<ID3D11Texture2D>(0);
        using var destino = dev.CreateRenderTargetView(fondo);

        var reloj = Stopwatch.StartNew();
        var cuadro = 0L;
        var enSegundo = 0;
        var siguiente = 1000L;
        Console.WriteLine($"Fuente de 60 fps en {monitor.Dispositivo} ({monitor.Ancho}×{monitor.Alto}) durante {segundos} s");
        Console.WriteLine("seg  presentaciones");
        while (reloj.ElapsedMilliseconds < segundos * 1000L && !forma.IsDisposed)
        {
            var t = cuadro++ / 60f;
            ctx.OMSetRenderTargets(destino);
            ctx1.ClearView(destino, new Color4(0.5f + 0.5f * MathF.Sin(t), 0.5f + 0.5f * MathF.Sin(t * 1.3f + 2), 0.5f + 0.5f * MathF.Sin(t * 0.7f + 4), 1), Array.Empty<Vortice.RawRect>());
            // Ocho franjas verticales que cruzan la pantalla (un poco de detalle para el codificador).
            for (var i = 0; i < 8; i++)
            {
                var x = (int)((cuadro * (6 + i) + i * 240) % (monitor.Ancho + 120)) - 120;
                ctx1.ClearView(destino, new Color4(i % 2, (i / 2) % 2, (i / 4) % 2, 1),
                    [new Vortice.RawRect(x, i * monitor.Alto / 8, x + 120, (i + 1) * monitor.Alto / 8)]);
            }
            cadena.Present(1, PresentFlags.None).CheckError();
            enSegundo++;
            if (reloj.ElapsedMilliseconds >= siguiente)
            {
                Console.WriteLine($"{siguiente / 1000,3}  {enSegundo,4}");
                enSegundo = 0;
                siguiente += 1000;
                Application.DoEvents();
            }
        }
        return 0;
    }
}
