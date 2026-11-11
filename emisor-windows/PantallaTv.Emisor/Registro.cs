using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// Un registro de texto corto de lo que decide el emisor y no se ve en pantalla: qué codificador eligió, qué ajustes le
/// rechazó, por qué cayó a una tubería de respaldo. Sirve para cuando falla en un PC ajeno: <c>--diagnostico</c> lo
/// incluye. Vive en <c>%LOCALAPPDATA%\PantallaTv\registro.txt</c>; al pasar de <see cref="TamanoMax"/> se guarda como
/// <c>registro.1.txt</c> (solo esa copia) y se empieza otro.
/// </summary>
internal static class Registro
{
    internal const long TamanoMax = 512 * 1024;
    private static readonly object Cerrojo = new();

    /// <summary>null = no escribir (las pruebas lo apagan).</summary>
    internal static string? Ruta { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PantallaTv", "registro.txt");

    public static void Anotar(string texto)
    {
        Trace.WriteLine(texto);
        if (Ruta is not { } ruta) return;
        try
        {
            lock (Cerrojo)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
                if (File.Exists(ruta) && new FileInfo(ruta).Length > TamanoMax)
                    File.Move(ruta, Path.ChangeExtension(ruta, ".1.txt"), overwrite: true);
                File.AppendAllText(ruta, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {texto}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // sin registro se sigue igual
        }
    }

    /// <summary>Las últimas <paramref name="lineas"/> líneas, o nada si no hay registro.</summary>
    public static IReadOnlyList<string> Ultimas(int lineas)
    {
        if (Ruta is not { } ruta) return [];
        try
        {
            lock (Cerrojo) return File.Exists(ruta) ? File.ReadLines(ruta).TakeLast(lineas).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
