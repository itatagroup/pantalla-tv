// Copia en launcher-tv/app/src/main/java/cl/itatagroup/launcher/Actualizador.java; un arreglo aquí va también allá.
package cl.itatagroup.pantalla;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.Charset;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicInteger;
import org.json.JSONObject;

/**
 * Busca, descarga e instala una versión nueva de esta misma app según el contrato común de actualización
 * (manifiesto {@code version.json} firmado en el release de GitHub). Reglas, en orden:
 * solo HTTPS → firma válida → formato y repo → versionCode mayor → URL del release → tamaño y SHA-256 exactos →
 * instalación siempre visible, confirmada por Android.
 *
 * <p>Todo lo de red va en un hilo de fondo; las respuestas llegan en el hilo principal. El pedido es un GET sin
 * datos del equipo (solo un User-Agent genérico). {@link #cancelar()} descarta lo que esté en curso: sus
 * resultados ya no se entregan.
 */
public final class Actualizador {
    private static final String TAG = "Actualizador";

    /** Clave pública de las actualizaciones (SubjectPublicKeyInfo, base64). La privada nunca sale del PC que publica. */
    public static final String CLAVE_PUBLICA =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENdrCrDU1A0B87/zG/2dD80YPGfx8+gK2CclanFT9+8hPOlWFc9Q4zYXJqMEdQEd6bdsQ2eMgjqwhea0vcj95zA==";

    private static final int TIEMPO_MS = 10000;
    private static final int MAX_MANIFIESTO = 64 * 1024;
    /** Tope sano para un APK de estas apps (pesan decenas de KB): un manifiesto que pida más es un error. */
    private static final long MAX_APK = 64L * 1024 * 1024;
    private static final int MAX_REDIRECCIONES = 5;
    private static final int MAX_NOTAS = 280;
    private static final Charset UTF8 = Charset.forName("UTF-8");

    /** Una versión nueva ya validada (firma, repo, versión y URL). */
    public static final class Oferta {
        public final String version;
        public final long versionCode;
        public final String url;
        public final long tamano;
        public final String sha256;
        public final String notas;

        Oferta(String version, long versionCode, String url, long tamano, String sha256, String notas) {
            this.version = version;
            this.versionCode = versionCode;
            this.url = url;
            this.tamano = tamano;
            this.sha256 = sha256;
            this.notas = notas;
        }
    }

    /** Resultado de {@link #comprobar}, en el hilo principal. */
    public interface AlComprobar {
        void hayVersion(Oferta oferta);

        void alDia();

        /** Red, firma o manifiesto: el detalle queda en el registro, no se le muestra al usuario. */
        void fallo();
    }

    /** Avance de {@link #descargarEInstalar}, en el hilo principal. */
    public interface AlDescargar {
        void avance(int porcentaje);

        /** El APK está verificado y entregado a Android, que ahora pide la confirmación. */
        void instalando();

        void fallo();
    }

    private final Context contexto;
    private final String repo;
    private final String app;
    private final String agente;
    private final Handler principal = new Handler(Looper.getMainLooper());
    /** Cada operación toma un turno; al cancelar se avanza y las respuestas viejas se descartan. */
    private final AtomicInteger turno = new AtomicInteger();
    private volatile HttpURLConnection conexionActual;

    /**
     * @param repo   «dueño/repo» esperado en el manifiesto (p. ej. {@code itatagroup/pantalla-tv})
     * @param app    nombre de la entrada de esta app en {@code apps}
     * @param producto nombre para el User-Agent, que queda «producto/versión» (nada del equipo)
     */
    public Actualizador(Context contexto, String repo, String app, String producto) {
        this.contexto = contexto.getApplicationContext();
        this.repo = repo;
        this.app = app;
        this.agente = producto + "/" + versionNombre();
    }

    /** Consulta el manifiesto. Cancela lo que hubiera en curso. */
    public void comprobar(final String urlManifiesto, final AlComprobar oyente) {
        final int miTurno = cancelar();
        new Thread(() -> {
            Oferta oferta;
            try {
                oferta = buscar(urlManifiesto, miTurno);
            } catch (Exception e) {
                Log.w(TAG, "no se pudo comprobar la actualización: " + e);
                entregar(miTurno, oyente::fallo);
                return;
            }
            if (oferta == null) entregar(miTurno, oyente::alDia);
            else entregar(miTurno, () -> oyente.hayVersion(oferta));
        }, "actualizador").start();
    }

    /** Descarga, verifica y deja la instalación en manos de Android. Cancela lo que hubiera en curso. */
    public void descargarEInstalar(final Oferta oferta, final AlDescargar oyente) {
        final int miTurno = cancelar();
        new Thread(() -> {
            File carpeta = new File(contexto.getCacheDir(), "actualizacion");
            // Restos de un intento anterior (la app se cerró a mitad). El nombre lleva el turno para que un hilo
            // cancelado, al limpiar lo suyo, no borre el archivo del intento nuevo.
            vaciar(carpeta);
            File apk = new File(carpeta, app + "-" + oferta.versionCode + "-" + miTurno + ".apk");
            try {
                if (!carpeta.isDirectory() && !carpeta.mkdirs()) throw new IOException("no se pudo crear " + carpeta);
                descargar(oferta, apk, miTurno, oyente);
                instalar(apk, miTurno);
                entregar(miTurno, oyente::instalando);
            } catch (Exception e) {
                Log.w(TAG, "no se pudo descargar o instalar la actualización: " + e);
                entregar(miTurno, oyente::fallo);
            } finally {
                // Ya copiado a la sesión de instalación (o descartado): no se deja en el caché.
                if (apk.exists() && !apk.delete()) Log.w(TAG, "no se pudo borrar " + apk);
            }
        }, "actualizador").start();
    }

    /** Corta la conexión en curso y descarta sus resultados. Devuelve el turno nuevo. */
    public int cancelar() {
        int nuevo = turno.incrementAndGet();
        final HttpURLConnection c = conexionActual;
        conexionActual = null;
        // Se llama desde el hilo principal y cerrar un socket TLS puede tocar la red: va en otro hilo.
        if (c != null) new Thread(c::disconnect, "actualizador-corte").start();
        return nuevo;
    }

    /** Desde Android 8 cada app necesita el permiso «instalar apps desconocidas» para ofrecer una instalación. */
    public boolean puedeInstalar() {
        return Build.VERSION.SDK_INT < 26 || contexto.getPackageManager().canRequestPackageInstalls();
    }

    /** Abre el ajuste donde se da ese permiso a esta app (o el de seguridad, si el equipo no tiene el propio). */
    public static boolean abrirPermisoDeInstalacion(Activity actividad) {
        Intent propio = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                Uri.parse("package:" + actividad.getPackageName()));
        Intent general = new Intent(Settings.ACTION_SECURITY_SETTINGS);
        for (Intent intent : new Intent[] {propio, general}) {
            try {
                actividad.startActivity(intent);
                return true;
            } catch (ActivityNotFoundException | SecurityException e) {
                // Algunas TV recortan los Ajustes: se prueba el siguiente.
            }
        }
        return false;
    }

    public String versionNombre() {
        try {
            String nombre = contexto.getPackageManager().getPackageInfo(contexto.getPackageName(), 0).versionName;
            return nombre == null ? "?" : nombre;
        } catch (PackageManager.NameNotFoundException e) {
            return "?";
        }
    }

    @SuppressWarnings("deprecation")
    private long versionCodeInstalado() throws PackageManager.NameNotFoundException {
        PackageInfo info = contexto.getPackageManager().getPackageInfo(contexto.getPackageName(), 0);
        return Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode;
    }

    // --- pasos ---

    private Oferta buscar(String urlManifiesto, int miTurno) throws Exception {
        byte[] manifiesto = bajarChico(urlManifiesto, miTurno);
        byte[] firma = bajarChico(urlManifiesto + ".sig", miTurno);
        if (!FirmaManifiesto.verificarFirma(manifiesto, new String(firma, UTF8).trim(), CLAVE_PUBLICA)) {
            throw new IOException("firma del manifiesto no válida");
        }
        JSONObject raiz = new JSONObject(new String(manifiesto, UTF8));
        if (raiz.optInt("formato", 0) != 1) throw new IOException("formato de manifiesto desconocido");
        if (!repo.equals(raiz.optString("repo"))) throw new IOException("el manifiesto es de otro repo");
        JSONObject entrada = raiz.getJSONObject("apps").getJSONObject(app);

        long versionCode = entrada.getLong("versionCode");
        if (versionCode <= versionCodeInstalado()) return null;

        String url = entrada.getString("url");
        if (!url.startsWith("https://github.com/" + repo + "/releases/download/")) {
            throw new IOException("URL de descarga fuera del release: " + url);
        }
        long tamano = entrada.getLong("tamano");
        if (tamano <= 0 || tamano > MAX_APK) throw new IOException("tamaño fuera de rango: " + tamano);
        String sha256 = entrada.getString("sha256").toLowerCase(Locale.ROOT);
        if (!sha256.matches("[0-9a-f]{64}")) throw new IOException("sha256 mal formado");
        String notas = entrada.optString("notas", "").trim();
        if (notas.length() > MAX_NOTAS) notas = notas.substring(0, MAX_NOTAS) + "…";
        return new Oferta(entrada.getString("version"), versionCode, url, tamano, sha256, notas);
    }

    private void descargar(Oferta oferta, File destino, int miTurno, AlDescargar oyente) throws Exception {
        HttpURLConnection c = abrir(oferta.url, miTurno);
        MessageDigest sha = MessageDigest.getInstance("SHA-256");
        long total = 0;
        try {
            long declarado = largoDeclarado(c);
            if (declarado > oferta.tamano) throw new IOException("el servidor anuncia más bytes que el manifiesto");
            int ultimo = -1;
            byte[] bufer = new byte[64 * 1024];
            try (InputStream entrada = c.getInputStream(); OutputStream salida = new FileOutputStream(destino)) {
                int n;
                while ((n = entrada.read(bufer)) != -1) {
                    if (!vigente(miTurno)) throw new IOException("cancelado");
                    total += n;
                    if (total > oferta.tamano) throw new IOException("la descarga pasa del tamaño del manifiesto");
                    sha.update(bufer, 0, n);
                    salida.write(bufer, 0, n);
                    final int porcentaje = (int) (total * 100 / oferta.tamano);
                    if (porcentaje != ultimo) {
                        ultimo = porcentaje;
                        entregar(miTurno, () -> oyente.avance(porcentaje));
                    }
                }
            }
        } finally {
            c.disconnect();
        }
        if (total != oferta.tamano) throw new IOException("tamaño distinto: " + total + " de " + oferta.tamano);
        if (!hex(sha.digest()).equals(oferta.sha256)) throw new IOException("SHA-256 distinto");
    }

    /** Sesión de PackageInstaller: Android muestra su propia confirmación (ver ResultadoInstalacion). */
    private void instalar(File apk, int miTurno) throws Exception {
        PackageInstaller instalador = contexto.getPackageManager().getPackageInstaller();
        // Sesiones viejas de un intento anterior que no terminó: se descartan para no acumularlas.
        for (PackageInstaller.SessionInfo vieja : instalador.getMySessions()) {
            try {
                instalador.abandonSession(vieja.getSessionId());
            } catch (SecurityException e) {
                // Ya no es nuestra o ya terminó.
            }
        }
        PackageInstaller.SessionParams parametros =
                new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        parametros.setAppPackageName(contexto.getPackageName());
        parametros.setSize(apk.length());
        int id = instalador.createSession(parametros);
        PackageInstaller.Session sesion = instalador.openSession(id);
        try {
            try (OutputStream salida = sesion.openWrite("base.apk", 0, apk.length());
                    InputStream entrada = new FileInputStream(apk)) {
                byte[] bufer = new byte[64 * 1024];
                int n;
                while ((n = entrada.read(bufer)) != -1) salida.write(bufer, 0, n);
                sesion.fsync(salida);
            }
            if (!vigente(miTurno)) throw new IOException("cancelado");
            Intent aviso = new Intent(contexto, ResultadoInstalacion.class);
            int banderas = PendingIntent.FLAG_UPDATE_CURRENT;
            // El sistema completa el intent con el resultado: tiene que ser mutable (Android 12+ lo exige explícito).
            if (Build.VERSION.SDK_INT >= 31) banderas |= PendingIntent.FLAG_MUTABLE;
            PendingIntent pendiente = PendingIntent.getBroadcast(contexto, id, aviso, banderas);
            sesion.commit(pendiente.getIntentSender());
        } catch (Exception e) {
            sesion.abandon();
            throw e;
        } finally {
            sesion.close();
        }
    }

    // --- red ---

    /** GET con redirecciones manuales: todas tienen que seguir en HTTPS. */
    private HttpURLConnection abrir(String direccion, int miTurno) throws IOException {
        URL url = new URL(direccion);
        for (int salto = 0; salto <= MAX_REDIRECCIONES; salto++) {
            if (!"https".equalsIgnoreCase(url.getProtocol())) throw new IOException("solo se acepta HTTPS: " + url);
            if (!vigente(miTurno)) throw new IOException("cancelado");
            HttpURLConnection c = (HttpURLConnection) url.openConnection();
            c.setInstanceFollowRedirects(false);
            c.setConnectTimeout(TIEMPO_MS);
            c.setReadTimeout(TIEMPO_MS);
            c.setUseCaches(false);
            c.setRequestProperty("User-Agent", agente);
            // Sin compresión: el tamaño recibido tiene que ser el del archivo.
            c.setRequestProperty("Accept-Encoding", "identity");
            conexionActual = c;
            int codigo = c.getResponseCode();
            if (codigo >= 300 && codigo < 400) {
                String destino = c.getHeaderField("Location");
                c.disconnect();
                if (destino == null) throw new IOException("redirección sin destino");
                url = new URL(url, destino);
                continue;
            }
            if (codigo != HttpURLConnection.HTTP_OK) {
                c.disconnect();
                throw new IOException("HTTP " + codigo + " en " + url.getHost());
            }
            return c;
        }
        throw new IOException("demasiadas redirecciones");
    }

    private byte[] bajarChico(String direccion, int miTurno) throws IOException {
        HttpURLConnection c = abrir(direccion, miTurno);
        try (InputStream entrada = c.getInputStream()) {
            if (largoDeclarado(c) > MAX_MANIFIESTO) throw new IOException("archivo demasiado grande");
            ByteArrayOutputStream salida = new ByteArrayOutputStream();
            byte[] bufer = new byte[8192];
            int n;
            while ((n = entrada.read(bufer)) != -1) {
                if (salida.size() + n > MAX_MANIFIESTO) throw new IOException("archivo demasiado grande");
                salida.write(bufer, 0, n);
            }
            return salida.toByteArray();
        } finally {
            c.disconnect();
        }
    }

    /** Content-Length, o -1 si no viene (getContentLengthLong recién existe desde Android 7). */
    private static long largoDeclarado(HttpURLConnection c) {
        String valor = c.getHeaderField("Content-Length");
        if (valor == null) return -1;
        try {
            return Long.parseLong(valor.trim());
        } catch (NumberFormatException e) {
            return -1;
        }
    }

    // --- apoyo ---

    private boolean vigente(int miTurno) {
        return turno.get() == miTurno;
    }

    private void entregar(final int miTurno, final Runnable accion) {
        principal.post(() -> {
            if (vigente(miTurno)) accion.run();
        });
    }

    private static void vaciar(File carpeta) {
        File[] archivos = carpeta.listFiles();
        if (archivos == null) return;
        for (File f : archivos) {
            if (!f.delete()) Log.w(TAG, "no se pudo borrar " + f);
        }
    }

    private static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) sb.append(String.format(Locale.ROOT, "%02x", b & 0xff));
        return sb.toString();
    }
}
