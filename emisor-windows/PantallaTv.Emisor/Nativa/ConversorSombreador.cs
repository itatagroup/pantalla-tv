using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// Pasa la imagen BGRA capturada a NV12, en la GPU, sobre la textura del codificador, con dos pasadas de
/// sombreador de píxeles: una escribe Y en una vista R8 de la textura NV12 y otra escribe UV a
/// media resolución en una vista R8G8. El muestreo lineal en el centro de cada bloque de 2×2 promedia los
/// cuatro píxeles para el croma.
/// <para>
/// El destino tiene siempre el tamaño con que se abrió el codificador (el que se anunció a la TV en el
/// <c>HELLO</c>). Si la captura cambia de tamaño (cambio de resolución en Windows), la imagen se escala y se
/// encaja sin deformarla, con franjas negras: así la transmisión sigue sin reabrir el codificador.
/// </para>
/// </summary>
internal sealed class ConversorSombreador : IDisposable
{
    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly int _ancho, _alto;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psY, _psUv;
    private readonly ID3D11SamplerState _muestreo;
    private readonly Dictionary<(nint, int), (ID3D11RenderTargetView Y, ID3D11RenderTargetView Uv)> _vistas = [];

    /// <param name="ancho">Ancho del destino (el del codificador).</param>
    /// <param name="alto">Alto del destino.</param>
    public ConversorSombreador(ID3D11Device dev, ID3D11DeviceContext ctx, int ancho, int alto, MatrizColor matriz)
    {
        _dev = dev; _ctx = ctx; _ancho = ancho; _alto = alto;
        var fuente = Fuente(matriz);
        _vs = dev.CreateVertexShader(Compilar(fuente, "vs", "vs_5_0"));
        _psY = dev.CreatePixelShader(Compilar(fuente, "psY", "ps_5_0"));
        _psUv = dev.CreatePixelShader(Compilar(fuente, "psUV", "ps_5_0"));
        _muestreo = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
    }

    /// <summary>El código HLSL, con la matriz de color escrita como constantes.</summary>
    internal static string Fuente(MatrizColor m) => $$"""
        struct S { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        S vs(uint id : SV_VertexID)
        {
            // Un triángulo que cubre todo el viewport; uv va de 0 a 1 sobre la imagen.
            S s;
            float2 t = float2((id << 1) & 2, id & 2);
            s.uv = t;
            s.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
            return s;
        }
        Texture2D<float4> img : register(t0);
        SamplerState lin : register(s0);
        static const float4 MY = {{MatrizColor.Hlsl(m.FilaY)}};
        static const float4 MU = {{MatrizColor.Hlsl(m.FilaU)}};
        static const float4 MV = {{MatrizColor.Hlsl(m.FilaV)}};
        float psY(S s) : SV_Target
        {
            // Con el mismo tamaño, el centro de cada píxel cae en el centro de un texel: el muestreo
            // lineal da el valor exacto. Con otro tamaño, escala.
            float3 c = img.Sample(lin, s.uv).rgb;
            return dot(MY.xyz, c) + MY.w;
        }
        float2 psUV(S s) : SV_Target
        {
            float3 c = img.Sample(lin, s.uv).rgb;
            return float2(dot(MU.xyz, c) + MU.w, dot(MV.xyz, c) + MV.w);
        }
        """;

    private static byte[] Compilar(string fuente, string entrada, string perfil) =>
        Compiler.Compile(fuente, entrada, "conversor.hlsl", perfil, ShaderFlags.OptimizationLevel3).ToArray();

    /// <summary>
    /// Dónde va una imagen de <paramref name="anchoOrigen"/>×<paramref name="altoOrigen"/> dentro del destino
    /// sin deformarla: centrada, en coordenadas pares (el croma va a media resolución).
    /// </summary>
    internal static (int X, int Y, int Ancho, int Alto) Encaje(int anchoOrigen, int altoOrigen, int ancho, int alto)
    {
        if (anchoOrigen <= 0 || altoOrigen <= 0 || (anchoOrigen == ancho && altoOrigen == alto)) return (0, 0, ancho, alto);
        int w, h;
        if ((long)anchoOrigen * alto >= (long)altoOrigen * ancho)
        {
            w = ancho;
            h = (int)Math.Round((double)ancho * altoOrigen / anchoOrigen);
        }
        else
        {
            h = alto;
            w = (int)Math.Round((double)alto * anchoOrigen / altoOrigen);
        }
        w = Math.Clamp(w & ~1, 2, ancho);
        h = Math.Clamp(h & ~1, 2, alto);
        return (((ancho - w) / 2) & ~1, ((alto - h) / 2) & ~1, w, h);
    }

    public void Convertir(ID3D11ShaderResourceView origen, int anchoOrigen, int altoOrigen, DestinoNv12 d)
    {
        var (y, uv) = Vistas(d);
        var (ex, ey, ew, eh) = Encaje(anchoOrigen, altoOrigen, _ancho, _alto);
        if (ew != _ancho || eh != _alto)
        {
            // Franjas negras: Y = 16, U = V = 128 (rango limitado).
            _ctx.ClearRenderTargetView(y, new Color4(16f / 255, 0, 0, 0));
            _ctx.ClearRenderTargetView(uv, new Color4(128f / 255, 128f / 255, 0, 0));
        }
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.IASetInputLayout(null);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShaderResource(0, origen);
        _ctx.PSSetSampler(0, _muestreo);

        _ctx.PSSetShader(_psY);
        _ctx.OMSetRenderTargets(y);
        _ctx.RSSetViewport(new Viewport(ex, ey, ew, eh));
        _ctx.Draw(3, 0);

        _ctx.PSSetShader(_psUv);
        _ctx.OMSetRenderTargets(uv);
        _ctx.RSSetViewport(new Viewport(ex / 2, ey / 2, ew / 2, eh / 2));
        _ctx.Draw(3, 0);

        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _ctx.PSSetShaderResource(0, null!);
    }

    private (ID3D11RenderTargetView, ID3D11RenderTargetView) Vistas(DestinoNv12 d)
    {
        var clave = (d.Textura.NativePointer, d.Rebanada);
        if (_vistas.TryGetValue(clave, out var v)) return v;
        var arreglo = d.Textura.Description.ArraySize > 1;
        RenderTargetViewDescription Desc(Format f) => arreglo
            ? new RenderTargetViewDescription(RenderTargetViewDimension.Texture2DArray, f, 0, (uint)d.Rebanada, 1)
            : new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, f);
        v = (_dev.CreateRenderTargetView(d.Textura, Desc(Format.R8_UNorm)),
             _dev.CreateRenderTargetView(d.Textura, Desc(Format.R8G8_UNorm)));
        _vistas[clave] = v;
        return v;
    }

    public void Dispose()
    {
        foreach (var (y, uv) in _vistas.Values) { y.Dispose(); uv.Dispose(); }
        _muestreo.Dispose(); _psUv.Dispose(); _psY.Dispose(); _vs.Dispose();
    }
}
