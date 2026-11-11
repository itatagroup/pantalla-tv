package cl.itatagroup.pantalla.emisor;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.SystemClock;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Escucha los avisos UDP de las teles con «Pantalla» abierta («PANTALLA-TV 1 nombre puerto», cada 2 s). */
final class Descubridor {
    /** Una tele deja de listarse si no avisa en este tiempo. */
    private static final long VIGENCIA_MS = 7000;

    static final class Tele {
        final String nombre;
        final String ip;
        final int puerto;
        long vistaMs;

        Tele(String nombre, String ip, int puerto) {
            this.nombre = nombre;
            this.ip = ip;
            this.puerto = puerto;
        }
    }

    private final Map<String, Tele> vistas = new LinkedHashMap<>();
    private final WifiManager.MulticastLock candado;
    private volatile DatagramSocket socket;
    private volatile boolean activo;

    Descubridor(Context contexto) {
        WifiManager wifi = (WifiManager) contexto.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        // Algunos teléfonos descartan la difusión UDP para ahorrar batería si no se pide este candado.
        candado = wifi.createMulticastLock("pantalla-emisor");
        candado.setReferenceCounted(false);
    }

    void iniciar() {
        if (activo) return;
        activo = true;
        candado.acquire();
        new Thread(this::escuchar, "emisor-descubridor").start();
    }

    void detener() {
        activo = false;
        DatagramSocket s = socket;
        if (s != null) s.close();
        if (candado.isHeld()) candado.release();
    }

    /** Las teles vistas hace poco, en el orden en que aparecieron. */
    List<Tele> vigentes() {
        long limite = SystemClock.elapsedRealtime() - VIGENCIA_MS;
        List<Tele> r = new ArrayList<>();
        synchronized (vistas) {
            for (Tele t : vistas.values()) if (t.vistaMs >= limite) r.add(t);
        }
        return r;
    }

    private void escuchar() {
        try (DatagramSocket s = new DatagramSocket(null)) {
            s.setReuseAddress(true);
            s.bind(new InetSocketAddress(Protocolo.PUERTO_UDP));
            socket = s;
            byte[] buf = new byte[256];
            while (activo) {
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                s.receive(p);
                Tele t = interpretar(new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8),
                        p.getAddress().getHostAddress());
                if (t == null) continue;
                t.vistaMs = SystemClock.elapsedRealtime();
                synchronized (vistas) {
                    vistas.put(t.ip + ":" + t.puerto, t);
                }
            }
        } catch (Exception e) {
            // socket cerrado al detener, o el puerto ocupado: queda escribir la IP a mano
        }
    }

    /** «PANTALLA-TV 1 nombre con espacios 5600» → tele; null si no es un aviso válido. */
    static Tele interpretar(String texto, String ip) {
        String[] p = texto.trim().split("\\s+");
        if (p.length < 4 || !p[0].equals("PANTALLA-TV") || !p[1].equals(String.valueOf(Protocolo.VERSION))) return null;
        int puerto;
        try {
            puerto = Integer.parseInt(p[p.length - 1]);
        } catch (NumberFormatException e) {
            return null;
        }
        if (puerto < 1 || puerto > 65535) return null;
        StringBuilder nombre = new StringBuilder();
        for (int i = 2; i < p.length - 1; i++) {
            if (nombre.length() > 0) nombre.append(' ');
            nombre.append(p[i]);
        }
        return new Tele(nombre.toString(), ip, puerto);
    }
}
