using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PantallaTv.Audio.Prueba;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var args2 = args;
string? Valor(string nombre)
{
    var i = Array.IndexOf(args2, nombre);
    return i >= 0 && i + 1 < args2.Length ? args2[i + 1] : null;
}
double Num(string nombre, double porDefecto) =>
    double.TryParse(Valor(nombre), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : porDefecto;
bool Tiene(string nombre) => Array.IndexOf(args2, nombre) >= 0;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => Restauracion.Ejecutar();

try
{
    if (Tiene("--tono"))
    {
        Herramientas.Tono(Num("--tono", 440), (int)Num("--seg", 5), Num("--amplitud", 0.05), cts.Token);
        return 0;
    }
    if (Tiene("--lanzar-tono")) // proceso intermedio: lanza el tono como hijo y espera (caso del árbol de procesos)
    {
        return Herramientas.LanzarTonoNieto(Num("--lanzar-tono", 440), (int)Num("--seg", 5), cts.Token);
    }
    if (Tiene("--sesiones")) return Sesiones.Listar();
    if (Tiene("--capturar"))
    {
        var pid = (int)Num("--pid", 0);
        if (pid <= 0) { Console.Error.WriteLine("Falta --pid P"); return 2; }
        var excluir = Tiene("--excluir");
        var seg = Num("--seg", 5);
        Console.WriteLine($"Capturando PID {pid} ({(excluir ? "excluir" : "incluir")} árbol) durante {seg:F0} s...");
        var r = Captura.Capturar(pid, excluir, seg, Valor("--wav"), true, cts.Token);
        Console.WriteLine(Captura.Resumen(r));
        return r.Error is null ? 0 : 1;
    }
    if (Tiene("--medir")) return Bateria.Correr(cts.Token);

    Console.WriteLine("""
        AudioPrueba: mide la captura de audio por proceso (process loopback).
          --tono <Hz> --seg N [--amplitud 0.05]       reproduce un tono e imprime su PID
          --sesiones                                  sesiones de audio activas y monitor de las ventanas
          --capturar --pid P [--excluir] [--seg N] [--wav archivo]
          --medir                                     batería completa (lanza sus propios tonos)
        """);
    return 0;
}
finally
{
    Restauracion.Ejecutar();
}

/// <summary>Deshace, aunque haya excepción o Ctrl+C, cualquier volumen o mute que la batería haya tocado.</summary>
internal static class Restauracion
{
    private static readonly Stack<(string Nombre, Action Accion)> Pendientes = new();
    private static readonly object Cerrojo = new();

    public static void Agregar(string nombre, Action accion)
    {
        lock (Cerrojo) Pendientes.Push((nombre, accion));
    }

    public static void Ejecutar()
    {
        while (true)
        {
            (string Nombre, Action Accion) item;
            lock (Cerrojo)
            {
                if (Pendientes.Count == 0) return;
                item = Pendientes.Pop();
            }
            try { item.Accion(); Console.WriteLine($"  [restaurado] {item.Nombre}"); }
            catch (Exception ex) { Console.WriteLine($"  [ERROR AL RESTAURAR] {item.Nombre}: {ex.Message}"); }
        }
    }
}

internal static class Herramientas
{
    public static void Tono(double hz, int seg, double amplitud, CancellationToken ct)
    {
        Console.WriteLine($"PID={Environment.ProcessId} tono={hz}Hz seg={seg} amplitud={amplitud}");
        var generador = new SignalGenerator(48000, 2) { Type = SignalGeneratorType.Sin, Frequency = hz, Gain = amplitud };
        using var salida = new WasapiOut(AudioClientShareMode.Shared, 50);
        salida.Init(new SampleToWaveProvider(generador));
        salida.Play();
        ct.WaitHandle.WaitOne(seg * 1000);
        salida.Stop();
    }

    public static int LanzarTonoNieto(double hz, int seg, CancellationToken ct)
    {
        Console.WriteLine($"PID={Environment.ProcessId} intermedio (sin audio propio)");
        using var nieto = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.ProcessPath!, $"--tono {hz.ToString(CultureInfo.InvariantCulture)} --seg {seg} --amplitud 0.05")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Console.WriteLine($"NIETO_PID={nieto.Id}");
            nieto.WaitForExit(seg * 1000 + 5000);
        }
        finally
        {
            if (!nieto.HasExited) nieto.Kill();
        }
        return 0;
    }
}

internal static class Bateria
{
    private sealed record Fila(string Caso, ResultadoCaptura R, double? Esperada, double? Otra);

    private static readonly List<System.Diagnostics.Process> Hijos = [];

    private static System.Diagnostics.Process Lanzar(string argumentos)
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, argumentos)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        lock (Hijos) Hijos.Add(p);
        return p;
    }

    private static System.Diagnostics.Process Tono(double hz, int seg = 9) =>
        Lanzar($"--tono {hz.ToString(CultureInfo.InvariantCulture)} --seg {seg} --amplitud 0.05");

    private static ResultadoCaptura Medir(int pid, bool excluir, CancellationToken ct) =>
        Captura.Capturar(pid, excluir, 4, null, false, ct);

    /// <summary>Control de volumen de la sesión de un PID, con las piezas COM vivas hasta restaurar.</summary>
    private sealed class SesionDe : IDisposable
    {
        private readonly MMDeviceEnumerator _en = new();
        private readonly MMDevice _disp;
        public AudioSessionControl? Control;

        public SesionDe(int pid, int esperaMs)
        {
            _disp = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var limite = Environment.TickCount64 + esperaMs;
            while (Control is null && Environment.TickCount64 < limite)
            {
                _disp.AudioSessionManager.RefreshSessions();
                var sesiones = _disp.AudioSessionManager.Sessions;
                for (var i = 0; i < sesiones.Count; i++)
                    if ((int)sesiones[i].GetProcessID == pid) { Control = sesiones[i]; break; }
                if (Control is null) Thread.Sleep(200);
            }
        }

        public void Dispose() { _disp.Dispose(); _en.Dispose(); }
    }

    public static int Correr(CancellationToken ct)
    {
        var filas = new List<Fila>();
        var notas = new List<string>();
        using var enumerador = new MMDeviceEnumerator();
        using var endpoint = enumerador.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var muteOriginal = endpoint.AudioEndpointVolume.Mute;
        var volOriginal = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        Console.WriteLine($"Estado inicial: {endpoint.FriendlyName}, endpoint mute={muteOriginal}, volumen={volOriginal:F2}");
        Console.WriteLine("Cada caso dura unos 6 s (tono de amplitud 0,05). Un caso toca el mute del endpoint un momento.");
        Restauracion.Agregar("endpoint mute", () =>
        {
            endpoint.AudioEndpointVolume.Mute = muteOriginal;

        });

        try
        {
            // a. Un tono, include.
            {
                using var t = Tono(440);
                Thread.Sleep(1500);
                filas.Add(new("a  440 Hz, incluir PID", Medir(t.Id, false, ct), 440, 880));
                Cerrar(t);
            }
            ct.ThrowIfCancellationRequested();

            // b. Dos tonos a la vez, cada PID por separado (en paralelo).
            {
                using var t1 = Tono(440);
                using var t2 = Tono(880);
                Thread.Sleep(1500);
                var c1 = Task.Run(() => Medir(t1.Id, false, ct), ct);
                var c2 = Task.Run(() => Medir(t2.Id, false, ct), ct);
                Task.WaitAll(c1, c2);
                filas.Add(new("b1 dos tonos, capturo el 440", c1.Result, 440, 880));
                filas.Add(new("b2 dos tonos, capturo el 880", c2.Result, 880, 440));
                Cerrar(t1); Cerrar(t2);
            }
            ct.ThrowIfCancellationRequested();

            // c1 y c2. Volumen de la SESIÓN en 0 y mute de la SESIÓN.
            foreach (var modo in new[] { "vol0", "mute" })
            {
                using var t = Tono(440);
                using var ses = new SesionDe(t.Id, 4000);
                if (ses.Control is null)
                {
                    notas.Add($"c ({modo}): no apareció la sesión del tono en el mezclador; caso omitido.");
                    Cerrar(t);
                    continue;
                }
                var vol = ses.Control.SimpleAudioVolume;
                var volOrig = vol.Volume;
                var muteOrig = vol.Mute;
                Restauracion.Agregar($"sesión PID {t.Id} ({modo})", () => { vol.Volume = volOrig; vol.Mute = muteOrig; });
                try
                {
                    if (modo == "vol0") vol.Volume = 0f; else vol.Mute = true;
                    Thread.Sleep(800);
                    filas.Add(new(modo == "vol0" ? "c1 sesión con volumen 0" : "c2 sesión con mute", Medir(t.Id, false, ct), 440, 880));
                }
                finally { Restauracion.Ejecutar(); }
                notas.Add($"c ({modo}): sesión restaurada a vol={vol.Volume:F2} mute={vol.Mute} (original {volOrig:F2}/{muteOrig}).");
                Cerrar(t);
                ct.ThrowIfCancellationRequested();
            }
            // Restauracion.Ejecutar() de arriba también deshizo lo del endpoint: se vuelve a registrar.
            Restauracion.Agregar("endpoint mute", () =>
            {
                endpoint.AudioEndpointVolume.Mute = muteOriginal;
            });

            // d. Mute del endpoint (lo que hace hoy CapturaAudio).
            {
                using var t = Tono(440);
                Thread.Sleep(1500);
                endpoint.AudioEndpointVolume.Mute = true;
                try
                {
                    Thread.Sleep(500);
                    filas.Add(new("d  mute del endpoint", Medir(t.Id, false, ct), 440, 880));
                }
                finally { endpoint.AudioEndpointVolume.Mute = muteOriginal; }
                Cerrar(t);
            }
            ct.ThrowIfCancellationRequested();

            // e. Excluir el proceso del 440 con los dos sonando.
            {
                using var t1 = Tono(440);
                using var t2 = Tono(880);
                Thread.Sleep(1500);
                filas.Add(new("e  excluir el 440 (suenan 440+880)", Medir(t1.Id, true, ct), 880, 440));
                Cerrar(t1); Cerrar(t2);
            }
            ct.ThrowIfCancellationRequested();

            // f. Árbol: el tono es nieto del PID capturado.
            {
                using var abuelo = Lanzar("--lanzar-tono 440 --seg 9");
                Thread.Sleep(2000);
                filas.Add(new("f  tono nieto, incluir árbol del abuelo", Medir(abuelo.Id, false, ct), 440, 880));
                Cerrar(abuelo);
            }
        }
        finally
        {
            foreach (var h in Hijos.ToArray()) Cerrar(h);
            Restauracion.Ejecutar();
        }

        Imprimir(filas, notas);
        var finalMute = endpoint.AudioEndpointVolume.Mute;
        var finalVol = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        Console.WriteLine($"Estado final: endpoint mute={finalMute}, volumen={finalVol:F2} (inicial {muteOriginal}/{volOriginal:F2}) -> {(finalMute == muteOriginal && Math.Abs(finalVol - volOriginal) < 0.005 ? "restaurado" : "REVISAR")}");
        return 0;
    }

    private static void Cerrar(System.Diagnostics.Process p)
    {
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(2000); } } catch { /* ya salió */ }
    }

    private static void Imprimir(List<Fila> filas, List<string> notas)
    {
        Console.WriteLine();
        Console.WriteLine($"{"caso",-40} {"llega",-5} {"paq",5} {"sil",4} {"disc",4} {"rms dBFS",9} {"freq Hz",8} {"a440 dB",8} {"a880 dB",8} {"sep dB",7} {"edad ms",8}");
        foreach (var f in filas)
        {
            var r = f.R;
            if (r.Error is not null)
            {
                Console.WriteLine($"{f.Caso,-40} ERROR {r.Error}");
                continue;
            }
            var sep = "-";
            if (r.Llega && f.Esperada is double e && f.Otra is double o)
            {
                var aE = e == 440 ? r.Amp440Db : r.Amp880Db;
                var aO = o == 440 ? r.Amp440Db : r.Amp880Db;
                sep = (aO - aE).ToString("F1");
            }
            Console.WriteLine($"{f.Caso,-40} {(r.Llega ? "si" : "no"),-5} {r.Paquetes,5} {r.Silenciosos,4} {r.Discontinuidades,4} {r.RmsDbfs,9:F1} {(r.Llega ? r.FrecHz.ToString("F0") : "-"),8} {r.Amp440Db,8:F1} {r.Amp880Db,8:F1} {sep,7} {(r.EdadMsMedia > 0 ? r.EdadMsMedia.ToString("F1") : "-"),8}");
        }
        Console.WriteLine("sep dB: energía de la frecuencia ajena menos la esperada (más negativo = mejor separada). edad ms: tiempo entre el QPC del paquete y su lectura (no es la latencia hasta el altavoz).");
        foreach (var n in notas) Console.WriteLine(n);
    }
}
