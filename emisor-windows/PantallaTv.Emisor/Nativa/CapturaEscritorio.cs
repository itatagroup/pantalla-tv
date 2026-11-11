using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// Duplicación de escritorio (DXGI) de un monitor. Cada cuadro nuevo se copia a una textura BGRA propia en la
/// GPU y se suelta enseguida. La duplicación no trae el puntero dibujado: si se le da un <see cref="Puntero"/>,
/// se dibuja encima en una segunda textura (así un movimiento del ratón no necesita una captura nueva).
/// <para>
/// Los errores de DXGI salen como excepción (<c>SharpGenException</c>): <c>ACCESS_LOST</c> (cambio de modo,
/// pantalla de UAC, bloqueo) obliga a crear otra duplicación; quien la usa decide (ver <see cref="TuberiaNativa"/>).
/// </para>
/// </summary>
internal sealed class CapturaEscritorio : IDisposable
{
    public enum Resultado { Nuevo, SoloRaton, NadaNuevo }

    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGIOutputDuplication _dup;
    private readonly Puntero? _cursor;
    private readonly ID3D11Texture2D _limpia;
    private readonly ID3D11ShaderResourceView _vistaLimpia;
    private readonly ID3D11Texture2D? _compuesta;
    private readonly ID3D11ShaderResourceView? _vistaCompuesta;
    private readonly ID3D11RenderTargetView? _destinoCompuesta;
    private bool _hayImagen;

    /// <summary>La imagen lista para convertir (con el puntero, si se dibuja y está en este monitor).</summary>
    public ID3D11ShaderResourceView Vista { get; private set; }
    public int Ancho { get; }
    public int Alto { get; }
    /// <summary>Del último cuadro nuevo: cuántas presentaciones juntó Windows desde el anterior (1 = ninguna perdida).</summary>
    public int Acumulados { get; private set; }
    /// <summary>Del último cuadro nuevo: cuándo lo presentó Windows (contador de rendimiento, como <c>Stopwatch.GetTimestamp</c>).</summary>
    public long CuandoQpc { get; private set; }

    public CapturaEscritorio(IDXGIOutput salida, ID3D11Device dispositivo, ID3D11DeviceContext ctx, Puntero? cursor)
    {
        _ctx = ctx;
        _cursor = cursor;
        using var salida5 = salida.QueryInterface<IDXGIOutput5>();
        // Como Sunshine: se pide el formato que la conversión acepta. Ojo: con una expresión de
        // colección ([formato]) esta sobrecarga de Vortice rompe el proceso (N1, problema 2).
        _dup = salida5.DuplicateOutput1(dispositivo, new[] { Format.B8G8R8A8_UNorm });
        try
        {
            var d = _dup.Description;
            Ancho = (int)d.ModeDescription.Width;
            Alto = (int)d.ModeDescription.Height;
            _limpia = Textura(dispositivo);
            _vistaLimpia = dispositivo.CreateShaderResourceView(_limpia);
            Vista = _vistaLimpia;
            if (cursor is not null)
            {
                _compuesta = Textura(dispositivo);
                _vistaCompuesta = dispositivo.CreateShaderResourceView(_compuesta);
                _destinoCompuesta = dispositivo.CreateRenderTargetView(_compuesta);
            }
        }
        catch
        {
            // Una duplicación viva ocupa la salida: los reintentos darían NOT_CURRENTLY_AVAILABLE.
            Dispose();
            throw;
        }
    }

    private ID3D11Texture2D Textura(ID3D11Device dispositivo) => dispositivo.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)Ancho, Height = (uint)Alto, MipLevels = 1, ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
    });

    /// <summary>
    /// Espera hasta <paramref name="ms"/> un cambio. <see cref="Resultado.Nuevo"/>: cambió la imagen;
    /// <see cref="Resultado.SoloRaton"/>: solo el puntero (y se dibuja). En los dos, <see cref="Vista"/> queda al día.
    /// </summary>
    public Resultado Esperar(int ms)
    {
        var r = _dup.AcquireNextFrame((uint)Math.Max(0, ms), out var info, out var recurso);
        if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return Resultado.NadaNuevo;
        r.CheckError();
        Resultado resultado;
        try
        {
            var raton = _cursor?.Actualizar(_dup, info) ?? false;
            if (info.LastPresentTime != 0)
            {
                using var textura = recurso!.QueryInterface<ID3D11Texture2D>();
                _ctx.CopyResource(_limpia, textura);
                _hayImagen = true;
                Acumulados = (int)Math.Max(1u, info.AccumulatedFrames);
                CuandoQpc = info.LastPresentTime;
                resultado = Resultado.Nuevo;
            }
            else if (raton && _hayImagen) resultado = Resultado.SoloRaton;
            else return Resultado.NadaNuevo;
        }
        finally
        {
            recurso?.Dispose();
            _dup.ReleaseFrame();
        }
        Componer();
        return resultado;
    }

    private void Componer()
    {
        if (_cursor is { Visible: true, TieneForma: true } && _destinoCompuesta is not null)
        {
            _ctx.CopyResource(_compuesta!, _limpia);
            _cursor.Dibujar(_destinoCompuesta, Ancho, Alto);
            Vista = _vistaCompuesta!;
        }
        else Vista = _vistaLimpia;
    }

    public void Dispose()
    {
        _destinoCompuesta?.Dispose();
        _vistaCompuesta?.Dispose();
        _compuesta?.Dispose();
        _vistaLimpia?.Dispose();
        _limpia?.Dispose();
        _dup.Dispose();
    }
}
