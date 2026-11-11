package cl.itatagroup.pantalla;

import android.util.Log;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Acepta conexiones en el puerto TCP. Un emisor a la vez: a los demás se les responde «ocupado».
 * Solo se aceptan direcciones de red local: nada va cifrado ni autenticado en la v1.
 */
final class Servidor {
    private static final String TAG = "Pantalla";

    private final Sesion.Pantalla pantalla;
    private final Sesion.Estado estado;
    private final AtomicBoolean ocupado = new AtomicBoolean(false);
    private volatile Socket activa;
    private ServerSocket servidor;

    Servidor(Sesion.Pantalla pantalla, Sesion.Estado estado) {
        this.pantalla = pantalla;
        this.estado = estado;
    }

    void iniciar() throws IOException {
        // Reutilizar la dirección hay que pedirlo ANTES de enlazar; si no, al volver rápido a la app
        // el puerto puede seguir «en uso».
        servidor = new ServerSocket();
        servidor.setReuseAddress(true);
        servidor.bind(new InetSocketAddress(Protocolo.PUERTO_TCP));
        new Thread(this::aceptar, "pantalla-servidor").start();
    }

    /** Deja de aceptar y corta la sesión en curso: al salir de la app no debe seguir sonando ni recibiendo. */
    void detener() {
        try {
            if (servidor != null) servidor.close();
        } catch (IOException ignorada) {
            // se cierra de todos modos
        }
        cerrar(activa);
    }

    private void aceptar() {
        while (!servidor.isClosed()) {
            try {
                Socket s = servidor.accept();
                if (!esDeLaRedLocal(s.getInetAddress())) {
                    Log.w(TAG, "conexión rechazada (no es de la red local): " + s.getInetAddress());
                    cerrar(s);
                } else if (ocupado.compareAndSet(false, true)) {
                    activa = s;
                    new Thread(new Sesion(s, pantalla, estado, () -> {
                        activa = null;
                        ocupado.set(false);
                    }), "pantalla-sesion").start();
                } else {
                    rechazarOcupado(s);
                }
            } catch (IOException e) {
                if (!servidor.isClosed()) Log.w(TAG, "accept: " + e.getMessage());
            }
        }
    }

    private static boolean esDeLaRedLocal(InetAddress a) {
        return a.isSiteLocalAddress() || a.isLoopbackAddress() || a.isLinkLocalAddress();
    }

    private void rechazarOcupado(Socket s) {
        try {
            DataOutputStream out = new DataOutputStream(s.getOutputStream());
            Protocolo.escribir(out, Protocolo.HELLO_ERR, 0, "ocupado".getBytes(StandardCharsets.UTF_8));
        } catch (IOException ignorada) {
            // el otro lado ya se fue
        } finally {
            cerrar(s);
        }
    }

    private static void cerrar(Socket s) {
        if (s == null) return;
        try {
            s.close();
        } catch (IOException ignorada) {
            // ya cerrado
        }
    }
}
