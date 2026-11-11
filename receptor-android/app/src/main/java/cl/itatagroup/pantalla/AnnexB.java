package cl.itatagroup.pantalla;

import java.util.ArrayList;
import java.util.List;

/** Utilidades para flujos H.264 en formato Annex B (NAL separados por 00 00 01 / 00 00 00 01). */
final class AnnexB {
    private AnnexB() {}

    /** Rangos [inicio, fin) de cada NAL, con su prefijo de inicio incluido. */
    static List<int[]> nals(byte[] d) {
        List<int[]> out = new ArrayList<>();
        int start = -1;
        int i = 0;
        while (i + 2 < d.length) {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) {
                int prefijo = (i > 0 && d[i - 1] == 0) ? i - 1 : i;
                if (start >= 0) out.add(new int[] {start, prefijo});
                start = prefijo;
                i += 3;
            } else {
                i++;
            }
        }
        if (start >= 0) out.add(new int[] {start, d.length});
        return out;
    }

    /** Tipo de NAL (7 = SPS, 8 = PPS, 5 = fotograma clave). */
    static int tipo(byte[] d, int inicio) {
        int p = inicio;
        while (p < d.length && d[p] == 0) p++;
        return (p + 1 < d.length) ? (d[p + 1] & 0x1F) : -1;
    }

    /** Copia el NAL con un prefijo de 4 bytes (00 00 00 01), que es lo que espera MediaCodec. */
    static byte[] conPrefijo4(byte[] d, int inicio, int fin) {
        int p = inicio;
        while (p < fin && d[p] == 0) p++;
        // p apunta al 01 del prefijo; el NAL empieza en p + 1.
        int largo = fin - (p + 1);
        byte[] r = new byte[4 + largo];
        r[3] = 1;
        System.arraycopy(d, p + 1, r, 4, largo);
        return r;
    }
}
