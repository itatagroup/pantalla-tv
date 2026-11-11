package cl.itatagroup.pantalla;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.util.Log;
import android.view.Surface;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.LockSupport;

/**
 * Decodifica H.264 por hardware y lo dibuja en la superficie.
 *
 * <p>Sin retardo prioriza la latencia: si se atrasa, tira cuadros hasta el siguiente fotograma
 * clave y lo pide. Con retardo (modo «fluidez», por ejemplo para ver vídeos) guarda los cuadros
 * <b>comprimidos</b> y se los entrega al decodificador a su hora de salida, para absorber los
 * tirones de la red.
 */
final class DecodificadorVideo {
    private static final String TAG = "Pantalla";
    /**
     * Cuadros que se aceptan esperando al decodificador cuando no hay retardo (~0,5 s a 60 fps). Una
     * pausa del Wi-Fi entrega varios cuadros de golpe: el decodificador los despacha más rápido de lo
     * que llegan y la cola se vacía sola. Con solo 3 (lo que había) cada pausa de 50 ms costaba un
     * salto y hasta 2 s de imagen congelada esperando el siguiente fotograma clave.
     */
    private static final int COLA_BAJA_LATENCIA = 30;
    /** Se entrega el cuadro al decodificador un poco antes de su hora, por lo que tarda en decodificar. */
    private static final long ADELANTO_US = 8_000;

    /**
     * Atraso máximo de un cuadro respecto de su hora de salida. El decodificador no va más rápido que
     * la llegada, así que un atraso (el arranque, una pausa larga del Wi-Fi) no se recupera solo: pasado
     * este límite se salta al siguiente fotograma clave, que llega fresco.
     */
    private static final long ATRASO_MAX_US = 250_000;

    /**
     * Un cuadro que sale del decodificador con más atraso que esto se descarta sin dibujarlo. La TV
     * refresca a 59,94 Hz y la fuente va a 60: sobra un cuadro cada ~17 s y, si se dibujaran todos, el
     * atraso crecería sin parar. Nunca se saltan más de dos seguidos, para que la imagen no se congele.
     */
    private static final long SIN_DIBUJAR_US = 90_000;
    private static final int SALTOS_SEGUIDOS_MAX = 2;
    private int saltosSeguidos;
    private int statSinDibujar;

    interface PedirClave {
        void pedir();

        /** Se mandó a dibujar un cuadro (desde el hilo del decodificador). */
        void cuadroDibujado();

        /** El decodificador murió: quien lo creó debe cortar la sesión (si no, quedaría una imagen congelada). */
        void fallo();
    }

    private static final class Cuadro {
        final byte[] datos;
        final boolean clave;
        final long ptsUs;

        Cuadro(byte[] datos, boolean clave, long ptsUs) {
            this.datos = datos;
            this.clave = clave;
            this.ptsUs = ptsUs;
        }
    }

    private final MediaCodec codec;
    private final PedirClave pedirClave;
    private final Reloj reloj;
    private final long retardoUs;
    private final ArrayBlockingQueue<Cuadro> cola;
    private final Thread hilo;
    private volatile boolean activo = true;
    private volatile boolean esperandoClave = true;

    // Estadísticas de los últimos ~2 s (solo las toca el hilo del decodificador).
    private long statInicio = System.nanoTime();
    private int statMostrados;
    private final long[] statTransito = new long[512];
    private int statN;
    private int statTirados;

    DecodificadorVideo(Surface superficie, int ancho, int alto, int fps, int retardoMs, byte[] config,
                       Reloj reloj, PedirClave pedirClave) throws Exception {
        this.pedirClave = pedirClave;
        this.reloj = reloj;
        this.retardoUs = retardoMs * 1000L;
        // Con retardo caben todos los cuadros de ese tiempo más un margen; sin él, solo unos pocos.
        int capacidad = retardoMs <= 0
                ? COLA_BAJA_LATENCIA
                : Math.max(COLA_BAJA_LATENCIA, (retardoMs * Math.max(fps, 30)) / 1000 + 60);
        this.cola = new ArrayBlockingQueue<>(capacidad);

        byte[] sps = null;
        byte[] pps = null;
        List<int[]> nals = AnnexB.nals(config);
        for (int[] r : nals) {
            int t = AnnexB.tipo(config, r[0]);
            if (t == 7) sps = AnnexB.conPrefijo4(config, r[0], r[1]);
            if (t == 8) pps = AnnexB.conPrefijo4(config, r[0], r[1]);
        }
        if (sps == null || pps == null) throw new IllegalArgumentException("config sin SPS/PPS");

        MediaFormat formato = MediaFormat.createVideoFormat("video/avc", ancho, alto);
        formato.setByteBuffer("csd-0", ByteBuffer.wrap(sps));
        formato.setByteBuffer("csd-1", ByteBuffer.wrap(pps));
        formato.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 2 * 1024 * 1024);
        formato.setInteger(MediaFormat.KEY_PRIORITY, 0); // tiempo real
        codec = MediaCodec.createDecoderByType("video/avc");
        codec.configure(formato, superficie, null, 0);
        codec.start();
        Log.i(TAG, "decodificador: " + codec.getName() + ", retardo " + retardoMs + " ms, cola " + capacidad);

        hilo = new Thread(this::bucle, "pantalla-decodificador");
        hilo.start();
    }

    /** Llamado desde el hilo de red. */
    void enviar(byte[] datos, boolean clave, long ptsUs) {
        if (esperandoClave && !clave) return; // tras un salto, todo lo que no sea clave se ignora
        if (clave) esperandoClave = false;
        if (!cola.offer(new Cuadro(datos, clave, ptsUs))) {
            cola.clear();
            esperandoClave = true;
            statTirados++;
            pedirClave.pedir();
        }
    }

    void detener() {
        activo = false;
        hilo.interrupt();
        try {
            hilo.join(500);
        } catch (InterruptedException ignorada) {
            Thread.currentThread().interrupt();
        }
        try {
            codec.stop();
        } catch (Exception ignorada) {
            // ya estaba detenido
        }
        codec.release();
    }

    private void bucle() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        Cuadro c = null;
        while (activo) {
            try {
                drenar(info);
                if (c == null) c = cola.poll(5, TimeUnit.MILLISECONDS);
                if (c == null) continue;
                if (!c.clave && reloj.listo()
                        && Reloj.ahoraUs() - reloj.horaDeSalidaUs(c.ptsUs, retardoUs) > ATRASO_MAX_US) {
                    esperandoClave = true;
                    cola.clear();
                    statTirados++;
                    pedirClave.pedir();
                    c = null;
                    continue;
                }
                if (retardoUs > 0 && reloj.listo()) {
                    long falta = reloj.horaDeSalidaUs(c.ptsUs, retardoUs) - Reloj.ahoraUs() - ADELANTO_US;
                    if (falta > 1000) {
                        // Aún no es su hora: se espera en pasos cortos (y se sigue drenando la salida).
                        LockSupport.parkNanos(Math.min(falta * 1000, 2_000_000L));
                        continue;
                    }
                }
                // Si el decodificador no tiene entrada libre todavía, el cuadro se conserva y se reintenta.
                if (alimentar(c)) c = null;
                drenar(info);
                estadisticas();
            } catch (InterruptedException e) {
                return;
            } catch (Exception e) {
                if (activo) {
                    Log.e(TAG, "decodificador", e);
                    pedirClave.fallo();
                }
                return;
            }
        }
    }

    /** Devuelve false si el decodificador aún no acepta entrada (hay que reintentar con el mismo cuadro). */
    private boolean alimentar(Cuadro c) {
        int i = codec.dequeueInputBuffer(10_000);
        if (i < 0) return false;
        ByteBuffer entrada = codec.getInputBuffer(i);
        entrada.clear();
        if (c.datos.length > entrada.capacity()) {
            // No cabe en el búfer del decodificador: se descarta este cuadro (y los que dependen de
            // él) en vez de tumbar el hilo. Se devuelve el búfer vacío para no perderlo.
            codec.queueInputBuffer(i, 0, 0, c.ptsUs, 0);
            Log.w(TAG, "cuadro de " + c.datos.length + " bytes descartado (búfer de " + entrada.capacity() + ")");
            esperandoClave = true;
            cola.clear();
            statTirados++;
            pedirClave.pedir();
            return true;
        }
        entrada.put(c.datos);
        codec.queueInputBuffer(i, 0, c.datos.length, c.ptsUs, c.clave ? MediaCodec.BUFFER_FLAG_KEY_FRAME : 0);
        return true;
    }

    private void drenar(MediaCodec.BufferInfo info) {
        while (true) {
            int o = codec.dequeueOutputBuffer(info, 0);
            if (o >= 0) {
                boolean tarde = reloj.listo()
                        && Reloj.ahoraUs() - reloj.horaDeSalidaUs(info.presentationTimeUs, retardoUs) > SIN_DIBUJAR_US;
                boolean dibujar = !tarde || saltosSeguidos >= SALTOS_SEGUIDOS_MAX;
                codec.releaseOutputBuffer(o, dibujar);
                if (dibujar) {
                    saltosSeguidos = 0;
                    pedirClave.cuadroDibujado();
                } else {
                    saltosSeguidos++;
                    statSinDibujar++;
                    continue;
                }
                statMostrados++;
                if (reloj.listo() && statN < statTransito.length) {
                    statTransito[statN++] = (Reloj.ahoraUs() - info.presentationTimeUs) - reloj.desfaseUs();
                }
            } else if (o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED
                    || o == MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED) {
                continue;
            } else {
                return;
            }
        }
    }

    private void estadisticas() {
        long ahora = System.nanoTime();
        if (ahora - statInicio < 2_000_000_000L) return;
        double segs = (ahora - statInicio) / 1e9;
        String transito = "n/d";
        if (statN > 0) {
            long[] v = Arrays.copyOf(statTransito, statN);
            Arrays.sort(v);
            long suma = 0;
            for (long x : v) suma += x;
            transito = String.format("media %d ms, p95 %d ms, max %d ms",
                    suma / v.length / 1000, v[Math.max(0, (int) (v.length * 0.95) - 1)] / 1000,
                    v[v.length - 1] / 1000);
        }
        Log.i(TAG, String.format("video: %.1f fps, tirados %d, sin dibujar %d, en cola %d, transito+decodificacion: %s",
                statMostrados / segs, statTirados, statSinDibujar, cola.size(), transito));
        statSinDibujar = 0;
        statInicio = ahora;
        statMostrados = 0;
        statN = 0;
        statTirados = 0;
    }
}
