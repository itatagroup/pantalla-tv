package cl.itatagroup.pantalla;

import android.content.Context;
import android.net.DhcpInfo;
import android.net.wifi.WifiManager;
import android.util.Log;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;

/** Avisa por UDP broadcast, cada 2 s, que este receptor está abierto: «PANTALLA-TV 1 nombre puerto». */
final class Anunciante {
    private static final String TAG = "Pantalla";

    private final Context contexto;
    private final byte[] mensaje;
    private volatile boolean activo;
    private Thread hilo;

    Anunciante(Context contexto, String nombre) {
        this.contexto = contexto.getApplicationContext();
        this.mensaje = ("PANTALLA-TV " + Protocolo.VERSION + " " + nombre + " " + Protocolo.PUERTO_TCP)
                .getBytes(StandardCharsets.UTF_8);
    }

    void iniciar() {
        activo = true;
        hilo = new Thread(this::bucle, "pantalla-anunciante");
        hilo.start();
    }

    void detener() {
        activo = false;
        if (hilo != null) hilo.interrupt();
    }

    private void bucle() {
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            while (activo) {
                enviar(socket, InetAddress.getByName("255.255.255.255"));
                InetAddress dirigido = broadcastDeLaRed();
                if (dirigido != null) enviar(socket, dirigido);
                Thread.sleep(2000);
            }
        } catch (InterruptedException e) {
            // detenido
        } catch (IOException e) {
            Log.w(TAG, "anunciante: " + e.getMessage());
        }
    }

    private void enviar(DatagramSocket socket, InetAddress destino) {
        try {
            socket.send(new DatagramPacket(mensaje, mensaje.length, destino, Protocolo.PUERTO_UDP));
        } catch (IOException e) {
            Log.w(TAG, "broadcast a " + destino + ": " + e.getMessage());
        }
    }

    /** Algunos routers descartan 255.255.255.255; el broadcast de la subred (x.x.x.255) casi siempre pasa. */
    private InetAddress broadcastDeLaRed() {
        try {
            WifiManager wifi = (WifiManager) contexto.getSystemService(Context.WIFI_SERVICE);
            DhcpInfo dhcp = wifi.getDhcpInfo();
            if (dhcp == null || dhcp.ipAddress == 0) return null;
            int difusion = (dhcp.ipAddress & dhcp.netmask) | ~dhcp.netmask; // enteros en orden little-endian
            byte[] b = {(byte) difusion, (byte) (difusion >> 8), (byte) (difusion >> 16), (byte) (difusion >> 24)};
            return InetAddress.getByAddress(b);
        } catch (Exception e) {
            return null;
        }
    }
}
