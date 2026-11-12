// Común a las apps Android de este repo (receptor y emisor): build-android.mjs compila android-comun/java en las dos.
// launcher-tv tiene una copia; un arreglo aquí va también allá.
package cl.itatagroup.actualizacion;

import android.content.ActivityNotFoundException;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.util.Log;
import android.widget.Toast;

/**
 * Recibe el resultado de la sesión de instalación que abre {@link Actualizador}. No está exportado: solo le llega
 * el PendingIntent propio que completa el sistema. Cuando Android pide la confirmación del usuario
 * (lo normal para una app que no es del sistema), abre su pantalla: nunca se instala nada en silencio.
 */
public class ResultadoInstalacion extends BroadcastReceiver {
    private static final String TAG = "Actualizador";
    /** Sin recursos propios: esta clase se comparte entre apps con distinto paquete de R. */
    private static final String NO_INSTALADA = "No se pudo instalar la actualización";

    @Override
    @SuppressWarnings("deprecation")
    public void onReceive(Context contexto, Intent intent) {
        int estado = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        if (estado == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirmar = intent.getParcelableExtra(Intent.EXTRA_INTENT);
            if (confirmar == null) return;
            confirmar.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            try {
                contexto.startActivity(confirmar);
            } catch (ActivityNotFoundException | SecurityException e) {
                Log.w(TAG, "no se pudo abrir la confirmación de instalación: " + e);
                Toast.makeText(contexto, NO_INSTALADA, Toast.LENGTH_LONG).show();
            }
        } else if (estado == PackageInstaller.STATUS_SUCCESS) {
            Log.i(TAG, "actualización instalada");
        } else {
            Log.w(TAG, "instalación terminada con estado " + estado + ": "
                    + intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE));
            // Si la cancelaste tú en la confirmación no hace falta avisar.
            if (estado != PackageInstaller.STATUS_FAILURE_ABORTED) {
                Toast.makeText(contexto, NO_INSTALADA, Toast.LENGTH_LONG).show();
            }
        }
    }
}
