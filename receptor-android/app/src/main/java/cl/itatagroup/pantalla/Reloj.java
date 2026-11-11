package cl.itatagroup.pantalla;

/**
 * Relaciona la hora del emisor (los {@code pts}) con la hora local. El desfase mínimo visto
 * ({@code llegada - pts}) es el desfase de relojes más el tránsito mínimo de la red: con él se fija a
 * qué hora local debe salir cada cuadro cuando hay un retardo a propósito. Todo en microsegundos,
 * con la base de {@code System.nanoTime()}.
 *
 * <p>El mínimo se toma sobre una ventana deslizante (10–20 s), no sobre toda la sesión: los relojes
 * del PC y de la TV se separan decenas de microsegundos por segundo, y con un mínimo «de siempre» el
 * buffer se iría comiendo (o creciendo) en una sesión larga.
 */
final class Reloj {
    private static final long VENTANA_US = 10_000_000L;

    private boolean iniciado;
    private long inicioVentanaUs;
    private long minActualUs = Long.MAX_VALUE;
    private long minAnteriorUs = Long.MAX_VALUE;
    private volatile long desfaseUs = Long.MAX_VALUE;

    /** Anota la llegada de un mensaje con marca de tiempo del emisor. */
    synchronized void anotar(long horaLocalUs, long ptsUs) {
        if (!iniciado) {
            iniciado = true;
            inicioVentanaUs = horaLocalUs;
        } else if (horaLocalUs - inicioVentanaUs > VENTANA_US) {
            minAnteriorUs = minActualUs;
            minActualUs = Long.MAX_VALUE;
            inicioVentanaUs = horaLocalUs;
        }
        long d = horaLocalUs - ptsUs;
        if (d < minActualUs) minActualUs = d;
        desfaseUs = Math.min(minActualUs, minAnteriorUs);
    }

    boolean listo() {
        return desfaseUs != Long.MAX_VALUE;
    }

    long desfaseUs() {
        return desfaseUs;
    }

    /** Hora local a la que debe mostrarse el cuadro con ese {@code pts}, con el retardo pedido. */
    long horaDeSalidaUs(long ptsUs, long retardoUs) {
        return ptsUs + desfaseUs + retardoUs;
    }

    static long ahoraUs() {
        return System.nanoTime() / 1000;
    }
}
