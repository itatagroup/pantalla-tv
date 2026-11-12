namespace PantallaTv.Emisor.Tests;

/// <summary>Lo que el diagnóstico saca del registro: los errores, cada uno con su detalle.</summary>
public sealed class RegistroTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-registro-").FullName;

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    [Fact]
    public void Errores_junta_cada_error_con_su_pila_y_deja_afuera_lo_demas()
    {
        var ruta = Path.Combine(_carpeta, "registro.txt");
        File.WriteAllLines(Path.ChangeExtension(ruta, ".1.txt"), [
            "2026-11-12 10:00:00.000  ERROR viejo: algo",
        ]);
        File.WriteAllLines(ruta, [
            "2026-11-12 10:00:01.000  codificador elegido: AMD",
            "2026-11-12 10:00:02.000  ERROR monitor virtual DISPLAY6: no apareció en 20 s",
            "2026-11-12 10:00:03.000  otra cosa",
            "2026-11-12 10:00:04.000  ERROR error inesperado al transmitir: System.InvalidOperationException: mal",
            "    at PantallaTv.Emisor.Emision.Algo()",
            "    at PantallaTv.Emisor.Emision.Otro()",
        ]);

        var errores = Registro.Errores(archivoRegistro: ruta);

        Assert.Equal(3, errores.Count);
        Assert.Contains("viejo", errores[0]); // primero la copia anterior
        Assert.Contains("DISPLAY6", errores[1]);
        Assert.DoesNotContain("otra cosa", errores[1]);
        Assert.Contains("InvalidOperationException", errores[2]);
        Assert.Contains("Emision.Otro()", errores[2]);
        Assert.Single(Registro.Errores(maximo: 1, archivoRegistro: ruta));
    }

    [Fact]
    public void Sin_registro_no_hay_errores() =>
        Assert.Empty(Registro.Errores(archivoRegistro: Path.Combine(_carpeta, "no-existe.txt")));
}
