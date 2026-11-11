using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// Una transmisión que se recupera sola: si se corta (el Wi-Fi, la TV cerró la app, otro emisor la
/// ocupó) vuelve a intentarlo hasta que el usuario la detenga. Los fallos que necesitan que alguien
/// haga algo (falta ffmpeg, otra versión) terminan de inmediato, como en <see cref="Emision"/>.
/// </summary>
internal sealed class EmisionPersistente : IEmision
{
    /// <summary>Primera espera antes de reintentar; se duplica en cada fallo seguido hasta <see cref="EsperaMaxima"/>.</summary>
    internal TimeSpan EsperaInicial { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan EsperaMaxima { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Una transmisión que duró al menos esto cuenta como buena: la espera vuelve al mínimo.</summary>
    internal TimeSpan DuracionQueReinicia { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tras tantos fallos seguidos sin llegar a transmitir, se da por perdida (~10 min con los valores por defecto).</summary>
    internal int IntentosSeguidosMax { get; init; } = 120;

    /// <summary>Para las pruebas: cómo se crea cada intento.</summary>
    internal Func<Emision> Crear { get; init; } = () => new Emision();

    public event Action<EstadoEmision>? CambioDeEstado;
    public event Action<Estadisticas>? NuevasEstadisticas;

    public async Task EjecutarAsync(Receptor receptor, OpcionesEmision op, Telemetria? telemetria, CancellationToken ct)
    {
        var espera = EsperaInicial;
        var fallosSeguidos = 0;
        while (true)
        {
            var emision = Crear();
            // Los errores del intento no se muestran tal cual: aquí se decide si son un «reconectando».
            emision.CambioDeEstado += e => { if (e.Fase != Fase.Error) CambioDeEstado?.Invoke(e); };
            emision.NuevasEstadisticas += e => NuevasEstadisticas?.Invoke(e);

            var reloj = Stopwatch.StartNew();
            try
            {
                await emision.EjecutarAsync(receptor, op, telemetria, ct);
                return; // detenida por el usuario
            }
            catch (EmisionException e)
            {
                if (reloj.Elapsed >= DuracionQueReinicia)
                {
                    espera = EsperaInicial;
                    fallosSeguidos = 0;
                }
                fallosSeguidos++;
                if (!e.Reintentable || fallosSeguidos > IntentosSeguidosMax)
                {
                    CambioDeEstado?.Invoke(new EstadoEmision(Fase.Error, e.Message));
                    throw;
                }
                CambioDeEstado?.Invoke(new EstadoEmision(Fase.Reconectando, $"Reconectando… ({e.Message})"));
            }

            try
            {
                await Task.Delay(espera, ct);
            }
            catch (OperationCanceledException)
            {
                CambioDeEstado?.Invoke(new EstadoEmision(Fase.Inactivo, "Detenida"));
                return;
            }
            espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, EsperaMaxima.Ticks));
        }
    }
}
