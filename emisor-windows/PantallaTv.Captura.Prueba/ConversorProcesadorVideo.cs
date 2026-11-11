using PantallaTv.Emisor.Nativa;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PantallaTv.Captura.Prueba;

/// <summary>
/// Conversión con el procesador de vídeo de D3D11 (<c>VideoProcessorBlt</c>), solo para comparar con los
/// sombreadores: Sunshine no lo usa. Entrada BGRA de rango completo, salida NV12 BT.601 limitada.
/// </summary>
internal sealed class ConversorProcesadorVideo : IDisposable
{
    private readonly ID3D11VideoDevice _vdev;
    private readonly ID3D11VideoContext _vctx;
    private readonly ID3D11VideoProcessorEnumerator _enum;
    private readonly ID3D11VideoProcessor _vp;
    private readonly ID3D11VideoProcessorInputView _entrada;
    private readonly Dictionary<(nint, int), ID3D11VideoProcessorOutputView> _salidas = [];

    public ConversorProcesadorVideo(ID3D11Device dev, ID3D11DeviceContext ctx, ID3D11Texture2D imagen, int ancho, int alto, int fps)
    {
        _vdev = dev.QueryInterface<ID3D11VideoDevice>();
        _vctx = ctx.QueryInterface<ID3D11VideoContext>();
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)ancho, InputHeight = (uint)alto, OutputWidth = (uint)ancho, OutputHeight = (uint)alto,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enum = _vdev.CreateVideoProcessorEnumerator(desc);
        _vp = _vdev.CreateVideoProcessor(_enum, 0);
        _entrada = _vdev.CreateVideoProcessorInputView(imagen, _enum, new VideoProcessorInputViewDescription
        {
            FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        // Entrada RGB 0–255; salida YCbCr BT.601 16–235 (bits de D3D11_VIDEO_PROCESSOR_COLOR_SPACE).
        _vctx.VideoProcessorSetStreamColorSpace(_vp, 0, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 0, YCbCr_xvYCC = 0, Nominal_Range = 2 });
        _vctx.VideoProcessorSetOutputColorSpace(_vp, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 0, YCbCr_xvYCC = 0, Nominal_Range = 1 });
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
    }

    public void Convertir(DestinoNv12 d)
    {
        var clave = (d.Textura.NativePointer, d.Rebanada);
        if (!_salidas.TryGetValue(clave, out var vista))
        {
            var arreglo = d.Textura.Description.ArraySize > 1;
            vista = _vdev.CreateVideoProcessorOutputView(d.Textura, _enum, arreglo
                ? new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2DArray, Texture2DArray = new Texture2DArrayVideoProcessorOutputView { MipSlice = 0, FirstArraySlice = (uint)d.Rebanada, ArraySize = 1 } }
                : new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 } });
            _salidas[clave] = vista;
        }
        var flujo = new VideoProcessorStream { Enable = true, InputSurface = _entrada };
        _vctx.VideoProcessorBlt(_vp, vista, 0, 1, [flujo]).CheckError();
    }

    public void Dispose()
    {
        foreach (var v in _salidas.Values) v.Dispose();
        _entrada.Dispose(); _vp.Dispose(); _enum.Dispose(); _vctx.Dispose(); _vdev.Dispose();
    }
}
