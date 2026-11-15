using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// Pasa un cuadro NV12 de una gráfica a otra por la memoria del sistema: copia la textura a una de lectura (staging) del
/// dispositivo de origen, la lee, la escribe en una de escritura del dispositivo de destino y la copia a la textura
/// destino. Es el último recurso para un portátil con dos gráficas cuando el codificador está en la que no dibuja el
/// monitor: una textura compartida entre adaptadores no es fiable con NV12, y esto funciona con cualquier par.
/// Cuesta ~3 MB por cuadro a 1080p (12 MB a 4K) en cada sentido.
/// </summary>
internal sealed class PuenteGpu : IDisposable
{
    private readonly ID3D11DeviceContext _ctxOrigen, _ctxDestino;
    private readonly ID3D11Texture2D _lectura, _escritura;
    private readonly int _ancho, _alto;
    private byte[] _paso = [];

    public PuenteGpu(ID3D11Device devOrigen, ID3D11DeviceContext ctxOrigen, ID3D11Device devDestino, ID3D11DeviceContext ctxDestino, int ancho, int alto)
    {
        _ctxOrigen = ctxOrigen;
        _ctxDestino = ctxDestino;
        _ancho = ancho;
        _alto = alto;
        _lectura = Staging(devOrigen, ancho, alto, CpuAccessFlags.Read);
        try { _escritura = Staging(devDestino, ancho, alto, CpuAccessFlags.Write); }
        catch { _lectura.Dispose(); throw; }
    }

    private static ID3D11Texture2D Staging(ID3D11Device dev, int ancho, int alto, CpuAccessFlags acceso) =>
        dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)ancho, Height = (uint)alto, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = acceso,
        });

    /// <summary>Copia <paramref name="origen"/> (en el dispositivo de origen) sobre <paramref name="destino"/> (en el de destino).</summary>
    public void Copiar(ID3D11Texture2D origen, ID3D11Texture2D destino)
    {
        _ctxOrigen.CopyResource(_lectura, origen);
        var leido = _ctxOrigen.Map(_lectura, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var escrito = _ctxDestino.Map(_escritura, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            try { CopiarNv12(leido.DataPointer, (int)leido.RowPitch, escrito.DataPointer, (int)escrito.RowPitch, _ancho, _alto, ref _paso); }
            finally { _ctxDestino.Unmap(_escritura, 0); }
        }
        finally { _ctxOrigen.Unmap(_lectura, 0); }
        _ctxDestino.CopyResource(destino, _escritura);
    }

    /// <summary>
    /// Copia una imagen NV12 entre dos zonas de memoria con pasos distintos: <paramref name="alto"/> filas de luma y, a
    /// continuación (a <c>paso × alto</c> bytes del comienzo), <c>alto / 2</c> de croma. Cada fila copia <paramref name="ancho"/> bytes.
    /// </summary>
    internal static void CopiarNv12(IntPtr origen, int pasoOrigen, IntPtr destino, int pasoDestino, int ancho, int alto, ref byte[] paso)
    {
        if (paso.Length < ancho) paso = new byte[ancho];
        CopiarPlano(origen, pasoOrigen, destino, pasoDestino, ancho, alto, paso);
        CopiarPlano(origen + pasoOrigen * alto, pasoOrigen, destino + pasoDestino * alto, pasoDestino, ancho, alto / 2, paso);
    }

    private static void CopiarPlano(IntPtr origen, int pasoOrigen, IntPtr destino, int pasoDestino, int ancho, int filas, byte[] paso)
    {
        for (var f = 0; f < filas; f++)
        {
            Marshal.Copy(origen + f * pasoOrigen, paso, 0, ancho);
            Marshal.Copy(paso, 0, destino + f * pasoDestino, ancho);
        }
    }

    public void Dispose()
    {
        _lectura.Dispose();
        _escritura.Dispose();
    }
}

/// <summary>
/// Un codificador de otra gráfica que recibe los cuadros de esta: la conversión a NV12 se hace donde se captura (las texturas
/// de <see cref="SiguienteDestino"/> son de esa gráfica) y, al codificar, cada cuadro se copia a la gráfica del codificador
/// por <see cref="PuenteGpu"/>. Con la pantalla quieta, la recodificación del último cuadro vuelve a copiarlo: es lo que hay.
/// </summary>
internal sealed class CodificadorConCopia : ICodificador
{
    private readonly ICodificador _interno;
    private readonly PuenteGpu _puente;
    private readonly AnilloNv12 _anillo;
    private readonly ID3D11Device _devCodificador;
    private readonly ID3D11DeviceContext _ctxCodificador;

    public string Descripcion { get; }
    public bool ClaveAPedido => _interno.ClaveAPedido;

    /// <summary>Toma posesión de <paramref name="devCodificador"/> y <paramref name="ctxCodificador"/> (los libera al cerrarse); <paramref name="interno"/> lo libera quien lo creó si este constructor lanza.</summary>
    public CodificadorConCopia(ID3D11Device devCaptura, ID3D11DeviceContext ctxCaptura, ID3D11Device devCodificador,
        ID3D11DeviceContext ctxCodificador, ICodificador interno, int ancho, int alto, string gpuCaptura, string gpuCodificador)
    {
        _interno = interno;
        _devCodificador = devCodificador;
        _ctxCodificador = ctxCodificador;
        _puente = new PuenteGpu(devCaptura, ctxCaptura, devCodificador, ctxCodificador, ancho, alto);
        try { _anillo = new AnilloNv12(devCaptura, ancho, alto, 4); }
        catch { _puente.Dispose(); throw; }
        Descripcion = $"cuadro copiado de {gpuCaptura} a {gpuCodificador} · {interno.Descripcion}";
    }

    public DestinoNv12 SiguienteDestino() => _anillo.Siguiente();

    public void Codificar(DestinoNv12 d, long tiempo100ns, bool clave)
    {
        var destino = _interno.SiguienteDestino();
        _puente.Copiar(d.Textura, destino.Textura);
        _interno.Codificar(destino, tiempo100ns, clave);
    }

    public void Vaciar() => _interno.Vaciar();

    public void Dispose()
    {
        _interno.Dispose();
        _anillo.Dispose();
        _puente.Dispose();
        _ctxCodificador.Dispose();
        _devCodificador.Dispose();
    }
}

/// <summary>
/// Abre el codificador de la tubería nativa siguiendo <see cref="PlanCodificador"/>: cada intento, con su HRESULT si falla,
/// queda en el registro (y en <c>--diagnostico</c>). Si ninguno sirve lanza, y la tubería pasa al respaldo con ffmpeg.
/// </summary>
internal static class FabricaCodificador
{
    public static ICodificador Crear(IDXGIAdapter1 adaptador, ID3D11Device dev, ID3D11DeviceContext ctx, int ancho, int alto, int fps, int kbps,
        SalidaCodificador salida)
    {
        var dueno = Gpu.Describir(adaptador);
        var plan = PlanCodificador.Ordenar(dueno, Gpu.Adaptadores(), ContarCodificadores);
        var fallos = new List<string>();
        for (var i = 0; i < plan.Count; i++)
        {
            var intento = plan[i];
            if (intento.Via == ViaCodificacion.RespaldoFfmpeg) break;
            var etiqueta = $"intento {i + 1}/{plan.Count - 1} ({intento})";
            try
            {
                var c = Abrir(intento, dueno, dev, ctx, ancho, alto, fps, kbps, salida);
                Registro.Anotar($"nativa: {etiqueta}: funcionó");
                return c;
            }
            catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException or InvalidCastException)
            {
                var porque = TuberiaNativa.Explicar(e);
                Registro.Anotar($"nativa: {etiqueta}: no funcionó: {porque}");
                fallos.Add($"{intento}: {porque}");
            }
        }
        throw new InvalidOperationException("Ningún codificador H.264 por hardware pudo con el monitor capturado ("
            + string.Join(" · ", fallos) + "). Queda el respaldo con ffmpeg.");
    }

    private static ICodificador Abrir(IntentoCodificador intento, AdaptadorGpu dueno, ID3D11Device dev, ID3D11DeviceContext ctx,
        int ancho, int alto, int fps, int kbps, SalidaCodificador salida)
    {
        if (intento.Via == ViaCodificacion.MismaGpu)
            return new CodificadorMf(dev, ancho, alto, fps, kbps, vbr: true, salida, ALuid(dueno.Luid));

        using var otra = Gpu.PorLuid(intento.Codifica!.Luid) ?? throw new InvalidOperationException("la gráfica ya no está");
        var (devB, ctxB) = Gpu.Abrir(otra);
        try
        {
            // Solo los codificadores de esa gráfica: los demás no leerían sus texturas.
            var interno = new CodificadorMf(devB, ancho, alto, fps, kbps, vbr: true, salida, ALuid(intento.Codifica.Luid), soloAdaptador: true);
            try { return new CodificadorConCopia(dev, ctx, devB, ctxB, interno, ancho, alto, dueno.Nombre, intento.Codifica.Nombre); }
            catch { interno.Dispose(); throw; }
        }
        catch
        {
            ctxB.Dispose();
            devB.Dispose();
            throw;
        }
    }

    private static int ContarCodificadores(AdaptadorGpu a)
    {
        try { return CodificadorMf.Listar(ALuid(a.Luid), soloAdaptador: true).Count; }
        catch (Exception e) when (e is SharpGenException or COMException)
        {
            Registro.Anotar($"nativa: no se pudieron listar los codificadores de {a.Nombre}: {TuberiaNativa.Explicar(e)}");
            return 0;
        }
    }

    internal static Vortice.Luid ALuid(long luid) => new((uint)luid, (int)(luid >> 32));
}
