using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PantallaTv.Audio.Prueba;

/// <summary>
/// Herramientas de la prueba real de F5/H2: escuchar lo que suena en una salida (loopback del dispositivo, no por proceso)
/// y silenciar la sesión de un proceso (p. ej. el emulador, para que su reproducción no se mezcle en los parlantes).
/// </summary>
internal static class Salida
{
    /// <summary>Captura <paramref name="seg"/> s de la salida y mide el nivel total y el de cada frecuencia, por segundo.</summary>
    public static int Escuchar(string? texto, double seg, double[] hz, CancellationToken ct)
    {
        var lista = Dispositivos.Listar();
        var (disp, error) = texto is null
            ? (lista.FirstOrDefault(d => d.EsDefecto), lista.Any(d => d.EsDefecto) ? null : "no hay salida por defecto")
            : Dispositivos.Elegir(lista, texto);
        if (disp is null) { Console.Error.WriteLine(error); return 2; }

        using var en = new MMDeviceEnumerator();
        using var dispositivo = en.GetDevice(disp.Id);
        using var cap = new WasapiLoopbackCapture(dispositivo);
        var fmt = cap.WaveFormat;
        if (fmt.BitsPerSample != 32 || (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.Encoding != WaveFormatEncoding.Extensible))
        {
            Console.Error.WriteLine($"formato no flotante: {fmt}");
            return 3;
        }
        var canales = fmt.Channels;
        var mono = new List<float>();
        var bloqueo = new object();
        cap.DataAvailable += (_, e) =>
        {
            lock (bloqueo)
            {
                var n = e.BytesRecorded / 4 / canales;
                for (var i = 0; i < n; i++)
                {
                    float suma = 0;
                    for (var c = 0; c < canales; c++) suma += BitConverter.ToSingle(e.Buffer, (i * canales + c) * 4);
                    mono.Add(suma / canales);
                }
            }
        };
        Console.WriteLine($"escuchando «{disp.Nombre}» {seg:0} s ({fmt.SampleRate} Hz, {canales} canales)");
        cap.StartRecording();
        try { Task.Delay(TimeSpan.FromSeconds(seg), ct).Wait(ct); } catch (OperationCanceledException) { }
        cap.StopRecording();

        float[] todo;
        lock (bloqueo) todo = mono.ToArray();
        var porSeg = fmt.SampleRate;
        Console.WriteLine("seg  rms_dBFS  " + string.Join("  ", hz.Select(h => $"{h,6:0}Hz")));
        for (var ini = 0; ini + porSeg <= todo.Length; ini += porSeg)
        {
            var tramo = todo.AsSpan(ini, porSeg).ToArray();
            var fila = string.Join("  ", hz.Select(h => $"{Analisis.ADb(Analisis.Amplitud(tramo, h, porSeg)),8:0.0}"));
            Console.WriteLine($"{ini / porSeg,3}  {Analisis.RmsDbfs(tramo),8:0.0}  {fila}");
        }
        if (todo.Length > 0)
        {
            var fila = string.Join("  ", hz.Select(h => $"{Analisis.ADb(Analisis.Amplitud(todo, h, porSeg)),8:0.0}"));
            Console.WriteLine($"tot  {Analisis.RmsDbfs(todo),8:0.0}  {fila}");
        }
        return 0;
    }

    /// <summary>Silencia (o devuelve) la sesión de audio de un proceso en todas las salidas.</summary>
    public static int SilenciarSesion(int pid, bool silenciar)
    {
        var cambiadas = 0;
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (d)
            {
                var sesiones = d.AudioSessionManager.Sessions;
                for (var i = 0; i < sesiones.Count; i++)
                {
                    using var s = sesiones[i];
                    if ((int)s.GetProcessID != pid) continue;
                    s.SimpleAudioVolume.Mute = silenciar;
                    cambiadas++;
                    Console.WriteLine($"{(silenciar ? "silenciada" : "devuelta")}: PID {pid} en «{d.FriendlyName}»");
                }
            }
        }
        if (cambiadas == 0) Console.WriteLine($"PID {pid}: sin sesiones de audio");
        return 0;
    }

    public static double[] Frecuencias(string? texto) =>
        (texto ?? "440,1000").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
}
