using System.Runtime.InteropServices;

namespace PantallaTv.Emisor;

/// <summary>
/// El perfil de las redes conectadas (pública, privada, de dominio), con la API de Windows «Network List Manager».
/// En una red pública Windows bloquea por defecto lo que llega de la red local: los anuncios de las teles (UDP 5601)
/// no se ven, aunque se puede transmitir escribiendo la IP.
/// </summary>
internal static class Red
{
    internal enum Perfil { Publica = 0, Privada = 1, Dominio = 2 }

    /// <summary>
    /// Los nombres de las redes conectadas que Windows trata como públicas (vacío si no hay o no se pudo saber).
    /// </summary>
    public static IReadOnlyList<string> PublicasConectadas() =>
        Conectadas()?.Where(r => r.Perfil == Perfil.Publica).Select(r => r.Nombre).ToList() ?? [];

    /// <summary>Las redes conectadas con su perfil, para el diagnóstico.</summary>
    public static string Describir() => Conectadas() is not { } redes ? "no se pudo leer el perfil de la red"
        : redes.Count == 0 ? "ninguna red conectada"
        : string.Join(" · ", redes.Select(r => $"{r.Nombre}: {r.Perfil switch { Perfil.Publica => "pública", Perfil.Privada => "privada", _ => "de dominio" }}"));

    /// <summary>Null si no se pudo leer (se anota en el registro).</summary>
    private static List<(string Nombre, Perfil Perfil)>? Conectadas()
    {
        try
        {
            return LeerConectadas();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or NotSupportedException)
        {
            Registro.Anotar($"perfil de la red: no se pudo leer ({e.Message})");
            return null;
        }
    }

    /// <summary>Si alguna red es pública, el aviso para la ventana; si no, null.</summary>
    internal static string? Aviso(IReadOnlyList<string> publicas) => publicas.Count == 0 ? null
        : $"Windows trata la red «{string.Join("», «", publicas)}» como pública: no deja ver las teles. Si es la red de tu "
          + "casa, cámbiala a privada; si no, escribe la IP de la tele.";

    /// <summary>Abre Configuración › Red e Internet, donde se cambia el perfil.</summary>
    public static void AbrirConfiguracion() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });

    private static List<(string Nombre, Perfil Perfil)> LeerConectadas()
    {
        var tipo = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: true)!;
        var gestor = (INetworkListManager)Activator.CreateInstance(tipo)!;
        var lista = new List<(string Nombre, Perfil Perfil)>();
        try
        {
            var redes = gestor.GetNetworks(NlmEnumNetworkConnected);
            while (redes.Next(1, out var red, out var traidas) == 0 && traidas == 1)
            {
                lista.Add((red.GetName(), (Perfil)red.GetCategory()));
                Marshal.ReleaseComObject(red);
            }
            Marshal.ReleaseComObject(redes);
        }
        finally
        {
            Marshal.ReleaseComObject(gestor);
        }
        return lista;
    }

    private const int NlmEnumNetworkConnected = 1;

    // Interfaces duales de netlistmgr.h, en el orden de su vtable hasta el último método que se usa.

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [DispId(1)] IEnumNetworks GetNetworks(int filtro);
    }

    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworks
    {
        [DispId(-4)] object NuevoEnumerador(); // _NewEnum, para mantener el orden de la vtable
        [DispId(1)] [PreserveSig] int Next(int cuantos, out INetwork red, out int traidas);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [DispId(1)] string GetName();
        [DispId(2)] void SetName(string nombre);
        [DispId(3)] string GetDescription();
        [DispId(4)] void SetDescription(string descripcion);
        [DispId(5)] Guid GetNetworkId();
        [DispId(6)] int GetDomainType();
        [DispId(7)] object GetNetworkConnections();
        [DispId(8)] void GetTimeCreatedAndConnected(out uint altoCreada, out uint bajoCreada, out uint altoConectada, out uint bajoConectada);
        [DispId(9)] bool IsConnectedToInternet();
        [DispId(10)] bool IsConnected();
        [DispId(11)] int GetConnectivity();
        [DispId(12)] int GetCategory();
    }
}
