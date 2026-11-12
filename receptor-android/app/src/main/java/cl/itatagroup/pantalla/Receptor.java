package cl.itatagroup.pantalla;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.Settings;
import android.text.format.Formatter;
import android.util.Log;
import android.view.Surface;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;
import android.widget.Toast;

/** Pantalla del receptor: mientras está abierta, escucha y se anuncia; al recibir vídeo lo muestra completo. */
public class Receptor extends Activity {
    private static final String TAG = "Pantalla";
    /** Retardos a propósito que se alternan con OK: 0 = mínima latencia; 2 s = ver vídeos con fluidez. */
    private static final int[] RETARDOS_MS = {0, 500, 1000, 2000};
    private static final String PREFS = "pantalla";
    private static final String PREF_RETARDO = "retardo_ms";
    private static final String PREF_AUTO = "actualizar_auto";
    /** Dónde se publica la versión nueva (contrato común de actualización). */
    private static final String MANIFIESTO =
            "https://github.com/itatagroup/pantalla-tv/releases/latest/download/version.json";
    private static final long REVISAR_CADA_MS = 24L * 60 * 60 * 1000;

    private volatile Surface superficie;
    private volatile int retardoElegido;
    private TextView textoEstado;
    /** Solo en el hilo de la interfaz: hay una sesión en curso (el texto de espera no debe volver). */
    private boolean enSesion;
    private SurfaceView video;
    private WifiManager.WifiLock bloqueoWifi;
    private Servidor servidor;
    private Anunciante anunciante;

    // Actualizaciones: todo en el hilo de la interfaz, y solo mientras está la pantalla de espera.
    private View controles;
    private TextView aviso;
    private Button botonRetardo;
    private Button botonActualizar;
    private Button botonAuto;
    private Actualizador actualizador;
    private Actualizador.Oferta oferta;
    private boolean descargando;
    private boolean abierta;
    /** elapsedRealtime de la última consulta; 0 = consultar apenas se pueda. */
    private long ultimaRevision;
    private final Handler hiloPrincipal = new Handler(Looper.getMainLooper());
    private final Runnable revisar = this::revisarActualizacion;

    private final Sesion.Pantalla pantalla = new Sesion.Pantalla() {
        @Override
        public Surface superficie() {
            return superficie;
        }

        @Override
        public int retardoMs() {
            return RETARDOS_MS[retardoElegido];
        }
    };

    private final Sesion.Estado estado = new Sesion.Estado() {
        @Override
        public void conectado(String desde, int ancho, int alto) {
            runOnUiThread(() -> {
                // La superficie aún guarda el último cuadro de la sesión anterior: se queda tapada (fondo
                // sólido, sin texto) hasta que llegue el primer cuadro de esta.
                enSesion = true;
                // Una sesión manda: se corta cualquier consulta o descarga y se esconden los botones.
                detenerActualizaciones();
                controles.setVisibility(View.GONE);
                textoEstado.setText("");
                textoEstado.setVisibility(View.VISIBLE);
                ajustarProporcion(ancho, alto);
            });
        }

        @Override
        public void primerCuadro() {
            runOnUiThread(() -> {
                if (enSesion) textoEstado.setVisibility(View.GONE);
            });
        }

        @Override
        public void desconectado() {
            runOnUiThread(() -> {
                enSesion = false;
                ajustarProporcion(0, 0);
                mostrarEsperando();
            });
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setContentView(R.layout.activity_receptor);
        textoEstado = (TextView) findViewById(R.id.estado);
        retardoElegido = indiceDeRetardo(getSharedPreferences(PREFS, MODE_PRIVATE).getInt(PREF_RETARDO, 0));
        video = (SurfaceView) findViewById(R.id.video);
        controles = findViewById(R.id.controles);
        aviso = (TextView) findViewById(R.id.aviso);
        botonRetardo = (Button) findViewById(R.id.boton_retardo);
        botonActualizar = (Button) findViewById(R.id.boton_actualizar);
        botonAuto = (Button) findViewById(R.id.boton_auto);
        botonRetardo.setOnClickListener(v -> fijarRetardo((retardoElegido + 1) % RETARDOS_MS.length));
        botonAuto.setOnClickListener(v -> cambiarAutomaticas());
        botonActualizar.setOnClickListener(v -> actualizar());
        actualizador = new Actualizador(this, "itatagroup/pantalla-tv", "receptor-android", "Pantalla");
        video.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                superficie = holder.getSurface();
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int formato, int ancho, int alto) {}

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                superficie = null;
            }
        });
    }

    @Override
    protected void onStart() {
        super.onStart();
        // Sin esto el Wi-Fi de la TV Box "duerme" entre ráfagas: se midió 40 % de pérdida en reposo.
        WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        int modo = Build.VERSION.SDK_INT >= 29
                ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
        bloqueoWifi = wifi.createWifiLock(modo, "pantalla-tv");
        bloqueoWifi.setReferenceCounted(false);
        bloqueoWifi.acquire();
        abierta = true;
        ultimaRevision = 0; // al abrir se consulta de nuevo

        try {
            servidor = new Servidor(pantalla, estado);
            servidor.iniciar();
            anunciante = new Anunciante(this, nombreDelEquipo());
            anunciante.iniciar();
        } catch (Exception e) {
            Log.e(TAG, "no se pudo abrir el puerto " + Protocolo.PUERTO_TCP, e);
            textoEstado.setText("No se pudo abrir el puerto " + Protocolo.PUERTO_TCP);
            return;
        }
        mostrarEsperando();
    }

    /** Para pruebas por adb: {@code am start -n ... --ei retardo_ms 2000}. */
    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
    }

    @Override
    protected void onResume() {
        super.onResume();
        int pedido = getIntent().getIntExtra("retardo_ms", -1);
        if (pedido >= 0) fijarRetardo(indiceDeRetardo(pedido));
    }

    /**
     * OK en el control remoto alterna el retardo: 0 → 0,5 s → 1 s → 2 s → 0. En la pantalla de espera lo hace el
     * botón «Retardo», que tiene el foco al abrir; durante una sesión no hay botones y OK llega aquí.
     */
    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER) {
            fijarRetardo((retardoElegido + 1) % RETARDOS_MS.length);
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    private void fijarRetardo(int indice) {
        retardoElegido = indice;
        getSharedPreferences(PREFS, MODE_PRIVATE).edit().putInt(PREF_RETARDO, RETARDOS_MS[indice]).apply();
        botonRetardo.setText(getString(R.string.retardo_boton, descripcionRetardo()));
        Toast.makeText(this, getString(R.string.retardo_aviso, descripcionRetardo()), Toast.LENGTH_SHORT).show();
    }

    private String descripcionRetardo() {
        int ms = RETARDOS_MS[retardoElegido];
        return ms == 0 ? getString(R.string.retardo_ninguno) : (ms / 1000.0) + " s";
    }

    private static int indiceDeRetardo(int ms) {
        for (int i = 0; i < RETARDOS_MS.length; i++) if (RETARDOS_MS[i] == ms) return i;
        return 0;
    }

    @Override
    protected void onStop() {
        abierta = false;
        detenerActualizaciones();
        if (anunciante != null) anunciante.detener();
        if (servidor != null) servidor.detener();
        if (bloqueoWifi != null && bloqueoWifi.isHeld()) bloqueoWifi.release();
        super.onStop();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                    | View.SYSTEM_UI_FLAG_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
        }
    }

    /**
     * Encaja la imagen en la pantalla sin deformarla, con franjas negras si la proporción no coincide
     * (un teléfono en vertical, un monitor ultrapanorámico). Con 0×0 vuelve a ocupar todo.
     */
    private void ajustarProporcion(int ancho, int alto) {
        View padre = (View) video.getParent();
        int w = ViewGroup.LayoutParams.MATCH_PARENT;
        int h = ViewGroup.LayoutParams.MATCH_PARENT;
        if (ancho > 0 && alto > 0 && padre.getWidth() > 0 && padre.getHeight() > 0) {
            w = padre.getWidth();
            h = (int) ((long) w * alto / ancho);
            if (h > padre.getHeight()) {
                h = padre.getHeight();
                w = (int) ((long) h * ancho / alto);
            }
        }
        video.setLayoutParams(new FrameLayout.LayoutParams(w, h, Gravity.CENTER));
    }

    private void mostrarEsperando() {
        WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        int ip = wifi.getConnectionInfo().getIpAddress();
        String detalle = ip == 0
                ? getString(R.string.sin_red)
                : getString(R.string.esperando_detalle, nombreDelEquipo(), Formatter.formatIpAddress(ip));
        textoEstado.setText(getString(R.string.esperando) + "\n\n" + detalle);
        textoEstado.setVisibility(View.VISIBLE);
        botonRetardo.setText(getString(R.string.retardo_boton, descripcionRetardo()));
        botonAuto.setText(automaticas() ? R.string.auto_si : R.string.auto_no);
        mostrarOferta();
        controles.setVisibility(View.VISIBLE);
        View foco = getCurrentFocus();
        if (foco == null || !foco.isShown()) botonRetardo.requestFocus();
        programarRevision();
    }

    // --- actualizaciones (ver Actualizador y el README, «Actualizaciones») ---

    private boolean automaticas() {
        return getSharedPreferences(PREFS, MODE_PRIVATE).getBoolean(PREF_AUTO, true);
    }

    /** En «No» no se hace ninguna conexión: se corta lo que esté en curso y no se programa nada. */
    private void cambiarAutomaticas() {
        boolean activas = !automaticas();
        getSharedPreferences(PREFS, MODE_PRIVATE).edit().putBoolean(PREF_AUTO, activas).apply();
        botonAuto.setText(activas ? R.string.auto_si : R.string.auto_no);
        detenerActualizaciones();
        oferta = null;
        ultimaRevision = 0;
        mostrarOferta();
        programarRevision();
    }

    /** Siguiente consulta: ya si nunca se hizo, o 24 h después de la última. Solo en espera y con «Sí». */
    private void programarRevision() {
        hiloPrincipal.removeCallbacks(revisar);
        if (!abierta || enSesion || descargando || !automaticas()) return;
        long espera = ultimaRevision == 0
                ? 0
                : Math.max(0, ultimaRevision + REVISAR_CADA_MS - SystemClock.elapsedRealtime());
        hiloPrincipal.postDelayed(revisar, espera);
    }

    private void revisarActualizacion() {
        if (!abierta || enSesion || descargando || !automaticas()) return;
        ultimaRevision = SystemClock.elapsedRealtime();
        actualizador.comprobar(urlManifiesto(), new Actualizador.AlComprobar() {
            @Override
            public void hayVersion(Actualizador.Oferta nueva) {
                oferta = nueva;
                mostrarOferta();
            }

            @Override
            public void alDia() {
                oferta = null;
                mostrarOferta();
            }

            @Override
            public void fallo() {
                // Sin aviso en pantalla (el detalle queda en el registro); se reintenta en 24 h.
            }
        });
        programarRevision();
    }

    /** Para pruebas: {@code am start -n cl.itatagroup.pantalla/.Receptor --es manifiesto_url https://…}. */
    private String urlManifiesto() {
        String pedida = getIntent().getStringExtra("manifiesto_url");
        return pedida != null && !pedida.isEmpty() ? pedida : MANIFIESTO;
    }

    private void mostrarOferta() {
        if (descargando) return;
        if (oferta == null) {
            boolean teniaFoco = botonActualizar.hasFocus();
            aviso.setVisibility(View.GONE);
            botonActualizar.setVisibility(View.GONE);
            if (teniaFoco) botonRetardo.requestFocus();
            return;
        }
        aviso.setText(oferta.notas.isEmpty()
                ? getString(R.string.hay_version_sin_notas, oferta.version)
                : getString(R.string.hay_version, oferta.version, oferta.notas));
        aviso.setVisibility(View.VISIBLE);
        botonActualizar.setVisibility(View.VISIBLE);
    }

    private void actualizar() {
        if (oferta == null || descargando || enSesion) return;
        if (!actualizador.puedeInstalar()) {
            boolean abierto = Actualizador.abrirPermisoDeInstalacion(this);
            aviso.setText(abierto ? R.string.falta_permiso : R.string.sin_ajuste_permiso);
            aviso.setVisibility(View.VISIBLE);
            return;
        }
        descargando = true;
        hiloPrincipal.removeCallbacks(revisar);
        final Actualizador.Oferta pedida = oferta;
        aviso.setText(getString(R.string.descargando, pedida.version, 0));
        actualizador.descargarEInstalar(pedida, new Actualizador.AlDescargar() {
            @Override
            public void avance(int porcentaje) {
                aviso.setText(getString(R.string.descargando, pedida.version, porcentaje));
            }

            @Override
            public void instalando() {
                descargando = false;
                aviso.setText(R.string.confirmar_instalacion);
                programarRevision();
            }

            @Override
            public void fallo() {
                descargando = false;
                aviso.setText(R.string.descarga_fallida);
                programarRevision();
            }
        });
    }

    /** Corta consultas y descargas en curso (una sesión que llega, la app que se cierra, «No»). */
    private void detenerActualizaciones() {
        hiloPrincipal.removeCallbacks(revisar);
        actualizador.cancelar();
        descargando = false;
    }

    private String nombreDelEquipo() {
        String nombre = Settings.Global.getString(getContentResolver(), "device_name");
        return (nombre == null || nombre.isEmpty()) ? Build.MODEL : nombre;
    }
}
