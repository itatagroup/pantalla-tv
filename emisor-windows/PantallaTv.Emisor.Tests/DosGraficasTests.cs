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
