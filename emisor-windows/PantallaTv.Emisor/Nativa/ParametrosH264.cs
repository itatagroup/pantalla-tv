
namespace PantallaTv.Emisor.Nativa;

/// <summary>
/// Garantiza que cada cuadro clave (IDR) lleve delante su SPS y PPS, como exige el receptor para poder
/// empezar a decodificar en cualquier clave. Guarda los últimos vistos y los antepone si un IDR llega sin
/// ellos. Cuenta cuántas veces tuvo que hacerlo (dice si el codificador ya los repite solo).
/// </summary>
internal sealed class ParametrosH264
{
    private byte[]? _sps, _pps;

    /// <summary>IDR a los que hubo que anteponer SPS/PPS.</summary>
    public int Antepuestos { get; private set; }

    /// <summary>IDR que ya traían SPS y PPS.</summary>
    public int Completos { get; private set; }

    /// <summary>Devuelve la unidad de acceso lista para enviar y si es un cuadro clave.</summary>
    public (byte[] Unidad, bool Clave) Procesar(byte[] unidad)
    {
        bool tieneSps = false, tienePps = false, idr = false;
        foreach (var n in AnnexB.Nals(unidad))
        {
            switch (n.Tipo)
            {
                case AnnexB.Sps: _sps = unidad[n.Inicio..n.Fin]; tieneSps = true; break;
                case AnnexB.Pps: _pps = unidad[n.Inicio..n.Fin]; tienePps = true; break;
                case AnnexB.CuadroClave: idr = true; break;
            }
        }
        if (!idr) return (unidad, false);
        if (tieneSps && tienePps) { Completos++; return (unidad, true); }
        // Solo se antepone si faltan los dos (si falta uno, ponerlo delante dejaría el PPS antes del SPS).
        if (tieneSps || tienePps || _sps is null || _pps is null) return (unidad, true);
        Antepuestos++;
        return ([.. _sps, .. _pps, .. unidad], true);
    }
}
