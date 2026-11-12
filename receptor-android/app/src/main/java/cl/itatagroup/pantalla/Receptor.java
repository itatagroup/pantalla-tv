package cl.itatagroup.pantalla;

import android.app.Activity;
import android.app.UiModeManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
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
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import cl.itatagroup.actualizacion.Actualizador;

/**
 * Pantalla del receptor: mientras está abierta, escucha y se anuncia; al recibir vídeo lo muestra completo.
 * La misma app sirve en una TV (control remoto, siempre horizontal) y en un teléfono o tablet (táctil: orientación
 * libre en espera y según la imagen al transmitir, y una barra con «Terminar» que aparece al tocar).
 */
public class Receptor extends Activity {
    private static final String TAG = "Pantalla";
    /**
     * Búfer: se guardan los cuadros y se muestran este tiempo después, a ritmo parejo, para que los tirones del Wi-Fi
     * no se noten. 0 = ninguno (lo más en vivo); 2 s = vídeos con una red mala. Se alternan con OK o con el botón.
     */
    private static final int[] RETARDOS_MS = {0, 500, 1000, 2000};
    /** Por defecto medio segundo (lo que antes pedía el emisor). El monitor virtual del PC va siempre sin búfer. */
    private static final int BUFER_POR_DEFECTO_MS = 500;
    private static final String PREFS = "pantalla";
    private static final String PREF_RETARDO = "retardo_ms";
    /** Táctil: 0 = automática (según la imagen al transmitir), 1 = siempre horizontal, 2 = siempre vertical. */
    private static final String PREF_ORIENTACION = "orientacion";
    /** Cuánto queda a la vista la barra de la sesión (táctil) después de tocar. */
    private static final long BARRA_MS = 4000;
    /**
     * El manifiesto propio del receptor (contrato común de actualización, docs/actualizaciones.md): cada app del
     * repo tiene el suyo en el release fijo «actualizaciones», así sus versiones no dependen de las otras.
     */
    private static final String MANIFIESTO =
            "https://github.com/itatagroup/pantalla-tv/releases/download/actualizaciones/receptor-android.json";

    private volatile Surface superficie;
    private volatile int retardoElegido;
    private TextView textoEstado;
    /** Solo en el hilo de la interfaz: hay una sesión en curso (el texto de espera no debe volver). */
    private boolean enSesion;
    private SurfaceView video;
    private WifiManager.WifiLock bloqueoWifi;
    private Servidor servidor;
    private Anunciante anunciante;

    /** TV (control remoto) o teléfono/tablet (táctil). Se decide una vez al abrir. */
    private boolean esTv;
    /** Tamaño de la imagen de la sesión en curso (0×0 sin sesión): para reencajarla al girar el equipo. */
    private int anchoImagen;
    private int altoImagen;
    private View barraSesion;
    private Button sesionRetardo;
    private Button botonOrientacion;
    private Button sesionOrientacion;
    private int orientacion;
    /** El búfer de la sesión en curso (-1 sin sesión): una interactiva va en 0 aunque el elegido sea otro. */
    private int buferSesionMs = -1;
    private final Handler hiloPrincipal = new Handler(Looper.getMainLooper());
    private final Runnable ocultarBarra = () -> barraSesion.setVisibility(View.GONE);

    // Actualizaciones: solo cuando se pulsa «Buscar actualización» (nunca sola), todo en el hilo de la interfaz y
    // solo en la pantalla de espera.
    private View controles;
    private TextView aviso;
    private Button botonRetardo;
    private Button botonActualizar;
    private Button botonBuscar;
    private Actualizador actualizador;
    private Actualizador.Oferta oferta;
    private boolean buscando;
    private boolean descargando;

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
        public void conectado(String desde, int ancho, int alto, int buferMs) {
            runOnUiThread(() -> {
                buferSesionMs = buferMs;
                // La superficie aún guarda el último cuadro de la sesión anterior: se queda tapada (fondo
                // sólido, sin texto) hasta que llegue el primer cuadro de esta.
                enSesion = true;
                // Una sesión manda: se corta cualquier consulta o descarga y se esconden los botones.
                detenerActualizaciones();
                controles.setVisibility(View.GONE);
                textoEstado.setText("");
                textoEstado.setVisibility(View.VISIBLE);
                ajustarProporcion(ancho, alto);
                if (!esTv) {
                    // Táctil: la pantalla no se apaga mientras se transmite, y se gira según la imagen
                    // (salvo que la orientación esté fijada).
                    getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                    aplicarOrientacion();
                }
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
                buferSesionMs = -1;
                ajustarProporcion(0, 0);
                if (!esTv) {
                    hiloPrincipal.removeCallbacks(ocultarBarra);
                    barraSesion.setVisibility(View.GONE);
                    getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                    aplicarOrientacion();
                }
                mostrarEsperando();
            });
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        esTv = esTelevision();
        if (esTv) {
            // En la TV la pantalla de espera también queda encendida (el salvapantallas taparía el aviso).
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
        }
        setContentView(R.layout.activity_receptor);
        textoEstado = (TextView) findViewById(R.id.estado);
        retardoElegido = indiceDeRetardo(getSharedPreferences(PREFS, MODE_PRIVATE).getInt(PREF_RETARDO, BUFER_POR_DEFECTO_MS));
        video = (SurfaceView) findViewById(R.id.video);
        controles = findViewById(R.id.controles);
        aviso = (TextView) findViewById(R.id.aviso);
        botonRetardo = (Button) findViewById(R.id.boton_retardo);
        botonActualizar = (Button) findViewById(R.id.boton_actualizar);
        botonBuscar = (Button) findViewById(R.id.boton_buscar);
        botonRetardo.setOnClickListener(v -> fijarRetardo((retardoElegido + 1) % RETARDOS_MS.length));
        botonBuscar.setOnClickListener(v -> buscarActualizacion());
        botonActualizar.setOnClickListener(v -> actualizar());
        actualizador = new Actualizador(this, "itatagroup/pantalla-tv", "receptor-android", "Pantalla");
        barraSesion = findViewById(R.id.barra_sesion);
        sesionRetardo = (Button) findViewById(R.id.sesion_retardo);
        if (!esTv) prepararTactil();
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
     * OK en el control remoto alterna el búfer: 0 → 0,5 s → 1 s → 2 s → 0. En la pantalla de espera lo hace el
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

    /** Atrás durante una sesión la termina (y vuelve a la espera); sin sesión, sale como siempre. */
    @Override
    public void onBackPressed() {
        if (enSesion && servidor != null) {
            servidor.terminarSesion();
            return;
        }
        super.onBackPressed();
    }

    @Override
    public void onConfigurationChanged(Configuration nueva) {
        super.onConfigurationChanged(nueva);
        // Giró el equipo: la imagen se vuelve a encajar cuando el contenedor ya tiene su tamaño nuevo.
        View padre = (View) video.getParent();
        padre.post(() -> ajustarProporcion(anchoImagen, altoImagen));
        if (!esTv) disponerTactil();
    }

    /**
     * Botones de la espera en un teléfono o tablet: en vertical, en columna y del mismo ancho; en horizontal, en
     * fila. Siempre con aire entre uno y otro, y el texto de espera más arriba para que no quede debajo.
     */
    private void disponerTactil() {
        float densidad = getResources().getDisplayMetrics().density;
        boolean vertical = getResources().getConfiguration().orientation == Configuration.ORIENTATION_PORTRAIT;
        int separacion = Math.round(8 * densidad);
        // Lo mismo para los botones de la espera y para la barra de la sesión.
        for (int id : new int[] {R.id.fila_botones, R.id.barra_sesion}) {
            LinearLayout fila = (LinearLayout) findViewById(id);
            fila.setOrientation(vertical ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL);
            for (int i = 0; i < fila.getChildCount(); i++) {
                Button b = (Button) fila.getChildAt(i);
                b.setTextSize(vertical ? 18 : 15);
                LinearLayout.LayoutParams lp = (LinearLayout.LayoutParams) b.getLayoutParams();
                lp.width = vertical ? ViewGroup.LayoutParams.MATCH_PARENT : ViewGroup.LayoutParams.WRAP_CONTENT;
                lp.topMargin = lp.bottomMargin = vertical ? separacion : 0;
                lp.leftMargin = lp.rightMargin = separacion;
                b.setLayoutParams(lp);
            }
        }
        textoEstado.setTextSize(vertical ? 22 : 20);
        int lado = Math.round(24 * densidad);
        // Lo que ocupan los botones abajo: en vertical, tres en columna; en horizontal, una fila.
        int abajo = Math.round((vertical ? 300 : 130) * densidad);
        textoEstado.setPadding(lado, 0, lado, abajo);
    }

    private boolean esTelevision() {
        UiModeManager modo = (UiModeManager) getSystemService(Context.UI_MODE_SERVICE);
        return (modo != null && modo.getCurrentModeType() == Configuration.UI_MODE_TYPE_TELEVISION)
                || getPackageManager().hasSystemFeature(PackageManager.FEATURE_LEANBACK);
    }

    /** Teléfono o tablet: botones en columna, texto más chico y la barra de la sesión que aparece al tocar. */
    private void prepararTactil() {
        disponerTactil();
        sesionRetardo.setOnClickListener(v -> {
            fijarRetardo((retardoElegido + 1) % RETARDOS_MS.length);
            mostrarBarra();
        });
        orientacion = getSharedPreferences(PREFS, MODE_PRIVATE).getInt(PREF_ORIENTACION, 0) % 3;
        botonOrientacion = (Button) findViewById(R.id.boton_orientacion);
        sesionOrientacion = (Button) findViewById(R.id.sesion_orientacion);
        botonOrientacion.setVisibility(View.VISIBLE);
        botonOrientacion.setOnClickListener(v -> cambiarOrientacion());
        sesionOrientacion.setOnClickListener(v -> {
            cambiarOrientacion();
            mostrarBarra();
        });
        aplicarOrientacion();
        findViewById(R.id.sesion_terminar).setOnClickListener(v -> {
            if (servidor != null) servidor.terminarSesion();
        });
        View.OnClickListener tocar = v -> {
            if (!enSesion) return;
            if (barraSesion.getVisibility() == View.VISIBLE) {
                hiloPrincipal.removeCallbacks(ocultarBarra);
                barraSesion.setVisibility(View.GONE);
            } else {
                mostrarBarra();
            }
        };
        video.setOnClickListener(tocar);
        textoEstado.setOnClickListener(tocar);
        ((View) video.getParent()).setOnClickListener(tocar);
    }

    /** Automática → horizontal → vertical. Se recuerda para la próxima vez. */
    private void cambiarOrientacion() {
        orientacion = (orientacion + 1) % 3;
        getSharedPreferences(PREFS, MODE_PRIVATE).edit().putInt(PREF_ORIENTACION, orientacion).apply();
        aplicarOrientacion();
    }

    /**
     * Fijada: siempre esa (se puede dar vuelta 180°, nunca pasar a la otra). Automática: libre en la espera y, con
     * una sesión, según la proporción de la imagen.
     */
    private void aplicarOrientacion() {
        int pedida;
        if (orientacion == 1) pedida = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE;
        else if (orientacion == 2) pedida = ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT;
        else if (enSesion && anchoImagen > 0 && altoImagen > 0) {
            pedida = anchoImagen >= altoImagen
                    ? ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    : ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT;
        } else pedida = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED;
        setRequestedOrientation(pedida);
        int texto = orientacion == 1 ? R.string.orientacion_horizontal
                : orientacion == 2 ? R.string.orientacion_vertical : R.string.orientacion_auto;
        botonOrientacion.setText(texto);
        sesionOrientacion.setText(texto);
    }

    private void mostrarBarra() {
        // En una sesión interactiva (el monitor virtual) el búfer va en 0: el botón lo dice y no se puede cambiar.
        boolean interactiva = buferSesionMs == 0 && RETARDOS_MS[retardoElegido] != 0;
        sesionRetardo.setEnabled(!interactiva);
        sesionRetardo.setText(interactiva ? getString(R.string.bufer_interactivo)
                : getString(R.string.retardo_boton, descripcionRetardo()));
        barraSesion.setVisibility(View.VISIBLE);
        hiloPrincipal.removeCallbacks(ocultarBarra);
        hiloPrincipal.postDelayed(ocultarBarra, BARRA_MS);
    }

    private void fijarRetardo(int indice) {
        retardoElegido = indice;
        getSharedPreferences(PREFS, MODE_PRIVATE).edit().putInt(PREF_RETARDO, RETARDOS_MS[indice]).apply();
        botonRetardo.setText(getString(R.string.retardo_boton, descripcionRetardo()));
        if (sesionRetardo != null) sesionRetardo.setText(getString(R.string.retardo_boton, descripcionRetardo()));
        // El búfer de una sesión se fija al empezar: el cambio vale para la siguiente.
        Toast.makeText(this, getString(enSesion ? R.string.retardo_aviso_sesion : R.string.retardo_aviso, descripcionRetardo()),
                Toast.LENGTH_SHORT).show();
    }

    private String descripcionRetardo() {
        return describirBufer(RETARDOS_MS[retardoElegido]);
    }

    private String describirBufer(int ms) {
        return ms == 0 ? getString(R.string.retardo_ninguno)
                : String.format(java.util.Locale.forLanguageTag("es"), "%.1f s", ms / 1000.0).replace(",0 s", " s");
    }

    private static int indiceDeRetardo(int ms) {
        for (int i = 0; i < RETARDOS_MS.length; i++) if (RETARDOS_MS[i] == ms) return i;
        return 0;
    }

    @Override
    protected void onStop() {
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
        anchoImagen = ancho;
        altoImagen = alto;
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
        botonBuscar.setText(buscando ? R.string.buscando : R.string.buscar);
        mostrarOferta();
        controles.setVisibility(View.VISIBLE);
        View foco = getCurrentFocus();
        if (foco == null || !foco.isShown()) botonRetardo.requestFocus();
    }

    // --- actualizaciones (ver Actualizador y el README, «Actualizaciones») ---

    /** «Buscar actualización»: la única forma en que el receptor se conecta a internet. */
    private void buscarActualizacion() {
        if (buscando || descargando || enSesion) return;
        buscando = true;
        botonBuscar.setText(R.string.buscando);
        actualizador.comprobar(urlManifiesto(), new Actualizador.AlComprobar() {
            @Override
            public void hayVersion(Actualizador.Oferta nueva) {
                terminarBusqueda();
                oferta = nueva;
                mostrarOferta();
                botonActualizar.requestFocus();
            }

            @Override
            public void alDia() {
                terminarBusqueda();
                oferta = null;
                mostrarOferta();
                avisar(getString(R.string.al_dia, actualizador.versionNombre()));
            }

            @Override
            public void fallo() {
                // El detalle queda en el registro («Actualizador» en logcat).
                terminarBusqueda();
                avisar(getString(R.string.no_se_pudo_comprobar));
            }
        });
    }

    private void terminarBusqueda() {
        buscando = false;
        botonBuscar.setText(R.string.buscar);
    }

    private void avisar(String texto) {
        aviso.setText(texto);
        aviso.setVisibility(View.VISIBLE);
    }

    /** Para pruebas: {@code am start -n cl.itatagroup.pantalla/.Receptor --es manifiesto_url https://…}. */
    private String urlManifiesto() {
        String pedida = getIntent().getStringExtra("manifiesto_url");
        return pedida != null && !pedida.isEmpty() ? pedida : MANIFIESTO;
    }

    private void mostrarOferta() {
        if (descargando || buscando) return;
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
            }

            @Override
            public void fallo() {
                descargando = false;
                aviso.setText(R.string.descarga_fallida);
            }
        });
    }

    /** Corta la búsqueda o la descarga en curso (una sesión que llega, la app que se cierra). */
    private void detenerActualizaciones() {
        actualizador.cancelar();
        if (buscando) terminarBusqueda();
        descargando = false;
    }

    private String nombreDelEquipo() {
        String nombre = Settings.Global.getString(getContentResolver(), "device_name");
        return (nombre == null || nombre.isEmpty()) ? Build.MODEL : nombre;
    }
}
