namespace PantallaTv.Emisor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Antes que nada: es el proceso elevado que lanzó el instalador o la bandeja (ver Controlador).
        if (args.Contains("--instalar-controlador") || args.Contains("--quitar-controlador")
            || args.Contains("--reubicar-monitor-virtual")) return Controlador.EjecutarOrdenElevada(args);
        if (args.Contains("--desinstalar")) return Instalador.Desinstalar(args);
        if (Instalacion.EsInstalador(args, Environment.ProcessPath)) return Instalador.Instalar(args);
        MonitorVirtual.RecuperarSiQuedoConectado();
        if (Cli.Pedido(args)) return Cli.EjecutarAsync(args).GetAwaiter().GetResult();

        // Una sola instancia: abrirlo dos veces dejaría dos íconos y dos escuchas del mismo puerto. La segunda le
        // pide a la primera que muestre su ventana (salvo desde el inicio de Windows, que la quiere oculta).
        var oculto = Arranque.EmpiezaOculto(args);
        using var unica = new Mutex(true, Arranque.NombreMutex, out var esLaPrimera);
        if (!esLaPrimera)
        {
            if (!oculto) Arranque.PedirQueSeMuestre();
            return 0;
        }

        // Si una sesión anterior se cayó con apps mandadas a la tele (F5), se les devuelve su salida. Después de la instancia única:
        // una segunda copia no debe deshacer lo que la primera está usando. Antes de abrir la ventana (sin archivo no cuesta
        // nada): en segundo plano podía cruzarse con una transmisión recién empezada y devolver una ruta que esta acaba de poner.
        try { AudioExclusivo.RestaurarPendientes(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Registro.Error("audio exclusivo: no se pudieron devolver las rutas pendientes", e); }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(mostrar: !oculto, Cli.UrlManifiesto(args)));
        return 0;
    }
}
