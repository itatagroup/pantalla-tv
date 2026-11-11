using System.Net;
using System.Net.Sockets;
using System.Text;
using PantallaTv.Emisor;

namespace PantallaTv.Emisor.Tests;

public class ReconexionTests
{
    /// <summary>Un «ffmpeg» que existe (para pasar la comprobación previa); no llega a ejecutarse.</summary>
    private static readonly string FfmpegFalso = typeof(ReconexionTests).Assembly.Location;

    [Fact]
    public async Task Reintenta_los_fallos_pasajeros_y_se_detiene_en_el_definitivo()
    {
        // El receptor falso contesta «ocupado» dos veces (pasajero) y a la tercera «version» (definitivo).
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var conexiones = 0;
        var servidor = Task.Run(async () =>
        {
            foreach (var motivo in new[] { "ocupado", "ocupado", "version" })
            {
                using var cliente = await escucha.AcceptTcpClientAsync();
                conexiones++;
                await Protocolo.LeerAsync(cliente.GetStream(), CancellationToken.None);
                await cliente.GetStream().WriteAsync(Protocolo.Armar(Protocolo.HelloErr, 0, Encoding.UTF8.GetBytes(motivo)));
            }
        });
        var receptor = new Receptor("Falso", IPAddress.Loopback, ((IPEndPoint)escucha.LocalEndpoint).Port);
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, FfmpegFalso);
        var estados = new List<EstadoEmision>();
        var persistente = new EmisionPersistente { EsperaInicial = TimeSpan.FromMilliseconds(20), EsperaMaxima = TimeSpan.FromMilliseconds(40) };
        persistente.CambioDeEstado += e => { lock (estados) estados.Add(e); };

        var error = await Assert.ThrowsAsync<EmisionException>(() => persistente.EjecutarAsync(receptor, op, null, CancellationToken.None));
        await servidor;
        escucha.Stop();

        Assert.False(error.Reintentable);
        Assert.Contains("versión", error.Message);
        Assert.Equal(3, conexiones);
        Assert.Equal(2, estados.Count(e => e.Fase == Fase.Reconectando));
        // Solo el fallo definitivo se muestra como error: los pasajeros no molestan al usuario.
        Assert.Single(estados, e => e.Fase == Fase.Error);
        Assert.Equal(Fase.Error, estados[^1].Fase);
    }

    [Fact]
    public async Task Detener_durante_la_espera_termina_sin_error()
    {
        var libre = new TcpListener(IPAddress.Loopback, 0);
        libre.Start();
        var puerto = ((IPEndPoint)libre.LocalEndpoint).Port;
        libre.Stop(); // nadie escucha: cada intento falla y es reintentable
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, FfmpegFalso);
        var estados = new List<EstadoEmision>();
        var persistente = new EmisionPersistente { EsperaInicial = TimeSpan.FromSeconds(30) };
        persistente.CambioDeEstado += e => { lock (estados) estados.Add(e); };
        using var cts = new CancellationTokenSource();

        var tarea = persistente.EjecutarAsync(new Receptor("Nadie", IPAddress.Loopback, puerto), op, null, cts.Token);
        for (var i = 0; i < 200 && !estados.Any(e => e.Fase == Fase.Reconectando); i++) await Task.Delay(25);
        cts.Cancel();
        await tarea; // no lanza

        Assert.Contains(estados, e => e.Fase == Fase.Reconectando);
        Assert.Equal(Fase.Inactivo, estados[^1].Fase);
    }

    [Fact]
    public async Task Se_rinde_tras_demasiados_fallos_seguidos()
    {
        var libre = new TcpListener(IPAddress.Loopback, 0);
        libre.Start();
        var puerto = ((IPEndPoint)libre.LocalEndpoint).Port;
        libre.Stop();
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, FfmpegFalso);
        var persistente = new EmisionPersistente { EsperaInicial = TimeSpan.FromMilliseconds(5), EsperaMaxima = TimeSpan.FromMilliseconds(5), IntentosSeguidosMax = 2 };

        var error = await Assert.ThrowsAsync<EmisionException>(() =>
            persistente.EjecutarAsync(new Receptor("Nadie", IPAddress.Loopback, puerto), op, null, CancellationToken.None));

        Assert.True(error.Reintentable); // el motivo era pasajero, pero se agotaron los intentos
    }
}

public class TuberiaMonitorTests
{
    [Theory]
    [InlineData(Tuberias.AmfGpu, "vsrc_amf=monitor_index=2:framerate=60")]
    [InlineData(Tuberias.X264, "ddagrab=2:framerate=60,hwdownload,format=bgra")]
    public void El_monitor_elegido_llega_a_ffmpeg(string tuberia, string esperado)
    {
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, null, Monitor: 2);

        Assert.Contains(esperado, Tuberias.Argumentos(tuberia, op));
    }

    [Fact]
    public void Solo_la_tuberia_amf_convierte_a_nv12_en_cpu()
    {
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, null, Monitor: 1);

        Assert.Contains("ddagrab=1:framerate=60,hwdownload,format=bgra,format=nv12", Tuberias.Argumentos(Tuberias.Amf, op));
        foreach (var otra in new[] { Tuberias.Mf, Tuberias.X264, Tuberias.Nvenc, Tuberias.Qsv })
            Assert.Contains("ddagrab=1:framerate=60,hwdownload,format=bgra", Tuberias.Argumentos(otra, op));
        foreach (var otra in new[] { Tuberias.Mf, Tuberias.X264, Tuberias.Nvenc, Tuberias.Qsv })
            Assert.DoesNotContain("ddagrab=1:framerate=60,hwdownload,format=bgra,format=nv12", Tuberias.Argumentos(otra, op));
        Assert.Equal("vsrc_amf=monitor_index=1:framerate=60", Tuberias.Argumentos(Tuberias.AmfGpu, op)[Tuberias.Argumentos(Tuberias.AmfGpu, op).IndexOf("-i") + 1]);
    }

    [Theory]
    [InlineData(true, null, 500, 0)]      // virtual: 0 por defecto
    [InlineData(false, null, 500, 500)]   // principal u otro: el de los ajustes
    [InlineData(false, null, 2000, 2000)]
    [InlineData(true, 1000, 500, 1000)]   // --retardo explícito manda, también en el virtual
    [InlineData(false, 0, 500, 0)]
    public void El_monitor_virtual_pide_retardo_cero_salvo_que_se_pida_otro(bool esVirtual, int? pedido, int ajustes, int esperado)
    {
        Assert.Equal(esperado, Monitores.RetardoPara(esVirtual, pedido, ajustes));
    }

    [Fact]
    public void Un_indice_negativo_cae_en_el_primer_monitor()
    {
        var op = new OpcionesEmision(60, 8000, 1920, 1080, 0, null, Monitor: -5);

        Assert.Contains("vsrc_amf=monitor_index=0:framerate=60", Tuberias.Argumentos(Tuberias.AmfGpu, op));
    }
}

public class MonitoresTests
{
    [Fact]
    public void El_nombre_del_monitor_sale_del_descriptor_FC_del_EDID()
    {
        var edid = new byte[128];
        // Segundo bloque descriptor (byte 72): 00 00 00 FC 00 + «VDD by MTT» + salto de línea + relleno.
        byte[] nombre = [0, 0, 0, 0xFC, 0, .. "VDD by MTT"u8, 0x0A, 0x20, 0x20];
        nombre.CopyTo(edid, 72);

        Assert.Equal("VDD by MTT", Edid.Nombre(edid));
        Assert.Equal("", Edid.Nombre(new byte[128])); // un monitor que no declara nombre
        Assert.Equal("", Edid.Nombre(new byte[10])); // un EDID truncado no rompe
    }

    [Fact]
    public void Un_monitor_virtual_se_ubica_por_su_adaptador_y_no_por_su_posicion_en_Windows()
    {
        // Windows lista primero el principal; DXGI dice que el segundo cuelga de OTRO adaptador, como salida 0.
        var pantallas = new List<(string, int, int, bool)>
        {
            (@"\\.\DISPLAY1", 1920, 1080, true),
            (@"\\.\DISPLAY5", 1280, 720, false),
        };
        var salidas = new List<Dxgi.Salida>
        {
            new(1, 0, @"\\.\DISPLAY5", "Controlador virtual"),
            new(0, 0, @"\\.\DISPLAY1", "AMD Radeon"),
        };

        var monitores = Monitores.Combinar(pantallas, salidas);

        Assert.Equal((0, 0), (monitores[0].Adaptador, monitores[0].Salida));
        Assert.Equal((1, 0), (monitores[1].Adaptador, monitores[1].Salida)); // por posición habría sido (0, 1)
        Assert.Contains("Controlador virtual", monitores[1].ToString());
    }

    [Fact]
    public void Si_DXGI_no_conoce_la_pantalla_se_usa_su_posicion_en_la_grafica_principal()
    {
        var pantallas = new List<(string, int, int, bool)> { (@"\\.\DISPLAY1", 1920, 1080, true), (@"\\.\DISPLAY2", 1920, 1080, false) };

        var monitores = Monitores.Combinar(pantallas, []);

        Assert.Equal((0, 1), (monitores[1].Adaptador, monitores[1].Salida));
    }

    [Fact]
    public void En_este_equipo_DXGI_conoce_todas_las_pantallas_de_Windows()
    {
        var salidas = Dxgi.Salidas();

        Assert.NotEmpty(salidas);
        foreach (var pantalla in Screen.AllScreens)
            Assert.Contains(salidas, s => s.Dispositivo == pantalla.DeviceName);
    }

    [Fact]
    public void Solo_el_monitor_principal_usa_la_captura_de_GPU_de_AMD()
    {
        // Regresión: con el monitor virtual (adaptador 0, salida 1) vsrc_amf mostraba el escritorio principal.
        var virtualEnLaMismaGrafica = new OpcionesEmision(60, 8000, 1920, 1080, 0, null, Monitor: 1, Adaptador: 0);

        Assert.DoesNotContain(Tuberias.AmfGpu, Tuberias.Candidatas(Tuberias.Todas, virtualEnLaMismaGrafica));
        Assert.Contains(Tuberias.Amf, Tuberias.Candidatas(Tuberias.Todas, virtualEnLaMismaGrafica));
        Assert.Contains("ddagrab=1:framerate=60,hwdownload,format=bgra,format=nv12", Tuberias.Argumentos(Tuberias.Amf, virtualEnLaMismaGrafica));
        Assert.Equal(Tuberias.Todas, Tuberias.Candidatas(Tuberias.Todas, new OpcionesEmision(60, 8000, 1920, 1080, 0, null)));
    }

    [Fact]
    public void Un_monitor_de_otro_adaptador_no_usa_la_captura_de_AMD_y_le_dice_a_ffmpeg_cual_es()
    {
        var op = new OpcionesEmision(60, 8000, 1280, 720, 0, null, Monitor: 0, Adaptador: 1);

        var candidatas = Tuberias.Candidatas(Tuberias.Todas, op);
        var args = Tuberias.Argumentos(Tuberias.Amf, op);

        Assert.DoesNotContain(Tuberias.AmfGpu, candidatas);
        Assert.Equal(Tuberias.Todas.Count - 1, candidatas.Count);
        Assert.Equal("d3d11va=cap:1", args[args.IndexOf("-init_hw_device") + 1]);
        Assert.Contains("ddagrab=0:framerate=60,hwdownload,format=bgra,format=nv12", args);
        // En la gráfica principal no se quita ninguna.
        Assert.Equal(Tuberias.Todas, Tuberias.Candidatas(Tuberias.Todas, op with { Adaptador = 0 }));
    }
}

public class InstalacionTests
{
    [Theory]
    [InlineData(@"C:\Descargas\PantallaTvSetup-0.4.0.exe", true)]
    [InlineData(@"C:\Descargas\Instalar Pantalla TV.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\PantallaTv\PantallaTv.exe", false)]
    public void El_nombre_del_archivo_decide_si_es_el_instalador(string ruta, bool esperado)
    {
        Assert.Equal(esperado, Instalacion.EsInstalador([], ruta));
    }

    [Fact]
    public void Con_el_argumento_instalar_siempre_es_el_instalador()
    {
        Assert.True(Instalacion.EsInstalador(["--instalar"], @"C:\x\PantallaTv.exe"));
        Assert.False(Instalacion.EsInstalador(["--cli"], null));
    }

    [Fact]
    public void Desinstalar_no_borra_lo_ajeno_que_haya_en_la_carpeta()
    {
        var id = "PantallaTvPrueba" + Guid.NewGuid().ToString("N")[..8];
        var carpeta = Path.Combine(Path.GetTempPath(), id);
        var origen = Path.Combine(Path.GetTempPath(), id + "-origen.exe");
        File.WriteAllText(origen, "no es un programa de verdad");
        var ajeno = Path.Combine(carpeta, "mis-fotos.txt");
        try
        {
            Directory.CreateDirectory(carpeta);
            File.WriteAllText(ajeno, "algo del usuario que ya estaba ahí");
            Instalacion.Instalar(new OpcionesInstalacion(carpeta, MenuInicio: false, Id: id), origen);

            Instalacion.Desinstalar(id);

            Assert.False(File.Exists(Path.Combine(carpeta, Instalacion.Ejecutable)));
            Assert.True(File.Exists(ajeno)); // lo ajeno sigue, y por eso la carpeta también
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"SoftwareMicrosoftWindowsCurrentVersionUninstall\" + id, false);
            if (Directory.Exists(carpeta)) Directory.Delete(carpeta, true);
            File.Delete(origen);
        }
    }

    [Fact]
    public void Instalar_y_desinstalar_no_dejan_rastro()
    {
        // Con un id propio, para no tocar una instalación real del usuario.
        var id = "PantallaTvPrueba" + Guid.NewGuid().ToString("N")[..8];
        var carpeta = Path.Combine(Path.GetTempPath(), id);
        var origen = Path.Combine(Path.GetTempPath(), id + "-origen.exe");
        File.WriteAllText(origen, "no es un programa de verdad");
        var acceso = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"Pantalla TV ({id}).lnk");
        try
        {
            var instalado = Instalacion.Instalar(new OpcionesInstalacion(carpeta, MenuInicio: true, Id: id), origen);

            Assert.True(File.Exists(instalado));
            Assert.Equal(carpeta, Instalacion.CarpetaInstalada(id));
            Assert.True(File.Exists(acceso));
            Assert.False(Instalacion.IniciaConWindows(id));

            // Reinstalar encima (una actualización) funciona y puede cambiar las opciones.
            Instalacion.Instalar(new OpcionesInstalacion(carpeta, MenuInicio: false, IniciarConWindows: true, Id: id), origen);
            Assert.False(File.Exists(acceso));
            Assert.True(Instalacion.IniciaConWindows(id));

            Instalacion.Desinstalar(id);

            Assert.Null(Instalacion.CarpetaInstalada(id));
            Assert.False(Instalacion.IniciaConWindows(id));
            Assert.False(Directory.Exists(carpeta));
        }
        finally
        {
            Instalacion.IniciarConWindows(false, "", id);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + id, false);
            if (File.Exists(acceso)) File.Delete(acceso);
            if (Directory.Exists(carpeta)) Directory.Delete(carpeta, true);
            File.Delete(origen);
        }
    }
}
