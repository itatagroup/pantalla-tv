using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PantallaTv.Emisor.Nativa;

/// <summary>Una textura NV12 (o una rebanada de un arreglo de texturas) donde dejar el cuadro convertido.</summary>
internal readonly record struct DestinoNv12(ID3D11Texture2D Textura, int Rebanada);

/// <summary>Una unidad de acceso H.264 (Annex B) que salió del codificador y el tiempo (100 ns) del cuadro que la originó.</summary>
internal delegate void SalidaCodificador(byte[] unidad, long tiempo100ns);

/// <summary>
/// Un codificador H.264 por hardware que recibe texturas NV12 de la GPU. Lo que sale va al
/// <see cref="SalidaCodificador"/> que se le dio al crearlo, que puede llamarse desde otro hilo.
/// </summary>
internal interface ICodificador : IDisposable
{
    string Descripcion { get; }

    /// <summary>La textura donde convertir el próximo cuadro (el codificador puede seguir leyendo las anteriores).</summary>
    DestinoNv12 SiguienteDestino();

    /// <summary>Entrega el cuadro ya convertido en <paramref name="d"/>; <paramref name="clave"/> fuerza un fotograma clave.</summary>
    void Codificar(DestinoNv12 d, long tiempo100ns, bool clave);

    /// <summary>Saca lo que quede dentro del codificador.</summary>
    void Vaciar();
}

/// <summary>Texturas NV12 propias, en anillo (el codificador puede tener varios cuadros en vuelo).</summary>
internal sealed class AnilloNv12 : IDisposable
{
    private readonly ID3D11Texture2D[] _texturas;
    private int _siguiente;

    public AnilloNv12(ID3D11Device dev, int ancho, int alto, int cuantas)
    {
        _texturas = new ID3D11Texture2D[cuantas];
        for (var i = 0; i < cuantas; i++)
            _texturas[i] = dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)ancho, Height = (uint)alto, MipLevels = 1, ArraySize = 1,
                Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
    }

    public DestinoNv12 Siguiente()
    {
        var t = _texturas[_siguiente];
        _siguiente = (_siguiente + 1) % _texturas.Length;
        return new DestinoNv12(t, 0);
    }

    public void Dispose()
    {
        foreach (var t in _texturas) t.Dispose();
    }
}
