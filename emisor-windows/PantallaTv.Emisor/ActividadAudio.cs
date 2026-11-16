using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PantallaTv.Emisor;

/// <summary>Quién está sonando ahora en el PC (una versión falsa en las pruebas).</summary>
internal interface IActividadAudio
{
    /// <summary>Los procesos que tienen una sesión de audio activa, es decir, que están sonando, en alguna salida del PC.</summary>
    IReadOnlySet<int> PidsSonando();
}

/// <summary>Qué programas, de una lista, están sonando (lógica pura; la parte de Windows es <see cref="ActividadAudioWindows"/>).</summary>
internal static class ProgramasSonando
{
    /// <summary>
    /// De <paramref name="exes"/> (rutas de .exe), los que tienen algún proceso en <paramref name="pidsSonando"/>. Se mira cada
    /// proceso del programa y no solo el principal: el audio de Edge o Chrome lo hace un proceso hijo.
    /// </summary>
    public static IReadOnlyList<string> Filtrar(IReadOnlyList<string> exes, IReadOnlyList<ProcesoInfo> procesos, IReadOnlySet<int> pidsSonando) =>
        [.. exes.Where(exe => procesos.Any(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase) && pidsSonando.Contains(p.Pid)))];
}

/// <summary>
/// Las sesiones de audio de Windows (WASAPI, <c>IAudioSessionManager2</c> de cada salida activa): una sesión «activa» es
/// una que está reproduciendo ahora. Se miran todas las salidas, porque un programa que acaba de pasar a la tele sigue
/// sonando por la que tenía, y uno que vuelve al PC suena todavía por VB-CABLE.
/// </summary>
internal sealed class ActividadAudioWindows : IActividadAudio
{
    public IReadOnlySet<int> PidsSonando()
    {
        var pids = new HashSet<int>();
        try
        {
            using var enumerador = new MMDeviceEnumerator();
            foreach (var dispositivo in enumerador.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dispositivo)
                {
                    try
                    {
                        var sesiones = dispositivo.AudioSessionManager.Sessions;
                        for (var i = 0; i < sesiones.Count; i++)
                        {
                            using var sesion = sesiones[i]; // RCW de COM: se libera al momento (el dispositivo libera su AudioSessionManager)
                            if (sesion.State == AudioSessionState.AudioSessionStateActive && sesion.GetProcessID != 0)
                                pids.Add((int)sesion.GetProcessID);
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException e)
                    {
                        Registro.Anotar($"audio exclusivo: no se pudieron leer las sesiones de una salida: {e.Message}");
                    }
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            Registro.Anotar($"audio exclusivo: no se pudo ver quién suena: {e.Message}");
        }
        return pids;
    }
}
