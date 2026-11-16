using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace PantallaTv.Audio.Prueba;

internal sealed record ResultadoCaptura(
    int Pid, bool Excluir, double Seg, long Paquetes, long Cuadros, long Silenciosos, long Discontinuidades,
    double RmsDbfs, double FrecHz, double Amp440Db, double Amp880Db, double EdadMsMedia, double EdadMsMax, string? Error)
{
    /// <summary>Llega audio de verdad: hubo cuadros y el nivel supera -70 dBFS.</summary>
    public bool Llega => Error is null && Cuadros > 0 && RmsDbfs > -70;
}

internal static class Captura
{
    private const uint SILENT = 2, DISCONTINUITY = 1;

    public static ResultadoCaptura Capturar(int pid, bool excluir, double seg, string? wav, bool imprimir, CancellationToken ct)
    {
        ClienteProcessLoopback cliente;
        try { cliente = ClienteProcessLoopback.Abrir((uint)pid, excluir); }
        catch (Exception ex)
        {
            return new ResultadoCaptura(pid, excluir, 0, 0, 0, 0, 0, Analisis.PisoDb, 0, Analisis.PisoDb, Analisis.PisoDb, 0, 0, ex.Message);
        }

        using (cliente)
        {
            long paquetes = 0, cuadros = 0, silenciosos = 0, discont = 0, paqSeg = 0, cuaSeg = 0, silSeg = 0, disSeg = 0;
            double sumaEdad = 0, maxEdad = 0;
            long conEdad = 0;
            var mono = new List<float>((int)(seg * ClienteProcessLoopback.Frecuencia) + 48000);
            var inicioSegundo = 0;
            var segundoActual = 1;
            string? error = null;

            var hr = cliente.Cliente.Start();
            if (hr < 0)
                return new ResultadoCaptura(pid, excluir, 0, 0, 0, 0, 0, Analisis.PisoDb, 0, Analisis.PisoDb, Analisis.PisoDb, 0, 0, $"Start HRESULT=0x{hr:X8}");

            var reloj = Stopwatch.StartNew();
            while (reloj.Elapsed.TotalSeconds < seg && !ct.IsCancellationRequested)
            {
                cliente.Evento.WaitOne(50);
                while (true)
                {
                    var rn = cliente.Captura.GetNextPacketSize(out var n);
                    // Un error (dispositivo invalidado, etc.) no es silencio: se informa en vez de medir ceros.
                    if (rn < 0) { error = $"GetNextPacketSize HRESULT=0x{rn:X8}"; break; }
                    if (n == 0) break;
                    var r = cliente.Captura.GetBuffer(out var datos, out var cua, out var banderas, out _, out var qpc);
                    if (r < 0) { error = $"GetBuffer HRESULT=0x{r:X8}"; break; }
                    if (cua == 0) { cliente.Captura.ReleaseBuffer(0); break; }

                    paquetes++; paqSeg++;
                    cuadros += cua; cuaSeg += cua;
                    var silencio = (banderas & SILENT) != 0;
                    if (silencio) { silenciosos++; silSeg++; }
                    if ((banderas & DISCONTINUITY) != 0) { discont++; disSeg++; }

                    if (silencio)
                    {
                        for (var i = 0; i < cua; i++) mono.Add(0f);
                    }
                    else
                    {
                        var tmp = new float[cua * ClienteProcessLoopback.Canales];
                        Marshal.Copy(datos, tmp, 0, tmp.Length);
                        for (var i = 0; i < cua; i++) mono.Add((tmp[2 * i] + tmp[2 * i + 1]) * 0.5f);
                        if (qpc > 0)
                        {
                            // QPC del momento de la lectura en unidades de 100 ns, el mismo reloj del paquete.
                            var ahora = Stopwatch.GetTimestamp() * 1e7 / Stopwatch.Frequency;
                            var edad = (ahora - qpc) / 1e4;
                            sumaEdad += edad; conEdad++;
                            if (edad > maxEdad) maxEdad = edad;
                        }
                    }
                    cliente.Captura.ReleaseBuffer(cua);
                }
                if (error is not null) break;

                if (reloj.Elapsed.TotalSeconds >= segundoActual)
                {
                    if (imprimir)
                    {
                        var ventana = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mono)[inicioSegundo..];
                        // Solo análisis barato (dos Goertzel): un barrido completo aquí frenaría la lectura y perdería cuadros.
                        var rms = Analisis.RmsDbfs(ventana);
                        var d440 = Analisis.ADb(Analisis.Amplitud(ventana, 440, ClienteProcessLoopback.Frecuencia));
                        var d880 = Analisis.ADb(Analisis.Amplitud(ventana, 880, ClienteProcessLoopback.Frecuencia));
                        Console.WriteLine($"  t={segundoActual}s paquetes={paqSeg} cuadros={cuaSeg} silenciosos={silSeg} discont={disSeg} rms={rms:F1} dBFS a440={d440:F1} dB a880={d880:F1} dB");
                    }
                    inicioSegundo = mono.Count;
                    paqSeg = cuaSeg = silSeg = disSeg = 0;
                    segundoActual++;
                }
            }
            cliente.Cliente.Stop();
            var duracion = reloj.Elapsed.TotalSeconds;

            // Se descarta el primer medio segundo (arranque) si hay suficiente captura.
            var todo = CollectionsMarshal.AsSpan(mono);
            var analizar = todo.Length > ClienteProcessLoopback.Frecuencia * 2 ? todo[(ClienteProcessLoopback.Frecuencia / 2)..] : todo;
            var rmsTotal = Analisis.RmsDbfs(analizar);
            var a440 = Analisis.Amplitud(analizar, 440, ClienteProcessLoopback.Frecuencia);
            var a880 = Analisis.Amplitud(analizar, 880, ClienteProcessLoopback.Frecuencia);
            var hzDom = rmsTotal > -70 ? Analisis.FrecuenciaDominante(analizar, ClienteProcessLoopback.Frecuencia).Hz : 0;

            if (wav is not null && mono.Count > 0)
            {
                using var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(ClienteProcessLoopback.Frecuencia, 1));
                w.WriteSamples(mono.ToArray(), 0, mono.Count);
            }

            return new ResultadoCaptura(pid, excluir, duracion, paquetes, cuadros, silenciosos, discont, rmsTotal, hzDom,
                Analisis.ADb(a440), Analisis.ADb(a880), conEdad > 0 ? sumaEdad / conEdad : 0, maxEdad, error);
        }
    }

    public static string Resumen(ResultadoCaptura r) =>
        $"RESUMEN pid={r.Pid} modo={(r.Excluir ? "excluir" : "incluir")} seg={r.Seg:F1} paquetes={r.Paquetes} cuadros={r.Cuadros} " +
        $"silenciosos={r.Silenciosos} discont={r.Discontinuidades} rms_dbfs={r.RmsDbfs:F1} freq_hz={r.FrecHz:F0} " +
        $"amp440_db={r.Amp440Db:F1} amp880_db={r.Amp880Db:F1} edad_ms_media={r.EdadMsMedia:F1} edad_ms_max={r.EdadMsMax:F1} " +
        $"llega={(r.Llega ? "si" : "no")} error={r.Error ?? "-"}";
}
