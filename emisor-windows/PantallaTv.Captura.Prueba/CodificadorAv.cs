using FFmpeg.AutoGen;
using PantallaTv.Emisor.Nativa;
using Vortice.Direct3D11;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace PantallaTv.Captura.Prueba;

/// <summary>
/// Candidato (b): libavcodec dentro del proceso (compilación LGPL compartida, ver <c>descargar-ffmpeg-lgpl.ps1</c>)
/// con <c>h264_amf</c> y cuadros de hardware D3D11 en NV12, como hace Sunshine en AMD. El
/// contexto de hardware usa el mismo dispositivo D3D11 que la captura; los cuadros salen de un grupo de
/// texturas creado por libavcodec con <c>BIND_RENDER_TARGET</c>, donde convierte el sombreador.
/// </summary>
internal sealed unsafe class CodificadorAv : ICodificador
{
    private AVCodecContext* _ctx;
    private AVBufferRef* _dispositivo;
    private AVBufferRef* _cuadros;
    private AVFrame* _ultimo;      // el cuadro convertido más reciente (se reenvía en los latidos)
    private AVPacket* _paquete;
    private readonly ID3D11Device _dev;
    private readonly List<string> _avisos = [];
    private readonly SalidaCodificador _salida;

    public string Descripcion { get; }

    public CodificadorAv(ID3D11Device dev, int ancho, int alto, Opciones o, SalidaCodificador salida)
    {
        _dev = dev;
        _salida = salida;
        ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg-lgpl", "bin");
        if (!Directory.Exists(ffmpeg.RootPath))
            ffmpeg.RootPath = Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-lgpl", "bin");
        DynamicallyLoadedBindings.Initialize();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);

        // Contexto de hardware sobre nuestro dispositivo (libavcodec se queda con una referencia).
        _dispositivo = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        var hw = (AVHWDeviceContext*)_dispositivo->data;
        var d3d = (AVD3D11VADeviceContext*)hw->hwctx;
        dev.AddRef();
        d3d->device = (FFmpeg.AutoGen.ID3D11Device*)dev.NativePointer;
        Comprobar(ffmpeg.av_hwdevice_ctx_init(_dispositivo), "av_hwdevice_ctx_init");

        _cuadros = ffmpeg.av_hwframe_ctx_alloc(_dispositivo);
        var fc = (AVHWFramesContext*)_cuadros->data;
        fc->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        fc->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        fc->width = ancho;
        fc->height = alto;
        // 0 = grupo dinámico de texturas sueltas. Con un tamaño fijo libavcodec crea un ARREGLO de texturas NV12 y
        // esta GPU lo rechaza con BIND_RENDER_TARGET (E_INVALIDARG, el mismo error de scale_d3d11 en las medidas de 0.6.1).
        fc->initial_pool_size = 0;
        ((AVD3D11VAFramesContext*)fc->hwctx)->BindFlags = 0x20 /*D3D11_BIND_RENDER_TARGET*/;
        Comprobar(ffmpeg.av_hwframe_ctx_init(_cuadros), "av_hwframe_ctx_init");

        var codec = ffmpeg.avcodec_find_encoder_by_name("h264_amf");
        if (codec is null) throw new InvalidOperationException("Esta compilación de libavcodec no trae h264_amf.");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->width = ancho;
        _ctx->height = alto;
        _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        _ctx->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        _ctx->time_base = new AVRational { num = 1, den = 10_000_000 }; // tiempos en 100 ns, como la captura
        _ctx->framerate = new AVRational { num = o.Fps, den = 1 };
        _ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(_cuadros);
        _ctx->max_b_frames = 0;
        _ctx->gop_size = int.MaxValue;     // sin claves periódicas: solo a pedido
        _ctx->keyint_min = int.MaxValue;
        _ctx->flags |= unchecked((int)(ffmpeg.AV_CODEC_FLAG_CLOSED_GOP | ffmpeg.AV_CODEC_FLAG_LOW_DELAY));
        _ctx->flags2 |= (int)ffmpeg.AV_CODEC_FLAG2_FAST;
        _ctx->bit_rate = o.Kbps * 1000L;
        _ctx->rc_max_rate = o.Kbps * 1000L;
        _ctx->rc_buffer_size = o.Kbps * 1000 / o.Fps; // un cuadro (Sunshine)
        if (o.Tasa == ModoTasa.Cbr) _ctx->rc_min_rate = o.Kbps * 1000L;
        _ctx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        _ctx->colorspace = AVColorSpace.AVCOL_SPC_SMPTE170M;
        _ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_SMPTE170M;
        _ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M;
        _ctx->profile = 100; // High

        // Opciones privadas de h264_amf (parámetros de Sunshine para AMD).
        Opcion("usage", "ultralowlatency");
        Opcion("quality", "speed");
        Opcion("rc", o.Tasa == ModoTasa.Cbr ? "cbr" : "vbr_latency");
        Opcion("enforce_hrd", "0");
        Opcion("filler_data", "0");
        Opcion("vbaq", "1");
        Opcion("preencode", "0");
        Opcion("frame_skipping", "0");
        Opcion("latency", "1");
        Opcion("forced_idr", "1");
        Opcion("header_spacing", "0");
        Opcion("aud", "0");

        Comprobar(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2");
        _paquete = ffmpeg.av_packet_alloc();
        Descripcion = $"libavcodec h264_amf · {(o.Tasa == ModoTasa.Cbr ? "cbr" : "vbr_latency")} {o.Kbps} kb/s"
            + (_avisos.Count > 0 ? $" · no aceptó: {string.Join(", ", _avisos)}" : "");
    }

    private void Opcion(string nombre, string valor)
    {
        var r = ffmpeg.av_opt_set(_ctx->priv_data, nombre, valor, 0);
        if (r < 0) _avisos.Add($"{nombre}={valor}");
    }

    private static void Comprobar(int r, string que)
    {
        if (r >= 0) return;
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(r, buf, 256);
        throw new InvalidOperationException($"{que}: {new string((sbyte*)buf)} ({r})");
    }

    public DestinoNv12 SiguienteDestino()
    {
        var f = ffmpeg.av_frame_alloc();
        Comprobar(ffmpeg.av_hwframe_get_buffer(_cuadros, f, 0), "av_hwframe_get_buffer");
        // data[0] = ID3D11Texture2D* (un arreglo), data[1] = índice de la rebanada.
        var textura = new ID3D11Texture2D((nint)f->data[0]);
        textura.AddRef(); // el envoltorio de Vortice libera al desecharse; la textura es del grupo de libavcodec
        _pendiente = f;
        return new DestinoNv12(textura, (int)(nint)f->data[1]);
    }

    private AVFrame* _pendiente;

    public void Codificar(DestinoNv12 d, long tiempo100ns, bool clave)
    {
        if (_pendiente is not null)
        {
            // Cuadro nuevo: pasa a ser «el último» y el anterior vuelve al grupo.
            if (_ultimo is not null) { var u = _ultimo; ffmpeg.av_frame_free(&u); }
            _ultimo = _pendiente;
            _pendiente = null;
            d.Textura.Dispose();
        }
        _ultimo->pts = tiempo100ns;
        _ultimo->pict_type = clave ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        Comprobar(ffmpeg.avcodec_send_frame(_ctx, _ultimo), "avcodec_send_frame");
        Recibir();
    }

    private void Recibir()
    {
        while (true)
        {
            var r = ffmpeg.avcodec_receive_packet(_ctx, _paquete);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) return;
            Comprobar(r, "avcodec_receive_packet");
            _salida(new ReadOnlySpan<byte>(_paquete->data, _paquete->size).ToArray(), _paquete->pts);
            ffmpeg.av_packet_unref(_paquete);
        }
    }

    public void Vaciar()
    {
        ffmpeg.avcodec_send_frame(_ctx, null);
        Recibir();
    }

    public void Dispose()
    {
        if (_pendiente is not null) { var p = _pendiente; ffmpeg.av_frame_free(&p); _pendiente = null; }
        if (_ultimo is not null) { var u = _ultimo; ffmpeg.av_frame_free(&u); _ultimo = null; }
        if (_paquete is not null) { var p = _paquete; ffmpeg.av_packet_free(&p); _paquete = null; }
        if (_ctx is not null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_cuadros is not null) { var c = _cuadros; ffmpeg.av_buffer_unref(&c); _cuadros = null; }
        if (_dispositivo is not null) { var d = _dispositivo; ffmpeg.av_buffer_unref(&d); _dispositivo = null; }
        GC.KeepAlive(_dev);
    }
}

/// <summary>Sin codificador: solo captura y conversión (para medir cuánto cuestan ellas solas).</summary>
internal sealed class SinCodificador(ID3D11Device dev, int ancho, int alto) : ICodificador
{
    private readonly AnilloNv12 _anillo = new(dev, ancho, alto, 2);
    public string Descripcion => "ninguno (solo captura y conversión)";
    public DestinoNv12 SiguienteDestino() => _anillo.Siguiente();
    public void Codificar(DestinoNv12 d, long tiempo100ns, bool clave) { }
    public void Vaciar() { }
    public void Dispose() => _anillo.Dispose();
}
