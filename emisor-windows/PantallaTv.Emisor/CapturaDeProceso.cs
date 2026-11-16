using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PantallaTv.Emisor;

#pragma warning disable SYSLIB1096 // Interop COM clásico a propósito (igual que el prototipo medido en F4).

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operacion);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int hresult, [MarshalAs(UnmanagedType.IUnknown)] out object? interfaz);
}

/// <summary>Marcador: dice a Windows que el objeto se puede llamar desde cualquier hilo (sin proxy de apartamento).</summary>
[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject { }

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int modo, uint banderas, long duracionBuffer, long periodicidad, IntPtr formato, IntPtr sesion);
    [PreserveSig] int GetBufferSize(out uint cuadros);
    [PreserveSig] int GetStreamLatency(out long latencia);
    [PreserveSig] int GetCurrentPadding(out uint cuadros);
    [PreserveSig] int IsFormatSupported(int modo, IntPtr formato, out IntPtr cercano);
    [PreserveSig] int GetMixFormat(out IntPtr formato);
    [PreserveSig] int GetDevicePeriod(out long porDefecto, out long minimo);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr evento);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? servicio);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr datos, out uint cuadros, out uint banderas, out ulong posicionDispositivo, out ulong qpc);
    [PreserveSig] int ReleaseBuffer(uint cuadros);
    [PreserveSig] int GetNextPacketSize(out uint cuadros);
}

/// <summary>
/// Completion handler de ActivateAudioInterfaceAsync. Implementa IAgileObject: sin eso, si el hilo que activa no es
/// MTA, Windows intenta empaquetar la llamada al apartamento y la activación falla o se cuelga. La espera se hace con
/// un evento, nunca dentro del callback.
/// </summary>
[ComVisible(true)]
internal sealed class ManejadorActivacion : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Listo = new(false);
    public IActivateAudioInterfaceAsyncOperation? Operacion;

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operacion)
    {
        Operacion = operacion;
        Listo.Set();
    }
}

/// <summary>Una captura de audio de un solo proceso (y los que lanza), ya abierta. Entrega flotante de 32 bits, 48000 Hz, estéreo.</summary>
internal interface ICapturaDeProceso : IDisposable
{
    /// <summary>false cuando la captura murió (el proceso terminó o el dispositivo falló): hay que cerrarla.</summary>
    bool Viva { get; }
}

/// <summary>Abre capturas por proceso. Es una interfaz para poder probar <see cref="AudioExclusivo"/> sin audio real.</summary>
internal interface IFabricaCaptura
{
    /// <param name="pid">Proceso raíz: se captura él y todo su árbol.</param>
    /// <param name="alLlegar">Muestras intercaladas L,R en flotante (cuadros × 2). Se llama desde un hilo propio de la captura.</param>
    ICapturaDeProceso Abrir(int pid, Action<float[]> alLlegar);
}

internal sealed class FabricaCapturaWindows : IFabricaCaptura
{
    public ICapturaDeProceso Abrir(int pid, Action<float[]> alLlegar) => new CapturaDeProceso(pid, alLlegar);
}

/// <summary>
/// Captura de audio por proceso con <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c> (incluir el árbol de
/// procesos). Medido en el prototipo (F4): cada app por separado (cruce ≤ −94 dB) y el silencio del endpoint no
/// la afecta. Con process loopback no sirve GetMixFormat: se pasa un formato fijo.
/// </summary>
internal sealed class CapturaDeProceso : ICapturaDeProceso
{
    public const int Frecuencia = 48000;
    public const int Canales = 2;

    private const string RutaDispositivo = "VAD\\Process_Loopback"; // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    private const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint SILENT = 2;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly IAudioClient _cliente = null!;
    private readonly IAudioCaptureClient _captura = null!;
    private readonly AutoResetEvent _evento = new(false);
    private readonly Action<float[]> _alLlegar;
    private readonly Thread _hilo;
    private volatile bool _detener;
    private volatile bool _viva = true;

    public bool Viva => _viva;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string ruta, ref Guid riid, IntPtr parametros,
        IActivateAudioInterfaceCompletionHandler manejador, out IActivateAudioInterfaceAsyncOperation operacion);

    /// <summary>Abre y empieza a capturar. Lanza <see cref="InvalidOperationException"/> con el HRESULT si algo falla. Llamar desde un hilo MTA.</summary>
    public CapturaDeProceso(int pid, Action<float[]> alLlegar)
    {
        _alLlegar = alLlegar;
        var parametros = ParametrosActivacion((uint)pid);
        var bufParam = Marshal.AllocHGlobal(parametros.Length);
        var pv = Marshal.AllocHGlobal(24); // PROPVARIANT x64: vt(2)+relleno(6)+cbSize(4)+relleno(4)+puntero(8)
        var fmt = FormatoFlotante32(Frecuencia, Canales);
        var bufFmt = Marshal.AllocHGlobal(fmt.Length);
        try
        {
            Marshal.Copy(parametros, 0, bufParam, parametros.Length);
            Marshal.Copy(fmt, 0, bufFmt, fmt.Length);
            for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65); // VT_BLOB
            Marshal.WriteInt32(pv, 8, parametros.Length);
            Marshal.WriteIntPtr(pv, 16, bufParam);

            var manejador = new ManejadorActivacion();
            var iid = IidAudioClient;
            ActivateAudioInterfaceAsync(RutaDispositivo, ref iid, pv, manejador, out var op);
            if (!manejador.Listo.Wait(5000))
            {
                // La activación sigue en curso y puede leer los parámetros: no se liberan (se pierden unos bytes).
                bufParam = pv = IntPtr.Zero;
                throw new InvalidOperationException("La activación no terminó en 5 s.");
            }
            (manejador.Operacion ?? op).GetActivateResult(out var hr, out var obj);
            if (hr < 0 || obj is not IAudioClient cliente)
                throw new InvalidOperationException($"GetActivateResult HRESULT=0x{hr:X8}");
            _cliente = cliente;

            // 200000 = 20 ms; si el controlador no lo acepta, que decida el sistema.
            var r = _cliente.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, 200000, 0, bufFmt, IntPtr.Zero);
            if (r < 0) r = _cliente.Initialize(0, LOOPBACK | EVENTCALLBACK, 0, 0, bufFmt, IntPtr.Zero);
            if (r < 0) throw new InvalidOperationException($"Initialize HRESULT=0x{r:X8}");

            var iidCap = IidCaptureClient;
            var rs = _cliente.GetService(ref iidCap, out var capObj);
            if (rs < 0 || capObj is not IAudioCaptureClient cap)
                throw new InvalidOperationException($"GetService(IAudioCaptureClient) HRESULT=0x{rs:X8}");
            _captura = cap;
            var rEv = _cliente.SetEventHandle(_evento.SafeWaitHandle.DangerousGetHandle());
            if (rEv < 0) throw new InvalidOperationException($"SetEventHandle HRESULT=0x{rEv:X8}");
            var rIni = _cliente.Start();
            if (rIni < 0) throw new InvalidOperationException($"Start HRESULT=0x{rIni:X8}");
        }
        catch
        {
            Liberar();
            throw;
        }
        finally
        {
            if (bufParam != IntPtr.Zero) Marshal.FreeHGlobal(bufParam);
            if (pv != IntPtr.Zero) Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(bufFmt);
        }
        _hilo = new Thread(Leer) { IsBackground = true, Name = $"Audio del proceso {pid}" };
        _hilo.Start();
    }

    private void Leer()
    {
        try
        {
            while (!_detener)
            {
                _evento.WaitOne(50);
                while (!_detener)
                {
                    // Un error (dispositivo invalidado, proceso terminado) no es silencio: la captura muere y quien la creó la cierra.
                    if (_captura.GetNextPacketSize(out var n) < 0) { _viva = false; return; }
                    if (n == 0) break;
                    if (_captura.GetBuffer(out var datos, out var cuadros, out var banderas, out _, out _) < 0) { _viva = false; return; }
                    if (cuadros == 0) { _captura.ReleaseBuffer(0); break; }
                    if ((banderas & SILENT) == 0)
                    {
                        var muestras = new float[cuadros * Canales];
                        Marshal.Copy(datos, muestras, 0, muestras.Length);
                        _captura.ReleaseBuffer(cuadros);
                        _alLlegar(muestras);
                    }
                    else
                    {
                        _captura.ReleaseBuffer(cuadros);
                    }
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ObjectDisposedException)
        {
            _viva = false;
        }
    }

    private void Liberar()
    {
        try { _cliente?.Stop(); }
        catch (Exception e) when (e is COMException or InvalidComObjectException) { /* ya detenido */ }
        // Primero se suelta el cliente (que tiene el evento registrado) y después se cierra el evento.
        if (_captura is not null) Marshal.ReleaseComObject(_captura);
        if (_cliente is not null) Marshal.ReleaseComObject(_cliente);
        _evento.Dispose();
    }

    public void Dispose()
    {
        _detener = true;
        _viva = false;
        // Si el hilo sigue dentro de GetBuffer o del callback, liberar el COM debajo de él lo rompería: se deja sin liberar.
        if (_hilo.Join(2000)) Liberar();
        else Registro.Anotar("audio exclusivo: el hilo de una captura no terminó en 2 s; se deja sin liberar");
    }

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS: tipo (1 = process loopback), PID, modo (0 = incluir el árbol de procesos).</summary>
    internal static byte[] ParametrosActivacion(uint pid)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), pid);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), 0);
        return b;
    }

    /// <summary>WAVEFORMATEX de 18 bytes para PCM flotante de 32 bits.</summary>
    internal static byte[] FormatoFlotante32(int frecuencia, int canales)
    {
        var b = new byte[18];
        var blockAlign = (ushort)(canales * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), 3); // WAVE_FORMAT_IEEE_FLOAT
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)canales);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)frecuencia);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(frecuencia * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 0); // cbSize
        return b;
    }
}
