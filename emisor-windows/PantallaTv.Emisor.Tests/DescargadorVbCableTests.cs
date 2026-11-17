using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace PantallaTv.Emisor.Tests;

/// <summary>
/// Ofrecer VB-CABLE: paquete fijado (tamaño y SHA-256), firma del instalador, UAC y devolver la salida por defecto. Con un
/// servidor y un sistema falsos: <b>nunca</b> se baja ni se instala VB-CABLE de verdad.
/// </summary>
public sealed class DescargadorVbCableTests : IDisposable
{
    private const string Instalador = "VBCABLE_Setup_x64.exe";
    private const string Huella = "A77952D93229D0EC36E2543081EEA7D125732B9C";
    private readonly string _carpeta = Path.Combine(Directory.CreateTempSubdirectory("pantallatv-vbcable-").FullName, "vbcable");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_carpeta)!, recursive: true);

    private static byte[] Zip(params string[] entradas)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var nombre in entradas.Length > 0 ? entradas : [Instalador, "readme.txt", "vbMmeCable64_win10.inf"])
                using (var e = new StreamWriter(zip.CreateEntry(nombre).Open())) e.Write("contenido de " + nombre);
        return memoria.ToArray();
    }

    private static OrigenVbCable Origen(byte[] zip, string? sha = null) =>
        new("Pack 99", new Uri("http://127.0.0.1:5999/vbcable.zip"), zip.Length, sha ?? Convert.ToHexStringLower(SHA256.HashData(zip)), Instalador, Huella);

    private static ManejadorFalso Sirve(byte[] zip) => new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

    private DescargadorVbCable Descargador(OrigenVbCable origen, ManejadorFalso handler, SistemaFalso sistema) =>
        new(handler, origen, _carpeta, sistema, permitirHttpLocal: true);

    private sealed class SistemaFalso : ISistemaVbCable
    {
        public bool Firma { get; set; } = true;
        public int Codigo { get; set; }
        public bool Rechaza { get; set; }
        /// <summary>Como el VB-CABLE real: al instalarse se pone como salida por defecto.</summary>
        public string? Defecto { get; set; } = "realtek";
        public List<string> Ejecutados { get; } = [];
        public List<string> Puestas { get; } = [];

        public bool FirmaValida(string ruta, string huella) => Firma && File.Exists(ruta) && huella == Huella;

        public int EjecutarElevado(string ruta, string argumentos)
        {
            if (Rechaza) throw new Win32Exception(1223);
            Ejecutados.Add(Path.GetFileName(ruta) + " " + argumentos);
            if (argumentos.StartsWith("-i")) Defecto = "cable";
            return Codigo;
        }

        public string? SalidaPorDefecto() => Defecto;

        public void PonerSalidaPorDefecto(string id)
        {
            Puestas.Add(id);
            Defecto = id;
        }
    }

    [Fact]
    public async Task Instala_con_UAC_devuelve_la_salida_por_defecto_y_deja_la_marca()
    {
        var zip = Zip();
        var sistema = new SistemaFalso();
        using var d = Descargador(Origen(zip), Sirve(zip), sistema);

        var r = await d.InstalarAsync();

        Assert.True(r.Hecho, r.Motivo);
        Assert.Equal(["VBCABLE_Setup_x64.exe -i -h"], sistema.Ejecutados);
        Assert.Equal(["realtek"], sistema.Puestas); // VB-CABLE se había puesto por defecto: se devolvió
        Assert.Equal("realtek", sistema.Defecto);
        Assert.True(DescargadorVbCable.LoInstalamosNosotros(_carpeta));
        Assert.False(File.Exists(Path.Combine(_carpeta, "vbcable.zip.parcial")));
    }

    [Fact]
    public async Task Con_otra_huella_no_instala_nada()
    {
        var zip = Zip();
        var sistema = new SistemaFalso();
        using var d = Descargador(Origen(zip, sha: new string('0', 64)), Sirve(zip), sistema);

        var r = await d.InstalarAsync();

        Assert.False(r.Hecho);
        Assert.Contains("SHA-256", r.Motivo);
        Assert.Empty(sistema.Ejecutados);
        Assert.False(DescargadorVbCable.LoInstalamosNosotros(_carpeta));
    }

    [Fact]
    public async Task Con_la_firma_que_no_es_de_VB_Audio_no_instala_nada()
    {
        var zip = Zip();
        var sistema = new SistemaFalso { Firma = false };
        using var d = Descargador(Origen(zip), Sirve(zip), sistema);

        var r = await d.InstalarAsync();

        Assert.False(r.Hecho);
        Assert.Contains("firma", r.Motivo);
        Assert.Empty(sistema.Ejecutados);
    }

    [Fact]
    public async Task Un_paquete_con_rutas_dentro_se_rechaza()
    {
        var zip = Zip(Instalador, "../fuera.txt");
        var sistema = new SistemaFalso();
        using var d = Descargador(Origen(zip), Sirve(zip), sistema);

        var r = await d.InstalarAsync();

        Assert.False(r.Hecho);
        Assert.Empty(sistema.Ejecutados);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_carpeta)!, "fuera.txt")));
    }

    [Fact]
    public async Task Si_se_dice_que_no_al_permiso_no_queda_la_marca()
    {
        var zip = Zip();
        var sistema = new SistemaFalso { Rechaza = true };
        using var d = Descargador(Origen(zip), Sirve(zip), sistema);

        var r = await d.InstalarAsync();

        Assert.False(r.Hecho);
        Assert.True(r.Rechazado);
        Assert.False(DescargadorVbCable.LoInstalamosNosotros(_carpeta));
    }

    [Fact]
    public async Task Si_el_instalador_falla_igual_se_devuelve_la_salida_y_no_queda_la_marca()
    {
        var zip = Zip();
        var sistema = new SistemaFalso { Codigo = 5 };
        using var d = Descargador(Origen(zip), Sirve(zip), sistema);

        var r = await d.InstalarAsync();

        Assert.False(r.Hecho);
        Assert.Contains("5", r.Motivo);
        Assert.Equal("realtek", sistema.Defecto);
        Assert.False(DescargadorVbCable.LoInstalamosNosotros(_carpeta));
    }

    [Fact]
    public async Task Quitar_solo_si_lo_instalo_Pantalla_TV()
    {
        var zip = Zip();
        var sistema = new SistemaFalso();
        using var d = Descargador(Origen(zip), Sirve(zip), sistema);
        Assert.False(d.Quitar().Hecho); // sin la marca no se toca
        Assert.Empty(sistema.Ejecutados);

        Assert.True((await d.InstalarAsync()).Hecho);
        var r = d.Quitar();

        Assert.True(r.Hecho, r.Motivo);
        Assert.Equal("VBCABLE_Setup_x64.exe -u -h", sistema.Ejecutados[^1]);
        Assert.False(DescargadorVbCable.LoInstalamosNosotros(_carpeta));
    }

    [Fact]
    public void El_origen_fijado_es_el_paquete_46_por_https()
    {
        var o = DescargadorVbCable.Fijado;
        Assert.Equal(Uri.UriSchemeHttps, o.Url.Scheme);
        Assert.Equal("download.vb-audio.com", o.Url.Host);
        Assert.Equal(64, o.Sha256.Length);
        Assert.Equal(Instalador, o.Instalador);
    }
}

public sealed class AvisoVbCableTests
{
    [Theory]
    [InlineData(true, true, false, false, true)]   // monitor virtual con sonido y PC en silencio, sin cable: avisar
    [InlineData(true, true, false, true, false)]   // ya está el cable
    [InlineData(true, true, true, false, false)]   // pantalla principal: no hace falta
    [InlineData(false, true, false, false, false)] // sin sonido
    [InlineData(true, false, false, false, false)] // el PC no se silencia
    public void Se_avisa_solo_cuando_haria_falta(bool sonido, bool silenciar, bool principal, bool cable, bool avisa) =>
        Assert.Equal(avisa, Mando.HaceFaltaVbCable(sonido, silenciar, principal, () => cable));

    [Fact]
    public void El_cable_no_se_mira_si_no_hace_falta() =>
        Assert.False(Mando.HaceFaltaVbCable(true, true, esPrincipal: true, () => throw new InvalidOperationException("no se debía mirar")));

    [Fact]
    public void La_confirmacion_dice_de_donde_que_pide_permiso_y_los_creditos()
    {
        var t = Mando.TextoConfirmacionVbCable();
        Assert.Contains("download.vb-audio.com", t);
        Assert.Contains("administrador", t);
        Assert.Contains("VB-Audio", t);
        Assert.Contains(DescargadorVbCable.UrlDonar, t);
    }
}
