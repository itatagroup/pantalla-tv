package cl.itatagroup.pantalla;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.util.Log;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.LockSupport;

/**
 * Reproduce PCM 16 bit. Sin retardo usa poco colchón y, si se llena, descarta lo más viejo. Con
 * retardo (modo «fluidez») retiene los paquetes y los escribe a la misma hora de salida que el
 * vídeo, para que sigan sincronizados.
 *
 * <p>Cada ~2 s registra cómo va (paquetes, cola, cortes, y a qué distancia del {@code pts} suena),
 * para poder comparar con la línea de vídeo y ver el desfase entre imagen y sonido.
 */
final class ReproductorAudio {
    private static final String TAG = "Pantalla";
    /** Cola sin retardo: ~240 ms con paquetes de 20 ms. */
    private static final int COLA_BAJA_LATENCIA = 16;
    /** Sin retardo: con más de estos paquetes esperando (140 ms) se salta hasta dejar ESPERA_MIN. */
    private static final int ESPERA_MAX = 7;
    private static final int ESPERA_MIN = 2;
    /** Se escribe un poco antes de la hora de salida, por lo que ya hay en el búfer de la pista. */
    private static final long ADELANTO_US = 40_000;
    /** Con retardo: un paquete con más atraso que esto respecto de su hora de salida se descarta. */
    private static final long ATRASO_MAX_US = 150_000;
    /** Búfer de la pista: lo que se escribe tarda más o menos esto en sonar. */
    private static final int BUFER_MS = 60;

    private static final class Paquete {
        final byte[] pcm;
        final long ptsUs;

        Paquete(byte[] pcm, long ptsUs) {
            this.pcm = pcm;
            this.ptsUs = ptsUs;
        }
    }

    private final AudioTrack pista;
    private final Reloj reloj;
    private final long retardoUs;
    private final int tasa;
    private final int bytesPorCuadro;
    private final ArrayBlockingQueue<Paquete> cola;
    private final Thread hilo;
    private volatile boolean activo = true;

    // Estadísticas (solo las toca el hilo de audio, salvo statDescartados).
    private long statInicio = System.nanoTime();
    private int statPaquetes;
    private volatile int statDescartados;
    private long statSumaSalidaUs;
    private int statPico;
    private long cuadrosEscritos;

    ReproductorAudio(int tasa, int canales, int retardoMs, Reloj reloj) {
        this.reloj = reloj;
        this.retardoUs = retardoMs * 1000L;
        this.tasa = tasa;
        this.bytesPorCuadro = canales * 2;
        this.cola = new ArrayBlockingQueue<>(retardoMs <= 0 ? COLA_BAJA_LATENCIA : retardoMs / 20 + 50);
        int mascara = canales == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
        int minimo = AudioTrack.getMinBufferSize(tasa, mascara, AudioFormat.ENCODING_PCM_16BIT);
        int objetivo = tasa * bytesPorCuadro * BUFER_MS / 1000;
        pista = new AudioTrack.Builder()
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setSampleRate(tasa)
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setChannelMask(mascara)
                        .build())
                .setBufferSizeInBytes(Math.max(minimo, objetivo))
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                .build();
        pista.play();
        Log.i(TAG, "audio: " + tasa + " Hz, " + canales + " canal(es), búfer "
                + (pista.getBufferSizeInFrames() * 1000L / tasa) + " ms, retardo " + retardoMs + " ms");
        hilo = new Thread(this::bucle, "pantalla-audio");
        hilo.start();
    }

    /** Llamado desde el hilo de red: no bloquea. */
    void enviar(byte[] pcm, long ptsUs) {
        Paquete p = new Paquete(pcm, ptsUs);
        if (!cola.offer(p)) {
            cola.poll(); // descarta el más viejo
            cola.offer(p);
            statDescartados++;
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
            pista.stop();
        } catch (Exception ignorada) {
            // ya estaba detenida
        }
        pista.release();
    }

    private void bucle() {
        Paquete p = null;
        while (activo) {
            try {
                if (p == null) p = cola.poll(20, TimeUnit.MILLISECONDS);
                if (p == null) {
                    estadisticas();
                    continue;
                }
                if (retardoUs == 0 && cola.size() > ESPERA_MAX) {
                    // Sin retardo, lo que espera en la cola es atraso puro respecto de la imagen (la
                    // pista consume a tiempo real y nunca la alcanzaría): se salta a lo más nuevo.
                    while (cola.size() > ESPERA_MIN) {
                        p = cola.poll();
                        statDescartados++;
                    }
                    if (p == null) continue;
                }
                if (retardoUs > 0 && reloj.listo()) {
                    long falta = reloj.horaDeSalidaUs(p.ptsUs, retardoUs) - Reloj.ahoraUs() - ADELANTO_US;
                    if (falta > 1000) {
                        LockSupport.parkNanos(Math.min(falta * 1000, 5_000_000L));
                        continue;
                    }
                    if (falta < -ATRASO_MAX_US) {
                        // Llegó después de su hora (una pausa del Wi-Fi más larga que el buffer). La pista
                        // consume a tiempo real, así que escribirlo dejaría el sonido atrasado para
                        // siempre mientras la imagen sí salta: se descarta.
                        statDescartados++;
                        p = null;
                        continue;
                    }
                }
                anotar(p);
                pista.write(p.pcm, 0, p.pcm.length - p.pcm.length % bytesPorCuadro);
                cuadrosEscritos += p.pcm.length / bytesPorCuadro;
                p = null;
                estadisticas();
            } catch (InterruptedException e) {
                return;
            } catch (Exception e) {
                Log.e(TAG, "audio", e);
                return;
            }
        }
    }

    /** Estima a qué distancia de su {@code pts} va a sonar este paquete: lo ya transcurrido más lo que hay por delante en la pista. */
    private void anotar(Paquete p) {
        statPaquetes++;
        for (int i = 0; i + 1 < p.pcm.length; i += 32) { // una muestra de cada 16 basta para el pico
            int v = Math.abs((short) ((p.pcm[i + 1] << 8) | (p.pcm[i] & 0xFF)));
            if (v > statPico) statPico = v;
        }
        if (!reloj.listo()) return;
        long pendientes = cuadrosEscritos - (pista.getPlaybackHeadPosition() & 0xFFFFFFFFL);
        long enPistaUs = Math.max(0, pendientes) * 1_000_000L / tasa;
        statSumaSalidaUs += (Reloj.ahoraUs() - p.ptsUs - reloj.desfaseUs()) + enPistaUs;
    }

    private void estadisticas() {
        long ahora = System.nanoTime();
        if (ahora - statInicio < 2_000_000_000L) return;
        double segs = (ahora - statInicio) / 1e9;
        String salida = statPaquetes == 0 ? "n/d" : (statSumaSalidaUs / statPaquetes / 1000) + " ms";
        Log.i(TAG, String.format("audio: %.0f paq/s, en cola %d, descartados %d, cortes %d, pico %d %%, suena a: %s",
                statPaquetes / segs, cola.size(), statDescartados, pista.getUnderrunCount(),
                statPico * 100 / 32768, salida));
        statInicio = ahora;
        statPaquetes = 0;
        statDescartados = 0;
        statSumaSalidaUs = 0;
        statPico = 0;
    }
}
