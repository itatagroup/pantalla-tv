using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PantallaTv.Audio.Prueba;

/// <summary>Captura en bucle (loopback) de TODO lo que suena en un endpoint, con la hora de llegada de cada trozo.</summary>
internal sealed class BucleEndpoint : IDisposable
{
    private readonly MMDevice _disp;
    private readonly WasapiLoopbackCapture _cap;
    private readonly Stopwatch _reloj = Stopwatch.StartNew();
    private readonly List<(double T, float[] Mono)> _trozos = [];
    private readonly object _cerrojo = new();
    public int Frecuencia { get; }
    public string? Error { get; private set; }

    public BucleEndpoint(MMDeviceEnumerator en, string id)
    {
        _disp = en.GetDevice(id);
        _cap = new WasapiLoopbackCapture(_disp);
        Frecuencia = _cap.WaveFormat.SampleRate;
        var canales = _cap.WaveFormat.Channels;
        var float32 = _cap.WaveFormat.BitsPerSample == 32;
        if (!float32) Error = $"formato no flotante de 32 bits ({_cap.WaveFormat})";
        _cap.DataAvailable += (_, e) =>
        {
            if (!float32 || e.BytesRecorded == 0) return;
            var cuadros = e.BytesRecorded / (4 * canales);
            var mono = new float[cuadros];
            for (var i = 0; i < cuadros; i++)
            {
                var suma = 0f;
                for (var c = 0; c < canales; c++) suma += BitConverter.ToSingle(e.Buffer, (i * canales + c) * 4);
                mono[i] = suma / canales;
            }
            lock (_cerrojo) _trozos.Add((_reloj.Elapsed.TotalSeconds, mono));
        };
        _cap.StartRecording();
    }

    public double Ahora => _reloj.Elapsed.TotalSeconds;

    public void Detener() { try { _cap.StopRecording(); } catch { /* ya parado */ } }

    /// <summary>Resultado de lo llegado entre t0 y t1 s desde que se abrió (por hora de llegada).</summary>
    public ResultadoCaptura Ventana(double t0, double t1)
    {
        var muestras = new List<float>();
        long paq = 0;
        lock (_cerrojo)
            foreach (var (t, m) in _trozos)
                if (t >= t0 && t < t1) { muestras.AddRange(m); paq++; }
        var x = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(muestras);
        var rms = Analisis.RmsDbfs(x);
        var hz = rms > -70 ? Analisis.FrecuenciaDominante(x, Frecuencia).Hz : 0;
        return new ResultadoCaptura(0, false, t1 - t0, paq, muestras.Count, 0, 0, rms, hz,
            Analisis.ADb(Analisis.Amplitud(x, 440, Frecuencia)), Analisis.ADb(Analisis.Amplitud(x, 880, Frecuencia)), 0, 0, Error);
    }

    public void Dispose() { Detener(); _cap.Dispose(); _disp.Dispose(); }
}

/// <summary>Casos g-k: mandar un proceso a otra salida y capturarlo por proceso; ver si el cambio es en caliente.</summary>
internal static class BateriaRuta
{
    private const int SegTono = 14;

    private static Process Tono(double hz, int retrasoMs, bool comoDotnet, int reabrirMs = 0)
    {
        var args = $"--tono {hz.ToString(CultureInfo.InvariantCulture)} --seg {SegTono} --amplitud 0.05 --retraso-ms {retrasoMs} --reabrir-ms {reabrirMs}";
        var info = comoDotnet
            // dotnet.exe es otro ejecutable: sirve para ver si la ruta es por PID o por .exe.
            ? new ProcessStartInfo("dotnet", $"\"{Path.ChangeExtension(Environment.ProcessPath!, ".dll")}\" {args}")
            : new ProcessStartInfo(Environment.ProcessPath!, args);
        info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true;
        var p = Process.Start(info)!;
        lock (Bateria.Hijos) Bateria.Hijos.Add(p);
        return p;
    }

    public static int Correr(string? dispositivo, CancellationToken ct)
    {
        var lista = Dispositivos.Listar();
        InfoDispositivo? segunda;
        if (dispositivo is not null)
        {
            var (e, err) = Dispositivos.Elegir(lista, dispositivo);
            if (e is null) { Console.WriteLine(err); return 2; }
            segunda = e;
        }
        else segunda = Dispositivos.SegundaSalida(lista);
        var defecto = lista.FirstOrDefault(d => d.EsDefecto);
        if (segunda is null || defecto is null || segunda.Id == defecto.Id)
        {
            Console.WriteLine("sin segunda salida: casos omitidos");
            return 0;
        }
        Console.WriteLine($"Salida por defecto: {defecto.Nombre}");
        Console.WriteLine($"Segunda salida:     {segunda.Nombre}");
        Console.WriteLine("Cada caso dura unos 7 s (tonos de amplitud 0,05). Solo se fija la ruta de los PID de prueba; se restaura al final de cada caso.");

        var filas = new List<Bateria.Fila>();
        var notas = new List<string>();
        var usados = new List<int>();
        using var en = new MMDeviceEnumerator();
        string? fijado = null;
        try
        {
            // h0. Control: sin ruta, el bucle del endpoint por defecto SÍ debe ver el 440.
            {
                using var t = Tono(440, 0, false); usados.Add(t.Id);
                using var bd = new BucleEndpoint(en, defecto.Id);
                Thread.Sleep(2500);
                var t0 = bd.Ahora;
                Thread.Sleep(3000);
                filas.Add(new("h0 control: 440 sin ruta, bucle defecto", bd.Ventana(t0, bd.Ahora), 440, 880));
                Bateria.Cerrar(t);
            }
            ct.ThrowIfCancellationRequested();

            // g y h. 440 a la segunda salida desde el primer momento (el tono abre su stream 1,5 s después de la ruta).
            {
                using var t = Tono(440, 1500, false); usados.Add(t.Id);
                fijado = RutaPorApp.Fijar(t.Id, segunda.Id, "(g/h)");
                Console.WriteLine($"  ruta fijada con id: {fijado}");
                using var bd = new BucleEndpoint(en, defecto.Id);
                using var bs = new BucleEndpoint(en, segunda.Id);
                Thread.Sleep(2500);
                var t0 = bd.Ahora;
                var cap = Task.Run(() => Captura.Capturar(t.Id, false, 4, null, false, ct), ct);
                cap.Wait(ct);
                var t1 = bd.Ahora;
                filas.Add(new("g  440 a 2a salida, captura por PID", cap.Result, 440, 880));
                filas.Add(new("h  440 a 2a salida, bucle defecto", bd.Ventana(t0, t1), 440, 880));
                filas.Add(new("h2 440 a 2a salida, bucle 2a salida", bs.Ventana(t0, t1), 440, 880));
                Restauracion.Ejecutar();
                Bateria.Cerrar(t);
            }
            ct.ThrowIfCancellationRequested();

            // i. 440 a la segunda (AudioPrueba.exe) y 880 en la por defecto (dotnet.exe, otro ejecutable).
            {
                using var t1 = Tono(440, 1500, false); usados.Add(t1.Id);
                using var t2 = Tono(880, 0, true); usados.Add(t2.Id);
                RutaPorApp.Fijar(t1.Id, segunda.Id, "(i)");
                using var bd = new BucleEndpoint(en, defecto.Id);
                using var bs = new BucleEndpoint(en, segunda.Id);
                Thread.Sleep(2500);
                var t0 = bd.Ahora;
                var cap = Task.Run(() => Captura.Capturar(t1.Id, false, 4, null, false, ct), ct);
                cap.Wait(ct);
                var te = bd.Ahora;
                filas.Add(new("i1 dos tonos, captura PID del 440", cap.Result, 440, 880));
                filas.Add(new("i2 dos tonos, bucle defecto (880)", bd.Ventana(t0, te), 880, 440));
                filas.Add(new("i3 dos tonos, bucle 2a salida (440)", bs.Ventana(t0, te), 440, 880));
                Restauracion.Ejecutar();
                Bateria.Cerrar(t1); Bateria.Cerrar(t2);
            }
            ct.ThrowIfCancellationRequested();

            // i4. Misma pregunta con el MISMO ejecutable: ¿la ruta de un PID arrastra a otro proceso del mismo .exe (p. ej. Chrome)?
            {
                using var t1 = Tono(440, 1500, false); usados.Add(t1.Id);
                RutaPorApp.Fijar(t1.Id, segunda.Id, "(i4)");
                using var t3 = Tono(880, 1500, false); usados.Add(t3.Id); // lanzado DESPUÉS de fijar la ruta del primero
                using var bd = new BucleEndpoint(en, defecto.Id);
                using var bs = new BucleEndpoint(en, segunda.Id);
                Thread.Sleep(3500);
                var t0 = bd.Ahora;
                Thread.Sleep(3000);
                var te = bd.Ahora;
                filas.Add(new("i4 mismo .exe: bucle defecto (880 si es por PID)", bd.Ventana(t0, te), 880, 440));
                filas.Add(new("i5 mismo .exe: bucle 2a salida (440; 880 si es por .exe)", bs.Ventana(t0, te), 440, 880));
                Restauracion.Ejecutar();
                Bateria.Cerrar(t1); Bateria.Cerrar(t3);
            }
            ct.ThrowIfCancellationRequested();

            // j. Cambio con el tono ya sonando.
            {
                using var bd = new BucleEndpoint(en, defecto.Id);
                using var bs = new BucleEndpoint(en, segunda.Id);
                using var t = Tono(440, 0, false); usados.Add(t.Id);
                Thread.Sleep(2500);
                var tCambio = bd.Ahora;
                RutaPorApp.Fijar(t.Id, segunda.Id, "(j)");
                Thread.Sleep(3500);
                var tFin = bd.Ahora;
                filas.Add(new("j1 en caliente: bucle defecto, antes", bd.Ventana(1.2, tCambio - 0.1), 440, 880));
                filas.Add(new("j2 en caliente: bucle defecto, despues", bd.Ventana(tCambio + 1.2, tFin), 440, 880));
                filas.Add(new("j3 en caliente: bucle 2a salida, antes", bs.Ventana(1.2, tCambio - 0.1), 440, 880));
                filas.Add(new("j4 en caliente: bucle 2a salida, despues", bs.Ventana(tCambio + 1.2, tFin), 440, 880));
                Restauracion.Ejecutar();
                Bateria.Cerrar(t);
            }
            ct.ThrowIfCancellationRequested();

            // j5. El proceso ya sonaba, se fija la ruta y luego ABRE UN STREAM NUEVO (sin reiniciar el proceso).
            {
                using var bd = new BucleEndpoint(en, defecto.Id);
                using var bs = new BucleEndpoint(en, segunda.Id);
                using var t = Tono(440, 0, false, 5500); usados.Add(t.Id);
                Thread.Sleep(2500);
                RutaPorApp.Fijar(t.Id, segunda.Id, "(j5)");
                Thread.Sleep(5000); // el stream nuevo se abre a los ~5,5 s del tono
                var tFin = bd.Ahora;
                filas.Add(new("j5 stream nuevo: bucle defecto, despues", bd.Ventana(tFin - 2.0, tFin), 440, 880));
                filas.Add(new("j6 stream nuevo: bucle 2a salida, despues", bs.Ventana(tFin - 2.0, tFin), 440, 880));
                Restauracion.Ejecutar();
                Bateria.Cerrar(t);
            }
        }
        finally
        {
            Restauracion.Ejecutar();
            foreach (var h in Bateria.Hijos.ToArray()) Bateria.Cerrar(h);
        }

        Bateria.Imprimir(filas, notas);

        // k. Verificación final: nada de lo nuestro queda persistido (el propio proceso, un AudioPrueba.exe nuevo y un dotnet.exe nuevo).
        Console.WriteLine();
        Console.WriteLine("k  verificación de que no queda ninguna ruta persistida:");
        var limpio = true;
        using (var n1 = Tono(440, 0, false))
        using (var n2 = Tono(880, 0, true))
        {
            Thread.Sleep(500);
            foreach (var (nombre, pid) in new[] { ("este proceso (AudioPrueba.exe)", Environment.ProcessId), ("AudioPrueba.exe nuevo", n1.Id), ("dotnet.exe nuevo", n2.Id) }
                .Concat(usados.Select(p => ($"PID usado {p}", p))))
            {
                var mm = RutaPorApp.Leer(pid, RutaPorApp.Multimedia, out var h1);
                var co = RutaPorApp.Leer(pid, RutaPorApp.Console, out var h2);
                var vacio = mm.Length == 0 && co.Length == 0;
                if (!vacio) limpio = false;
                Console.WriteLine($"   {nombre,-34} PID {pid,6}  multimedia='{mm}' (hr 0x{h1:X8})  console='{co}' (hr 0x{h2:X8})  {(vacio ? "vacío" : "QUEDA RUTA")}");
            }
            Bateria.Cerrar(n1); Bateria.Cerrar(n2);
        }
        Console.WriteLine(limpio ? "k  resultado: todo vacío, nada persistido." : "k  resultado: REVISAR, quedó una ruta persistida.");
        Console.WriteLine($"Salida por defecto del sistema al terminar: {Dispositivos.Listar().FirstOrDefault(d => d.EsDefecto)?.Nombre} (al empezar: {defecto.Nombre})");
        return limpio ? 0 : 1;
    }
}
