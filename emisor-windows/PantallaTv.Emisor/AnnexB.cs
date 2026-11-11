namespace PantallaTv.Emisor;

/// <summary>Utilidades para H.264 en formato Annex B (NAL separados por 00 00 01 o 00 00 00 01).</summary>
internal static class AnnexB
{
    public const int Sps = 7;
    public const int Pps = 8;
    public const int Aud = 9;
    public const int CuadroClave = 5;
    public const int CuadroNoClave = 1;

    /// <summary>Un NAL: rango [Inicio, Fin) con su prefijo de inicio incluido, y su tipo.</summary>
    public readonly record struct Nal(int Inicio, int Fin, int Tipo);

    public static List<Nal> Nals(ReadOnlySpan<byte> d)
    {
        var inicios = new List<int>();
        for (var i = 0; i + 2 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1) continue;
            inicios.Add(i > 0 && d[i - 1] == 0 ? i - 1 : i);
            i += 2;
        }

        var r = new List<Nal>(inicios.Count);
        for (var k = 0; k < inicios.Count; k++)
        {
            var fin = k + 1 < inicios.Count ? inicios[k + 1] : d.Length;
            r.Add(new Nal(inicios[k], fin, TipoEn(d, inicios[k])));
        }
        return r;
    }

    private static int TipoEn(ReadOnlySpan<byte> d, int inicio)
    {
        var p = inicio;
        while (p < d.Length && d[p] == 0) p++;
        return p + 1 < d.Length ? d[p + 1] & 0x1F : -1;
    }

    /// <summary>¿Hay al menos un cuadro de vídeo (no solo AUD/SPS/PPS)?</summary>
    public static bool TieneVideo(ReadOnlySpan<byte> d)
    {
        foreach (var n in Nals(d))
            if (n.Tipo is CuadroClave or CuadroNoClave) return true;
        return false;
    }

    /// <summary>Posiciones donde empieza cada unidad de acceso (marca AUD, con su 00 previo si lo hay).</summary>
    public static List<int> IniciosDeUnidad(ReadOnlySpan<byte> d)
    {
        var r = new List<int>();
        for (var i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1 && (d[i + 3] & 0x1F) == Aud)
                r.Add(i > 0 && d[i - 1] == 0 ? i - 1 : i);
        }
        return r;
    }
}

/// <summary>
/// Convierte el flujo de bytes de ffmpeg en unidades de acceso completas (un cuadro cada una).
/// Un trozo sin cuadro de vídeo (solo AUD/SPS/PPS) se une al siguiente, porque el codificador puede
/// emitir dos marcas AUD por cuadro.
/// </summary>
internal sealed class SeparadorUnidades
{
    /// <summary>Lo máximo que se guarda sin haber visto ninguna marca AUD.</summary>
    internal const int MaxSinMarca = 16 * 1024 * 1024;

    private byte[] _buf = new byte[1 << 20];
    private int _largo;
    private byte[]? _acarreo;

    public void Agregar(ReadOnlySpan<byte> datos, Action<byte[]> alCompletar)
    {
        if (_largo + datos.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _largo + datos.Length));
        datos.CopyTo(_buf.AsSpan(_largo));
        _largo += datos.Length;

        var inicios = AnnexB.IniciosDeUnidad(_buf.AsSpan(0, _largo));
        if (inicios.Count == 0)
        {
            // Todavía no hay ninguna marca AUD: lo que haya antes de la primera no sirve. Si el flujo
            // nunca las trae, no se acumula sin fin: se conserva solo la cola (una marca partida en dos).
            if (_largo > MaxSinMarca)
            {
                Buffer.BlockCopy(_buf, _largo - 4, _buf, 0, 4);
                _largo = 4;
            }
            return;
        }

        for (var k = 0; k + 1 < inicios.Count; k++)
        {
            var au = _buf.AsSpan(inicios[k], inicios[k + 1] - inicios[k]).ToArray();
            if (_acarreo is not null)
            {
                au = [.. _acarreo, .. au];
                _acarreo = null;
            }
            if (!AnnexB.TieneVideo(au))
            {
                _acarreo = au;
                continue;
            }
            alCompletar(au);
        }

        var resto = _largo - inicios[^1];
        Buffer.BlockCopy(_buf, inicios[^1], _buf, 0, resto);
        _largo = resto;
    }
}
