using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PantallaTv.Audio.Prueba;

/// <summary>Una salida de audio (render) activa.</summary>
internal sealed record InfoDispositivo(string Id, string Nombre, bool EsDefecto);

/// <summary>Lógica pura de ids y elección de dispositivo (con pruebas unitarias).</summary>
internal static class Dispositivos
{
    public const string FichaMmdevapi = @"\\?\SWD#MMDEVAPI#";
    public const string InterfazRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    public const string InterfazCaptura = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    /// <summary>De «{0.0.0.00000000}.{guid}» a la forma que espera la política de audio. Idempotente.</summary>
    public static string Envolver(string mmDeviceId, bool render = true)
    {
        if (string.IsNullOrWhiteSpace(mmDeviceId)) return "";
        if (mmDeviceId.StartsWith(FichaMmdevapi, StringComparison.OrdinalIgnoreCase)) return mmDeviceId;
        return FichaMmdevapi + mmDeviceId + (render ? InterfazRender : InterfazCaptura);
    }

    /// <summary>Inversa de <see cref="Envolver"/>; un id ya simple se devuelve igual.</summary>
    public static string Desenvolver(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (id.StartsWith(FichaMmdevapi, StringComparison.OrdinalIgnoreCase)) id = id[FichaMmdevapi.Length..];
        foreach (var cola in new[] { InterfazRender, InterfazCaptura })
            if (id.EndsWith(cola, StringComparison.OrdinalIgnoreCase)) return id[..^cola.Length];
        return id;
    }

    /// <summary>Elige por id exacto, luego por parte del id, luego por parte del nombre. Error si es ambiguo o no hay.</summary>
    public static (InfoDispositivo? Elegido, string? Error) Elegir(IReadOnlyList<InfoDispositivo> lista, string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return (null, "Falta indicar el dispositivo.");
        var id = Desenvolver(texto.Trim());
        var exacto = lista.Where(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exacto.Count == 1) return (exacto[0], null);
        var cand = lista.Where(d => d.Id.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cand.Count == 0)
            cand = lista.Where(d => d.Nombre.Contains(texto.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return cand.Count switch
        {
            1 => (cand[0], null),
            0 => (null, $"Ninguna salida coincide con «{texto}»."),
            _ => (null, $"«{texto}» es ambiguo: " + string.Join(" | ", cand.Select(d => d.Nombre))),
        };
    }

    /// <summary>La segunda salida de la batería: la primera que no es la por defecto, prefiriendo VB-CABLE.</summary>
    public static InfoDispositivo? SegundaSalida(IReadOnlyList<InfoDispositivo> lista) =>
        lista.Where(d => !d.EsDefecto).OrderByDescending(d => d.Nombre.Contains("CABLE", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

    public static List<InfoDispositivo> Listar()
    {
        using var en = new MMDeviceEnumerator();
        string? defecto = null;
        try { using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defecto = d.ID; } catch { /* sin salida */ }
        var lista = new List<InfoDispositivo>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (d) lista.Add(new InfoDispositivo(d.ID, d.FriendlyName, d.ID == defecto));
        }
        return lista;
    }
}

/// <summary>
/// Salida de audio persistida por app: la API no documentada del mezclador de Windows (clase WinRT
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>), la misma que usan EarTrumpet y SoundVolumeView.
/// Referencia (MIT): EarTrumpet, <c>Interop/MMDeviceAPI/IAudioPolicyConfigFactoryVariantFor21H2.cs</c> (IID
/// ab3d4648-e242-459f-b02f-541c70306324, 19 métodos de relleno antes de Set/Get/Clear),
/// <c>Interop/Helpers/AudioPolicyConfigFactoryImplFor21H2.cs</c> y
/// <c>DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs</c> (forma del id y roles Multimedia y Console).
/// Antes de Windows 10 21H2 el IID era 2a59116d-6c4f-45e0-a74f-707e3fef9258 con otro orden: no se soporta aquí.
/// La interfaz es IInspectable, que .NET moderno ya no marshala: se llama por la vtable a mano
/// (IUnknown 0-2, IInspectable 3-5, los 19 métodos de relleno 6-24, Set=25, Get=26, Clear=27).
/// La preferencia es PERSISTENTE y por ejecutable: todo cambio se registra en <see cref="Restauracion"/>.
/// </summary>
#pragma warning disable SYSLIB1054 // P/Invoke clásico a propósito: es una herramienta de medición.
internal static class RutaPorApp
{
    public const int Render = 0;
    public const int Console = 0, Multimedia = 1;
    private const int IndiceSet = 25, IndiceGet = 26;
    private static readonly Guid IidWindows21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr clase, ref Guid iid, out IntPtr fabrica);
    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string texto, uint largo, out IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint largo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DSet(IntPtr yo, uint pid, int flujo, int rol, IntPtr hstring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DGet(IntPtr yo, uint pid, int flujo, int rol, out IntPtr hstring);

    private static IntPtr _fabrica;

    private static IntPtr Fabrica()
    {
        if (_fabrica != IntPtr.Zero) return _fabrica;
        const string clase = "Windows.Media.Internal.AudioPolicyConfig";
        Marshal.ThrowExceptionForHR(WindowsCreateString(clase, (uint)clase.Length, out var hs));
        try
        {
            var iid = IidWindows21H2;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out _fabrica));
        }
        finally { WindowsDeleteString(hs); }
        return _fabrica;
    }

    private static T Metodo<T>(IntPtr yo, int indice) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(yo);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, indice * IntPtr.Size));
    }

    /// <summary>Fija la salida de un PID para un rol. Id vacío = borrar la preferencia. Devuelve el HRESULT.</summary>
    public static int FijarRol(int pid, string idEnvuelto, int rol)
    {
        var f = Fabrica();
        var hs = IntPtr.Zero;
        if (!string.IsNullOrEmpty(idEnvuelto))
        {
            var hr0 = WindowsCreateString(idEnvuelto, (uint)idEnvuelto.Length, out hs);
            if (hr0 < 0) return hr0;
        }
        try { return Metodo<DSet>(f, IndiceSet)(f, (uint)pid, Render, rol, hs); }
        finally { if (hs != IntPtr.Zero) WindowsDeleteString(hs); }
    }

    /// <summary>Lee la salida persistida (id tal cual lo devuelve Windows, envuelto; vacío si no hay).</summary>
    public static string Leer(int pid, int rol, out int hr)
    {
        var f = Fabrica();
        hr = Metodo<DGet>(f, IndiceGet)(f, (uint)pid, Render, rol, out var hs);
        if (hr < 0 || hs == IntPtr.Zero) return "";
        try
        {
            var p = WindowsGetStringRawBuffer(hs, out var largo);
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p, (int)largo) ?? "";
        }
        finally { WindowsDeleteString(hs); }
    }

    /// <summary>Fija ambos roles (Multimedia y Console) y registra cómo deshacerlo (restaura lo que había).</summary>
    public static string Fijar(int pid, string mmDeviceId, string etiqueta = "")
    {
        var antesMm = Leer(pid, Multimedia, out _);
        var antesCo = Leer(pid, Console, out _);
        // La preferencia es por ejecutable: si el proceso ya terminó (E_INVALIDARG con su PID), se restaura con
        // el PID propio cuando es el mismo .exe (los tonos son AudioPrueba.exe); si no, queda el error a la vista.
        var mismoExe = MismoEjecutable(pid);
        Restauracion.Agregar($"ruta PID {pid} {etiqueta}".Trim(), () =>
        {
            var p = pid;
            if (FijarRol(p, antesMm, Multimedia) < 0 && mismoExe) p = Environment.ProcessId;
            Chk(FijarRol(p, antesMm, Multimedia));
            Chk(FijarRol(p, antesCo, Console));
            var mm = Leer(p, Multimedia, out _); var co = Leer(p, Console, out _);
            if (mm != antesMm || co != antesCo) throw new InvalidOperationException("la lectura posterior no coincide con el original");
        });
        // Forma envuelta (la de EarTrumpet); si Windows no la acepta, el id simple. Un proceso recién lanzado
        // aún puede no ser válido para la política (E_INVALIDARG): se reintenta unos segundos.
        var envuelto = Dispositivos.Envolver(mmDeviceId);
        var usada = envuelto;
        var hr = unchecked((int)0x80070057);
        for (var intento = 0; intento < 25 && hr < 0; intento++)
        {
            if (intento > 0) Thread.Sleep(200);
            foreach (var candidato in new[] { envuelto, mmDeviceId })
            {
                usada = candidato;
                hr = FijarRol(pid, candidato, Multimedia);
                if (hr >= 0) hr = FijarRol(pid, candidato, Console);
                if (hr >= 0 && Leer(pid, Multimedia, out _).Length > 0) break;
                if (hr >= 0) hr = unchecked((int)0x80070057);
            }
        }
        Chk(hr);
        return usada;
    }

    private static bool MismoEjecutable(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Chk(int hr) { if (hr < 0) throw new InvalidOperationException($"HRESULT=0x{hr:X8}"); }

    /// <summary>Borra la preferencia de ese PID (ambos roles).</summary>
    public static void Borrar(int pid)
    {
        Chk(FijarRol(pid, "", Multimedia));
        Chk(FijarRol(pid, "", Console));
    }
}
