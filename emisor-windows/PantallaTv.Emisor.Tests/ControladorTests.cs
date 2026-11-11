using System.Reflection;
using System.Text;
using System.Xml.Linq;
using PantallaTv.Emisor;

namespace PantallaTv.Emisor.Tests;

public class ControladorTests
{
    [Fact]
    public void Reconoce_el_inf_del_controlador_por_su_contenido()
    {
        var inf = File.ReadAllText(RutaControlador("MttVDD.inf"));
        Assert.True(Controlador.EsPaqueteDelControlador(inf));
        Assert.True(Controlador.EsPaqueteDelControlador(inf.ToLowerInvariant()));
    }

    [Fact]
    public void No_confunde_otros_controladores_de_pantalla()
    {
        // El monitor virtual de TeamViewer también está en la clase Display de este PC.
        Assert.False(Controlador.EsPaqueteDelControlador("[Version]\nClass=Display\n[Standard.NTamd64]\n%D%=I, Root\\TvVirtualMonitor\n"));
        Assert.False(Controlador.EsPaqueteDelControlador("Root\\MttVDD sin la dll"));
        Assert.False(Controlador.EsPaqueteDelControlador(""));
    }

    [Fact]
    public void La_configuracion_propia_es_xml_valido_con_un_monitor_y_1080p60()
    {
        var xml = XDocument.Parse(Controlador.ConfiguracionPropia());
        Assert.Equal("1", xml.Root!.Element("monitors")!.Element("count")!.Value);
        var modos = xml.Root.Element("resolutions")!.Elements("resolution")
            .Select(r => (int.Parse(r.Element("width")!.Value), int.Parse(r.Element("height")!.Value), int.Parse(r.Element("refresh_rate")!.Value)))
            .ToList();
        Assert.Contains((1920, 1080, 60), modos);
        Assert.Equal((1920, 1080, 60), MonitorVirtual.ElegirModo(modos));
        // Sin frecuencias globales: el controlador las cruzaría con cada resolución y duplicaría modos.
        Assert.Null(xml.Root.Element("global"));
    }

    [Fact]
    public void Solo_reconoce_como_propia_la_configuracion_sin_cambios()
    {
        var propia = Controlador.ConfiguracionPropia();
        Assert.True(Controlador.EsConfiguracionPropia(propia));
        Assert.True(Controlador.EsConfiguracionPropia(propia.ReplaceLineEndings("\n")));
        Assert.False(Controlador.EsConfiguracionPropia(propia.Replace("<count>1</count>", "<count>2</count>")));
        Assert.False(Controlador.EsConfiguracionPropia("<vdd_settings/>"));
        Assert.False(Controlador.EsConfiguracionPropia(""));
    }

    [Fact]
    public void Multi_sz_ida_y_vuelta()
    {
        var bytes = Controlador.MultiSz(Controlador.IdHardware);
        Assert.Equal(Encoding.Unicode.GetBytes("Root\\MttVDD\0\0"), bytes);
        Assert.Equal([Controlador.IdHardware], Controlador.LeerMultiSz(bytes));
        Assert.Equal(["a", "b"], Controlador.LeerMultiSz(Controlador.MultiSz("a", "b")));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)] // lo quitaron a mano: nada que preguntar
    [InlineData(false, true, false)] // lo instaló el usuario por su cuenta: no se toca
    [InlineData(false, false, false)]
    public void Al_desinstalar_solo_pregunta_si_lo_instalamos_nosotros(bool nuestro, bool instalado, bool pregunta)
    {
        Assert.Equal(pregunta, Instalador.DebePreguntarPorElControlador(nuestro, instalado));
    }

    [Fact]
    public void El_ejecutable_trae_los_archivos_del_controlador_identicos_a_los_del_repo()
    {
        var ensamblado = typeof(Controlador).Assembly;
        foreach (var nombre in new[] { "MttVDD.inf", "MttVDD.dll", "mttvdd.cat" })
        {
            using var recurso = ensamblado.GetManifestResourceStream("controlador/" + nombre);
            Assert.NotNull(recurso);
            using var copia = new MemoryStream();
            recurso.CopyTo(copia);
            Assert.Equal(File.ReadAllBytes(RutaControlador(nombre)), copia.ToArray());
        }
    }

    /// <summary>emisor-windows/controlador/, subiendo desde la carpeta de salida de las pruebas.</summary>
    private static string RutaControlador(string archivo)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "controlador"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("controlador/"), "controlador", archivo);
    }
}
