using System.Buffers.Binary;
using System.Drawing;
using System.Net;

namespace PantallaTv.Emisor.Tests;

/// <summary>Elegir qué programas van a la tele según dónde están sus ventanas (F5/H2).</summary>
public class SeleccionAudioTests
{
    private static readonly Rectangle Tele = new(1920, 0, 1920, 1080);
    private const string Chrome = @"C:\Program Files\Google\Chrome\chrome.exe";
    private const string Vlc = @"C:\Program Files\VLC\vlc.exe";

    private static VentanaVisible V(string exe, int x, int y, int ancho, int alto, int pid = 1) => new(pid, exe, new Rectangle(x, y, ancho, alto));

    [Fact]
    public void Una_ventana_mayoritariamente_en_el_monitor_va_a_la_tele()
    {
        var s = SeleccionAudio.Elegir([V(Chrome, 2000, 100, 1000, 700), V(Vlc, 100, 100, 800, 600)], Tele);

        Assert.Equal([Chrome], s.EnTele);
        Assert.Empty(s.TambienEnPc);
    }

    [Fact]
    public void Una_ventana_a_medias_cuenta_solo_si_mas_de_la_mitad_esta_dentro()
    {
        // 800 de ancho: 400 dentro y 400 fuera = justo la mitad: no cuenta. Con 401 dentro, sí.
        Assert.Empty(SeleccionAudio.Elegir([V(Chrome, 1520, 0, 800, 600)], Tele).EnTele);
        Assert.Single(SeleccionAudio.Elegir([V(Chrome, 1519 + 2, 0, 800, 600)], Tele).EnTele);
    }

    [Fact]
    public void Un_programa_con_ventanas_en_las_dos_pantallas_va_a_la_tele_y_queda_marcado()
    {
        var s = SeleccionAudio.Elegir([V(Chrome, 2000, 0, 900, 600, 1), V(Chrome, 100, 0, 900, 600, 2)], Tele);

        Assert.Equal([Chrome], s.EnTele);
        Assert.Equal([Chrome], s.TambienEnPc);
    }

    [Fact]
    public void Las_rutas_se_comparan_sin_distinguir_mayusculas()
    {
        var s = SeleccionAudio.Elegir([V(Chrome, 2000, 0, 900, 600), V(Chrome.ToUpperInvariant(), 0, 0, 900, 600)], Tele);

        Assert.Single(s.EnTele);
        Assert.Single(s.TambienEnPc);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Pantalla TV\PantallaTv.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Windows\System32\dwm.exe")]
    [InlineData(@"C:\Windows\SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe")]
    [InlineData(@"C:\Windows\System32\ApplicationFrameHost.exe")]
    public void Los_programas_del_sistema_y_el_propio_nunca_se_mueven(string exe)
    {
        Assert.Empty(SeleccionAudio.Elegir([V(exe, 2000, 0, 900, 600)], Tele).EnTele);
        Assert.True(SeleccionAudio.EsExcluido(exe));
    }

    [Fact]
    public void Las_ventanas_sin_area_o_sin_programa_se_ignoran()
    {
        var s = SeleccionAudio.Elegir([V(Chrome, 2000, 0, 0, 600), V("", 2000, 0, 900, 600)], Tele);

        Assert.Empty(s.EnTele);
    }

    [Fact]
    public void Un_excluido_con_ventana_fuera_no_marca_a_nadie()
    {
        var s = SeleccionAudio.Elegir([V(Chrome, 2000, 0, 900, 600), V(@"C:\Windows\explorer.exe", 0, 0, 900, 600)], Tele);

        Assert.Empty(s.TambienEnPc);
    }
}

public class DiferenciaAppsTests
{
    private static HashSet<string> C(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Entran_y_salen_se_calculan_entre_dos_miradas()
    {
        var d = DiferenciaApps.Calcular(C(@"C:\a\uno.exe", @"C:\a\dos.exe"), C(@"C:\a\dos.exe", @"C:\a\tres.exe"));

        Assert.Equal([@"C:\a\tres.exe"], d.Entran);
        Assert.Equal([@"C:\a\uno.exe"], d.Salen);
        Assert.True(d.HayCambios);
    }

    [Fact]
    public void Sin_cambios_no_hay_nada()
    {
        var d = DiferenciaApps.Calcular(C("x.exe"), C("X.EXE"));

        Assert.False(d.HayCambios);
    }

    [Fact]
    public void El_nombre_es_el_archivo_sin_extension()
    {
        Assert.Equal("chrome", DiferenciaApps.Nombre(@"C:\Program Files\Google\Chrome\chrome.exe"));
    }
}

public class MezcladorPcmTests
{
    private const int P = MezcladorPcm.MuestrasPorPaquete;

    private static float[] Constante(float valor, int n = P) => Enumerable.Repeat(valor, n).ToArray();

    private static short Muestra(byte[] pcm, int i) => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));

    [Fact]
    public void Sumar_suma_las_fuentes_y_satura_en_vez_de_desbordar()
    {
        var pcm = MezcladorPcm.Sumar([[0.25f, 0.9f, -0.9f, float.NaN], [0.25f, 0.9f, -0.9f, 0.5f]]);

        Assert.Equal(8, pcm.Length);
        Assert.Equal(16384, Muestra(pcm, 0));
        Assert.Equal(short.MaxValue, Muestra(pcm, 1));
        Assert.Equal(-short.MaxValue, Muestra(pcm, 2));
        Assert.Equal(16384, Muestra(pcm, 3)); // el NaN de una fuente no contamina la suma
    }

    [Fact]
    public void Sumar_con_fuentes_de_distinto_largo_completa_con_silencio()
    {
        var pcm = MezcladorPcm.Sumar([[0.5f, 0.5f, 0.5f], [0.25f]]);

        Assert.Equal(6, pcm.Length);
        Assert.Equal(24575, Muestra(pcm, 0));
        Assert.Equal(16384, Muestra(pcm, 1));
    }

    [Fact]
    public void Sin_fuentes_o_sin_datos_no_hay_paquete()
    {
        var m = new MezcladorPcm();

        Assert.Null(m.TomarPaquete());
        m.Agregar(1, []);
        Assert.Null(m.TomarPaquete());
    }

    [Fact]
    public void Un_paquete_completo_sale_de_inmediato_con_el_largo_de_20_ms()
    {
        var m = new MezcladorPcm();
        m.Agregar(1, Constante(0.5f));

        var pcm = m.TomarPaquete();

        Assert.NotNull(pcm);
        Assert.Equal(P * 2, pcm.Length); // 960 cuadros × 2 canales × 2 bytes = 3840
        Assert.Equal(3840, pcm.Length);
        Assert.Equal(16384, Muestra(pcm, 0));
        Assert.Null(m.TomarPaquete());
    }

    [Fact]
    public void Dos_fuentes_se_mezclan_en_el_mismo_paquete()
    {
        var m = new MezcladorPcm();
        m.Agregar(1, Constante(0.25f));
        m.Agregar(2, Constante(0.25f));

        Assert.Equal(16384, Muestra(m.TomarPaquete()!, 0));
    }

    [Fact]
    public void Una_fuente_que_aun_no_junta_un_paquete_espera_antes_de_salir()
    {
        var m = new MezcladorPcm();
        m.Agregar(1, Constante(0.5f, P / 2)); // llegó medio paquete

        Assert.Null(m.TomarPaquete());       // turno 1: la fuente llegó, se espera a juntar más
        m.Agregar(1, Constante(0.5f, P / 2));
        Assert.NotNull(m.TomarPaquete());    // ya hay uno completo
    }

    [Fact]
    public void El_final_de_un_sonido_sale_con_ceros_cuando_la_fuente_deja_de_llegar()
    {
        var m = new MezcladorPcm();
        m.Agregar(1, Constante(0.5f, P / 4));

        Assert.Null(m.TomarPaquete()); // turno en que llegó
        Assert.Null(m.TomarPaquete()); // 1 turno sin llegar
        var cola = m.TomarPaquete();   // 2 turnos sin llegar: sale el resto, rellenado
        Assert.NotNull(cola);
        Assert.Equal(16384, Muestra(cola, 0));
        Assert.Equal(0, Muestra(cola, P / 4 + 10));
        Assert.Null(m.TomarPaquete());
    }

    [Fact]
    public void Quitar_una_fuente_descarta_lo_que_tenia()
    {
        var m = new MezcladorPcm();
        m.Agregar(1, Constante(0.5f));
        m.Quitar(1);

        Assert.Null(m.TomarPaquete());
    }

    [Fact]
    public void Lo_que_se_acumula_pasado_de_medio_segundo_se_tira()
    {
        var m = new MezcladorPcm();
        m.Agregar(1, Constante(0.1f, P * 25));
        m.Agregar(1, Constante(0.9f, P * 5)); // lo nuevo desplaza lo viejo

        var n = 0;
        while (m.TomarPaquete() is not null && n < 100) n++;
        Assert.Equal(25, n);
    }
}

public class DispositivosAudioTests
{
    private static InfoDispositivo D(string nombre, bool defecto = false) => new("{0.0.0.00000000}.{" + Guid.NewGuid() + "}", nombre, defecto);

    [Fact]
    public void Busca_el_cable_por_el_nombre_de_su_entrada()
    {
        var cable = D("CABLE Input (VB-Audio Virtual Cable)");

        Assert.Same(cable, DispositivosAudio.BuscarCable([D("Altavoces (Realtek)", true), cable, D("CABLE Output (VB-Audio Virtual Cable)")]));
    }

    [Fact]
    public void Sin_cable_no_devuelve_nada()
    {
        Assert.Null(DispositivosAudio.BuscarCable([D("Altavoces (Realtek)", true), D("CABLE-A Output (VB-Audio Cable A)")]));
    }

    [Fact]
    public void Envolver_y_desenvolver_son_inversas_y_envolver_es_idempotente()
    {
        const string id = "{0.0.0.00000000}.{7a3cbf2a-0001-4a41-aaaa-0123456789ab}";

        var envuelto = DispositivosAudio.Envolver(id);

        Assert.StartsWith(DispositivosAudio.FichaMmdevapi, envuelto);
        Assert.Equal(envuelto, DispositivosAudio.Envolver(envuelto));
        Assert.Equal(id, DispositivosAudio.Desenvolver(envuelto));
        Assert.Equal(id, DispositivosAudio.Desenvolver(id));
        Assert.Equal("", DispositivosAudio.Envolver(""));
    }
}

/// <summary>Un «sistema» de mentira: ventanas, procesos, política de audio y capturas en memoria.</summary>
internal sealed class SistemaFalso : IVentanas, IProcesos, IRutasAudio, IFabricaCaptura
{
    public sealed class CapturaFalsa(int pid, Action<float[]> alLlegar) : ICapturaDeProceso
    {
        public int Pid { get; } = pid;
        public bool Viva { get; set; } = true;
        public bool Cerrada { get; private set; }
        public void Llegar(float[] muestras) => alLlegar(muestras);
        public void Dispose() => Cerrada = true;
    }

    public List<VentanaVisible> Ventanas { get; } = [];
    public List<ProcesoInfo> Procesos { get; } = [];
    /// <summary>La política es por programa: ruta del .exe → (multimedia, consola).</summary>
    public Dictionary<string, (string, string)> Salidas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CapturaFalsa> Capturas { get; } = [];
    public List<string> Fijados { get; } = [];
    public string? Defecto { get; set; } = "{0.0.0.00000000}.{defecto}";
    public bool FallaFijar { get; set; }

    public IReadOnlyList<VentanaVisible> Listar() => [.. Ventanas];

    public IReadOnlyList<ProcesoInfo> De(IReadOnlyCollection<string> exes) =>
        [.. Procesos.Where(p => exes.Contains(p.Exe, StringComparer.OrdinalIgnoreCase))];

    private string ExeDe(int pid) => Procesos.FirstOrDefault(p => p.Pid == pid)?.Exe ?? throw new InvalidOperationException($"no hay proceso {pid}");

    public (string Multimedia, string Consola) Leer(int pid) => Salidas.TryGetValue(ExeDe(pid), out var s) ? s : ("", "");

    public void Fijar(int pid, string idSalida)
    {
        if (FallaFijar) throw new InvalidOperationException("HRESULT=0x80070057");
        Salidas[ExeDe(pid)] = (idSalida, idSalida);
        Fijados.Add($"{pid}:{idSalida}");
    }

    public void Restaurar(int pid, string multimedia, string consola)
    {
        var exe = ExeDe(pid);
        if (multimedia.Length == 0 && consola.Length == 0) Salidas.Remove(exe);
        else Salidas[exe] = (multimedia, consola);
    }

    public string? SalidaPorDefecto() => Defecto;

    public ICapturaDeProceso Abrir(int pid, Action<float[]> alLlegar)
    {
        var c = new CapturaFalsa(pid, alLlegar);
        Capturas.Add(c);
        return c;
    }
}

public sealed class RutasPersistidasTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-rutas-").FullName;
    private string Archivo => Path.Combine(_carpeta, "rutas-audio.json");

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private const string Chrome = @"C:\Chrome\chrome.exe";
    private const string Vlc = @"C:\VLC\vlc.exe";

    [Fact]
    public void Agregar_guarda_y_no_pisa_lo_original()
    {
        var g = new RutasPersistidas(Archivo);

        g.Agregar(new RutaGuardada(Chrome, "{original}", ""));
        g.Agregar(new RutaGuardada(Chrome.ToUpperInvariant(), "{cable}", "{cable}")); // ya estaba: lo primero es lo que vale

        var otra = new RutasPersistidas(Archivo); // otra instancia lee el archivo
        Assert.Equal("{original}", Assert.Single(otra.Todas()).Multimedia);
        Assert.True(otra.Contiene(Chrome));
    }

    [Fact]
    public void Quitar_el_ultimo_borra_el_archivo()
    {
        var g = new RutasPersistidas(Archivo);
        g.Agregar(new RutaGuardada(Chrome, "", ""));

        g.Quitar(Chrome);

        Assert.False(File.Exists(Archivo));
        Assert.Empty(g.Todas());
    }

    [Fact]
    public void Un_archivo_danado_se_toma_como_vacio()
    {
        File.WriteAllText(Archivo, "{esto no es json");

        Assert.Empty(new RutasPersistidas(Archivo).Todas());
    }

    [Fact]
    public void Restaurar_devuelve_la_salida_original_y_olvida_la_ruta()
    {
        var sis = new SistemaFalso();
        sis.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        sis.Salidas[Chrome] = ("{cable}", "{cable}");
        var g = new RutasPersistidas(Archivo);
        g.Agregar(new RutaGuardada(Chrome, "{altavoces}", "{altavoces}"));

        var r = g.RestaurarTodo(sis, sis);

        Assert.Equal([Chrome], r.Restauradas);
        Assert.Empty(r.Pendientes);
        Assert.Equal(("{altavoces}", "{altavoces}"), sis.Salidas[Chrome]);
        Assert.Empty(g.Todas());
    }

    [Fact]
    public void Si_no_habia_preferencia_la_restauracion_la_borra()
    {
        var sis = new SistemaFalso();
        sis.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        sis.Salidas[Chrome] = ("{cable}", "{cable}");
        var g = new RutasPersistidas(Archivo);
        g.Agregar(new RutaGuardada(Chrome, "", ""));

        g.RestaurarTodo(sis, sis);

        Assert.False(sis.Salidas.ContainsKey(Chrome));
    }

    [Fact]
    public void Un_programa_que_no_esta_en_marcha_queda_pendiente()
    {
        var sis = new SistemaFalso();
        var g = new RutasPersistidas(Archivo);
        g.Agregar(new RutaGuardada(Vlc, "", ""));

        var r = g.RestaurarTodo(sis, sis);

        Assert.Equal([Vlc], r.Pendientes);
        Assert.True(g.Contiene(Vlc)); // se reintenta la próxima vez
    }

    [Fact]
    public void Lo_que_se_sigue_usando_no_se_toca()
    {
        var sis = new SistemaFalso();
        sis.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        sis.Salidas[Chrome] = ("{cable}", "{cable}");
        var g = new RutasPersistidas(Archivo);
        g.Agregar(new RutaGuardada(Chrome, "", ""));

        var r = g.RestaurarTodo(sis, sis, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Chrome });

        Assert.Empty(r.Restauradas);
        Assert.Equal(("{cable}", "{cable}"), sis.Salidas[Chrome]);
        Assert.True(g.Contiene(Chrome));
    }
}

public sealed class AudioExclusivoTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-audio-").FullName;
    private static readonly Rectangle Tele = new(1920, 0, 1920, 1080);
    private const string Chrome = @"C:\Chrome\chrome.exe";
    private const string Vlc = @"C:\VLC\vlc.exe";
    private const string Cable = "{0.0.0.00000000}.{cable}";

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private RutasPersistidas Guardadas => new(Path.Combine(_carpeta, "rutas-audio.json"));

    private AudioExclusivo Nuevo(SistemaFalso s) => new(Tele, Cable, s, s, s, s, Guardadas);

    private static void VentanaEnTele(SistemaFalso s, string exe, int pid) => s.Ventanas.Add(new VentanaVisible(pid, exe, new Rectangle(2000, 100, 900, 600)));

    [Fact]
    public void Un_programa_que_entra_a_la_tele_se_manda_al_cable_y_se_captura()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        s.Salidas[Chrome] = ("{altavoces}", "");
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);
        IReadOnlyList<string>? llegaron = null;
        audio.PasaronATele += l => llegaron = l;

        audio.Sondear();

        Assert.Equal([Chrome], llegaron);
        Assert.Equal((Cable, Cable), s.Salidas[Chrome]);
        var anotada = Guardadas.De(Chrome)!; // lo de antes quedó anotado
        Assert.Equal(("{altavoces}", ""), (anotada.Multimedia, anotada.Consola));
        Assert.Equal(10, Assert.Single(s.Capturas).Pid);
        Assert.Equal([Chrome], audio.EnTele);
    }

    [Fact]
    public void Con_procesos_hijos_se_captura_solo_la_raiz_y_se_fija_una_vez_por_programa()
    {
        var s = new SistemaFalso();
        s.Procesos.AddRange([new ProcesoInfo(10, 1, Chrome), new ProcesoInfo(11, 10, Chrome), new ProcesoInfo(12, 10, Chrome)]);
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);

        audio.Sondear();

        Assert.Equal(10, Assert.Single(s.Capturas).Pid); // el árbol de la raíz ya incluye a los hijos
        Assert.Single(s.Fijados); // la preferencia es por ejecutable
    }

    [Fact]
    public void Una_mirada_repetida_no_vuelve_a_abrir_ni_a_fijar()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);

        audio.Sondear();
        audio.Sondear();

        Assert.Single(s.Capturas);
        Assert.Single(s.Fijados);
    }

    [Fact]
    public void Un_proceso_nuevo_del_mismo_programa_no_se_vuelve_a_fijar()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);
        audio.Sondear();

        s.Procesos.Add(new ProcesoInfo(20, 10, Chrome));
        audio.Sondear();

        Assert.Equal(["10:" + Cable], s.Fijados); // la preferencia del ejecutable ya lo cubre
        Assert.Single(s.Capturas);
    }

    [Fact]
    public void Un_programa_que_sale_de_la_tele_recupera_su_salida_y_se_cierra_su_captura()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        s.Salidas[Chrome] = ("{altavoces}", "{altavoces}");
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);
        audio.Sondear();

        s.Ventanas.Clear(); // lo arrastraron a la pantalla del PC
        audio.Sondear();

        Assert.Equal(("{altavoces}", "{altavoces}"), s.Salidas[Chrome]);
        Assert.True(s.Capturas[0].Cerrada);
        Assert.Empty(audio.EnTele);
        Assert.Empty(Guardadas.Todas());
    }

    [Fact]
    public void Un_programa_que_sale_y_vuelve_se_manda_otra_vez_al_cable()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        s.Salidas[Chrome] = ("{altavoces}", "{altavoces}");
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);
        audio.Sondear();
        s.Ventanas.Clear();
        audio.Sondear();

        VentanaEnTele(s, Chrome, 10);
        audio.Sondear();

        Assert.Equal((Cable, Cable), s.Salidas[Chrome]);
        Assert.Equal(2, s.Fijados.Count);
    }

    [Fact]
    public void Una_captura_muerta_se_reabre_en_la_siguiente_mirada()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);
        audio.Sondear();

        s.Capturas[0].Viva = false;
        audio.Sondear();

        Assert.Equal(2, s.Capturas.Count);
        Assert.True(s.Capturas[0].Cerrada);
        Assert.False(s.Capturas[1].Cerrada);
    }

    [Fact]
    public void El_programa_que_tambien_esta_en_el_pc_se_avisa_una_vez()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        s.Ventanas.Add(new VentanaVisible(10, Chrome, new Rectangle(0, 0, 900, 600)));
        using var audio = Nuevo(s);
        var avisos = new List<IReadOnlyCollection<string>>();
        audio.CambioTambienEnPc += avisos.Add;

        audio.Sondear();
        audio.Sondear();

        Assert.Equal([Chrome], Assert.Single(avisos));
        Assert.Equal([Chrome], audio.TambienEnPc);
    }

    [Fact]
    public void Detener_devuelve_todas_las_rutas_y_cierra_las_capturas()
    {
        var s = new SistemaFalso();
        s.Procesos.AddRange([new ProcesoInfo(10, 1, Chrome), new ProcesoInfo(30, 1, Vlc)]);
        s.Salidas[Chrome] = ("{altavoces}", "{altavoces}");
        VentanaEnTele(s, Chrome, 10);
        VentanaEnTele(s, Vlc, 30);
        var audio = Nuevo(s);
        audio.Sondear();
        Assert.Equal(2, s.Capturas.Count);

        audio.Dispose();

        Assert.Equal(("{altavoces}", "{altavoces}"), s.Salidas[Chrome]);
        Assert.False(s.Salidas.ContainsKey(Vlc));
        Assert.All(s.Capturas, c => Assert.True(c.Cerrada));
        Assert.Empty(Guardadas.Todas());
        audio.Dispose(); // idempotente
    }

    [Fact]
    public void Una_sesion_caida_se_recupera_con_lo_anotado_en_el_archivo()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        var audio = Nuevo(s);
        audio.Sondear();
        // El programa murió sin pasar por Dispose: solo queda el archivo y la preferencia puesta en Windows.
        Assert.Equal((Cable, Cable), s.Salidas[Chrome]);

        var r = Guardadas.RestaurarTodo(s, s);

        Assert.Equal([Chrome], r.Restauradas);
        Assert.False(s.Salidas.ContainsKey(Chrome));
        audio.Dispose();
    }

    [Fact]
    public void Si_windows_rechaza_la_ruta_se_sigue_y_se_reintenta_despues()
    {
        var s = new SistemaFalso { FallaFijar = true };
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);

        audio.Sondear();
        s.FallaFijar = false;
        audio.Sondear();

        Assert.Equal((Cable, Cable), s.Salidas[Chrome]);
    }

    [Fact]
    public void Un_programa_sin_proceso_todavia_se_espera_sin_anotar_nada()
    {
        var s = new SistemaFalso();
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);

        audio.Sondear();

        Assert.Empty(s.Capturas);
        Assert.Empty(Guardadas.Todas());
    }

    [Fact]
    public void La_salida_por_defecto_no_se_toca()
    {
        var s = new SistemaFalso();
        s.Procesos.Add(new ProcesoInfo(10, 1, Chrome));
        VentanaEnTele(s, Chrome, 10);
        using var audio = Nuevo(s);

        audio.Sondear();

        Assert.Equal("{0.0.0.00000000}.{defecto}", s.Defecto);
    }

    [Fact]
    public void El_audio_capturado_sale_mezclado_en_paquetes_de_20_ms()
    {
        var s = new SistemaFalso();
        s.Procesos.AddRange([new ProcesoInfo(10, 1, Chrome), new ProcesoInfo(30, 1, Vlc)]);
        VentanaEnTele(s, Chrome, 10);
        VentanaEnTele(s, Vlc, 30);
        var audio = Nuevo(s);
        var paquetes = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        audio.Iniciar(paquetes.Enqueue);
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (s.Capturas.Count < 2 && DateTime.UtcNow < limite) Thread.Sleep(10);
        Assert.Equal(2, s.Capturas.Count);

        foreach (var c in s.Capturas) c.Llegar(Enumerable.Repeat(0.25f, MezcladorPcm.MuestrasPorPaquete * 3).ToArray());
        while (paquetes.IsEmpty && DateTime.UtcNow < limite) Thread.Sleep(10);
        audio.Dispose();

        Assert.True(paquetes.TryDequeue(out var p));
        Assert.Equal(3840, p.Length);
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(p)); // 0,25 + 0,25 = 0,5
        Assert.Equal(48000, audio.Tasa);
        Assert.Equal(2, audio.Canales);
    }
}

/// <summary>La decisión de usar el audio exclusivo (F5/H2) y cómo llega a la emisión.</summary>
public sealed class DecisionAudioTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pantallatv-decision-").FullName;
    private static readonly Receptor Sala = new("Sala", IPAddress.Parse("10.0.0.20"), Protocolo.PuertoTcp);
    private static readonly Rectangle Limites = new(1920, 0, 1920, 1080);
    private EmisionFalsa? _emision;

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    [Theory]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, true, false, true, false)]  // sin sonido
    [InlineData(true, false, false, true, false)]  // el usuario no quiere el PC en silencio
    [InlineData(true, true, true, true, false)]    // la pantalla principal: todo suena ahí, como siempre
    [InlineData(true, true, false, false, false)]  // sin VB-CABLE
    public void Solo_con_cable_un_monitor_que_no_es_el_principal_y_el_pc_en_silencio(bool conSonido, bool silenciarPc, bool principal, bool cable, bool esperado)
    {
        Assert.Equal(esperado, Mando.UsaAudioExclusivo(conSonido, silenciarPc, principal, cable));
    }

    private async Task<OpcionesEmision> Transmitir(bool conAudio, bool silenciarPc, bool principal, bool cable, bool conLimites = true)
    {
        var ajustes = Ajustes.Cargar(Path.Combine(_carpeta, "ajustes.json"));
        ajustes.ConAudio = conAudio;
        ajustes.SilenciarPc = silenciarPc;
        ajustes.Reconectar = false;
        using var mando = new Mando(ajustes, new ContextoInmediato(), () => [Sala])
        {
            FabricaEmision = () => _emision = new EmisionFalsa(), // las emisiones son falsas: no se captura ni se silencia nada
            Preparador = (nombre, _, _) => new Monitores.Preparado(new Monitor(1, nombre ?? @"\\.\DISPLAY2", 1920, 1080, principal, 0, 1, "AMD"), () => { }),
            HayCable = () => cable,
            LimitesDeMonitor = _ => conLimites ? Limites : null,
        };
        Assert.True(mando.Iniciar(Sala, @"\\.\DISPLAY2"));
        var emision = _emision!;
        await emision.Empezo.WaitAsync(TimeSpan.FromSeconds(10));
        var op = emision.Opciones!;
        mando.Detener();
        return op;
    }

    [Fact]
    public async Task Con_cable_y_un_monitor_secundario_la_emision_recibe_el_rectangulo_del_monitor()
    {
        var op = await Transmitir(conAudio: true, silenciarPc: true, principal: false, cable: true);

        Assert.Equal(Limites, op.AudioDeMonitor);
        Assert.True(op.ConAudio);
        Assert.True(op.SilenciarPc); // sigue siendo el respaldo si el exclusivo no se puede abrir
    }

    [Fact]
    public async Task Con_el_monitor_principal_sigue_la_captura_de_siempre()
    {
        var op = await Transmitir(conAudio: true, silenciarPc: true, principal: true, cable: true);

        Assert.Null(op.AudioDeMonitor);
        Assert.True(op.SilenciarPc);
    }

    [Fact]
    public async Task Sin_cable_sigue_la_captura_de_siempre()
    {
        var op = await Transmitir(conAudio: true, silenciarPc: true, principal: false, cable: false);

        Assert.Null(op.AudioDeMonitor);
    }

    [Fact]
    public async Task Si_no_se_encuentra_el_rectangulo_del_monitor_sigue_la_captura_de_siempre()
    {
        var op = await Transmitir(conAudio: true, silenciarPc: true, principal: false, cable: true, conLimites: false);

        Assert.Null(op.AudioDeMonitor);
    }
}

/// <summary>Lo de Windows de verdad que solo LEE (no fija rutas, no captura): que no falle y devuelva algo coherente.</summary>
public class WindowsRealSoloLecturaTests
{
    [Fact]
    public void Los_procesos_encuentran_al_proceso_de_pruebas_con_su_ruta_y_su_padre()
    {
        var yo = Environment.ProcessPath!;

        var r = new ProcesosWindows().De([yo]);

        var propio = Assert.Single(r, p => p.Pid == Environment.ProcessId);
        Assert.Equal(yo, propio.Exe, ignoreCase: true);
        Assert.True(propio.PidPadre > 0);
    }

    [Fact]
    public void Listar_ventanas_y_salidas_de_audio_no_falla()
    {
        var ventanas = new VentanasWindows().Listar();
        Assert.All(ventanas, v => { Assert.True(v.Pid > 0); Assert.NotEmpty(v.Exe); Assert.True(v.Limites.Width > 0); });

        Assert.NotNull(DispositivosAudio.Listar());
        _ = AudioExclusivo.HayCable();
    }
}
