package cl.itatagroup.pantalla;

import android.util.Log;
import android.view.Surface;
import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

/** Una conexión con un emisor: lee los mensajes y los reparte al decodificador y al audio. */
final class Sesion implements Runnable {
    private static final String TAG = "Pantalla";
    /** El emisor manda PING cada 2 s; sin nada en 6 s se da por caído. */
    private static final int SIN_NOTICIAS_MS = 6000;

    interface Pantalla {
        /** La superficie de dibujo, o null si la pantalla no está a la vista. */
        Surface superficie();

        /** El búfer que el usuario eligió en este equipo (0 = ninguno). Es el único que cuenta. */
        int retardoMs();
    }

    interface Estado {
        /**
         * @param ancho y alto de la imagen que va a llegar, para respetar su proporción.
         * @param buferMs el búfer de esta sesión (0 en una interactiva, aunque el usuario haya elegido otro).
         */
        void conectado(String desde, int ancho, int alto, int buferMs);

        /** Se dibujó el primer cuadro de la sesión: ya se puede quitar la cubierta. */
        void primerCuadro();

        void desconectado();
    }

    private final Socket socket;
    private final Pantalla pantalla;
    private final Estado estado;
    private final Runnable alTerminar;
    private DataOutputStream out;
    private DecodificadorVideo video;
    private ReproductorAudio audio;
    private byte[] configActual;
    private final Reloj reloj = new Reloj();
    private int ancho;
    private int alto;
    private int fps;
    private int retardoMs;
    /** Lo toca el hilo del decodificador (o el de red al cambiar de decodificador, nunca a la vez). */
    private volatile boolean primerCuadroAvisado;

    Sesion(Socket socket, Pantalla pantalla, Estado estado, Runnable alTerminar) {
        this.socket = socket;
        this.pantalla = pantalla;
        this.estado = estado;
        this.alTerminar = alTerminar;
    }

    @Override
    public void run() {
        String desde = String.valueOf(socket.getInetAddress().getHostAddress());
        boolean saludoHecho = false;
        try {
            socket.setTcpNoDelay(true);
            socket.setSoTimeout(SIN_NOTICIAS_MS);
            DataInputStream in = new DataInputStream(new BufferedInputStream(socket.getInputStream(), 1 << 16));
            out = new DataOutputStream(new BufferedOutputStream(socket.getOutputStream(), 1 << 12));

            while (true) {
                int tipo = in.readUnsignedByte();
                int largo = in.readInt();
                long pts = in.readLong();
                if (largo < 0 || largo > Protocolo.MAX_CARGA) throw new IOException("carga inválida: " + largo);
                byte[] carga = new byte[largo];
                in.readFully(carga);

                if (!saludoHecho) {
                    if (tipo != Protocolo.HELLO) throw new IOException("se esperaba HELLO");
                    if (!saludar(carga)) return;
                    saludoHecho = true;
                    estado.conectado(desde, ancho, alto, retardoMs);
                    continue;
                }
                atender(tipo, pts, carga);
            }
        } catch (SocketTimeoutException e) {
            Log.w(TAG, "sesión: el emisor dejó de responder");
        } catch (IOException e) {
            Log.i(TAG, "sesión cerrada: " + e.getMessage());
        } catch (Exception e) {
            Log.e(TAG, "sesión", e);
        } finally {
            cerrarMedios();
            try {
                socket.close();
            } catch (IOException ignorada) {
                // ya cerrado
            }
            if (saludoHecho) estado.desconectado();
            alTerminar.run();
        }
    }

    /** Valida el HELLO y responde. Devuelve false si se rechazó. */
    private boolean saludar(byte[] c) throws IOException {
        if (c.length < 15) {
            rechazar("version");
            return false;
        }
        ByteBuffer b = ByteBuffer.wrap(c);
        int version = b.get() & 0xFF;
        ancho = b.getShort() & 0xFFFF;
        alto = b.getShort() & 0xFFFF;
        fps = b.get() & 0xFF;
        int tasaAudio = b.getInt();
        int canales = b.get() & 0xFF;
        b.position(b.position() + 4); // el PIN: en la v1 no se exige
        // Campos opcionales al final. El retardo que pedían los emisores anteriores se ignora: el búfer lo decide
        // quien mira (desde la 0.6.0). Las banderas dicen si es una sesión interactiva (el monitor virtual del PC,
        // que se usa con el ratón): esa va sin búfer.
        if (c.length >= 17) b.getShort();
        boolean interactiva = c.length >= 18 && (b.get() & Protocolo.BANDERA_INTERACTIVA) != 0;
        // Con tope: estos números dimensionan las colas.
        retardoMs = interactiva ? 0 : Math.min(Math.max(pantalla.retardoMs(), 0), Protocolo.RETARDO_MAX_MS);
        fps = Math.min(Math.max(fps, 1), Protocolo.FPS_MAX);
        if (version != Protocolo.VERSION) {
            rechazar("version");
            return false;
        }
        if (pantalla.superficie() == null) {
            rechazar("sin pantalla");
            return false;
        }
        if (tasaAudio >= 8000 && tasaAudio <= 192000 && (canales == 1 || canales == 2)) audio = new ReproductorAudio(tasaAudio, canales, retardoMs, reloj);
        enviar(Protocolo.HELLO_OK, 0, new byte[0]);
        return true;
    }

    private void atender(int tipo, long pts, byte[] c) throws Exception {
        long ahoraUs = System.nanoTime() / 1000;
        switch (tipo) {
            case Protocolo.VIDEO_CONFIG:
                // Recrear el decodificador es lento y puede atascarlo: solo si la configuración cambió de verdad.
                if (video != null && Arrays.equals(c, configActual)) break;
                if (video != null) video.detener();
                configActual = c;
                video = new DecodificadorVideo(pantalla.superficie(), ancho, alto, fps, retardoMs, c, reloj,
                        new DecodificadorVideo.PedirClave() {
                            @Override
                            public void cuadroDibujado() {
                                if (!primerCuadroAvisado) {
                                    primerCuadroAvisado = true;
                                    estado.primerCuadro();
                                }
                            }

                            @Override
                            public void pedir() {
                                pedirClave();
                            }

                            @Override
                            public void fallo() {
                                cortar();
                            }
                        });
                break;
            case Protocolo.VIDEO:
                reloj.anotar(ahoraUs, pts);
                if (video != null && c.length > 1) {
                    video.enviar(Arrays.copyOfRange(c, 1, c.length), (c[0] & 1) != 0, pts);
                }
                break;
            case Protocolo.AUDIO:
                reloj.anotar(ahoraUs, pts);
                if (audio != null) audio.enviar(c, pts);
                break;
            case Protocolo.PING:
                reloj.anotar(ahoraUs, pts);
                enviar(Protocolo.PONG, pts, c);
                break;
            default:
                break; // tipos desconocidos: se ignoran (compatibilidad hacia adelante)
        }
    }

    private void pedirClave() {
        try {
            enviar(Protocolo.PEDIR_CLAVE, 0, new byte[0]);
        } catch (IOException e) {
            Log.w(TAG, "no se pudo pedir fotograma clave: " + e.getMessage());
        }
    }

    /** Cierra el socket: el bucle de lectura termina y libera todo en su {@code finally}. */
    private void cortar() {
        try {
            socket.close();
        } catch (IOException ignorada) {
            // ya cerrado
        }
    }

    private void rechazar(String motivo) throws IOException {
        enviar(Protocolo.HELLO_ERR, 0, motivo.getBytes(StandardCharsets.UTF_8));
    }

    /** Varios hilos escriben (red y decodificador), así que se serializa. */
    private void enviar(int tipo, long pts, byte[] carga) throws IOException {
        synchronized (out) {
            Protocolo.escribir(out, tipo, pts, carga);
        }
    }

    private void cerrarMedios() {
        if (video != null) video.detener();
        if (audio != null) audio.detener();
        video = null;
        audio = null;
    }
}
