namespace PantallaTv.Emisor.Nativa;

/// <summary>Una gráfica del PC, con lo que hace falta para decidir dónde codificar. Sin tipos de DirectX: se prueba sin GPU.</summary>
/// <param name="Luid">El LUID del adaptador (alto y bajo en un solo número): lo que lo identifica entre DXGI y Media Foundation.</param>
internal sealed record AdaptadorGpu(long Luid, string Nombre, uint VendorId, long MemoriaDedicada, bool PorSoftware = false)
{
    public override string ToString() => $"{Nombre} (VEN_{VendorId:X4}, {MemoriaDedicada / 1048576} MB)";
}

/// <summary>Por dónde se codifica un intento del plan.</summary>
internal enum ViaCodificacion
{
    /// <summary>Con un codificador de la gráfica que dibuja el monitor, abierto sobre el mismo dispositivo D3D11: sin copias.</summary>
    MismaGpu,
    /// <summary>Con un codificador de otra gráfica: el cuadro convertido se copia a ella.</summary>
    CopiaEntreGpus,
    /// <summary>Ya no queda nada de la tubería nativa: sigue ffmpeg (si está).</summary>
    RespaldoFfmpeg,
}

/// <summary>Un paso del plan: con qué gráfica se codifica y cómo le llega el cuadro.</summary>
internal sealed record IntentoCodificador(ViaCodificacion Via, AdaptadorGpu? Codifica)
{
    public override string ToString() => Via switch
    {
        ViaCodificacion.MismaGpu => $"codificar en la misma gráfica que dibuja el monitor ({Codifica})",
        ViaCodificacion.CopiaEntreGpus => $"copiar el cuadro a otra gráfica y codificar allí ({Codifica})",
        _ => "respaldo con ffmpeg",
    };
}

/// <summary>
/// El orden en que se prueba dónde codificar el monitor capturado. En un portátil con dos gráficas (una integrada y una
/// dedicada) el monitor lo dibuja una y la MFT de la otra no lee sus texturas. Orden:
/// <list type="number">
/// <item>La gráfica dueña de la salida, sobre su propio dispositivo (siempre se prueba, aunque no se sepa de ningún codificador:
/// Windows puede no listarlos y aun así servir).</item>
/// <item>Cada otra gráfica con codificador de hardware, copiando allí el cuadro: primero la de más memoria de vídeo (la
/// dedicada). Las de software («Microsoft Basic Render») no cuentan, ni las que se llaman como la dueña (DXGI las lista
/// dos veces), y las que se repiten por nombre cuentan una vez.</item>
/// <item>El respaldo con ffmpeg (lo hace quien llama: la tubería nativa lanza <see cref="NativaNoDisponibleException"/>).</item>
/// </list>
/// Es una función pura: recibe lo que se sabe de las gráficas y devuelve el orden; no abre nada.
/// </summary>
internal static class PlanCodificador
{
    /// <param name="dueno">La gráfica a la que está conectada la salida capturada.</param>
    /// <param name="adaptadores">Todas las gráficas del PC (puede incluir a <paramref name="dueno"/>).</param>
    /// <param name="cuantosCodificadores">Cuántos codificadores H.264 de hardware tiene una gráfica (<c>MFTEnum2</c> por su LUID).</param>
    public static IReadOnlyList<IntentoCodificador> Ordenar(AdaptadorGpu dueno, IReadOnlyList<AdaptadorGpu> adaptadores,
        Func<AdaptadorGpu, int> cuantosCodificadores)
    {
        var plan = new List<IntentoCodificador> { new(ViaCodificacion.MismaGpu, dueno) };
        // DXGI puede listar la misma gráfica dos veces (dos LUID, mismo nombre): copiar a «la otra» NVIDIA, que es la misma,
        // sería un intento inútil que siempre falla igual. Se quita la que se llama como la dueña y se deja una por nombre.
        plan.AddRange(adaptadores
            .Where(a => a.Luid != dueno.Luid && !a.PorSoftware
                        && !string.Equals(a.Nombre.Trim(), dueno.Nombre.Trim(), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.Luid)
            .OrderByDescending(a => a.MemoriaDedicada)
            .ThenBy(a => a.Luid)
            .DistinctBy(a => a.Nombre.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(a => cuantosCodificadores(a) > 0)
            .Select(a => new IntentoCodificador(ViaCodificacion.CopiaEntreGpus, a)));
        plan.Add(new IntentoCodificador(ViaCodificacion.RespaldoFfmpeg, null));
        return plan;
    }
}
