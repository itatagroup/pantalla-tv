namespace PantallaTv.Emisor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Antes que nada: es el proceso elevado que lanzó el instalador o la bandeja (ver Controlador).
        if (args.Contains("--instalar-controlador") || args.Contains("--quitar-controlador")) return Controlador.EjecutarOrdenElevada(args);
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

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(mostrar: !oculto));
        return 0;
    }
}
