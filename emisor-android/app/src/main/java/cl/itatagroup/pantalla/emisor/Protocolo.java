package cl.itatagroup.pantalla.emisor;

import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;

/**
 * El protocolo de pantalla-tv visto desde un emisor (docs/protocolo.md). Cabecera de cada mensaje:
 * tipo (1 byte), largo (4) y pts en microsegundos (8), en big-endian.
 */
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

    static final int MAX_CARGA = 8 * 1024 * 1024;

    /** Escribe un mensaje completo. Quien llama se encarga de que escriba un hilo a la vez. */
    static void escribir(DataOutputStream out, int tipo, long ptsUs, byte[] carga, int desde, int largo)
            throws IOException {
        out.writeByte(tipo);
        out.writeInt(largo);
        out.writeLong(ptsUs);
        out.write(carga, desde, largo);
        out.flush();
    }

    /** Carga del HELLO: 17 bytes, con el retardo pedido al final. */
    static byte[] hello(int ancho, int alto, int fps, int tasaAudio, int canales, int retardoMs) {
        ByteBuffer b = ByteBuffer.allocate(17);
        b.put((byte) VERSION);
        b.putShort((short) ancho);
        b.putShort((short) alto);
        b.put((byte) fps);
        b.putInt(tasaAudio);
        b.put((byte) canales);
        b.put("0000".getBytes(StandardCharsets.US_ASCII)); // PIN: todavía no se exige
        b.putShort((short) retardoMs);
        return b.array();
    }
}
