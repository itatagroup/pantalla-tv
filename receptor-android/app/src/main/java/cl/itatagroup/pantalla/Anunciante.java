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

/**
 * Avisa por UDP broadcast, cada 2 s, que este receptor está abierto: «PANTALLA-TV 1 nombre puerto». Desde la 0.8.0
 * manda además, aparte, su pantalla: «PANTALLA-TV-PANTALLA 1 puerto ancho alto hz tipo», para que el emisor ajuste el
 * monitor virtual a ella. Va en otro datagrama porque los emisores anteriores leen el último dato del primero como
 * puerto: así lo ignoran sin dejar de ver la tele.
 */
final class Anunciante {
    private static final String TAG = "Pantalla";

    private final Context contexto;
    private final byte[] mensaje;
    private final byte[] pantalla;
    private volatile boolean activo;
    private Thread hilo;

    /** @param pantalla ancho, alto (en píxeles reales), Hz y tipo («tv», «tableta», «telefono»); null si no se sabe. */
    Anunciante(Context contexto, String nombre, String pantalla) {
        this.contexto = contexto.getApplicationContext();
        this.mensaje = ("PANTALLA-TV " + Protocolo.VERSION + " " + nombre + " " + Protocolo.PUERTO_TCP)
                .getBytes(StandardCharsets.UTF_8);
        this.pantalla = pantalla == null ? null
                : ("PANTALLA-TV-PANTALLA " + Protocolo.VERSION + " " + Protocolo.PUERTO_TCP + " " + pantalla)
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
                InetAddress todos = InetAddress.getByName("255.255.255.255");
                InetAddress dirigido = broadcastDeLaRed();
                for (byte[] m : pantalla == null ? new byte[][] {mensaje} : new byte[][] {mensaje, pantalla}) {
                    enviar(socket, todos, m);
                    if (dirigido != null) enviar(socket, dirigido, m);
                }
                Thread.sleep(2000);
            }
        } catch (InterruptedException e) {
            // detenido
        } catch (IOException e) {
            Log.w(TAG, "anunciante: " + e.getMessage());
        }
    }

    private void enviar(DatagramSocket socket, InetAddress destino, byte[] datos) {
        try {
            socket.send(new DatagramPacket(datos, datos.length, destino, Protocolo.PUERTO_UDP));
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
