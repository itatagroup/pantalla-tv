using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// El codificador H.264 por hardware de Media Foundation (la MFT del fabricante), asíncrono, con el dispositivo
/// D3D11 compartido por <c>IMFDXGIDeviceManager</c>: recibe la textura NV12 sin bajarla a memoria. Los ajustes
/// van por <c>ICodecAPI</c>; el clave a pedido, con <c>CODECAPI_AVEncVideoForceKeyFrame</c> (comprobado en N1).
/// <para>
/// Un hilo propio atiende los eventos de la MFT (pide entrada / tiene salida) y entrega cada unidad apenas
/// sale. En el prototype N1 la salida se recogía al entregar el cuadro siguiente: con ritmo variable, el último
/// cambio de la pantalla se habría quedado dentro del codificador hasta el próximo. Las llamadas que procesan
/// cuadros (<c>ProcessInput</c>, <c>ProcessOutput</c>, ajustes) van bajo un mismo cerrojo.
/// </para>
/// <para>
/// <b>Ojo (medido en N2):</b> la MFT de AMD retiene un cuadro: la salida del cuadro N sale recién al entregarle el N+1,
/// aunque esté en baja latencia. Ni <c>COMMAND_DRAIN</c> (vuelve clave cada cuadro) ni <c>COMMAND_MARKER</c> (irregular)
/// lo evitan. Quien lo usa con ritmo variable tiene que «empujar» el último cuadro (ver <see cref="TuberiaNativa"/>).
/// </para>
/// </summary>
internal sealed class CodificadorMf : ICodificador
{
    private const int SinEventos = unchecked((int)0xC00D3E80);   // MF_E_NO_EVENTS_AVAILABLE
    private const int Apagado = unchecked((int)0xC00D3E85);      // MF_E_SHUTDOWN
    private const int CambioDeFlujo = unchecked((int)0xC00D6D61); // MF_E_TRANSFORM_STREAM_CHANGE
    private const int FaltaEntrada = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT

    /// <summary>Si la MFT no pide otro cuadro en este tiempo, se da por colgada.</summary>
    internal static readonly TimeSpan PlazoEntrada = TimeSpan.FromSeconds(2);

    // Los objetos de la MFT elegida. No son readonly: se prueban varias MFT hasta que una acepta (ver Preparar).
    private IMFTransform _mft = null!;
    private IMFMediaEventGenerator _eventos = null!;
    private IMFShutdown? _apagar;
    private IMFDXGIDeviceManager? _gestor;
    private ICodecApi? _api;
    private readonly AnilloNv12 _anillo;
    private readonly SalidaCodificador _salida;
    private readonly long _duracion;
    private readonly object _cerrojo = new();
    private readonly SemaphoreSlim _pideEntrada = new(0);
    private readonly ManualResetEventSlim _vaciado = new();
    private readonly Thread _bomba;
    private volatile bool _cerrando;
    private volatile Exception? _error;
    private readonly List<string> _avisos = [];
    private static readonly object Uno = 1u;

    public string Descripcion { get; }

    /// <summary>Si la MFT acepta <c>CODECAPI_AVEncVideoForceKeyFrame</c>; si no, manda una clave cada <see cref="SegundosClaveSinPedido"/>.</summary>
    public bool ClaveAPedido { get; private set; }

    /// <summary>Con una MFT que no da claves a pedido: una periódica cada tantos segundos (como ffmpeg).</summary>
    internal const int SegundosClaveSinPedido = 4;

    /// <param name="vbr">true = VBR con tope (<c>PeakConstrainedVBR</c>, <paramref name="kbps"/> es el máximo); false = CBR (rellena con la pantalla quieta).</param>
    /// <param name="adaptador">LUID del adaptador de <paramref name="dev"/>: se prueban primero las MFT de ese adaptador (en un
    /// portátil con dos gráficas, la MFT de la otra no lee las texturas de este dispositivo). null = en el orden de Windows.</param>
    public CodificadorMf(ID3D11Device dev, int ancho, int alto, int fps, int kbps, bool vbr, SalidaCodificador salida,
        Vortice.Luid? adaptador = null)
    {
        _salida = salida;
        MediaFactory.MFStartup(true).CheckError();
        _duracion = 10_000_000L / fps;
        // Se prueban las MFT de hardware (NV12 → H.264) en orden hasta que una acepte el dispositivo y los ajustes.
        var descartadas = new List<string>();
        string? nombre = null;
        try
        {
            foreach (var (activar, nombreMft) in Candidatas(adaptador))
            {
                using (activar)
                {
                    if (nombre is not null) continue; // ya hay una: solo se liberan las demás
                    try
                    {
                        Preparar(activar, dev, ancho, alto, fps, kbps, vbr);
                        nombre = nombreMft;
                    }
                    catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException or InvalidCastException)
                    {
                        descartadas.Add($"{nombreMft}: {(e is SharpGenException s ? $"{s.Message.Trim()} (0x{s.HResult:X8})" : e.Message)}");
                        Soltar();
                        try { activar.ShutdownObject(); } catch (SharpGenException) { }
                    }
                }
            }
            if (nombre is null)
                throw new InvalidOperationException(descartadas.Count == 0
                    ? "No hay codificador H.264 por hardware en Media Foundation."
                    : "Ningún codificador H.264 por hardware aceptó esta gráfica: " + string.Join(" · ", descartadas));
            _anillo = new AnilloNv12(dev, ancho, alto, 4);
            _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
        catch
        {
            // Nada de lo creado debe quedar vivo: la nativa reintenta hasta 4 veces por minuto.
            Soltar();
            _anillo?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
        _bomba = new Thread(Bombear) { IsBackground = true, Name = "pantalla-codificador-mf" };
        _bomba.Start();
        Descripcion = $"Media Foundation · {nombre} · {(vbr ? "VBR con tope" : "CBR")} {kbps} kb/s"
            + (ClaveAPedido ? "" : $" · sin clave a pedido (una cada {SegundosClaveSinPedido} s)")
            + (_avisos.Count > 0 ? $" · rechazó: {string.Join(", ", _avisos)}" : "")
            + (descartadas.Count > 0 ? $" · descartadas antes: {string.Join(" · ", descartadas)}" : "");
    }

    /// <summary>
    /// Las MFT de hardware de NV12 a H.264, con su nombre: primero las del adaptador pedido (<c>MFTEnum2</c> filtra por
    /// LUID) y después las demás que lista <c>MFTEnumEx</c>, sin repetir. Quien recorre la lista libera cada una.
    /// </summary>
    private static List<(IMFActivate Activar, string Nombre)> Candidatas(Vortice.Luid? adaptador)
    {
        var entrada = new RegisterTypeInfo { GuidMajorType = Guids.Video, GuidSubtype = Guids.Nv12 };
        var salida = new RegisterTypeInfo { GuidMajorType = Guids.Video, GuidSubtype = Guids.H264 };
        var todas = new List<(IMFActivate, string)>();
        var vistas = new HashSet<Guid>();

        void Sumar(IMFActivate a)
        {
            var clsid = Leer(() => a.GetGUID(Guids.TransformClsid)) ?? Guid.NewGuid();
            if (!vistas.Add(clsid)) { a.Dispose(); return; }
            var nombre = LeerTexto(() => a.GetAllocatedString(Guids.FriendlyName)) ?? clsid.ToString();
            if (LeerTexto(() => a.GetAllocatedString(Guids.VendorId)) is { } fabricante) nombre += $" [{fabricante}]";
            todas.Add((a, nombre));
        }

        if (adaptador is { } luid)
        {
            // MFTEnum2 (Windows 10 1703 o posterior) con MFT_ENUM_ADAPTER_LUID: solo las MFT de esa gráfica.
            try
            {
                using var filtro = MediaFactory.MFCreateAttributes(1);
                filtro.Set(Guids.AdapterLuid, ((ulong)(uint)luid.HighPart << 32) | luid.LowPart);
                MediaFactory.MFTEnum2(TransformCategoryGuids.VideoEncoder, Mft.EnumHardware | Mft.EnumSortAndFilter,
                    entrada, salida, filtro, out var arreglo, out var cuantas);
                try
                {
                    for (var i = 0; i < cuantas; i++) Sumar(new IMFActivate(Marshal.ReadIntPtr(arreglo, i * IntPtr.Size)));
                }
                finally { Marshal.FreeCoTaskMem(arreglo); }
            }
            catch (Exception e) when (e is SharpGenException or EntryPointNotFoundException)
            {
                Trace.WriteLine($"codificador: MFTEnum2 no disponible ({e.Message}); se usa MFTEnumEx");
            }
        }

        try
        {
            using var lista = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, Mft.EnumHardware | Mft.EnumSortAndFilter, entrada, salida);
            foreach (var a in lista)
            {
                // La colección libera las suyas al cerrarse: se toma una referencia propia.
                a.AddRef();
                Sumar(new IMFActivate(a.NativePointer));
            }
        }
        catch
        {
            foreach (var (a, _) in todas) a.Dispose();
            throw;
        }
        return todas;
    }

    /// <summary>Los nombres de las MFT que se probarían para ese adaptador, en orden (para <c>--diagnostico</c>).</summary>
    public static IReadOnlyList<string> Listar(Vortice.Luid? adaptador)
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            var candidatas = Candidatas(adaptador);
            foreach (var (a, _) in candidatas) a.Dispose();
            return [.. candidatas.Select(c => c.Nombre)];
        }
        finally { MediaFactory.MFShutdown(); }
    }

    private static T? Leer<T>(Func<T> leer) where T : struct
    {
        try { return leer(); }
        catch (SharpGenException) { return null; }
    }

    private static string? LeerTexto(Func<string?> leer)
    {
        try { return leer(); }
        catch (SharpGenException) { return null; }
    }

    /// <summary>Activa y configura una MFT. Si algo falla lanza, y quien llama libera lo que haya quedado (<see cref="Soltar"/>).</summary>
    private void Preparar(IMFActivate activar, ID3D11Device dev, int ancho, int alto, int fps, int kbps, bool vbr)
    {
        _avisos.Clear();
        _mft = activar.ActivateObject<IMFTransform>();

        using (var atr = _mft.Attributes)
        {
            // Este código depende de los eventos de una MFT asíncrona que lea texturas D3D11: si no lo es, a la siguiente.
            if (Leer(() => atr.GetUInt32(Guids.TransformAsync)) != 1) throw new InvalidOperationException("no es asíncrona");
            // MF_SA_D3D11_AWARE es sobre todo de decodificadores: si falta solo se anota; si de verdad no lee texturas
            // D3D11, lo dice SetD3DManager más abajo (y se pasa a la siguiente).
            if (Leer(() => atr.GetUInt32(Guids.D3D11Aware)) != 1) _avisos.Add("sin MF_SA_D3D11_AWARE");
            atr.Set(Guids.AsyncUnlock, 1u).CheckError();
            atr.Set(Guids.LowLatency, 1u);
        }
        _eventos = _mft.QueryInterface<IMFMediaEventGenerator>();
        _apagar = _mft.QueryInterfaceOrNull<IMFShutdown>();

        _gestor = MediaFactory.MFCreateDXGIDeviceManager();
        _gestor.ResetDevice(dev).CheckError();
        _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_gestor.NativePointer);

        try { _api = (ICodecApi)Marshal.GetObjectForIUnknown(_mft.NativePointer); }
        catch (InvalidCastException) { _avisos.Add("sin ICodecAPI"); }

        // Antes de fijar los tipos: modo de tasa, baja latencia, sin cuadros B.
        ClaveAPedido = _api is not null && _api.IsSupported(CodecApi.ForceKeyFrame) == 0;
        var bps = (uint)kbps * 1000;
        Fijar(CodecApi.RateControlMode, vbr ? 1u /*PeakConstrainedVBR*/ : 0u /*CBR*/);
        Fijar(CodecApi.MeanBitRate, bps);
        if (vbr) Fijar(CodecApi.MaxBitRate, bps);
        Fijar(CodecApi.BufferSize, bps / (uint)fps); // un cuadro, como Sunshine
        Fijar(CodecApi.LowLatencyMode, true);
        Fijar(CodecApi.BPictureCount, 0u);
        // 0 = sin claves periódicas (solo a pedido). Sin clave a pedido la TV no se recuperaría de un salto: periódica.
        Fijar(CodecApi.GopSize, ClaveAPedido ? 0u : (uint)(fps * SegundosClaveSinPedido));
        Fijar(CodecApi.QualityVsSpeed, 0u); // 0 = lo más rápido

        using (var tipo = MediaFactory.MFCreateMediaType())
        {
            tipo.Set(Guids.MajorType, Guids.Video);
            tipo.Set(Guids.Subtype, Guids.H264);
            tipo.Set(Guids.AvgBitrate, bps);
            tipo.Set(Guids.FrameSize, ((ulong)ancho << 32) | (uint)alto);
            tipo.Set(Guids.FrameRate, ((ulong)fps << 32) | 1u);
            tipo.Set(Guids.PixelAspect, (1ul << 32) | 1u);
            tipo.Set(Guids.InterlaceMode, 2u); // progresivo
            tipo.Set(Guids.Mpeg2Profile, 100u); // High
            _mft.SetOutputType(0, tipo, 0);
        }
        using (var entrada = MediaFactory.MFCreateMediaType())
        {
            entrada.Set(Guids.MajorType, Guids.Video);
            entrada.Set(Guids.Subtype, Guids.Nv12);
            entrada.Set(Guids.FrameSize, ((ulong)ancho << 32) | (uint)alto);
            entrada.Set(Guids.FrameRate, ((ulong)fps << 32) | 1u);
            entrada.Set(Guids.PixelAspect, (1ul << 32) | 1u);
            entrada.Set(Guids.InterlaceMode, 2u);
            entrada.Set(Guids.NominalRange, 2u); // 16–235
            entrada.Set(Guids.YuvMatrix, 2u); // BT.601
            _mft.SetInputType(0, entrada, 0);
        }
    }

    /// <summary>Libera lo que <see cref="Preparar"/> alcanzó a crear de una MFT que no sirvió.</summary>
    private void Soltar()
    {
        if (_api is not null) { Marshal.ReleaseComObject(_api); _api = null; }
        _apagar?.Dispose(); _apagar = null;
        _eventos?.Dispose(); _eventos = null!;
        _mft?.Dispose(); _mft = null!;
        _gestor?.Dispose(); _gestor = null;
    }

    private void Fijar(Guid clave, object valor)
    {
        if (_api is null) return;
        var nombre = CodecApi.Nombre(clave);
        if (_api.IsSupported(clave) != 0) { _avisos.Add($"{nombre} (no lo soporta)"); return; }
        var hr = _api.SetValue(clave, ref valor);
        if (hr != 0) _avisos.Add($"{nombre} (0x{hr:X8})");
    }

    public DestinoNv12 SiguienteDestino() => _anillo.Siguiente();

    public void Codificar(DestinoNv12 d, long tiempo100ns, bool clave)
    {
        ComprobarError();
        // La MFT asíncrona avisa con un evento cada vez que acepta otro cuadro.
        if (!_pideEntrada.Wait(PlazoEntrada))
            throw new TimeoutException($"El codificador no aceptó otro cuadro en {PlazoEntrada.TotalSeconds:0} s.");
        ComprobarError();

        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, d.Textura, (uint)d.Rebanada, false);
        using (var b2 = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = b2.ContiguousLength;
        using var muestra = MediaFactory.MFCreateSample();
        muestra.AddBuffer(buffer);
        muestra.SampleTime = tiempo100ns;
        muestra.SampleDuration = _duracion;
        lock (_cerrojo)
        {
            if (clave && ClaveAPedido)
            {
                var uno = Uno;
                _api!.SetValue(CodecApi.ForceKeyFrame, ref uno); // aceptado y cumplido en AMD (N1); se cuentan las claves que salen
            }
            _mft.ProcessInput(0, muestra, 0);
        }
    }

    private void ComprobarError()
    {
        if (_error is { } e) throw new InvalidOperationException("El codificador falló: " + e.Message, e);
    }

    /// <summary>Hilo de eventos de la MFT. Termina cuando se apaga la MFT (<c>MF_E_SHUTDOWN</c>) o al cerrar.</summary>
    private void Bombear()
    {
        try
        {
            while (!_cerrando)
            {
                IMFMediaEvent ev;
                try
                {
                    // Con IMFShutdown se espera bloqueado: Dispose despierta al hilo apagando la MFT. Sin él (no
                    // debería pasar en una MFT asíncrona), se consulta sin esperar.
                    ev = _eventos.GetEvent(_apagar is null ? 1 /*MF_EVENT_FLAG_NO_WAIT*/ : 0);
                }
                catch (SharpGenException e) when (e.HResult == SinEventos)
                {
                    Thread.Sleep(1);
                    continue;
                }
                using (ev)
                {
                    if (ev.Status.Failure) throw new InvalidOperationException($"evento {ev.EventType} con error 0x{ev.Status.Code:X8}");
                    switch (ev.EventType)
                    {
                        case MediaEventTypes.TransformNeedInput: _pideEntrada.Release(); break;
                        case MediaEventTypes.TransformHaveOutput: Sacar(); break;
                        case MediaEventTypes.TransformDrainComplete: _vaciado.Set(); break;
                        case MediaEventTypes.Error: throw new InvalidOperationException("la MFT informó un error");
                    }
                }
            }
        }
        catch (Exception e)
        {
            // Al cerrar, la MFT puede responder MF_E_SHUTDOWN o E_UNEXPECTED a una salida pendiente: no es un fallo.
            // Nada puede escapar de este hilo (tumbaría el proceso entero).
            if (_cerrando || e is SharpGenException { HResult: Apagado }) return;
            _error = e;
            try { _pideEntrada.Release(); } // despierta a Codificar para que vea el error
            catch (ObjectDisposedException) { }
        }
    }

    private void Sacar()
    {
        byte[] bytes;
        long tiempo;
        lock (_cerrojo)
        {
            var buf = new OutputDataBuffer { StreamID = 0 };
            var r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
            buf.Events?.Dispose();
            if (r.Code == CambioDeFlujo)
            {
                // Algunas MFT (no la de AMD; NVIDIA e Intel, según Sunshine) cambian el tipo de salida al empezar: se acepta
                // el primero H.264 que ofrecen y se vuelve a pedir la salida. Nunca se vio pasar (no hay otra gráfica aquí).
                buf.Sample?.Dispose();
                Registro.Anotar("codificador: la MFT pidió renegociar el tipo de salida (STREAM_CHANGE)");
                IMFMediaType? elegido = null;
                for (var i = 0; elegido is null && i < 16; i++)
                {
                    IMFMediaType t;
                    try { t = _mft.GetOutputAvailableType(0, i); }
                    catch (SharpGenException) { break; } // MF_E_NO_MORE_TYPES
                    if (Leer(() => t.GetGUID(Guids.Subtype)) == Guids.H264) elegido = t;
                    else t.Dispose();
                }
                using var tipo = elegido ?? throw new InvalidOperationException("la MFT pidió otro tipo de salida y no ofrece H.264");
                _mft.SetOutputType(0, tipo, 0);
                buf = new OutputDataBuffer { StreamID = 0 };
                r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
                buf.Events?.Dispose();
            }
            if (r.Code == FaltaEntrada) { buf.Sample?.Dispose(); return; }
            if (r.Failure)
            {
                buf.Sample?.Dispose();
                throw new InvalidOperationException($"ProcessOutput falló (0x{r.Code:X8})");
            }
            using var muestra = buf.Sample;
            if (muestra is null) return;
            tiempo = muestra.SampleTime;
            using var contiguo = muestra.ConvertToContiguousBuffer();
            contiguo.Lock(out var ptr, out _, out var largo);
            try
            {
                bytes = new byte[largo];
                Marshal.Copy(ptr, bytes, 0, largo);
            }
            finally { contiguo.Unlock(); }
        }
        _salida(bytes, tiempo);
    }

    public void Vaciar()
    {
        if (_error is not null) return;
        _vaciado.Reset();
        lock (_cerrojo) _mft.ProcessMessage(TMessageType.MessageCommandDrain, 0);
        _vaciado.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        _cerrando = true;
        try { lock (_cerrojo) _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0); } catch (SharpGenException) { }
        try { _apagar?.Shutdown(); } catch (SharpGenException) { }
        if (!_bomba.Join(TimeSpan.FromSeconds(2)))
        {
            // El hilo sigue dentro de la MFT: liberarla ahora lo haría usar objetos ya liberados. Mejor perderla.
            Trace.WriteLine("codificador: el hilo de eventos no terminó; la MFT se deja sin liberar");
            return;
        }
        if (_api is not null) Marshal.ReleaseComObject(_api);
        _apagar?.Dispose();
        _eventos.Dispose();
        _mft.Dispose();
        _gestor?.Dispose();
        _anillo.Dispose();
        _pideEntrada.Dispose();
        _vaciado.Dispose();
        MediaFactory.MFShutdown();
    }

    /// <summary>Banderas de MFTEnumEx.</summary>
    private static class Mft
    {
        public const uint EnumHardware = 0x4, EnumSortAndFilter = 0x40;
    }

    private static class Guids
    {
        public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
        public static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
        public static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
        public static readonly Guid TransformClsid = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c"); // MFT_TRANSFORM_CLSID_Attribute
        public static readonly Guid VendorId = new("3aecb0cc-035b-4bcc-8185-2b8d551ef3af");       // MFT_ENUM_HARDWARE_VENDOR_ID_Attribute
        public static readonly Guid AdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");    // MFT_ENUM_ADAPTER_LUID
        public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a"); // MF_TRANSFORM_ASYNC
        public static readonly Guid D3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");     // MF_SA_D3D11_AWARE
    }
}

/// <summary>Las propiedades de <c>ICodecAPI</c> que se usan (codecapi.h).</summary>
internal static class CodecApi
{
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    public static string Nombre(Guid g) =>
        g == RateControlMode ? "RateControlMode" : g == MeanBitRate ? "MeanBitRate" : g == MaxBitRate ? "MaxBitRate"
        : g == BufferSize ? "BufferSize" : g == LowLatencyMode ? "LowLatencyMode" : g == GopSize ? "GOPSize"
        : g == BPictureCount ? "BPictureCount" : g == ForceKeyFrame ? "ForceKeyFrame"
        : g == QualityVsSpeed ? "QualityVsSpeed" : g.ToString();
}

/// <summary>
/// <c>ICodecAPI</c> (strmif.h). Vortice no la trae; se declaran los métodos en el orden de la tabla hasta
/// <c>SetValue</c>, que es el último que se usa.
/// </summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApi
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object min,
        [MarshalAs(UnmanagedType.Struct)] out object max, [MarshalAs(UnmanagedType.Struct)] out object paso);
    [PreserveSig] int GetParameterValues(in Guid api, out nint valores, out uint cuantos);
    [PreserveSig] int GetDefaultValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object valor);
    [PreserveSig] int GetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object valor);
    [PreserveSig] int SetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] ref object valor);
}
