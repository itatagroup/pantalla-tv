using System.Diagnostics;
using System.Globalization;
using PantallaTv.Captura.Prueba;
using PantallaTv.Emisor.Nativa;
using Vortice.Direct3D11;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
// DuplicateOutput1 devuelve DXGI_ERROR_UNSUPPORTED si el proceso no es «per monitor v2» (Sunshine hace lo mismo).
PantallaTv.Emisor.Nativa.Gpu.ConscienteDeDpi();
Opciones o;
try { o = Opciones.Leer(args); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Opciones.Uso);
    return 2;
}

var salidas = ListaMonitores.Salidas();
if (o.Listar)
{
    foreach (var s in salidas)
        Console.WriteLine($"{s.Indice}: {s.Dispositivo} · {s.Monitor} · {s.Ancho}×{s.Alto} en ({s.X},{s.Y}) · adaptador {s.Adaptador} «{s.NombreAdaptador}», salida {s.Salida}");
    return 0;
}

var elegida = ListaMonitores.Elegir(salidas, o.Monitor);
if (o.Fuente) return Fuente60.Ejecutar(elegida, o.Segundos);
var (adaptador, salida) = PantallaTv.Emisor.Nativa.Gpu.Buscar(elegida.Dispositivo, 0, 0)
    ?? throw new InvalidOperationException($"{elegida.Dispositivo} desapareció");
var (dev, ctx) = PantallaTv.Emisor.Nativa.Gpu.Abrir(adaptador);
using var _a = adaptador; using var _s = salida; using var _d = dev; using var _c = ctx;
using var captura = new CapturaEscritorio(salida, dev, ctx, cursor: null);
using var imagen = captura.Vista.Resource.QueryInterface<ID3D11Texture2D>(); // sin puntero, la vista es siempre la misma
using var sombreador = o.Conversion == Conversion.Sombreador ? new ConversorSombreador(dev, ctx, captura.Ancho, captura.Alto, MatrizColor.Bt601) : null;
using var vp = o.Conversion == Conversion.ProcesadorVideo ? new ConversorProcesadorVideo(dev, ctx, imagen, captura.Ancho, captura.Alto, o.Fps) : null;
void Convertir(DestinoNv12 d)
{
    if (sombreador is not null) sombreador.Convertir(captura.Vista, captura.Ancho, captura.Alto, d);
    else vp!.Convertir(d);
}
using var archivo = o.Salida is null ? null : File.Create(o.Salida);
var parametros = new ParametrosH264();
var medidor = new Medidor();
var latencias = new List<double>();
var entregados = new System.Collections.Concurrent.ConcurrentDictionary<long, long>(); // tiempo → cuándo se entregó (ticks)
using ICodificador codificador = o.Codificador switch
{
    Candidato.MediaFoundation => new CodificadorMf(dev, captura.Ancho, captura.Alto, o.Fps, o.Kbps, o.Tasa == ModoTasa.Vbr, AlSalir),
    Candidato.Libavcodec => new CodificadorAv(dev, captura.Ancho, captura.Alto, o, AlSalir),
    _ => new SinCodificador(dev, captura.Ancho, captura.Alto),
};

Console.WriteLine($"Monitor: {elegida.Dispositivo} · {elegida.Monitor} · {captura.Ancho}×{captura.Alto}");
Console.WriteLine($"Conversión: {o.Conversion} · Codificador: {codificador.Descripcion}");
Console.WriteLine($"Latido: {(o.LatidoFps > 0 ? $"{o.LatidoFps} fps con la pantalla quieta" : "no")} · {o.Segundos} s");

var proceso = Process.GetCurrentProcess();
var reloj = Stopwatch.StartNew();
var cpuAntes = proceso.TotalProcessorTime;
var finSegundo = 1000L;
var latidoMs = o.LatidoFps > 0 ? 1000.0 / o.LatidoFps : double.MaxValue;
DestinoNv12? ultimo = null;
var ultimaCodificacion = double.MinValue;
var claveSiguiente = 0L; // ms en que toca pedir clave; el primer cuadro siempre es clave
var clavesPedidas = 0;
long ultimoTiempo = -1;

// Con Media Foundation llega desde el hilo del codificador: se serializa.
void AlSalir(byte[] unidad, long tiempo)
{
    lock (medidor)
    {
        var (u, clave) = parametros.Procesar(unidad);
        medidor.Codificado(u.Length, clave);
        archivo?.Write(u);
        if (entregados.TryRemove(tiempo, out var cuando)) latencias.Add(Stopwatch.GetElapsedTime(cuando).TotalMilliseconds);
    }
}

void Enviar(DestinoNv12 d)
{
    var ahora = reloj.Elapsed.TotalMilliseconds;
    var tiempo = Math.Max(ultimoTiempo + 1, (long)(ahora * 10_000));
    ultimoTiempo = tiempo;
    var clave = claveSiguiente >= 0 && ahora >= claveSiguiente;
    if (clave)
    {
        clavesPedidas++;
        claveSiguiente = o.ClaveCadaSeg > 0 ? (long)ahora + o.ClaveCadaSeg * 1000L : -1;
    }
    entregados[tiempo] = Stopwatch.GetTimestamp();
    codificador.Codificar(d, tiempo, clave);
    ultimo = d;
    ultimaCodificacion = ahora;
}

Console.WriteLine("seg  capt  ratón  codif  claves  Mb/s   CPU(núcleos)");
while (reloj.ElapsedMilliseconds < o.Segundos * 1000L)
{
    var ahora = reloj.Elapsed.TotalMilliseconds;
    var hastaLatido = ultimo is null ? 100 : ultimaCodificacion + latidoMs - ahora;
    var espera = (int)Math.Clamp(Math.Min(hastaLatido, finSegundo - ahora), 0, 100);
    var r = captura.Esperar(espera);
    switch (r)
    {
        case CapturaEscritorio.Resultado.Nuevo:
            medidor.Capturado(captura.Acumulados);
            var d = codificador.SiguienteDestino();
            Convertir(d);
            Enviar(d);
            break;
        case CapturaEscritorio.Resultado.SoloRaton:
            // Aquí no se dibuja el puntero: un movimiento del ratón no cambia la imagen y no se codifica.
            medidor.SoloRaton();
            break;
    }
    if (r != CapturaEscritorio.Resultado.Nuevo && ultimo is { } u
        && reloj.Elapsed.TotalMilliseconds - ultimaCodificacion >= latidoMs)
        Enviar(u); // Pantalla quieta: se recodifica el último cuadro (P casi vacío), como Sunshine

    if (reloj.ElapsedMilliseconds >= finSegundo)
    {
        var cpu = proceso.TotalProcessorTime;
        Segundo s;
        lock (medidor) s = medidor.Cerrar((cpu - cpuAntes).TotalSeconds); // en un segundo de reloj: núcleos usados
        cpuAntes = cpu;
        Console.WriteLine($"{finSegundo / 1000,3}  {s.Capturados,4}  {s.SoloRaton,5}  {s.Codificados,5}  {s.Claves,6}  {s.Mbps,5:F2}  {s.CpuNucleos,6:F3}");
        finSegundo += 1000;
    }
}
codificador.Vaciar();

var res = medidor.Resumir();
Console.WriteLine();
Console.WriteLine($"RESUMEN ({res.Segundos} s útiles, sin el primero ni el último): capturados {res.CapturadosMedio} medio / {res.CapturadosMinimo} mín · " +
    $"codificados {res.CodificadosMedio} medio / {res.CodificadosMinimo} mín · CPU {res.CpuMedia:F3} núcleos · {res.MbpsMedio:F2} Mb/s · " +
    $"solo ratón {res.SoloRaton} · perdidos por la captura {res.Perdidos}");
Console.WriteLine($"Claves: pedidas {clavesPedidas}, salidas {res.Claves} · IDR con SPS/PPS propios {parametros.Completos}, " +
    $"con SPS/PPS antepuestos {parametros.Antepuestos}");
if (latencias.Count > 0)
{
    latencias.Sort();
    Console.WriteLine($"Latencia de conversión y codificación (entregado → unidad lista): media {latencias.Average():F2} ms, " +
        $"p95 {latencias[(int)(latencias.Count * 0.95)]:F2} ms, máx {latencias[^1]:F2} ms (n={latencias.Count})");
}
return 0;
