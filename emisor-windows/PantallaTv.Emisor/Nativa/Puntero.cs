using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// El puntero del ratón. La duplicación de escritorio no lo trae dibujado: entrega su forma (cuando cambia) y su
/// posición aparte. Aquí se guarda la forma como textura y se dibuja encima de la imagen capturada, en la GPU.
/// <para>
/// Las formas de Windows tienen partes que <b>invierten</b> lo que hay debajo (el cursor de texto, por ejemplo).
/// Se dibujan en dos pasadas: una con transparencia normal y otra con una mezcla que invierte el color
/// (<c>InvDestColor</c>/<c>InvSrcColor</c>): blanco invierte, negro deja igual.
/// </para>
/// </summary>
internal sealed class Puntero : IDisposable
{
    /// <summary>DXGI_OUTDUPL_POINTER_SHAPE_TYPE.</summary>
    internal const uint Monocromo = 1, Color = 2, ColorConMascara = 4;

    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _muestreo;
    private readonly ID3D11BlendState _mezclaNormal, _mezclaInvertir;
    private readonly ID3D11Buffer _rect;
    private byte[] _buffer = new byte[64 * 64 * 4];

    private ID3D11ShaderResourceView? _normal, _invertir;
    private ID3D11Texture2D? _texNormal, _texInvertir;

    public bool Visible { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Ancho { get; private set; }
    public int Alto { get; private set; }
    /// <summary>¿Hay una forma lista para dibujar?</summary>
    public bool TieneForma => _normal is not null;

    public Puntero(ID3D11Device dev, ID3D11DeviceContext ctx)
    {
        _dev = dev; _ctx = ctx;
        const string fuente = """
            cbuffer R : register(b0) { float4 rect; };
            struct S { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
            S vs(uint id : SV_VertexID)
            {
                // Tira de cuatro vértices: (x0,y0) (x1,y0) (x0,y1) (x1,y1), en coordenadas de pantalla (-1..1).
                float2 t = float2(id & 1, id >> 1);
                S s;
                s.uv = t;
                s.pos = float4(lerp(rect.xy, rect.zw, t), 0, 1);
                return s;
            }
            Texture2D<float4> forma : register(t0);
            SamplerState punto : register(s0);
            float4 ps(S s) : SV_Target { return forma.Sample(punto, s.uv); }
            """;
        _vs = dev.CreateVertexShader(Compiler.Compile(fuente, "vs", "cursor.hlsl", "vs_5_0").ToArray());
        _ps = dev.CreatePixelShader(Compiler.Compile(fuente, "ps", "cursor.hlsl", "ps_5_0").ToArray());
        _muestreo = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _mezclaNormal = dev.CreateBlendState(Mezcla(Blend.SourceAlpha, Blend.InverseSourceAlpha));
        _mezclaInvertir = dev.CreateBlendState(Mezcla(Blend.InverseDestinationColor, Blend.InverseSourceColor));
        _rect = dev.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
    }

    private static BlendDescription Mezcla(Blend origen, Blend destino)
    {
        var d = new BlendDescription();
        d.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = origen, DestinationBlend = destino, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.Zero, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return d;
    }

    /// <summary>
    /// Toma la posición y, si cambió, la forma, de un cuadro de la duplicación. Devuelve true si cambió algo que
    /// se ve (moverse fuera de este monitor y seguir fuera no cuenta).
    /// </summary>
    public bool Actualizar(IDXGIOutputDuplication dup, in OutduplFrameInfo info)
    {
        var cambio = false;
        if (info.LastMouseUpdateTime != 0)
        {
            var visible = info.PointerPosition.Visible;
            var (x, y) = (info.PointerPosition.Position.X, info.PointerPosition.Position.Y);
            cambio = visible != Visible || (visible && (x != X || y != Y));
            Visible = visible;
            X = x;
            Y = y;
        }
        if (info.PointerShapeBufferSize > 0)
        {
            if (_buffer.Length < info.PointerShapeBufferSize) _buffer = new byte[info.PointerShapeBufferSize];
            var h = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                dup.GetFramePointerShape((uint)_buffer.Length, h.AddrOfPinnedObject(), out var usados, out var forma).CheckError();
                CrearTexturas(Formas(forma.Type, (int)forma.Width, (int)forma.Height, (int)forma.Pitch, _buffer.AsSpan(0, (int)usados)));
                cambio |= Visible;
            }
            finally { h.Free(); }
        }
        return cambio;
    }

    /// <summary>Una forma convertida a BGRA: la parte normal (con transparencia) y, si la hay, la que invierte.</summary>
    internal readonly record struct FormaBgra(int Ancho, int Alto, byte[] Normal, byte[]? Invertir);

    /// <summary>Pasa la forma de DXGI (1 bit con máscaras AND/XOR, color, o color con máscara) a BGRA.</summary>
    internal static FormaBgra Formas(uint tipo, int ancho, int alto, int pitch, ReadOnlySpan<byte> datos)
    {
        if (tipo == Monocromo)
        {
            // Mitad superior: máscara AND; inferior: máscara XOR; 1 bit por píxel, el más significativo primero.
            var h = alto / 2;
            var normal = new byte[ancho * h * 4];
            byte[]? invertir = null;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < ancho; x++)
                {
                    var bit = 0x80 >> (x & 7);
                    var and = (datos[y * pitch + x / 8] & bit) != 0;
                    var xor = (datos[(y + h) * pitch + x / 8] & bit) != 0;
                    var i = (y * ancho + x) * 4;
                    if (!and)
                    {
                        var c = xor ? (byte)255 : (byte)0; // AND 0: negro o blanco opaco
                        normal[i] = normal[i + 1] = normal[i + 2] = c;
                        normal[i + 3] = 255;
                    }
                    else if (xor)
                    {
                        invertir ??= new byte[normal.Length];
                        invertir[i] = invertir[i + 1] = invertir[i + 2] = invertir[i + 3] = 255; // invierte
                    }
                    // AND 1 y XOR 0: transparente
                }
            return new FormaBgra(ancho, h, normal, invertir);
        }

        var norm = new byte[ancho * alto * 4];
        byte[]? inv = null;
        for (var y = 0; y < alto; y++)
        {
            var fila = datos.Slice(y * pitch, ancho * 4);
            for (var x = 0; x < ancho; x++)
            {
                var o = x * 4;
                var i = (y * ancho + x) * 4;
                if (tipo == Color)
                {
                    fila.Slice(o, 4).CopyTo(norm.AsSpan(i, 4));
                    continue;
                }
                // Con máscara: alfa 0 = reemplaza con el color; alfa 255 = XOR con lo de debajo. El XOR con un color
                // cualquiera se aproxima invirtiendo (es lo que se ve en la práctica); con color 0 no cambia nada.
                if (fila[o + 3] == 0)
                {
                    norm[i] = fila[o]; norm[i + 1] = fila[o + 1]; norm[i + 2] = fila[o + 2]; norm[i + 3] = 255;
                }
                else if ((fila[o] | fila[o + 1] | fila[o + 2]) != 0)
                {
                    inv ??= new byte[norm.Length];
                    inv[i] = inv[i + 1] = inv[i + 2] = inv[i + 3] = 255;
                }
            }
        }
        return new FormaBgra(ancho, alto, norm, inv);
    }

    private void CrearTexturas(FormaBgra f)
    {
        Liberar();
        if (f.Ancho <= 0 || f.Alto <= 0) return;
        Ancho = f.Ancho;
        Alto = f.Alto;
        (_texNormal, _normal) = Textura(f.Normal);
        if (f.Invertir is { } inv) (_texInvertir, _invertir) = Textura(inv);

        (ID3D11Texture2D, ID3D11ShaderResourceView) Textura(byte[] bgra)
        {
            var h = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var t = _dev.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)f.Ancho, Height = (uint)f.Alto, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                }, new SubresourceData(h.AddrOfPinnedObject(), (uint)f.Ancho * 4));
                return (t, _dev.CreateShaderResourceView(t));
            }
            finally { h.Free(); }
        }
    }

    /// <summary>Dibuja el puntero sobre <paramref name="destino"/> (una imagen de <paramref name="ancho"/>×<paramref name="alto"/>).</summary>
    public void Dibujar(ID3D11RenderTargetView destino, int ancho, int alto)
    {
        if (!Visible || _normal is null) return;
        var rect = new Vector4(
            2f * X / ancho - 1, 1 - 2f * Y / alto,
            2f * (X + Ancho) / ancho - 1, 1 - 2f * (Y + Alto) / alto);
        _ctx.UpdateSubresource(in rect, _rect);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _ctx.IASetInputLayout(null);
        _ctx.VSSetShader(_vs);
        _ctx.VSSetConstantBuffer(0, _rect);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetSampler(0, _muestreo);
        _ctx.OMSetRenderTargets(destino);
        _ctx.RSSetViewport(new Viewport(0, 0, ancho, alto));

        _ctx.PSSetShaderResource(0, _normal);
        _ctx.OMSetBlendState(_mezclaNormal);
        _ctx.Draw(4, 0);
        if (_invertir is not null)
        {
            _ctx.PSSetShaderResource(0, _invertir);
            _ctx.OMSetBlendState(_mezclaInvertir);
            _ctx.Draw(4, 0);
        }

        _ctx.OMSetBlendState(null);
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _ctx.PSSetShaderResource(0, null!);
    }

    private void Liberar()
    {
        _normal?.Dispose(); _texNormal?.Dispose(); _invertir?.Dispose(); _texInvertir?.Dispose();
        _normal = _invertir = null;
        _texNormal = _texInvertir = null;
    }

    public void Dispose()
    {
        Liberar();
        _rect.Dispose(); _mezclaInvertir.Dispose(); _mezclaNormal.Dispose(); _muestreo.Dispose(); _ps.Dispose(); _vs.Dispose();
    }
}
