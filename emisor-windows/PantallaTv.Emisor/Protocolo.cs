using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PantallaTv.Emisor;

/// <summary>Un receptor visto en la red (la TV Box con «Pantalla» abierta).</summary>
internal sealed record Receptor(string Nombre, IPAddress Direccion, int Puerto)
{
    public override string ToString() => $"{Nombre} ({Direccion})";
}

/// <summary>Constantes y armado de mensajes del protocolo (ver docs/protocolo.md). Enteros en big-endian.</summary>
internal static class Protocolo
{
    public const int Version = 1;
    public const int PuertoTcp = 5600;
    public const int PuertoUdp = 5601;

    public const byte Hello = 0x01;
    public const byte HelloOk = 0x02;
    public const byte HelloErr = 0x03;
    public const byte VideoConfig = 0x10;
    public const byte Video = 0x11;
    public const byte Audio = 0x20;
    public const byte Ping = 0x30;
    public const byte Pong = 0x31;
    public const byte PedirClave = 0x40;

    public const int LargoCabecera = 13;
    public const int MaxCarga = 8 * 1024 * 1024;

    /// <summary>Banderas del HELLO (byte 17): bit 0 = sesión interactiva (el monitor virtual), sin búfer en el receptor.</summary>
    public const byte BanderaInteractiva = 0x01;

    /// <summary>Cabecera (tipo, largo, pts en µs) seguida de la carga.</summary>
    public static byte[] Armar(byte tipo, ulong ptsUs, ReadOnlySpan<byte> carga)
    {
        var m = new byte[LargoCabecera + carga.Length];
        m[0] = tipo;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(1, 4), (uint)carga.Length);
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(5, 8), ptsUs);
        carga.CopyTo(m.AsSpan(LargoCabecera));
        return m;
    }

    /// <summary>
    /// HELLO de 18 bytes: versión, tamaño, fps, audio, PIN, el retardo pedido (siempre 0: el búfer lo decide el receptor;
    /// el campo queda por los receptores anteriores a la 0.6.0) y las banderas (ver <see cref="BanderaInteractiva"/>).
    /// </summary>
    public static byte[] ArmarHello(int ancho, int alto, int fps, int tasaAudio, int canales, string pin, bool interactiva)
    {
        var c = new byte[18];
        c[0] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(1, 2), (ushort)ancho);
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(3, 2), (ushort)alto);
        c[5] = (byte)fps;
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(6, 4), (uint)tasaAudio);
        c[10] = (byte)canales;
        Encoding.ASCII.GetBytes(pin.PadLeft(4, '0')[..4]).CopyTo(c.AsSpan(11, 4));
        // c[15..17]: retardo pedido = 0.
        c[17] = interactiva ? BanderaInteractiva : (byte)0;
        return Armar(Hello, 0, c);
    }

    /// <summary>Lee un mensaje completo; devuelve null si el otro lado cerró la conexión.</summary>
    public static async Task<Mensaje?> LeerAsync(Stream flujo, CancellationToken ct)
    {
        var cab = new byte[LargoCabecera];
        if (!await LeerExactoAsync(flujo, cab, ct)) return null;
        var largo = BinaryPrimitives.ReadUInt32BigEndian(cab.AsSpan(1, 4));
        if (largo > MaxCarga) throw new InvalidDataException($"Carga inválida: {largo} bytes.");
        var pts = BinaryPrimitives.ReadUInt64BigEndian(cab.AsSpan(5, 8));
        var carga = new byte[largo];
        if (!await LeerExactoAsync(flujo, carga, ct)) return null;
        return new Mensaje(cab[0], pts, carga);
    }

    private static async Task<bool> LeerExactoAsync(Stream flujo, byte[] destino, CancellationToken ct)
    {
        var leido = 0;
        while (leido < destino.Length)
        {
            var n = await flujo.ReadAsync(destino.AsMemory(leido), ct);
            if (n == 0) return false;
            leido += n;
        }
        return true;
    }

    /// <summary>Interpreta «PANTALLA-TV 1 nombre con espacios 5600». Devuelve null si no es un anuncio válido.</summary>
    public static Receptor? InterpretarAnuncio(string texto, IPAddress origen)
    {
        var p = texto.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 4 || p[0] != "PANTALLA-TV" || p[1] != Version.ToString()) return null;
        if (!int.TryParse(p[^1], out var puerto) || puerto is < 1 or > 65535) return null;
        return new Receptor(string.Join(' ', p[2..^1]), origen, puerto);
    }
}

internal sealed record Mensaje(byte Tipo, ulong PtsUs, byte[] Carga);
