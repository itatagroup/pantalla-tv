using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PantallaTv.Emisor;

/// <summary>Una salida de audio (render) activa.</summary>
internal sealed record InfoDispositivo(string Id, string Nombre, bool EsDefecto);

/// <summary>Ids de dispositivo de Windows y búsqueda de VB-CABLE (lógica pura salvo <see cref="Listar"/>).</summary>
internal static class DispositivosAudio
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

    /// <summary>
    /// La salida de VB-CABLE («CABLE Input (VB-Audio Virtual Cable)»): la que lleva «CABLE Input» en el nombre. No
    /// sirve «CABLE-A/B» ni «Hi-Fi Cable»: son otros productos con otro nombre. Null si no está instalado.
    /// </summary>
    public static InfoDispositivo? BuscarCable(IReadOnlyList<InfoDispositivo> lista) =>
        lista.FirstOrDefault(d => d.Nombre.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
                                  && d.Nombre.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
        ?? lista.FirstOrDefault(d => d.Nombre.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

    public static List<InfoDispositivo> Listar()
    {
        using var en = new MMDeviceEnumerator();
        var defecto = IdPorDefecto(en);
        var lista = new List<InfoDispositivo>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (d) lista.Add(new InfoDispositivo(d.ID, d.FriendlyName, d.ID == defecto));
        }
        return lista;
    }

    /// <summary>El id de la salida por defecto (rol multimedia); null si no hay.</summary>
    public static string? IdPorDefecto(MMDeviceEnumerator? enumerador = null)
    {
        var propio = enumerador is null;
        enumerador ??= new MMDeviceEnumerator();
        try
        {
            using var d = enumerador.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            return null; // sin salida
        }
        finally
        {
            if (propio) enumerador.Dispose();
        }
    }
}

/// <summary>
/// Cómo se fija, se lee y se devuelve la salida de audio de un proceso. Es una interfaz para poder probar el resto
/// con una versión falsa: la real (<see cref="RutasWindows"/>) toca la configuración de Windows.
/// </summary>
internal interface IRutasAudio
{
    /// <summary>La salida guardada para ese proceso (rol multimedia y rol consola); vacío = ninguna (la por defecto).</summary>
    (string Multimedia, string Consola) Leer(int pid);

    /// <summary>Manda el audio de ese proceso (y de su ejecutable) a esa salida. Lanza <see cref="InvalidOperationException"/> si Windows no lo acepta.</summary>
    void Fijar(int pid, string idSalida);

    /// <summary>Devuelve lo que había (un valor vacío borra la preferencia). Lanza <see cref="InvalidOperationException"/> si no se pudo.</summary>
    void Restaurar(int pid, string multimedia, string consola);

    /// <summary>El id de la salida por defecto de Windows (null si no hay): se mira para comprobar que no se movió.</summary>
    string? SalidaPorDefecto();
}

/// <summary>
/// Salida de audio persistida por app: la API no documentada del mezclador de Windows (clase WinRT
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>), la misma que usan EarTrumpet y SoundVolumeView.
/// Referencia (MIT): EarTrumpet, <c>Interop/MMDeviceAPI/IAudioPolicyConfigFactoryVariantFor21H2.cs</c> (IID
/// ab3d4648-e242-459f-b02f-541c70306324, 19 métodos de relleno antes de Set/Get/Clear).
/// Antes de Windows 10 21H2 el IID era otro y con otro orden: no se soporta aquí.
/// La interfaz es IInspectable, que .NET moderno ya no marshala: se llama por la vtable a mano
/// (IUnknown 0-2, IInspectable 3-5, los 19 métodos de relleno 6-24, Set=25, Get=26, Clear=27).
/// Medido en el prototipo (F4): la preferencia es PERSISTENTE, por ejecutable, y solo la toman los streams nuevos.
/// </summary>
internal sealed class RutasWindows : IRutasAudio
{
    private const int Render = 0, Console = 0, Multimedia = 1;
    private const int IndiceSet = 25, IndiceGet = 26;
    private static readonly Guid IidWindows21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static IntPtr _fabrica;

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

    private static IntPtr Fabrica()
    {
        if (_fabrica != IntPtr.Zero) return _fabrica;
        const string clase = "Windows.Media.Internal.AudioPolicyConfig";
        Marshal.ThrowExceptionForHR(WindowsCreateString(clase, (uint)clase.Length, out var hs));
        try
        {
            var iid = IidWindows21H2;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out var f));
            _fabrica = f;
        }
        finally { WindowsDeleteString(hs); }
        return _fabrica;
    }

    private static T Metodo<T>(IntPtr yo, int indice) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(yo);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, indice * IntPtr.Size));
    }

    private static int FijarRol(int pid, string idEnvuelto, int rol)
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

    private static string LeerRol(int pid, int rol)
    {
        var f = Fabrica();
        var hr = Metodo<DGet>(f, IndiceGet)(f, (uint)pid, Render, rol, out var hs);
        if (hr < 0 || hs == IntPtr.Zero) return "";
        try
        {
            var p = WindowsGetStringRawBuffer(hs, out var largo);
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p, (int)largo) ?? "";
        }
        finally { WindowsDeleteString(hs); }
    }

    private static void Verificar(int hr)
    {
        if (hr < 0) throw new InvalidOperationException($"HRESULT=0x{hr:X8}");
    }

    public (string Multimedia, string Consola) Leer(int pid) => (LeerRol(pid, Multimedia), LeerRol(pid, Console));

    public void Fijar(int pid, string idSalida)
    {
        // Forma envuelta (la de EarTrumpet); si Windows no la acepta, el id simple. Un proceso recién lanzado aún
        // puede no ser válido para la política (E_INVALIDARG): se reintenta unos instantes.
        var hr = unchecked((int)0x80070057);
        foreach (var candidato in new[] { DispositivosAudio.Envolver(idSalida), idSalida })
        {
            for (var intento = 0; intento < 5 && hr < 0; intento++)
            {
                if (intento > 0) Thread.Sleep(150);
                hr = FijarRol(pid, candidato, Multimedia);
                if (hr >= 0) hr = FijarRol(pid, candidato, Console);
                if (hr >= 0 && LeerRol(pid, Multimedia).Length == 0) hr = unchecked((int)0x80070057); // no quedó guardado
            }
            if (hr >= 0) return;
        }
        Verificar(hr);
    }

    public void Restaurar(int pid, string multimedia, string consola)
    {
        Verificar(FijarRol(pid, multimedia, Multimedia));
        Verificar(FijarRol(pid, consola, Console));
        var (mm, co) = Leer(pid);
        if (mm != multimedia || co != consola) throw new InvalidOperationException("la lectura posterior no coincide con la original");
    }

    public string? SalidaPorDefecto() => DispositivosAudio.IdPorDefecto();
}
