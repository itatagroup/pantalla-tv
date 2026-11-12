package cl.itatagroup.pantalla.emisor;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Patterns;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import cl.itatagroup.actualizacion.Actualizador;
import java.util.List;

/**
 * La pantalla del emisor: elegir la tele (las que se anuncian en la red, o su IP a mano), las
 * opciones y un botón para transmitir o detener. Funciona con el dedo y con control remoto.
 */
public class Principal extends Activity {
    private static final int PEDIR_PANTALLA = 1;
    private static final int PEDIR_SONIDO = 2;
    private static final String PREFS = "emisor";

    /** Lado mayor de la imagen enviada, y la tasa que le corresponde a 30 fps (a 60 se sube un 50 %). */
    private static final int[] LADOS = {1280, 1920};
    private static final String[] CALIDADES = {"720p (recomendada)", "1080p"};
    private static final int[] KBPS = {4000, 8000};
    private static final int[] FPS = {30, 60};
    /** El manifiesto propio de esta app (docs/actualizaciones.md); solo se pide al pulsar «Buscar actualización». */
    private static final String MANIFIESTO =
            "https://github.com/itatagroup/pantalla-tv/releases/download/actualizaciones/emisor-android.json";

    private final Handler reloj = new Handler(Looper.getMainLooper());
    private SharedPreferences prefs;
    private Descubridor descubridor;
    private TextView estado;
    private Button accion;
    private EditText ip;
    private LinearLayout teles;
    private TextView buscando;
    private Button bCalidad;
    private Button bFluidez;
    private Button bSonido;
    private Button bBuscar;
    private Button bActualizar;
    private TextView avisoActualizacion;
    private Actualizador actualizador;
    private Actualizador.Oferta oferta;
    private boolean buscandoVersion;
    private boolean descargando;

    private int calidad;
    private int fluidez;
    private boolean sonido;
    private String ipElegida = "";

    private final Runnable refrescar = new Runnable() {
        @Override
        public void run() {
            pintar();
            reloj.postDelayed(this, 1000);
        }
    };

    @Override
    protected void onCreate(Bundle guardado) {
        super.onCreate(guardado);
        setContentView(R.layout.activity_principal);
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        calidad = acotar(prefs.getInt("calidad", 0), LADOS.length);
        fluidez = acotar(prefs.getInt("fluidez", 0), FPS.length);
        sonido = prefs.getBoolean("sonido", true);
        ipElegida = prefs.getString("ip", "");
        // Para pruebas por adb: am start -n …/.Principal --es ip 10.0.0.20
        String pedida = getIntent().getStringExtra("ip");
        if (pedida != null) ipElegida = pedida;

        estado = (TextView) findViewById(R.id.estado);
        accion = (Button) findViewById(R.id.accion);
        ip = (EditText) findViewById(R.id.ip);
        teles = (LinearLayout) findViewById(R.id.teles);
        buscando = (TextView) findViewById(R.id.buscando);
        ip.setText(ipElegida);
        accion.setOnClickListener(v -> alPulsarAccion());

        LinearLayout opciones = (LinearLayout) findViewById(R.id.opciones);
        bCalidad = opcion(opciones, v -> { calidad = (calidad + 1) % LADOS.length; guardar(); });
        bFluidez = opcion(opciones, v -> { fluidez = (fluidez + 1) % FPS.length; guardar(); });
        bSonido = opcion(opciones, v -> { sonido = !sonido; guardar(); });

        actualizador = new Actualizador(this, "itatagroup/pantalla-tv", "emisor-android", "EnviarAPantalla");
        LinearLayout actualizaciones = (LinearLayout) findViewById(R.id.actualizaciones);
        bBuscar = opcion(actualizaciones, v -> buscarActualizacion());
        bActualizar = opcion(actualizaciones, v -> actualizar());
        bActualizar.setVisibility(View.GONE);
        avisoActualizacion = (TextView) findViewById(R.id.aviso_actualizacion);
        avisoActualizacion.setText(getString(R.string.nota_actualizaciones, actualizador.versionNombre()));

        descubridor = new Descubridor(this);
        accion.requestFocus();
    }

    @Override
    protected void onStart() {
        super.onStart();
        descubridor.iniciar();
        reloj.post(refrescar);
    }

    /**
     * Con control remoto, al volver del aviso de captura de Android (o de otra app) ninguna vista tiene
     * el foco y no habría cómo pulsar «Detener»: se le devuelve al botón principal.
     */
    @Override
    protected void onResume() {
        super.onResume();
        accion.requestFocus();
    }

    @Override
    protected void onStop() {
        // Una búsqueda o descarga a medias se corta (también al abrir los ajustes del permiso de instalación).
        actualizador.cancelar();
        if (buscandoVersion || descargando) {
            // Lo que estaba en curso ya no va a terminar: no dejar «Descargando… 40 %» a la vista al volver.
            avisoActualizacion.setText(getString(R.string.nota_actualizaciones, actualizador.versionNombre()));
        }
        buscandoVersion = false;
        descargando = false;
        reloj.removeCallbacks(refrescar);
        descubridor.detener();
        super.onStop();
    }

    private static int acotar(int v, int n) {
        return v < 0 || v >= n ? 0 : v;
    }

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics());
    }

    /** Un botón de la lista de opciones (o de teles): ancho completo, texto a la izquierda. */
    private Button opcion(LinearLayout padre, View.OnClickListener alPulsar) {
        Button b = new Button(this);
        b.setAllCaps(false);
        b.setBackgroundResource(R.drawable.opcion);
        b.setTextColor(getColor(R.color.texto));
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        b.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        b.setPadding(dp(14), 0, dp(14), 0);
        b.setOnClickListener(alPulsar);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48));
        lp.topMargin = dp(6);
        padre.addView(b, lp);
        return b;
    }

    private void guardar() {
        prefs.edit().putInt("calidad", calidad).putInt("fluidez", fluidez)
                .putBoolean("sonido", sonido).putString("ip", ipElegida).apply();
        pintar();
    }

    /** Pone en pantalla el estado, las opciones y las teles encontradas. */
    private void pintar() {
        boolean activo = ServicioEmision.activo;
        estado.setText(ServicioEmision.estado.isEmpty() ? getString(R.string.sin_transmitir) : ServicioEmision.estado);
        accion.setText(activo ? getString(R.string.detener)
                : ipElegida.isEmpty() ? getString(R.string.transmitir) : getString(R.string.transmitir_a, ipElegida));
        bCalidad.setText(getString(R.string.calidad, CALIDADES[calidad]));
        bFluidez.setText(getString(R.string.fluidez, FPS[fluidez]));
        bSonido.setText(sonido ? R.string.sonido_si : R.string.sonido_no);
        for (Button b : new Button[] {bCalidad, bFluidez, bSonido}) b.setEnabled(!activo);
        // Mientras transmite no se busca ni se instala nada.
        bBuscar.setText(buscandoVersion ? R.string.buscando_actualizacion : R.string.buscar);
        bBuscar.setEnabled(!activo && !buscandoVersion && !descargando);
        bActualizar.setVisibility(oferta != null ? View.VISIBLE : View.GONE);
        if (oferta != null) bActualizar.setText(getString(R.string.actualizar_a, oferta.version));
        bActualizar.setEnabled(!activo && !descargando);
        ip.setEnabled(!activo);

        List<Descubridor.Tele> vistas = descubridor.vigentes();
        buscando.setVisibility(vistas.isEmpty() ? View.VISIBLE : View.GONE);
        // Solo se rehace la lista si cambió: rehacerla cada segundo le quitaría el foco al control remoto.
        StringBuilder firma = new StringBuilder();
        for (Descubridor.Tele t : vistas) firma.append(t.ip).append('|').append(t.nombre).append(';');
        firma.append(ipElegida);
        if (!firma.toString().equals(teles.getTag())) {
            teles.setTag(firma.toString());
            teles.removeAllViews();
            for (Descubridor.Tele t : vistas) {
                Button b = opcion(teles, v -> {
                    ipElegida = t.ip;
                    ip.setText(t.ip);
                    guardar();
                });
                b.setText(t.nombre + "  ·  " + t.ip);
                b.setSelected(t.ip.equals(ipElegida));
            }
        }
        for (int i = 0; i < teles.getChildCount(); i++) teles.getChildAt(i).setEnabled(!activo);
    }

    private void alPulsarAccion() {
        if (ServicioEmision.activo) {
            startService(new Intent(this, ServicioEmision.class).setAction(ServicioEmision.ACCION_DETENER));
            return;
        }
        String escrita = ip.getText().toString().trim();
        if (escrita.isEmpty()) {
            Toast.makeText(this, R.string.falta_ip, Toast.LENGTH_LONG).show();
            return;
        }
        if (!Patterns.IP_ADDRESS.matcher(escrita).matches()) {
            Toast.makeText(this, R.string.ip_invalida, Toast.LENGTH_LONG).show();
            return;
        }
        ipElegida = escrita;
        guardar();
        if (sonido && checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] {android.Manifest.permission.RECORD_AUDIO}, PEDIR_SONIDO);
            return;
        }
        pedirPantalla();
    }

    @Override
    public void onRequestPermissionsResult(int codigo, String[] permisos, int[] resultados) {
        if (codigo != PEDIR_SONIDO) return;
        if (resultados.length == 0 || resultados[0] != PackageManager.PERMISSION_GRANTED)
            Toast.makeText(this, R.string.sin_permiso_sonido, Toast.LENGTH_LONG).show();
        pedirPantalla(); // con o sin sonido, se transmite
    }

    /** Android muestra su propio aviso («¿empezar a grabar o transmitir?»); la respuesta llega a onActivityResult. */
    private void pedirPantalla() {
        MediaProjectionManager gestor = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        startActivityForResult(gestor.createScreenCaptureIntent(), PEDIR_PANTALLA);
    }

    @Override
    protected void onActivityResult(int codigo, int resultado, Intent datos) {
        if (codigo != PEDIR_PANTALLA) return;
        if (resultado != RESULT_OK || datos == null) {
            Toast.makeText(this, R.string.sin_permiso_pantalla, Toast.LENGTH_LONG).show();
            return;
        }
        int fps = FPS[fluidez];
        Intent servicio = new Intent(this, ServicioEmision.class)
                .setAction(ServicioEmision.ACCION_INICIAR)
                .putExtra(ServicioEmision.EXTRA_RESULTADO, resultado)
                .putExtra(ServicioEmision.EXTRA_PERMISO, datos)
                .putExtra(ServicioEmision.EXTRA_IP, ipElegida)
                .putExtra(ServicioEmision.EXTRA_PUERTO, Protocolo.PUERTO_TCP)
                .putExtra(ServicioEmision.EXTRA_LADO, LADOS[calidad])
                .putExtra(ServicioEmision.EXTRA_FPS, fps)
                .putExtra(ServicioEmision.EXTRA_KBPS, fps > 30 ? KBPS[calidad] * 3 / 2 : KBPS[calidad])
                .putExtra(ServicioEmision.EXTRA_SONIDO, sonido);
        startForegroundService(servicio);
        pintar();
        accion.requestFocus();
    }

    // --- actualizaciones (solo cuando se pulsa «Buscar actualización»; ver docs/actualizaciones.md) ---

    private void buscarActualizacion() {
        if (ServicioEmision.activo || buscandoVersion || descargando) return;
        buscandoVersion = true;
        pintar();
        actualizador.comprobar(MANIFIESTO, new Actualizador.AlComprobar() {
            @Override
            public void hayVersion(Actualizador.Oferta nueva) {
                buscandoVersion = false;
                oferta = nueva;
                avisoActualizacion.setText(getString(R.string.hay_version, nueva.version, nueva.notas));
                pintar();
                bActualizar.requestFocus();
            }

            @Override
            public void alDia() {
                buscandoVersion = false;
                oferta = null;
                avisoActualizacion.setText(getString(R.string.al_dia, actualizador.versionNombre()));
                pintar();
            }

            @Override
            public void fallo() {
                // El detalle queda en el registro («Actualizador» en logcat).
                buscandoVersion = false;
                avisoActualizacion.setText(R.string.no_se_pudo_comprobar);
                pintar();
            }
        });
    }

    private void actualizar() {
        if (oferta == null || descargando || ServicioEmision.activo) return;
        if (!actualizador.puedeInstalar()) {
            boolean abierto = Actualizador.abrirPermisoDeInstalacion(this);
            avisoActualizacion.setText(abierto ? R.string.falta_permiso : R.string.sin_ajuste_permiso);
            return;
        }
        descargando = true;
        final Actualizador.Oferta pedida = oferta;
        avisoActualizacion.setText(getString(R.string.descargando, pedida.version, 0));
        pintar();
        actualizador.descargarEInstalar(pedida, new Actualizador.AlDescargar() {
            @Override
            public void avance(int porcentaje) {
                avisoActualizacion.setText(getString(R.string.descargando, pedida.version, porcentaje));
            }

            @Override
            public void instalando() {
                descargando = false;
                avisoActualizacion.setText(R.string.confirmar_instalacion);
                pintar();
            }

            @Override
            public void fallo() {
                descargando = false;
                avisoActualizacion.setText(R.string.descarga_fallida);
                pintar();
            }
        });
    }
}
