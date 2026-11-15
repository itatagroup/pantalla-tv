using System.Runtime.InteropServices;
using PantallaTv.Emisor.Nativa;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PantallaTv.Emisor.Tests;

/// <summary>
/// Un portátil con dos gráficas (diagnóstico de 13/11/2026): el orden en que se prueba dónde codificar, el paso de
/// «Duplicar» a «Extender» y el modo del monitor virtual. Lo que depende de hardware se prueba con lo que haya aquí y
/// <b>no sustituye</b> probarlo en un portátil con dos gráficas.
/// </summary>
public class PlanCodificadorTests
{
    private static readonly AdaptadorGpu Amd = new(1, "AMD Radeon(TM) Graphics", 0x1002, 512L << 20);
    private static readonly AdaptadorGpu Nvidia = new(2, "NVIDIA GeForce RTX", 0x10DE, 6L << 30);
    private static readonly AdaptadorGpu Intel = new(3, "Intel(R) UHD Graphics", 0x8086, 128L << 20);
    private static readonly AdaptadorGpu Wrap = new(4, "Microsoft Basic Render Driver", 0x1414, 0, PorSoftware: true);

    private static IReadOnlyList<IntentoCodificador> Plan(AdaptadorGpu dueno, AdaptadorGpu[] todos, Func<AdaptadorGpu, int>? codificadores = null) =>
        PlanCodificador.Ordenar(dueno, todos, codificadores ?? (_ => 1));

    [Theory]
    [InlineData(0L)]
    [InlineData(512L << 20)]
    [InlineData(6001L << 20)] // RTX 3050 de 6 GB (portátil de César, 15/11/2026): pasaba de uint y la nativa se descartaba
    [InlineData(24L << 30)]
    public void La_memoria_de_video_de_mas_de_4_GB_no_desborda(long bytes)
    {
        var d = new AdapterDescription1 { DedicatedVideoMemory = (nuint)bytes };
        Assert.Equal(bytes, Gpu.MemoriaDedicada(d));
    }

    [Theory]
    [InlineData(123456L, 1u, true)]  // monitor normal: presentación con hora
    [InlineData(0L, 1u, true)]       // monitor virtual en otra gráfica: sin hora, pero con cuadros acumulados
    [InlineData(123456L, 0u, true)]
    [InlineData(0L, 0u, false)]      // solo el puntero
    public void Un_cuadro_es_nuevo_con_hora_de_presentacion_o_con_cuadros_acumulados(long ultimaPresentacion, uint acumulados, bool nuevo) =>
        Assert.Equal(nuevo, CapturaEscritorio.EsCuadroNuevo(ultimaPresentacion, acumulados));

    [Fact]
    public void Primero_la_gpu_duena_de_la_salida_y_al_final_ffmpeg()
    {
        var plan = Plan(Nvidia, [Amd, Nvidia]);
        Assert.Equal([ViaCodificacion.MismaGpu, ViaCodificacion.CopiaEntreGpus, ViaCodificacion.RespaldoFfmpeg], plan.Select(p => p.Via));
        Assert.Equal(Nvidia, plan[0].Codifica);
        Assert.Equal(Amd, plan[1].Codifica);
        Assert.Null(plan[2].Codifica);
    }

    [Fact]
    public void La_dueña_va_primero_sea_cual_sea_el_orden_de_windows()
    {
        Assert.Equal(Amd, Plan(Amd, [Nvidia, Amd])[0].Codifica);
        Assert.Equal(Nvidia, Plan(Nvidia, [Amd, Nvidia])[0].Codifica);
    }

    [Fact]
    public void La_misma_gpu_se_prueba_aunque_no_se_sepa_de_codificadores()
    {
        var plan = Plan(Amd, [Amd, Nvidia], _ => 0);
        Assert.Equal([ViaCodificacion.MismaGpu, ViaCodificacion.RespaldoFfmpeg], plan.Select(p => p.Via));
    }

    [Fact]
    public void Las_otras_van_de_mas_a_menos_memoria_y_sin_las_de_software()
    {
        var plan = Plan(Amd, [Amd, Intel, Wrap, Nvidia]);
        Assert.Equal([Amd, Nvidia, Intel], plan.Where(p => p.Codifica is not null).Select(p => p.Codifica!));
        Assert.DoesNotContain(plan, p => p.Codifica == Wrap);
    }

    [Fact]
    public void Una_gpu_sin_codificador_no_se_prueba_con_copia()
    {
        var plan = Plan(Amd, [Amd, Nvidia, Intel], a => a == Intel ? 0 : 1);
        Assert.Equal([Amd, Nvidia], plan.Where(p => p.Codifica is not null).Select(p => p.Codifica!));
    }

    [Fact]
    public void Una_sola_gpu_solo_tiene_la_propia_y_el_respaldo()
    {
        var plan = Plan(Amd, [Amd]);
        Assert.Equal([ViaCodificacion.MismaGpu, ViaCodificacion.RespaldoFfmpeg], plan.Select(p => p.Via));
    }

    [Fact]
    public void Las_gpus_repetidas_cuentan_una_vez()
    {
        var plan = Plan(Amd, [Amd, Nvidia, Nvidia with { Nombre = "otra vez" }]);
        Assert.Single(plan, p => p.Via == ViaCodificacion.CopiaEntreGpus);
    }

    [Fact]
    public void El_texto_de_cada_intento_dice_donde_se_codifica()
    {
        var plan = Plan(Nvidia, [Amd, Nvidia]);
        Assert.Contains("misma gráfica", plan[0].ToString());
        Assert.Contains("AMD", plan[1].ToString());
        Assert.Contains("ffmpeg", plan[2].ToString());
    }
}

public class TopologiaTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(0, false)]
    public void Solo_Duplicar_hay_que_pasarla_a_Extender(int actual, bool esperado) =>
        Assert.Equal(esperado, Topologia.HayQuePasarAExtender((TopologiaPantalla)actual));

    [Theory]
    [InlineData(1u, 1)]
    [InlineData(2u, 2)]
    [InlineData(4u, 4)]
    [InlineData(8u, 8)]
    [InlineData(0u, 0)]
    [InlineData(3u, 0)]
    [InlineData(0xFFFFFFFFu, 0)]
    public void Interpreta_el_valor_de_Windows(uint id, int esperado) =>
        Assert.Equal((TopologiaPantalla)esperado, Topologia.Interpretar(id));

    [Fact]
    public void Consultar_la_topologia_actual_no_cambia_nada_ni_lanza()
    {
        // Solo lectura: no toca las pantallas de quien corre las pruebas.
        var t = Topologia.Actual();
        Assert.True(Enum.IsDefined(t));
    }

    [Fact]
    public void Pasar_a_una_topologia_desconocida_se_niega_sin_tocar_nada() =>
        Assert.NotEqual(0, Topologia.PasarA(TopologiaPantalla.Desconocida));

    [Fact]
    public void El_error_se_explica_con_palabras_y_el_codigo()
    {
        var texto = Topologia.Explicar(5);
        Assert.Contains("(error 5)", texto);
        Assert.True(texto.Length > "(error 5)".Length + 3);
    }

    [Fact]
    public void El_aviso_de_Duplicar_dice_que_hay_que_desmarcar_el_monitor_virtual()
    {
        Assert.Contains("Extender", MonitorVirtual.TextoPasoAExtender);
        Assert.Contains("desmarca el monitor virtual", MonitorVirtual.TextoPasoAExtender);
    }
}

public class ModoDelMonitorVirtualYaConectadoTests
{
    private static readonly (int, int, int)[] Ofrecidos = [(3840, 2160, 60), (2560, 1440, 60), (1920, 1080, 60), (1920, 1080, 30), (1280, 720, 60)];

    [Fact]
    public void Un_monitor_que_Windows_dejo_en_4K_se_lleva_al_modo_pedido()
    {
        // El caso del portátil de dos gráficas: ya conectado a 3840×2160 y la tele pide 1080p.
        var elegido = MonitorVirtual.ElegirModo(Ofrecidos, (1920, 1080, 60));
        Assert.Equal((1920, 1080, 60), elegido);
        Assert.True(MonitorVirtual.NecesitaCambioDeModo((3840, 2160, 60), elegido!.Value));
    }

    [Fact]
    public void Si_ya_esta_en_el_modo_pedido_no_se_toca()
    {
        var elegido = MonitorVirtual.ElegirModo(Ofrecidos, (1920, 1080, 60));
        Assert.False(MonitorVirtual.NecesitaCambioDeModo((1920, 1080, 60), elegido!.Value));
    }

    [Fact]
    public void La_frecuencia_tambien_cuenta()
    {
        Assert.True(MonitorVirtual.NecesitaCambioDeModo((1920, 1080, 30), (1920, 1080, 60)));
    }
}

/// <summary>F1: el monitor virtual en la gráfica de la pantalla física (friendlyname de vdd_settings.xml).</summary>
public class GraficaDelMonitorVirtualTests
{
    private const string Amd = "AMD Radeon(TM) Graphics";
    private const string Nvidia = "NVIDIA GeForce RTX 3050 6GB Laptop GPU";

    private static AdaptadorGpu G(long luid, string nombre, bool software = false) => new(luid, nombre, 0, 0, software);

    [Fact]
    public void Con_una_sola_grafica_de_hardware_deja_default()
    {
        Assert.Equal("default", Controlador.ElegirGpu([G(1, Amd)], Amd));
        Assert.Equal("default", Controlador.ElegirGpu([], Amd));
    }

    [Fact]
    public void Con_dos_graficas_elige_la_que_dibuja_la_principal()
    {
        Assert.Equal(Amd, Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia)], Amd));
        Assert.Equal(Nvidia, Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia)], " " + Nvidia + " "));
    }

    [Fact]
    public void La_misma_grafica_listada_dos_veces_cuenta_una_sola()
    {
        // En el portátil DXGI listó dos veces la NVIDIA, con LUID distintos.
        Assert.Equal("default", Controlador.ElegirGpu([G(1, Nvidia), G(2, Nvidia)], Nvidia));
        Assert.Equal(Amd, Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia), G(3, Nvidia)], Amd));
    }

    [Fact]
    public void Las_graficas_por_software_no_cuentan()
    {
        Assert.Equal("default", Controlador.ElegirGpu([G(1, Amd), G(4, "Microsoft Basic Render Driver", software: true)], Amd));
        Assert.Equal(Amd, Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia), G(4, "Microsoft Basic Render Driver", software: true)], Amd));
    }

    [Fact]
    public void Con_varias_graficas_y_sin_saber_la_de_la_principal_no_decide()
    {
        // null: quien llama conserva la gráfica que ya tenía la configuración (no la devuelve a «default»).
        Assert.Null(Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia)], null));
        Assert.Null(Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia)], "  "));
        Assert.Null(Controlador.ElegirGpu([G(1, Amd), G(2, Nvidia)], "Otra"));
    }

    [Theory]
    [InlineData("GpuPreference=2;", "Alto rendimiento")]
    [InlineData("GpuPreference=1;", "Ahorro de energía")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", "Alto rendimiento")]
    [InlineData("GpuPreference=0;", null)]
    [InlineData("AppStatus=0;", null)]
    [InlineData(null, null)]
    public void La_preferencia_de_grafica_de_windows_se_lee(string? valor, string? esperado) =>
        Assert.Equal(esperado, Controlador.InterpretarPreferencia(valor));

    [Fact]
    public void La_configuracion_lleva_el_friendlyname_pedido_escapado()
    {
        Assert.Equal("default", Controlador.GpuDeConfiguracion(Controlador.ConfiguracionPropia()));
        Assert.Equal(Amd, Controlador.GpuDeConfiguracion(Controlador.ConfiguracionPropia(Amd)));
        var rara = "Gráfica <A&B> \"x\"";
        var xml = Controlador.ConfiguracionPropia(rara);
        Assert.Contains("&lt;A&amp;B&gt;", xml);
        Assert.Equal(rara, Controlador.GpuDeConfiguracion(xml)); // XML válido, y el nombre vuelve igual
        Assert.Equal("default", Controlador.GpuDeConfiguracion(Controlador.ConfiguracionPropia("  ")));
    }

    [Theory]
    [InlineData("default")]
    [InlineData(Amd)]
    [InlineData(Nvidia)]
    [InlineData("Gráfica <A&B>")]
    public void Reconoce_como_propia_la_configuracion_con_cualquier_friendlyname(string gpu)
    {
        var propia = Controlador.ConfiguracionPropia(gpu);
        Assert.True(Controlador.EsConfiguracionPropia(propia));
        Assert.True(Controlador.EsConfiguracionPropia(propia.ReplaceLineEndings("\n")));
        Assert.False(Controlador.EsConfiguracionAnterior(propia)); // con los modos de ahora: no hay nada que actualizar
        Assert.False(Controlador.EsConfiguracionPropia(propia.Replace("<count>1</count>", "<count>2</count>")));
    }

    [Fact]
    public void Las_configuraciones_anteriores_se_reconocen_con_cualquier_friendlyname()
    {
        foreach (var anterior in Controlador.ConfiguracionesAnteriores)
        {
            Assert.True(Controlador.EsConfiguracionPropia(anterior));
            Assert.True(Controlador.EsConfiguracionAnterior(anterior));
            var conAmd = anterior.Replace("<friendlyname>default</friendlyname>", $"<friendlyname>{Amd}</friendlyname>");
            Assert.NotEqual(anterior, conAmd);
            Assert.True(Controlador.EsConfiguracionPropia(conAmd));
            Assert.True(Controlador.EsConfiguracionAnterior(conAmd));
        }
        var de010 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Datos", "configuracion-0.10.xml"));
        Assert.True(Controlador.EsConfiguracionPropia(de010.Replace("<friendlyname>default</friendlyname>", $"<friendlyname>{Nvidia}</friendlyname>")));
    }

    [Fact]
    public void Una_configuracion_del_usuario_con_otra_gpu_y_otros_cambios_no_es_nuestra()
    {
        var suya = Controlador.ConfiguracionPropia(Amd).Replace("<HardwareCursor>true<", "<HardwareCursor>false<");
        Assert.False(Controlador.EsConfiguracionPropia(suya));
    }

    private static readonly IReadOnlyList<AdaptadorGpu> DosGraficas = [G(1, Amd), G(2, Nvidia)];

    [Fact]
    public void Avisa_si_el_virtual_lo_dibuja_otra_grafica_y_se_puede_mover()
    {
        var aviso = MonitorVirtual.TextoAvisoGrafica(Nvidia, Amd, DosGraficas, configuracionNuestra: true, @"C:\VirtualDisplayDriver\vdd_settings.xml");
        Assert.NotNull(aviso);
        Assert.Contains($"«{Nvidia}»", aviso);
        Assert.Contains($"«{Amd}»", aviso);
        Assert.Contains("se copia", aviso);
        Assert.Contains("«Avanzado»", aviso);
        Assert.Contains("administrador", aviso);
    }

    [Fact]
    public void Si_la_configuracion_no_es_nuestra_dice_que_hay_que_editarla_a_mano_y_donde()
    {
        var ruta = @"C:\VirtualDisplayDriver\vdd_settings.xml";
        var aviso = MonitorVirtual.TextoAvisoGrafica(Nvidia, Amd, DosGraficas, configuracionNuestra: false, ruta);
        Assert.NotNull(aviso);
        Assert.Contains($"<friendlyname>{Amd}</friendlyname>", aviso);
        Assert.Contains(ruta, aviso);
        Assert.DoesNotContain("«Avanzado»", aviso);
    }

    [Fact]
    public void No_avisa_si_coinciden_si_no_se_sabe_o_si_hay_una_sola_grafica()
    {
        Assert.Null(MonitorVirtual.TextoAvisoGrafica(Amd, amdEnMinusculas(), DosGraficas, true, "x"));
        Assert.Null(MonitorVirtual.TextoAvisoGrafica(null, Amd, DosGraficas, true, "x"));
        Assert.Null(MonitorVirtual.TextoAvisoGrafica(Nvidia, null, DosGraficas, true, "x"));
        Assert.Null(MonitorVirtual.TextoAvisoGrafica(Nvidia, Amd, [G(2, Nvidia), G(3, Nvidia)], true, "x")); // la misma dos veces
        Assert.Null(MonitorVirtual.TextoAvisoGrafica(Nvidia, Amd, [G(1, Amd), G(4, "Basic Render", software: true)], true, "x"));

        static string amdEnMinusculas() => Amd.ToLowerInvariant();
    }

    [Fact]
    public void El_diagnostico_dice_si_coincide_con_la_principal()
    {
        var igual = Diagnostico.DescribirGrafica(Amd, Amd, Amd);
        Assert.Contains("la dibuja «" + Amd + "»", igual);
        Assert.Contains("friendlyname configurado «" + Amd + "»", igual);
        Assert.Contains("→ coincide", igual);
        Assert.Contains("NO coincide", Diagnostico.DescribirGrafica(Nvidia, "default", Amd));
        Assert.Contains("no se puede comparar", Diagnostico.DescribirGrafica(null, null, Amd));
    }
}

/// <summary>La parte de la copia entre gráficas que se puede probar sin dos gráficas.</summary>
public class PuenteGpuTests
{
    [Fact]
    public void Copia_las_filas_de_luma_y_de_croma_con_pasos_distintos()
    {
        const int ancho = 10, alto = 6, pasoO = 64, pasoD = 48;
        var origen = Marshal.AllocHGlobal(pasoO * alto * 3 / 2);
        var destino = Marshal.AllocHGlobal(pasoD * alto * 3 / 2);
        try
        {
            var datos = new byte[pasoO * alto * 3 / 2];
            for (var i = 0; i < datos.Length; i++) datos[i] = (byte)(i * 7 + 1);
            Marshal.Copy(datos, 0, origen, datos.Length);
            var vacio = new byte[pasoD * alto * 3 / 2];
            Array.Fill(vacio, (byte)0xEE);
            Marshal.Copy(vacio, 0, destino, vacio.Length);

            var paso = Array.Empty<byte>();
            PuenteGpu.CopiarNv12(origen, pasoO, destino, pasoD, ancho, alto, ref paso);

            var copia = new byte[vacio.Length];
            Marshal.Copy(destino, copia, 0, copia.Length);
            for (var f = 0; f < alto; f++) // luma
                Assert.Equal(datos.AsSpan(f * pasoO, ancho).ToArray(), copia.AsSpan(f * pasoD, ancho).ToArray());
            for (var f = 0; f < alto / 2; f++) // croma, a paso × alto del comienzo
                Assert.Equal(datos.AsSpan(pasoO * alto + f * pasoO, ancho).ToArray(), copia.AsSpan(pasoD * alto + f * pasoD, ancho).ToArray());
            Assert.Equal(0xEE, copia[ancho]); // el relleno de cada fila no se toca
        }
        finally
        {
            Marshal.FreeHGlobal(origen);
            Marshal.FreeHGlobal(destino);
        }
    }

    [Fact]
    public void El_luid_va_y_vuelve_entre_DXGI_y_el_numero_del_plan()
    {
        foreach (var a in Gpu.Adaptadores())
        {
            using var real = Gpu.PorLuid(a.Luid);
            Assert.NotNull(real);
            var luid = real!.Description1.Luid;
            var vuelta = FabricaCodificador.ALuid(a.Luid);
            Assert.Equal((luid.LowPart, luid.HighPart), (vuelta.LowPart, vuelta.HighPart));
        }
    }

    /// <summary>
    /// Dos dispositivos distintos (sobre la misma gráfica, si es la única que hay) y un cuadro NV12 que pasa del uno al otro por el
    /// puente. Prueba el camino de verdad (staging, Map, copia), pero entre gráficas distintas no se pudo probar.
    /// Sin una gráfica que acepte NV12 la prueba no tiene qué verificar y termina sin comprobar nada.
    /// </summary>
    [Fact]
    public void Un_cuadro_NV12_pasa_de_un_dispositivo_a_otro_sin_cambiar()
    {
        var gpu = Gpu.Adaptadores().FirstOrDefault(a => !a.PorSoftware);
        if (gpu is null) return;
        using var adaptador = Gpu.PorLuid(gpu.Luid)!;
        var (devA, ctxA) = Gpu.Abrir(adaptador);
        var (devB, ctxB) = Gpu.Abrir(adaptador);
        using var _1 = devA; using var _2 = ctxA; using var _3 = devB; using var _4 = ctxB;

        const int ancho = 64, alto = 32;
        var datos = new byte[ancho * alto * 3 / 2];
        for (var i = 0; i < datos.Length; i++) datos[i] = (byte)(i * 13 + 5);
        var pin = GCHandle.Alloc(datos, GCHandleType.Pinned);
        ID3D11Texture2D origen;
        try
        {
            origen = devA.CreateTexture2D(new Texture2DDescription
            {
                Width = ancho, Height = alto, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            }, new SubresourceData(pin.AddrOfPinnedObject(), ancho));
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return; // esta gráfica no crea NV12 con datos iniciales: no hay con qué probar
        }
        finally { pin.Free(); }

        using var _5 = origen;
        using var anillo = new AnilloNv12(devB, ancho, alto, 1);
        var destino = anillo.Siguiente().Textura;
        using var puente = new PuenteGpu(devA, ctxA, devB, ctxB, ancho, alto);
        puente.Copiar(origen, destino);

        using var lectura = devB.CreateTexture2D(new Texture2DDescription
        {
            Width = ancho, Height = alto, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        ctxB.CopyResource(lectura, destino);
        var mapa = ctxB.Map(lectura, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var leido = new byte[datos.Length];
        try
        {
            var paso = Array.Empty<byte>();
            var dest = GCHandle.Alloc(leido, GCHandleType.Pinned);
            try { PuenteGpu.CopiarNv12(mapa.DataPointer, (int)mapa.RowPitch, dest.AddrOfPinnedObject(), ancho, ancho, alto, ref paso); }
            finally { dest.Free(); }
        }
        finally { ctxB.Unmap(lectura, 0); }
        Assert.Equal(datos, leido);
    }
}
