package cl.itatagroup.pantalla;

/** Constantes del protocolo (ver docs/protocolo.md). Cabecera: tipo(1) largo(4) pts(8), big-endian. */
final class Protocolo {
    private Protocolo() {}

    static final int VERSION = 1;
    static final int PUERTO_TCP = 5600;
    static final int PUERTO_UDP = 5601;

    static final int HELLO = 0x01;
    static final int HELLO_OK = 0x02;
    static final int HELLO_ERR = 0x03;
    static final int VIDEO_CONFIG = 0x10;
    static final int VIDEO = 0x11;
    static final int AUDIO = 0x20;
    static final int PING = 0x30;
    static final int PONG = 0x31;
    static final int PEDIR_CLAVE = 0x40;

    /** Tope de una carga: un fotograma clave de 1080p no llega a 2 MB. */
    static final int MAX_CARGA = 8 * 1024 * 1024;

    /** Topes de lo que puede pedir un emisor (dimensionan las colas del receptor). */
    static final int RETARDO_MAX_MS = 5000;
    static final int FPS_MAX = 120;

    /** Escribe un mensaje completo (cabecera + carga) y vacía el búfer. Un hilo a la vez por flujo. */
    static void escribir(java.io.DataOutputStream out, int tipo, long pts, byte[] carga)
            throws java.io.IOException {
        out.writeByte(tipo);
        out.writeInt(carga.length);
        out.writeLong(pts);
        out.write(carga);
        out.flush();
    }
}
