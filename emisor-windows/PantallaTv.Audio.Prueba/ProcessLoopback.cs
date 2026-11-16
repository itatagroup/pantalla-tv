using System.Runtime.InteropServices;

namespace PantallaTv.Audio.Prueba;

#pragma warning disable SYSLIB1096 // Interop COM clásico a propósito: es un prototipo de medición.

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
/// Completion handler de ActivateAudioInterfaceAsync. Implementa IAgileObject: sin eso, si el hilo que
/// activa no es MTA, Windows intenta empaquetar la llamada al apartamento y la activación falla o se cuelga.
/// Además el programa corre en MTA (Main sin STAThread) y la espera se hace con un evento, nunca
/// dentro del callback.
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

/// <summary>Una captura de audio por proceso (process loopback) ya inicializada.</summary>
internal sealed class ClienteProcessLoopback : IDisposable
{
    public const int Frecuencia = 48000;
    public const int Canales = 2;

    private const string RutaDispositivo = "VAD\\Process_Loopback"; // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    private const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public IAudioClient Cliente { get; }
    public IAudioCaptureClient Captura { get; }
    public AutoResetEvent Evento { get; } = new(false);

    private ClienteProcessLoopback(IAudioClient c, IAudioCaptureClient cap) { Cliente = c; Captura = cap; }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string ruta, ref Guid riid, IntPtr parametros,
        IActivateAudioInterfaceCompletionHandler manejador, out IActivateAudioInterfaceAsyncOperation operacion);

    /// <summary>Activa e inicializa. Lanza InvalidOperationException con el HRESULT si algo falla.</summary>
    public static ClienteProcessLoopback Abrir(uint pid, bool excluir)
    {
        var parametros = Analisis.ParametrosActivacion(pid, excluir);
        var bufParam = Marshal.AllocHGlobal(parametros.Length);
        var pv = Marshal.AllocHGlobal(24); // PROPVARIANT x64: vt(2)+relleno(6)+cbSize(4)+relleno(4)+puntero(8)
        var fmt = Analisis.FormatoFlotante32(Frecuencia, Canales);
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

            // 200000 = 20 ms; si el controlador no lo acepta, que decida el sistema (0).
            var banderas = LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY;
            var r = cliente.Initialize(0, banderas, 200000, 0, bufFmt, IntPtr.Zero);
            if (r < 0) r = cliente.Initialize(0, LOOPBACK | EVENTCALLBACK, 0, 0, bufFmt, IntPtr.Zero);
            if (r < 0) throw new InvalidOperationException($"Initialize HRESULT=0x{r:X8}");

            var iidCap = IidCaptureClient;
            var rs = cliente.GetService(ref iidCap, out var capObj);
            if (rs < 0 || capObj is not IAudioCaptureClient cap)
                throw new InvalidOperationException($"GetService(IAudioCaptureClient) HRESULT=0x{rs:X8}");

            var resultado = new ClienteProcessLoopback(cliente, cap);
            var rEv = cliente.SetEventHandle(resultado.Evento.SafeWaitHandle.DangerousGetHandle());
            if (rEv < 0) throw new InvalidOperationException($"SetEventHandle HRESULT=0x{rEv:X8}");
            return resultado;
        }
        finally
        {
            if (bufParam != IntPtr.Zero) Marshal.FreeHGlobal(bufParam);
            if (pv != IntPtr.Zero) Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(bufFmt);
        }
    }

    public void Dispose()
    {
        try { Cliente.Stop(); } catch { /* ya detenido */ }
        // Primero se suelta el cliente (que tiene el evento registrado) y después se cierra el evento.
        Marshal.ReleaseComObject(Captura);
        Marshal.ReleaseComObject(Cliente);
        Evento.Dispose();
    }
}
