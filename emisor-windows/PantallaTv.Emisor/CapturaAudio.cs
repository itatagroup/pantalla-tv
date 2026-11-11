using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PantallaTv.Emisor;

/// <summary>Cómo vienen las muestras del dispositivo de salida de Windows.</summary>
internal readonly record struct FormatoPcm(int Bits, bool EsFlotante, int Canales);

/// <summary>Convierte lo que entrega Windows (casi siempre flotante de 32 bits) al PCM de 16 bits del protocolo.</summary>
internal static class ConversorPcm
{
    /// <summary>
    /// Devuelve PCM de 16 bits, little-endian, intercalado, con <paramref name="canalesSalida"/> canales
    /// (los primeros de la entrada; si la entrada es mono y se piden dos, se duplica).
    /// </summary>
    public static byte[] A16Bits(ReadOnlySpan<byte> entrada, FormatoPcm f, int canalesSalida)
    {
        var bytesMuestra = f.Bits / 8;
        var bytesCuadro = bytesMuestra * f.Canales;
        if (bytesCuadro <= 0) return [];
        var cuadros = entrada.Length / bytesCuadro;
        var salida = new byte[cuadros * canalesSalida * 2];
        for (var i = 0; i < cuadros; i++)
        {
            for (var c = 0; c < canalesSalida; c++)
            {
                var canalEntrada = Math.Min(c, f.Canales - 1);
                var muestra = entrada.Slice(i * bytesCuadro + canalEntrada * bytesMuestra, bytesMuestra);
                BinaryPrimitives.WriteInt16LittleEndian(salida.AsSpan((i * canalesSalida + c) * 2, 2), A16(muestra, f));
            }
        }
        return salida;
    }

    private static short A16(ReadOnlySpan<byte> m, FormatoPcm f)
    {
        if (f.EsFlotante && f.Bits == 32)
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(m);
            if (float.IsNaN(v)) return 0;
            return (short)Math.Round(Math.Clamp(v, -1f, 1f) * short.MaxValue);
        }
        return f.Bits switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(m),
            24 => (short)((m[2] << 8) | m[1]), // los 16 bits más significativos
            32 => (short)(BinaryPrimitives.ReadInt32LittleEndian(m) >> 16),
            8 => (short)((m[0] - 128) << 8),
            _ => 0,
        };
    }
}

/// <summary>
/// Captura lo que suena en el PC (WASAPI en modo bucle, el dispositivo de salida por defecto) y lo
/// entrega en paquetes de 20 ms de PCM de 16 bits. Mientras no suena nada, Windows no entrega datos.
/// </summary>
internal sealed class CapturaAudio : IDisposable
{
    public const int MsPorPaquete = 20;

    /// <summary>Subformato «IEEE float» de un WAVEFORMATEXTENSIBLE.</summary>
    private static readonly Guid SubformatoFlotante = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiLoopbackCapture _captura = new();
    private readonly FormatoPcm _formato;
    private readonly byte[] _paquete;
    private int _lleno;
    private Action<byte[]>? _alTenerPaquete;
    private readonly bool _silenciarPc;
    // El enumerador y el dispositivo se conservan vivos: si se liberan, el control de volumen deja de servir.
    private MMDeviceEnumerator? _dispositivos;
    private MMDevice? _salida;
    private bool _silenciadoPorNosotros;

    /// <summary>Muestras por segundo del dispositivo (48000 o 44100 casi siempre). Se manda tal cual: no se remuestrea.</summary>
    public int Tasa { get; }

    /// <summary>Canales que se envían: 1 o 2 (de un dispositivo 5.1 se toman los dos primeros).</summary>
    public int Canales { get; }

    /// <param name="silenciarPc">
    /// Silencia los parlantes del PC mientras dura la captura, para que el sonido salga solo por la TV.
    /// La captura en bucle toma el sonido antes del volumen del dispositivo, así que sigue llegando.
    /// </param>
    public CapturaAudio(bool silenciarPc = false)
    {
        _silenciarPc = silenciarPc;
        var f = _captura.WaveFormat;
        var flotante = f.Encoding == WaveFormatEncoding.IeeeFloat
                       || (f is WaveFormatExtensible ext && ext.SubFormat == SubformatoFlotante);
        _formato = new FormatoPcm(f.BitsPerSample, flotante, f.Channels);
        Tasa = f.SampleRate;
        Canales = Math.Min(f.Channels, 2);
        _paquete = new byte[Tasa * MsPorPaquete / 1000 * Canales * 2];
        _captura.DataAvailable += AlLlegar;
    }

    public void Iniciar(Action<byte[]> alTenerPaquete)
    {
        _alTenerPaquete = alTenerPaquete;
        _captura.StartRecording();
        if (_silenciarPc) SilenciarPc();
    }

    private void SilenciarPc()
    {
        try
        {
            _dispositivos = new MMDeviceEnumerator();
            _salida = _dispositivos.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_salida.AudioEndpointVolume.Mute) return; // ya estaba en silencio: al terminar no se toca
            _salida.AudioEndpointVolume.Mute = true;
            _silenciadoPorNosotros = true;
            // Si el programa termina sin pasar por Dispose (se cierra la sesión de Windows, una salida
            // apurada), que el PC no quede mudo. No cubre que maten el proceso: para eso está el rescate.
            AppDomain.CurrentDomain.ProcessExit += AlTerminarElProceso;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // sin permiso o sin dispositivo: se sigue con sonido doble antes que fallar
        }
    }

    private void AlLlegar(object? origen, WaveInEventArgs e)
    {
        ReadOnlySpan<byte> resto = ConversorPcm.A16Bits(e.Buffer.AsSpan(0, e.BytesRecorded), _formato, Canales);
        while (resto.Length > 0)
        {
            var n = Math.Min(resto.Length, _paquete.Length - _lleno);
            resto[..n].CopyTo(_paquete.AsSpan(_lleno));
            _lleno += n;
            resto = resto[n..];
            if (_lleno < _paquete.Length) continue;
            _alTenerPaquete?.Invoke(_paquete.ToArray());
            _lleno = 0;
        }
    }

    private void AlTerminarElProceso(object? origen, EventArgs e) => RestaurarSonido();

    private void RestaurarSonido()
    {
        if (!_silenciadoPorNosotros) return;
        _silenciadoPorNosotros = false;
        AppDomain.CurrentDomain.ProcessExit -= AlTerminarElProceso;
        try
        {
            _salida!.AudioEndpointVolume.Mute = false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // el dispositivo desapareció: queda «PantallaTv.exe --quitar-silencio» como rescate
        }
    }

    public void Dispose()
    {
        _captura.DataAvailable -= AlLlegar;
        RestaurarSonido();
        _salida?.Dispose();
        _dispositivos?.Dispose();
        try
        {
            _captura.StopRecording();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // no había empezado o el dispositivo desapareció
        }
        _captura.Dispose();
    }
}
