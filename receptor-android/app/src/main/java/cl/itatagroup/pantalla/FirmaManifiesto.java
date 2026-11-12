// Copia en launcher-tv/app/src/main/java/cl/itatagroup/launcher/FirmaManifiesto.java; un arreglo aquí va también allá.
package cl.itatagroup.pantalla;

import java.security.KeyFactory;
import java.security.PublicKey;
import java.security.Signature;
import java.security.spec.X509EncodedKeySpec;

/**
 * Verificación de la firma del manifiesto de actualización (contrato común con el emisor de Windows y el
 * launcher): ECDSA P-256 + SHA-256, firma DER en base64, clave pública SubjectPublicKeyInfo en base64.
 *
 * <p>Va en una clase aparte y sin APIs de Android a propósito: así {@code tools/probar-firma.mjs} del launcher la
 * compila y la prueba tal cual en el PC. Por eso trae su propio decodificador base64 ({@code java.util.Base64}
 * no existe antes de Android 8 y el launcher corre desde Android 5).
 */
public final class FirmaManifiesto {
    private FirmaManifiesto() {}

    /** true solo si {@code firmaB64} es una firma válida de {@code datos} con esa clave. Nunca lanza. */
    public static boolean verificarFirma(byte[] datos, String firmaB64, String clavePublicaB64) {
        try {
            byte[] firma = base64(firmaB64);
            byte[] clave = base64(clavePublicaB64);
            if (datos == null || firma == null || clave == null || firma.length == 0) return false;
            PublicKey publica = KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(clave));
            Signature verificador = Signature.getInstance("SHA256withECDSA");
            verificador.initVerify(publica);
            verificador.update(datos);
            return verificador.verify(firma);
        } catch (Exception e) {
            // Firma mal formada, clave rara o algoritmo ausente: para el actualizador es lo mismo que «no válida».
            return false;
        }
    }

    /** Base64 estándar (con o sin relleno, ignora espacios y saltos de línea). null si trae otra cosa. */
    static byte[] base64(String texto) {
        if (texto == null) return null;
        byte[] salida = new byte[texto.length() * 3 / 4 + 3];
        int largo = 0;
        int acumulado = 0;
        int bits = 0;
        boolean relleno = false;
        for (int i = 0; i < texto.length(); i++) {
            char c = texto.charAt(i);
            int v;
            if (c >= 'A' && c <= 'Z') v = c - 'A';
            else if (c >= 'a' && c <= 'z') v = c - 'a' + 26;
            else if (c >= '0' && c <= '9') v = c - '0' + 52;
            else if (c == '+') v = 62;
            else if (c == '/') v = 63;
            else if (c == '=') { relleno = true; continue; }
            else if (c == ' ' || c == '\n' || c == '\r' || c == '\t') continue;
            else return null;
            if (relleno) return null; // datos después del relleno
            acumulado = (acumulado << 6) | v;
            bits += 6;
            if (bits >= 8) {
                bits -= 8;
                salida[largo++] = (byte) (acumulado >> bits);
                acumulado &= (1 << bits) - 1;
            }
        }
        byte[] exacto = new byte[largo];
        System.arraycopy(salida, 0, exacto, 0, largo);
        return exacto;
    }
}
