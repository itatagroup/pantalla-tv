using PantallaTv.Emisor.Nativa;

namespace PantallaTv.Emisor.Tests;

// ParametrosH264 y MatrizColor nacieron en el prototipo de N1 (y sus pruebas con ellos); desde N2 viven en el emisor.
public class ParametrosH264Tests
{
    private static readonly byte[] Sps = [0, 0, 0, 1, 0x67, 1, 2];
    private static readonly byte[] Pps = [0, 0, 0, 1, 0x68, 3];
    private static readonly byte[] Idr = [0, 0, 0, 1, 0x65, 9, 9];
    private static readonly byte[] P = [0, 0, 0, 1, 0x41, 7];

    [Fact]
    public void Idr_completo_se_deja_igual()
    {
        var p = new ParametrosH264();
        byte[] au = [.. Sps, .. Pps, .. Idr];
        var (u, clave) = p.Procesar(au);
        Assert.True(clave);
        Assert.Equal(au, u);
        Assert.Equal((1, 0), (p.Completos, p.Antepuestos));
    }

    [Fact]
    public void Idr_sin_parametros_recibe_los_ultimos()
    {
        var p = new ParametrosH264();
        p.Procesar([.. Sps, .. Pps, .. Idr]);
        Assert.False(p.Procesar(P).Clave);
        var (u, clave) = p.Procesar(Idr);
        Assert.True(clave);
        Assert.Equal([.. Sps, .. Pps, .. Idr], u);
        Assert.Equal(1, p.Antepuestos);
    }

    [Fact]
    public void Sin_parametros_previos_no_inventa_nada()
    {
        var p = new ParametrosH264();
        Assert.Equal(Idr, p.Procesar(Idr).Unidad);
        Assert.Equal(0, p.Antepuestos);
    }
}

public class MatrizColorTests
{
    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(255, 0, 0, 81.5, 90.3, 240)]   // rojo BT.601: Y≈82, Cb≈90, Cr=240
    [InlineData(0, 0, 255, 41, 240, 109.8)]    // azul: Cb=240 (si se cambiaran R y B daría Cr=240)
    public void Bt601_limitada(byte r, byte g, byte b, double y, double u, double v)
    {
        var (ry, ru, rv) = MatrizColor.Bt601.Aplicar(r, g, b);
        Assert.Equal(y, ry, 0.6);
        Assert.Equal(u, ru, 0.6);
        Assert.Equal(v, rv, 0.6);
    }

    [Fact]
    public void Hlsl_con_punto_decimal()
    {
        var t = MatrizColor.Hlsl(MatrizColor.Bt601.FilaY);
        Assert.Matches(@"^float4\(0\.256\d*, 0\.504\d*, 0\.097\d*, 0\.062\d*\)$", t);
    }

    [Fact]
    public void El_sombreador_lleva_las_tres_filas()
    {
        var f = ConversorSombreador.Fuente(MatrizColor.Bt601);
        Assert.Contains("static const float4 MY", f);
        Assert.Contains("static const float4 MU", f);
        Assert.Contains("static const float4 MV", f);
    }
}

public class EncajeTests
{
    [Fact]
    public void Mismo_tamano_ocupa_todo() =>
        Assert.Equal((0, 0, 1920, 1080), ConversorSombreador.Encaje(1920, 1080, 1920, 1080));

    [Fact]
    public void Mas_ancho_deja_franjas_arriba_y_abajo()
    {
        var (x, y, w, h) = ConversorSombreador.Encaje(2560, 1080, 1920, 1080);
        Assert.Equal((0, 1920), (x, w));
        Assert.Equal(810, h); // 1920 × 1080 / 2560
        Assert.Equal(134, y); // (1080 − 810) / 2 = 135 → par
    }

    [Fact]
    public void Mas_alto_deja_franjas_a_los_lados()
    {
        var (x, y, w, h) = ConversorSombreador.Encaje(1280, 1024, 1920, 1080);
        Assert.Equal((0, 1080), (y, h));
        Assert.Equal(1350, w);
        Assert.Equal(284, x); // (1920 − 1350) / 2 = 285 → par
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(3840, 2160)]
    [InlineData(1, 1)]
    public void Siempre_par_y_dentro(int ancho, int alto)
    {
        var (x, y, w, h) = ConversorSombreador.Encaje(ancho, alto, 1920, 1080);
        Assert.True(x % 2 == 0 && y % 2 == 0 && w % 2 == 0 && h % 2 == 0);
        Assert.True(x >= 0 && y >= 0 && x + w <= 1920 && y + h <= 1080);
    }
}

public class PunteroTests
{
    [Fact]
    public void Monocromo_con_sus_cuatro_casos()
    {
        // 2×1 píxeles; pitch 1 byte. AND: 0b00.., XOR: 0b01.. → píxel 0 negro, píxel 1 blanco.
        // Segundo caso en otra forma: AND 1 → transparente (XOR 0) e invierte (XOR 1).
        var f = Puntero.Formas(Puntero.Monocromo, 2, 2, 1, [0b0000_0000, 0b0100_0000]);
        Assert.Equal((2, 1), (f.Ancho, f.Alto));
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, f.Normal);
        Assert.Null(f.Invertir);

        var g = Puntero.Formas(Puntero.Monocromo, 2, 2, 1, [0b1100_0000, 0b0100_0000]);
        Assert.Equal(new byte[8], g.Normal);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, g.Invertir);
    }

    [Fact]
    public void Color_se_copia_tal_cual_respetando_el_pitch()
    {
        byte[] datos = [1, 2, 3, 4, 9, 9, 9, 9, /* relleno del pitch */ 5, 6, 7, 8, 9, 9, 9, 9];
        var f = Puntero.Formas(Puntero.Color, 1, 2, 8, datos);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, f.Normal);
    }

    [Fact]
    public void Color_con_mascara_reemplaza_o_invierte()
    {
        // Píxel 0: máscara 0 → opaco con su color. Píxel 1: máscara 255 y color → invierte. Píxel 2: máscara 255 y negro → nada.
        byte[] datos = [10, 20, 30, 0, 50, 50, 50, 255, 0, 0, 0, 255];
        var f = Puntero.Formas(Puntero.ColorConMascara, 3, 1, 12, datos);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 0, 0, 0, 0, 0, 0, 0, 0 }, f.Normal);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0 }, f.Invertir);
    }
}

public class OpcionesNativaTests
{
    [Fact]
    public void Pasa_lo_de_la_emision()
    {
        var op = new OpcionesEmision(60, 6000, 1920, 1080, 0, null, Tuberias.Nativa, Monitor: 1, Adaptador: 2,
            MonitorNombre: @"\.\DISPLAY9", LatidoFps: 10, Cursor: false, SegClave: 30);
        Assert.Equal(new OpcionesNativa(@"\.\DISPLAY9", 2, 1, 1920, 1080, 60, 6000, 10, 30, false), op.ParaNativa());
    }

    [Fact]
    public void Desde_el_070_la_nativa_es_la_primera_en_todos_los_monitores()
    {
        var orden = Tuberias.Orden(new Dictionary<string, DateTime>(), DateTime.UtcNow);
        Assert.Equal(Tuberias.Nativa, orden[0]);
        var virtualEnOtraGrafica = new OpcionesEmision(60, 8000, 1920, 1080, 0, null, Monitor: 0, Adaptador: 1);
        Assert.Equal(Tuberias.Nativa, Tuberias.Candidatas(orden, virtualEnOtraGrafica)[0]);
        Assert.Equal(Tuberias.Nativa, Tuberias.Candidatas(orden, new OpcionesEmision(60, 8000, 1920, 1080, 0, null))[0]);
    }
}

public class RitmoTests
{
    private const long Cuadro = 16_666; // 60 fps

    [Fact]
    public void Tras_un_cambio_empuja_y_late_medio_segundo_y_despues_calla()
    {
        var r = new Ritmo(Cuadro, latidoFps: 30, claveCadaUs: Ritmo.Nunca);
        r.Codificado(1_000_000, real: true, clave: false);
        Assert.Equal(1_000_000 + Cuadro + 3_000, r.Proximo); // empujón

        var t = r.Proximo;
        var latidos = 0;
        while (r.Proximo < Ritmo.Nunca)
        {
            t = r.Proximo;
            r.Codificado(t, real: false, clave: false);
            latidos++;
        }
        Assert.InRange(latidos, 15, 17);            // ~30/s durante 0,5 s
        Assert.True(t - 1_000_000 < Ritmo.VentanaLatidoUs);
    }

    [Fact]
    public void Sin_latido_solo_empuja()
    {
        var r = new Ritmo(Cuadro, latidoFps: 0, claveCadaUs: Ritmo.Nunca);
        r.Codificado(0, real: true, clave: false);
        r.Codificado(r.Proximo, real: false, clave: false);
        Assert.Equal(Ritmo.Nunca, r.Proximo);
    }

    [Fact]
    public void Una_clave_tambien_se_empuja()
    {
        var r = new Ritmo(Cuadro, latidoFps: 0, claveCadaUs: Ritmo.Nunca);
        r.Codificado(5_000_000, real: false, clave: true);
        Assert.Equal(5_000_000 + Cuadro + 3_000, r.Proximo);
    }

    [Fact]
    public void Clave_de_seguridad_aunque_no_cambie_nada()
    {
        var r = new Ritmo(Cuadro, latidoFps: 0, claveCadaUs: 30_000_000);
        r.Codificado(0, real: false, clave: true);
        r.Codificado(r.Proximo, real: false, clave: false); // el empujón
        Assert.Equal(30_000_000, r.Proximo);
    }

    [Fact]
    public void Sin_empujon_tras_un_cambio_solo_late()
    {
        // Un codificador que no retiene cuadros (DetectorRetencion): el primer repetido es el del latido, no el empujón.
        var r = new Ritmo(Cuadro, latidoFps: 30, claveCadaUs: Ritmo.Nunca) { Empujon = false };
        r.Codificado(1_000_000, real: true, clave: false);
        Assert.Equal(1_000_000 + 1_000_000 / 30, r.Proximo);
        var sinLatido = new Ritmo(Cuadro, latidoFps: 0, claveCadaUs: Ritmo.Nunca) { Empujon = false };
        sinLatido.Codificado(1_000_000, real: true, clave: false);
        Assert.Equal(Ritmo.Nunca, sinLatido.Proximo);
    }

    [Fact]
    public void Antes_del_primer_cuadro_no_hay_nada_que_repetir()
    {
        var r = new Ritmo(Cuadro, latidoFps: 30, claveCadaUs: Ritmo.Nunca);
        Assert.Equal(Ritmo.Nunca, r.Proximo);
    }
}

public class EstadisticasNativaTests
{
    [Fact]
    public void Csv_tiene_tantas_columnas_como_la_cabecera()
    {
        var columnas = EstadisticasNativa.CabeceraCsv.Split(';').Length;
        Assert.Equal(columnas, EstadisticasNativa.Csv(null).Split(';').Length);
        var e = new EstadisticasNativa(60, 2, 15, 0, 1, 0.6, 18.9, 21.5, 0);
        Assert.Equal("60;2;15;0;1;0.60;18.90;21.50;0", EstadisticasNativa.Csv(e));
    }
}

public class DetectorRetencionTests
{
    private const long Holgura = 12_000;

    /// <summary>Entrega cuadros cada <paramref name="cadaUs"/>; cada uno sale del codificador <paramref name="latenciaUs"/>
    /// después, o recién al entregar el siguiente si <paramref name="retiene"/>. Devuelve la decisión.</summary>
    private static (bool? Decision, int Cuadros) Simular(long cadaUs, long latenciaUs, bool retiene)
    {
        var d = new DetectorRetencion(Holgura);
        long ultimoSalido = -1, anterior = -1, anteriorUs = -1;
        for (var i = 0; i < 50; i++)
        {
            var ahora = 1_000_000 + i * cadaUs;
            var tiempo = ahora * 10;
            // Lo que salió hasta ahora: sin retener, el anterior si ya pasó su latencia; reteniendo, nada hasta entregar este.
            if (!retiene && anterior >= 0 && ahora - anteriorUs >= latenciaUs) ultimoSalido = anterior;
            if (d.AntesDeEntregar(ahora, ultimoSalido) is { } decision) return (decision, i);
            if (retiene && anterior >= 0) ultimoSalido = anterior; // al entregar este sale el anterior
            d.Entregado(ahora, tiempo);
            anterior = tiempo;
            anteriorUs = ahora;
        }
        return (null, 50);
    }

    [Fact]
    public void Como_la_MFT_de_AMD_retiene_y_se_empuja() =>
        Assert.Equal((true, DetectorRetencion.Muestras), Simular(cadaUs: 20_000, latenciaUs: 0, retiene: true));

    [Fact]
    public void Un_codificador_que_no_retiene_no_se_empuja() =>
        Assert.Equal((false, DetectorRetencion.Muestras), Simular(cadaUs: 33_333, latenciaUs: 9_000, retiene: false));

    [Fact]
    public void Los_cuadros_muy_seguidos_no_cuentan()
    {
        // Entregas cada 5 ms: no hubo tiempo de codificar, así que no se sabe nada.
        Assert.Equal((null, 50), Simular(cadaUs: 5_000, latenciaUs: 9_000, retiene: false));
    }
}
