namespace PantallaTv.Captura.Prueba;

/// <summary>Lo ocurrido en un segundo de medición.</summary>
internal readonly record struct Segundo(
    int Capturados, int Perdidos, int SoloRaton, int Codificados, int Claves, long Bytes, double CpuNucleos)
{
    public double Mbps => Bytes * 8 / 1_000_000.0;
}

/// <summary>Resumen de una corrida, sin el primer segundo (arranque) ni el último (parcial).</summary>
internal sealed record Resumen(
    int Segundos, double CapturadosMedio, int CapturadosMinimo, double CodificadosMedio, int CodificadosMinimo,
    double CpuMedia, double MbpsMedio, int Claves, int SoloRaton, int Perdidos);

/// <summary>
/// Cuenta por segundo cuadros capturados, cuadros que solo movieron el ratón, cuadros codificados, claves,
/// bytes y CPU. No lee el reloj ni el proceso: se le pasan los datos (así se puede probar).
/// </summary>
internal sealed class Medidor
{
    private readonly List<Segundo> _segundos = [];
    private int _capturados, _perdidos, _soloRaton, _codificados, _claves;
    private long _bytes;

    public IReadOnlyList<Segundo> Segundos => _segundos;

    /// <param name="acumulados">AccumulatedFrames de DXGI: más de 1 = presentaciones que la captura no alcanzó a recoger.</param>
    public void Capturado(int acumulados = 1)
    {
        _capturados++;
        _perdidos += Math.Max(0, acumulados - 1);
    }
    public void SoloRaton() => _soloRaton++;

    public void Codificado(int bytes, bool clave)
    {
        _codificados++;
        _bytes += bytes;
        if (clave) _claves++;
    }

    /// <summary>Cierra el segundo en curso con la CPU que gastó el proceso (en núcleos: 1,0 = un núcleo entero).</summary>
    public Segundo Cerrar(double cpuNucleos)
    {
        var s = new Segundo(_capturados, _perdidos, _soloRaton, _codificados, _claves, _bytes, cpuNucleos);
        _segundos.Add(s);
        _capturados = _perdidos = _soloRaton = _codificados = _claves = 0;
        _bytes = 0;
        return s;
    }

    public Resumen Resumir()
    {
        var todos = _segundos;
        var utiles = todos.Count > 3 ? todos.Skip(1).Take(todos.Count - 2).ToList() : todos.ToList();
        if (utiles.Count == 0) return new Resumen(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new Resumen(
            utiles.Count,
            Math.Round(utiles.Average(s => s.Capturados), 1),
            utiles.Min(s => s.Capturados),
            Math.Round(utiles.Average(s => s.Codificados), 1),
            utiles.Min(s => s.Codificados),
            Math.Round(utiles.Average(s => s.CpuNucleos), 3),
            Math.Round(utiles.Average(s => s.Mbps), 2),
            todos.Sum(s => s.Claves),
            todos.Sum(s => s.SoloRaton),
            utiles.Sum(s => s.Perdidos));
    }
}
