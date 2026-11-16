using System.Collections.Concurrent;
using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// H3. Los textos de los avisos del sonido separado de la tele y el nombre con el que se nombra a cada programa. Los textos van en
/// plural cuando son varios programas a la vez, en un solo aviso.
/// </summary>
internal sealed class AvisosAudio
{
    private readonly Func<string, string?> _descripcion;
    private readonly ConcurrentDictionary<string, string> _nombres = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="descripcion">La descripción del archivo (<c>FileDescription</c>); null o vacío si no hay o falla.</param>
    public AvisosAudio(Func<string, string?>? descripcion = null) => _descripcion = descripcion ?? DescripcionDelArchivo;

    private static string? DescripcionDelArchivo(string ruta) => FileVersionInfo.GetVersionInfo(ruta).FileDescription;

    /// <summary>
    /// «Microsoft Edge» en vez de «msedge»: la descripción del .exe; si está vacía o no se puede leer, el nombre del archivo sin
    /// «.exe». Se recuerda por ruta (leer el archivo cuesta).
    /// </summary>
    public string Nombre(string exe) => _nombres.GetOrAdd(exe, ruta =>
    {
        string? texto = null;
        try { texto = _descripcion(ruta); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // el programa ya no está o no se puede leer: queda el nombre del archivo
        }
        return string.IsNullOrWhiteSpace(texto) ? DiferenciaApps.Nombre(ruta) : texto.Trim();
    });

    /// <summary>«A», «A y B», «A, B y C».</summary>
    public static string Unir(IReadOnlyList<string> nombres) => nombres.Count switch
    {
        0 => "",
        1 => nombres[0],
        _ => string.Join(", ", nombres.Take(nombres.Count - 1)) + " y " + nombres[^1],
    };

    private string Nombres(IReadOnlyCollection<string> exes) => Unir([.. exes.Select(Nombre).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)]);

    public string PasaronATele(IReadOnlyCollection<string> exes) => exes.Count == 1
        ? $"{Nombres(exes)} pasó a la tele: pausa y vuelve a reproducir para que suene allá."
        : $"{Nombres(exes)} pasaron a la tele: pausa y vuelve a reproducir para que suenen allá.";

    public string VolvieronAlPc(IReadOnlyCollection<string> exes) => exes.Count == 1
        ? $"{Nombres(exes)} volvió a tu pantalla: pausa y vuelve a reproducir para que suene en el PC."
        : $"{Nombres(exes)} volvieron a tu pantalla: pausa y vuelve a reproducir para que suenen en el PC.";

    /// <summary>Sin punto final: va en la nota de la fila y, con un punto, en el aviso.</summary>
    public string TambienEnPc(IReadOnlyCollection<string> exes) => exes.Count == 1
        ? $"{Nombres(exes)} también tiene ventanas en tu pantalla: su sonido sale por la tele"
        : $"{Nombres(exes)} también tienen ventanas en tu pantalla: su sonido sale por la tele";
}
