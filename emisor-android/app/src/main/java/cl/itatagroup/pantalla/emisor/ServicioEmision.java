package cl.itatagroup.pantalla.emisor;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Surface;
import android.view.WindowManager;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;

/**
 * Captura la pantalla (y el sonido, si se pidió) y los envía a una tele. Es un servicio en primer
 * plano porque Android solo deja capturar la pantalla así, con una notificación a la vista.
 *
 * <p>Vive mientras dure la transmisión: si la conexión se corta, vuelve a intentarlo solo cada
 * {@link #ESPERA_REINTENTO_MS} hasta que el usuario detenga. El permiso de captura se pide una vez
 * (en {@link Principal}) y sirve para todos los reintentos.
 */
public class ServicioEmision extends Service {
    private static final String TAG = "PantallaEmisor";
    static final String ACCION_INICIAR = "cl.itatagroup.pantalla.emisor.INICIAR";
    static final String ACCION_DETENER = "cl.itatagroup.pantalla.emisor.DETENER";
    static final String EXTRA_RESULTADO = "resultado";
    static final String EXTRA_PERMISO = "permiso";
    static final String EXTRA_IP = "ip";
    static final String EXTRA_PUERTO = "puerto";
    static final String EXTRA_LADO = "lado";
    static final String EXTRA_FPS = "fps";
    static final String EXTRA_KBPS = "kbps";
    static final String EXTRA_SONIDO = "sonido";

    private static final String CANAL = "transmision";
    private static final int ESPERA_REINTENTO_MS = 2000;
    private static final int TASA_AUDIO = 48000;
    private static final int MS_POR_PAQUETE = 20;
    /** Segundos entre fotogramas clave. Si la tele pide uno antes (PEDIR_CLAVE), se le da al momento. */
    private static final int SEG_CLAVE = 4;

    /** Lo que muestra la pantalla principal; lo escribe el servicio y lo lee la actividad. */
    static volatile boolean activo;
    static volatile String estado = "";

    private final Handler principal = new Handler(Looper.getMainLooper());
    private MediaProjection proyeccion;
    private VirtualDisplay pantallaVirtual;
    private Thread hilo;
    private volatile Socket socket;
    private volatile boolean detenido;

    private String ip;
    private int puerto;
    private int fps;
    private int kbps;
    private boolean conSonido;
    /** La tele mandó TERMINAR: quien mira terminó, así que no se reconecta. */
    private volatile boolean terminadoPorLaTele;
    /** Lado mayor pedido (calidad), para volver a medir al girar el teléfono. */
    private int ladoMax;
    private int ancho;
    private int alto;
    private int densidad;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || ACCION_DETENER.equals(intent.getAction())) {
            detener(getString(R.string.sin_transmitir));
            return START_NOT_STICKY;
        }
        if (activo) return START_NOT_STICKY; // ya hay una transmisión

        ip = intent.getStringExtra(EXTRA_IP);
        puerto = intent.getIntExtra(EXTRA_PUERTO, Protocolo.PUERTO_TCP);
        fps = intent.getIntExtra(EXTRA_FPS, 30);
        kbps = intent.getIntExtra(EXTRA_KBPS, 6000);
        conSonido = intent.getBooleanExtra(EXTRA_SONIDO, false)
                && checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
        ladoMax = intent.getIntExtra(EXTRA_LADO, 1280);
        medirPantalla(ladoMax);

        // Primero la notificación del servicio en primer plano; recién entonces Android entrega la captura.
        startForeground(1, notificacion(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        MediaProjectionManager gestor = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        Intent permiso = intent.getParcelableExtra(EXTRA_PERMISO);
        proyeccion = gestor.getMediaProjection(intent.getIntExtra(EXTRA_RESULTADO, Activity.RESULT_CANCELED), permiso);
        if (proyeccion == null) {
            detener(getString(R.string.sin_permiso_pantalla));
            return START_NOT_STICKY;
        }
        proyeccion.registerCallback(new MediaProjection.Callback() {
            @Override
            public void onStop() {
                // El usuario cortó la captura desde el sistema (o empezó otra app a capturar).
                detener(getString(R.string.sin_transmitir));
            }
        }, principal);
        // Una sola pantalla virtual para toda la transmisión: en cada reconexión solo se le cambia la
        // superficie (Android 14 no deja crear más de una por permiso).
        pantallaVirtual = proyeccion.createVirtualDisplay("pantalla-tv", ancho, alto, densidad,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, null, null, null);

        activo = true;
        terminadoPorLaTele = false;
        detenido = false;
        estado = "Conectando con " + ip + "…";
        hilo = new Thread(this::bucle, "emisor-sesion");
        hilo.start();
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        detener(getString(R.string.sin_transmitir));
        super.onDestroy();
    }

    /** Tamaño de lo que se envía: la pantalla, reducida para que su lado mayor no pase de {@code ladoMax}. */
    private void medirPantalla(int ladoMax) {
        DisplayMetrics m = new DisplayMetrics();
        ((WindowManager) getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay().getRealMetrics(m);
        double escala = Math.min(1.0, (double) ladoMax / Math.max(m.widthPixels, m.heightPixels));
        // Los codificadores piden dimensiones pares.
        ancho = Math.max(2, (int) Math.round(m.widthPixels * escala) & ~1);
        alto = Math.max(2, (int) Math.round(m.heightPixels * escala) & ~1);
        densidad = m.densityDpi;
    }

    private void bucle() {
        while (!detenido) {
            try {
                sesion();
            } catch (CambioDeOrientacion e) {
                // Se giró el teléfono: la pantalla virtual ya tiene el tamaño nuevo y se vuelve a saludar a la tele
                // con él al tiro, para que encaje la imagen sin franjas de más.
                Log.i(TAG, "orientación nueva: " + ancho + "x" + alto);
                continue;
            } catch (FalloDefinitivo e) {
                principal.post(() -> detener(e.getMessage()));
                return;
            } catch (Exception e) {
                if (detenido) return;
                Log.w(TAG, "sesión: " + e);
                estado = "Reconectando… (" + mensaje(e) + ")";
                Log.i(TAG, estado);
            }
            if (detenido) return;
            try {
                Thread.sleep(ESPERA_REINTENTO_MS);
            } catch (InterruptedException e) {
                return;
            }
        }
    }

    /** Lo que se le muestra al usuario mientras se reintenta: sin jerga de red. */
    private static String mensaje(Exception e) {
        if (e instanceof java.net.ConnectException || e instanceof java.net.SocketTimeoutException
                || e instanceof java.net.NoRouteToHostException || e instanceof java.net.UnknownHostException)
            return "no hay respuesta de la tele: ¿está abierta la app «Pantalla»?";
        if (e instanceof java.io.EOFException || e instanceof java.net.SocketException)
            return "se cortó la conexión con la tele";
        String m = e.getMessage();
        return m == null || m.isEmpty() ? "se cortó la transmisión" : m;
    }

    /** El teléfono se giró: hay que volver a empezar la sesión con el tamaño nuevo. */
    private static final class CambioDeOrientacion extends Exception {}

    /**
     * Si el teléfono se giró (la pantalla pasó de vertical a horizontal o al revés), mide de nuevo y le da el tamaño
     * nuevo a la pantalla virtual. Sin esto, la captura sigue con el tamaño del comienzo y Android achica la pantalla
     * girada dentro de ese marco: a la tele le llega con franjas y recortada.
     */
    private boolean cambioLaOrientacion() {
        DisplayMetrics m = new DisplayMetrics();
        ((WindowManager) getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay().getRealMetrics(m);
        boolean horizontalAhora = m.widthPixels > m.heightPixels;
        if (horizontalAhora == (ancho > alto)) return false;
        medirPantalla(ladoMax);
        VirtualDisplay vd = pantallaVirtual;
        if (vd != null) vd.resize(ancho, alto, densidad);
        return true;
    }

    /** Un fallo que reintentar no arregla (por ejemplo, otra versión del protocolo). */
    private static final class FalloDefinitivo extends Exception {
        FalloDefinitivo(String mensaje) {
            super(mensaje);
        }
    }

    /** Una conexión completa: saludo, codificar y enviar hasta que se corte o se detenga. */
    private void sesion() throws Exception {
        MediaCodec codec = null;
        Surface superficie = null;
        AudioRecord audio = null;
        Thread hiloAudio = null;
        Thread hiloLectura = null;
        try (Socket s = new Socket()) {
            socket = s;
            s.setTcpNoDelay(true);
            s.connect(new InetSocketAddress(ip, puerto), 5000);
            s.setSoTimeout(6000); // la tele contesta los PING cada 2 s: sin nada en 6 s, se cortó
            final DataOutputStream out = new DataOutputStream(new BufferedOutputStream(s.getOutputStream(), 1 << 16));
            DataInputStream in = new DataInputStream(s.getInputStream());

            if (conSonido) audio = abrirAudio();
            synchronized (out) {
                byte[] hello = Protocolo.hello(ancho, alto, fps, audio != null ? TASA_AUDIO : 0, 2);
                Protocolo.escribir(out, Protocolo.HELLO, 0, hello, 0, hello.length);
            }
            int tipo = in.readUnsignedByte();
            int largo = in.readInt();
            if (largo < 0 || largo > 256) throw new IOException("respuesta inesperada de la tele");
            byte[] carga = new byte[largo];
            in.readLong();
            in.readFully(carga);
            if (tipo == Protocolo.HELLO_ERR) {
                String motivo = new String(carga, StandardCharsets.UTF_8);
                if (motivo.equals("version")) throw new FalloDefinitivo("La tele usa otra versión: actualiza las dos apps.");
                if (motivo.equals("ocupado")) throw new IOException("la tele ya recibe de otro equipo");
                if (motivo.equals("sin pantalla")) throw new IOException("abre «Pantalla» en la tele");
                throw new IOException("la tele rechazó la conexión: " + motivo);
            }
            if (tipo != Protocolo.HELLO_OK) throw new IOException("respuesta inesperada de la tele");

            MediaFormat formato = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, ancho, alto);
            formato.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            formato.setInteger(MediaFormat.KEY_BIT_RATE, kbps * 1000);
            formato.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
            formato.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, SEG_CLAVE);
            // Con la pantalla quieta Android no entrega cuadros nuevos: se repite el último cada 100 ms
            // para que la tele siga recibiendo imagen (y un fotograma clave a su tiempo).
            formato.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000L);
            codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
            codec.configure(formato, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            superficie = codec.createInputSurface();
            codec.start();
            pantallaVirtual.setSurface(superficie);

            final long inicioNs = System.nanoTime();
            final MediaCodec codecFinal = codec;
            hiloLectura = new Thread(() -> leer(in, codecFinal), "emisor-lectura");
            hiloLectura.start();
            if (audio != null) {
                final AudioRecord audioFinal = audio;
                hiloAudio = new Thread(() -> enviarAudio(audioFinal, out, inicioNs), "emisor-audio");
                hiloAudio.start();
            }

            estado = "Transmitiendo a " + ip;
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            byte[] buf = new byte[1 << 20];
            long ultimoPing = 0;
            long ultimaMirada = 0;
            long ventana = System.nanoTime();
            int cuadros = 0;
            long bytes = 0;
            while (!detenido && !s.isClosed()) {
                int i = codec.dequeueOutputBuffer(info, 20_000);
                long ahoraNs = System.nanoTime();
                long ptsUs = (ahoraNs - inicioNs) / 1000;
                if (i >= 0) {
                    ByteBuffer salida = codec.getOutputBuffer(i);
                    if (info.size > 0 && salida != null) {
                        if (buf.length < info.size + 1) buf = new byte[info.size + 1];
                        salida.position(info.offset);
                        boolean config = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                        synchronized (out) {
                            if (config) {
                                // SPS y PPS, ya con sus prefijos de inicio.
                                salida.get(buf, 0, info.size);
                                Protocolo.escribir(out, Protocolo.VIDEO_CONFIG, ptsUs, buf, 0, info.size);
                            } else {
                                buf[0] = (byte) ((info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0 ? 1 : 0);
                                salida.get(buf, 1, info.size);
                                Protocolo.escribir(out, Protocolo.VIDEO, ptsUs, buf, 0, info.size + 1);
                                cuadros++;
                                bytes += info.size;
                            }
                        }
                    }
                    codec.releaseOutputBuffer(i, false);
                }
                if (ahoraNs - ultimoPing > 2_000_000_000L) {
                    ultimoPing = ahoraNs;
                    synchronized (out) {
                        Protocolo.escribir(out, Protocolo.PING, ptsUs, new byte[8], 0, 8);
                    }
                    double segs = (ahoraNs - ventana) / 1e9;
                    estado = String.format("Transmitiendo a %s · %dx%d · %.0f cuadros/s · %.1f Mb/s%s", ip, ancho, alto,
                            cuadros / segs, bytes * 8 / segs / 1e6, audio != null ? " · con sonido" : "");
                    Log.i(TAG, estado);
                    ventana = ahoraNs;
                    cuadros = 0;
                    bytes = 0;
                }
                if (hiloLectura != null && !hiloLectura.isAlive()) {
                    if (terminadoPorLaTele) throw new FalloDefinitivo(getString(R.string.terminado_por_la_tele));
                    throw new IOException("la tele cerró la conexión");
                }
                // Dos veces por segundo: ¿se giró el teléfono?
                if (ahoraNs - ultimaMirada > 500_000_000L) {
                    ultimaMirada = ahoraNs;
                    if (cambioLaOrientacion()) throw new CambioDeOrientacion();
                }
            }
        } finally {
            socket = null;
            // Cada paso por separado: si uno falla (detener() pudo liberar ya la pantalla virtual), los
            // demás igual tienen que soltar el codificador y la captura de sonido.
            VirtualDisplay vd = pantallaVirtual;
            if (vd != null) {
                try {
                    vd.setSurface(null);
                } catch (Exception ignorada) {
                    // ya estaba liberada
                }
            }
            if (hiloAudio != null) hiloAudio.interrupt();
            if (audio != null) {
                try {
                    audio.stop();
                } catch (Exception ignorada) {
                    // no había empezado
                }
                audio.release();
            }
            if (codec != null) {
                try {
                    codec.stop();
                } catch (Exception ignorada) {
                    // ya estaba detenido
                }
                codec.release();
            }
            if (superficie != null) superficie.release();
        }
    }

    /** Lo que manda la tele: PONG (mantiene viva la conexión), PEDIR_CLAVE y TERMINAR. Termina cuando se corta. */
    private void leer(DataInputStream in, MediaCodec codec) {
        try {
            while (true) {
                int tipo = in.readUnsignedByte();
                int largo = in.readInt();
                in.readLong();
                if (largo < 0 || largo > Protocolo.MAX_CARGA) return;
                in.skipBytes(largo);
                if (tipo == Protocolo.TERMINAR) {
                    // Quien mira terminó: el bucle lo nota al ver este hilo terminado y no reconecta.
                    terminadoPorLaTele = true;
                    return;
                }
                if (tipo == Protocolo.PEDIR_CLAVE) {
                    Bundle b = new Bundle();
                    b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                    try {
                        codec.setParameters(b);
                    } catch (IllegalStateException ignorada) {
                        // el codificador ya se cerró
                    }
                }
            }
        } catch (IOException e) {
            // se cortó (o pasaron 6 s sin PONG): el bucle principal lo nota y reconecta
        }
    }

    /** El sonido que reproducen las apps que lo permiten (Android 10+). null si el sistema no deja. */
    private AudioRecord abrirAudio() {
        try {
            AudioPlaybackCaptureConfiguration que = new AudioPlaybackCaptureConfiguration.Builder(proyeccion)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .build();
            AudioFormat formato = new AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(TASA_AUDIO)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                    .build();
            int minimo = AudioRecord.getMinBufferSize(TASA_AUDIO, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            AudioRecord r = new AudioRecord.Builder()
                    .setAudioFormat(formato)
                    .setBufferSizeInBytes(Math.max(minimo, TASA_AUDIO * 4 / 5)) // 200 ms
                    .setAudioPlaybackCaptureConfig(que)
                    .build();
            if (r.getState() != AudioRecord.STATE_INITIALIZED) {
                r.release();
                return null;
            }
            r.startRecording();
            return r;
        } catch (Exception e) {
            Log.w(TAG, "sin sonido: " + e);
            return null;
        }
    }

    private void enviarAudio(AudioRecord audio, DataOutputStream out, long inicioNs) {
        byte[] paquete = new byte[TASA_AUDIO * MS_POR_PAQUETE / 1000 * 4]; // estéreo de 16 bits
        try {
            while (!Thread.currentThread().isInterrupted()) {
                int leido = 0;
                while (leido < paquete.length) {
                    int n = audio.read(paquete, leido, paquete.length - leido);
                    if (n <= 0) return;
                    leido += n;
                }
                // La marca de tiempo es el inicio del paquete.
                long ptsUs = Math.max(0, (System.nanoTime() - inicioNs) / 1000 - MS_POR_PAQUETE * 1000L);
                synchronized (out) {
                    Protocolo.escribir(out, Protocolo.AUDIO, ptsUs, paquete, 0, paquete.length);
                }
            }
        } catch (IOException e) {
            // se cortó: lo maneja el bucle principal
        }
    }

    /** Termina todo y deja {@code mensaje} como estado. Se puede llamar más de una vez. */
    private synchronized void detener(String mensaje) {
        boolean estaba = activo;
        detenido = true;
        activo = false;
        if (estaba || estado.isEmpty()) estado = mensaje;
        Socket s = socket;
        if (s != null) {
            try {
                s.close();
            } catch (IOException ignorada) {
                // ya cerrado
            }
        }
        if (hilo != null) hilo.interrupt();
        if (pantallaVirtual != null) {
            pantallaVirtual.release();
            pantallaVirtual = null;
        }
        if (proyeccion != null) {
            proyeccion.stop();
            proyeccion = null;
        }
        stopForeground(true);
        stopSelf();
    }

    private Notification notificacion() {
        NotificationManager gestor = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        gestor.createNotificationChannel(new NotificationChannel(CANAL, getString(R.string.canal), NotificationManager.IMPORTANCE_LOW));
        PendingIntent abrir = PendingIntent.getActivity(this, 0, new Intent(this, Principal.class), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent parar = PendingIntent.getService(this, 1,
                new Intent(this, ServicioEmision.class).setAction(ACCION_DETENER), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CANAL)
                .setSmallIcon(android.R.drawable.ic_menu_slideshow)
                .setContentTitle(getString(R.string.notificacion_titulo))
                .setContentText(ip)
                .setContentIntent(abrir)
                .setOngoing(true)
                .addAction(new Notification.Action.Builder(null, getString(R.string.detener), parar).build())
                .build();
    }
}
