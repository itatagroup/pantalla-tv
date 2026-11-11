namespace PantallaTv.Captura.Prueba;

/// <summary>Qué codificador usa el prototipo.</summary>
internal enum Candidato { Ninguno, MediaFoundation, Libavcodec }

/// <summary>Modo de control de tasa. <see cref="Vbr"/> es el «vbr_latency» de AMF o su equivalente en MF.</summary>
internal enum ModoTasa { Cbr, Vbr }

/// <summary>Cómo se pasa de BGRA a NV12 en la GPU.</summary>
internal enum Conversion { Sombreador, ProcesadorVideo }

/// <summary>Lo que se pide por la línea de comandos. Sin efectos: se puede probar.</summary>
internal sealed record Opciones
{
    /// <summary><c>\\.\DISPLAYn</c>, un índice de la lista (<c>--listar</c>) o <c>virtual</c>.</summary>
    public string Monitor { get; init; } = "0";
    public Candidato Codificador { get; init; } = Candidato.MediaFoundation;
    public ModoTasa Tasa { get; init; } = ModoTasa.Cbr;
    public Conversion Conversion { get; init; } = Conversion.Sombreador;
    public int Kbps { get; init; } = 8000;
    public int Fps { get; init; } = 60;
    public int Segundos { get; init; } = 25;
    /// <summary>Con la pantalla quieta, cada cuánto se recodifica el último cuadro (Sunshine: fps/2). 0 = nunca.</summary>
    public int LatidoFps { get; init; } = 30;
    /// <summary>Archivo .h264 (Annex B) de salida; null = no se escribe.</summary>
    public string? Salida { get; init; }
    /// <summary>Pedir un fotograma clave cada tantos segundos (para comprobar el clave a pedido). 0 = nunca.</summary>
    public int ClaveCadaSeg { get; init; }
    public bool Listar { get; init; }
    /// <summary>En vez de medir, abrir una fuente fiable de 60 fps sobre el monitor (<see cref="Fuente60"/>).</summary>
    public bool Fuente { get; init; }
    /// <summary>Guardar el primer cuadro NV12 convertido como PNG (para revisar colores).</summary>
    public string? Captura { get; init; }

    public const string Uso = """
        CapturaPrueba — prototipo N1: duplicación de escritorio → NV12 en la GPU → H.264 por hardware.
          --listar                       salidas de vídeo (índice, nombre, posición)
          --fuente                       no mide: abre una fuente de 60 fps a pantalla completa en --monitor durante --seg
          --monitor N|\\.\DISPLAYn|virtual  monitor a capturar (por defecto 0)
          --codificador mf|av|ninguno    Media Foundation, libavcodec (h264_amf) o solo capturar y convertir
          --tasa cbr|vbr                 control de tasa (vbr = vbr_latency sin relleno en AMF)
          --conversion sombreador|vp     sombreadores propios o procesador de vídeo de D3D11
          --kbps N  --fps N  --seg N  --latido N (fps mínimos con la pantalla quieta; 0 = no recodificar)
          --salida archivo.h264          escribir el flujo Annex B
          --clave-cada S                 pedir un fotograma clave cada S segundos
          --png archivo.png              guardar el primer cuadro convertido (vuelto a RGB) para revisarlo
        """;

    /// <summary>Lee los argumentos; lanza <see cref="ArgumentException"/> con un mensaje legible si algo no cuadra.</summary>
    public static Opciones Leer(IReadOnlyList<string> args)
    {
        var o = new Opciones();
        for (var i = 0; i < args.Count; i++)
        {
            string Valor() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}");
            int Entero(int min, int max)
            {
                var nombre = args[i];
                var v = Valor();
                return int.TryParse(v, out var n) && n >= min && n <= max
                    ? n : throw new ArgumentException($"{nombre}: «{v}» no es un número entre {min} y {max}");
            }
            o = args[i] switch
            {
                "--listar" => o with { Listar = true },
                "--fuente" => o with { Fuente = true },
                "--monitor" => o with { Monitor = Valor() },
                "--codificador" => o with
                {
                    Codificador = Valor() switch
                    {
                        "mf" => Candidato.MediaFoundation,
                        "av" => Candidato.Libavcodec,
                        "ninguno" => Candidato.Ninguno,
                        var x => throw new ArgumentException($"Codificador desconocido: {x}"),
                    },
                },
                "--tasa" => o with
                {
                    Tasa = Valor() switch
                    {
                        "cbr" => ModoTasa.Cbr,
                        "vbr" => ModoTasa.Vbr,
                        var x => throw new ArgumentException($"Tasa desconocida: {x}"),
                    },
                },
                "--conversion" => o with
                {
                    Conversion = Valor() switch
                    {
                        "sombreador" => Conversion.Sombreador,
                        "vp" => Conversion.ProcesadorVideo,
                        var x => throw new ArgumentException($"Conversión desconocida: {x}"),
                    },
                },
                "--kbps" => o with { Kbps = Entero(100, 100_000) },
                "--fps" => o with { Fps = Entero(1, 240) },
                "--seg" => o with { Segundos = Entero(1, 3600) },
                "--latido" => o with { LatidoFps = Entero(0, 240) },
                "--salida" => o with { Salida = Valor() },
                "--clave-cada" => o with { ClaveCadaSeg = Entero(0, 3600) },
                "--png" => o with { Captura = Valor() },
                var x => throw new ArgumentException($"Opción desconocida: {x}"),
            };
        }
        if (o.LatidoFps > o.Fps) throw new ArgumentException("--latido no puede ser mayor que --fps");
        return o;
    }
}
