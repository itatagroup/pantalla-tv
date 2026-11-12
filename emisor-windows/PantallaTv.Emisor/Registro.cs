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

    /// <summary>Marca de las líneas de error: <see cref="Errores"/> las busca por ella.</summary>
    internal const string MarcaError = "ERROR ";

    /// <summary>
    /// Un error con todo su detalle (tipo, mensaje, causa y pila), para que el diagnóstico lo muestre completo. Las
    /// líneas de la pila van con sangría, así se sabe dónde termina cada error.
    /// </summary>
    public static void Error(string contexto, Exception? e = null)
    {
        var texto = MarcaError + contexto;
        if (e is not null)
            texto += ": " + string.Join(Environment.NewLine + "    ", e.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Anotar(texto);
    }

    /// <summary>
    /// Los últimos <paramref name="maximo"/> errores (con su detalle), del registro actual y de la copia anterior, del
    /// más viejo al más nuevo.
    /// </summary>
    public static IReadOnlyList<string> Errores(int maximo = 30, string? archivoRegistro = null)
    {
        if ((archivoRegistro ?? Ruta) is not { } ruta) return [];
        var errores = new List<string>();
        try
        {
            lock (Cerrojo)
            {
                foreach (var archivo in new[] { Path.ChangeExtension(ruta, ".1.txt"), ruta })
                {
                    if (!File.Exists(archivo)) continue;
                    System.Text.StringBuilder? actual = null;
                    foreach (var linea in File.ReadLines(archivo))
                    {
                        if (linea.Contains("  " + MarcaError, StringComparison.Ordinal))
                        {
                            if (actual is not null) errores.Add(actual.ToString());
                            actual = new System.Text.StringBuilder(linea);
                        }
                        else if (actual is not null && linea.StartsWith("    ", StringComparison.Ordinal))
                        {
                            actual.AppendLine().Append(linea);
                        }
                        else if (actual is not null)
                        {
                            errores.Add(actual.ToString());
                            actual = null;
                        }
                    }
                    if (actual is not null) errores.Add(actual.ToString());
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // sin registro no hay errores que mostrar
        }
        return errores.TakeLast(maximo).ToList();
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
