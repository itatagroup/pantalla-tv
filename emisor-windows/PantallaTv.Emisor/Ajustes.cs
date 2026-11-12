using System.Text.Json;
using System.Text.Json.Serialization;

namespace PantallaTv.Emisor;

/// <summary>Preferencias del usuario, guardadas en %AppData%\PantallaTv\ajustes.json.</summary>
internal sealed class Ajustes
{
    public string? UltimaIp { get; set; }
    public int Fps { get; set; } = 60;
    public int Kbps { get; set; } = 8000;
    public bool GuardarTelemetria { get; set; }
    public bool ConAudio { get; set; } = true;
    public bool SilenciarPc { get; set; } = true;
    /// <summary>Nombre de Windows del monitor a transmitir (<c>\\.\DISPLAY2</c>); null = el principal.</summary>
    public string? MonitorNombre { get; set; }
    /// <summary>Forma de capturar elegida a mano (una de <see cref="Tuberias.Todas"/>); null = automática (la nativa y, si falla, ffmpeg).</summary>
    public string? Tuberia { get; set; }
    /// <summary>
    /// Modo del monitor virtual elegido a mano, «1920x1080@60». Null: automático, según la pantalla que informa la tele
    /// (receptor 0.8.0 o posterior) o, si no informa, 1080p a 60 Hz.
    /// </summary>
    public string? ModoMonitorVirtual { get; set; }
    /// <summary>Si se corta la conexión, volver a intentarlo solo hasta que el usuario detenga.</summary>
    public bool Reconectar { get; set; } = true;
    /// <summary>Ya se avisó una vez que cerrar la ventana deja el programa en la bandeja.</summary>
    public bool AvisoBandejaVisto { get; set; }

    /// <summary>Dónde se guarda este juego de ajustes (no va dentro del archivo).</summary>
    [JsonIgnore]
    public string Ruta { get; private set; } = RutaPorDefecto;

    /// <summary>
    /// Los ajustes normales, o una carpeta aparte con <see cref="Arranque.Perfil"/>: así una copia de
    /// desarrollo no pisa los ajustes del programa instalado. (Archivo: <c>ajustes.json</c> en %AppData%\PantallaTv.)
    /// </summary>
    public static string RutaPorDefecto => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PantallaTv",
        Arranque.Perfil is { } perfil ? $"perfil-{perfil}" : "", "ajustes.json");

    public static string CarpetaTelemetria => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PantallaTv", "telemetria");

    public static Ajustes Cargar(string? ruta = null)
    {
        ruta ??= RutaPorDefecto;
        Ajustes ajustes;
        try
        {
            ajustes = File.Exists(ruta)
                ? JsonSerializer.Deserialize<Ajustes>(File.ReadAllText(ruta)) ?? new Ajustes()
                : new Ajustes();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            ajustes = new Ajustes(); // un archivo dañado no debe impedir abrir el programa
        }
        ajustes.Ruta = ruta;
        return ajustes;
    }

    public void Guardar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
            File.WriteAllText(Ruta, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // sin permiso para guardar: se sigue con lo que hay en memoria
        }
    }
}
