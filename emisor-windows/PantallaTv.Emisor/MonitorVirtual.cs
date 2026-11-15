using System.Drawing;
using System.Runtime.InteropServices;

namespace PantallaTv.Emisor;

/// <summary>
/// El monitor virtual (controlador Virtual-Display-Driver) visto desde el programa: se detecta aunque
/// no esté conectado al escritorio y se conecta solo mientras se transmite. Así abrir el programa no
/// agrega un monitor ni mueve las ventanas del usuario; el escritorio solo se extiende cuando hace falta
/// (decisión de diseño). Conectar y quitar un monitor no pide administrador.
/// </summary>
internal sealed class MonitorVirtual
{
    /// <summary>Identificador del controlador en Windows (<c>Root\MttVDD</c>).</summary>
    public const string IdControlador = "MttVDD";

    private const int ConectadoAlEscritorio = 0x1; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
    private const int ActualizarRegistro = 0x1; // CDS_UPDATEREGISTRY
    private const int SinReiniciar = 0x10000000; // CDS_NORESET: se acumulan los cambios y se aplican de una vez
    private const int CampoPosicion = 0x20, CampoBits = 0x40000, CampoAncho = 0x80000, CampoAlto = 0x100000, CampoFrecuencia = 0x400000;

    /// <summary>Nombre de Windows de la salida del monitor virtual (<c>\\.\DISPLAY6</c>).</summary>
    public string Dispositivo { get; }

    private MonitorVirtual(string dispositivo) => Dispositivo = dispositivo;

    /// <summary>El monitor virtual, si el controlador está instalado; null si no.</summary>
    public static MonitorVirtual? Detectar()
    {
        for (uint i = 0; i < 64; i++)
        {
            var d = Monitores.NuevoDispositivo();
            if (!Monitores.EnumerarDispositivo(null, i, ref d, 0)) break;
            if (d.Id.Contains(IdControlador, StringComparison.OrdinalIgnoreCase)) return new MonitorVirtual(d.Nombre);
        }
        return null;
    }

    /// <summary>¿Está conectado al escritorio ahora mismo?</summary>
    public bool EstaConectado()
    {
        for (uint i = 0; i < 64; i++)
        {
            var d = Monitores.NuevoDispositivo();
            if (!Monitores.EnumerarDispositivo(null, i, ref d, 0)) break;
            if (EsEste(d.Nombre)) return (d.Estado & ConectadoAlEscritorio) != 0;
        }
        return false;
    }

    public enum Resultado { YaEstaba, Conectado, Fallo }

    /// <summary>
    /// Lo conecta a la derecha de las demás pantallas, a 1080p y 60 Hz si el controlador lo ofrece, y
    /// espera a que Windows lo aplique (tarda unos segundos). Devuelve <see cref="Resultado.YaEstaba"/> si
    /// ya lo estaba: en ese caso no hay que quitarlo después, porque el usuario lo tenía puesto.
    /// Antes de tocar nada deja una marca en disco: Windows guarda el cambio en el registro y, si el
    /// proceso muere conectado, el próximo arranque lo desconecta (<see cref="RecuperarSiQuedoConectado"/>).
    /// </summary>
    /// <summary>El modo con que quedó conectado en el último <see cref="Conectar"/> (null si ya estaba o falló).</summary>
    public (int Ancho, int Alto, int Hz)? ModoConectado { get; private set; }

    /// <summary>
    /// Si <see cref="Conectar"/> dejó el monitor en otro modo que el que tenía (ya estaba conectado, por ejemplo porque
    /// Windows lo puso en su primer modo, 3840×2160), el que tenía antes: <see cref="RestaurarModoAnterior"/> lo devuelve.
    /// </summary>
    public (int Ancho, int Alto, int Hz)? ModoAnterior { get; private set; }

    /// <summary>Qué se hizo con las pantallas para poder conectarlo (se pasó de «Duplicar» a «Extender»), para mostrárselo a la persona; null si nada.</summary>
    public string? AvisoTopologia { get; private set; }

    /// <summary>La gráfica (descripción DXGI) que dibuja el monitor virtual tras el último <see cref="Conectar"/>; null si no se supo.</summary>
    public string? GraficaQueLoDibuja { get; private set; }

    /// <summary>La gráfica que dibuja la pantalla principal tras el último <see cref="Conectar"/>; null si no se supo.</summary>
    public string? GraficaDePrincipal { get; private set; }

    /// <summary>
    /// Si el monitor virtual lo dibuja otra gráfica que la pantalla principal (y hay más de una de hardware), el aviso en palabras
    /// (cada cuadro se copia de una a otra); null si coinciden o no se sabe.
    /// </summary>
    public string? AvisoGrafica { get; private set; }

    /// <summary>Por qué falló el último <see cref="Conectar"/>, en palabras para la persona; null si no hay un motivo más preciso.</summary>
    public string? Motivo { get; private set; }

    /// <summary>Se pasó de «Duplicar» a «Extender» para conectarlo: al soltarlo se vuelve a «Duplicar».</summary>
    private bool _restaurarDuplicar;

    /// <summary>El texto que explica el paso de «Duplicar» a «Extender».</summary>
    internal const string TextoPasoAExtender =
        "Las pantallas estaban en «Duplicar» y Windows no deja agregar el monitor virtual así: se pasaron a «Extender» "
        + "mientras se transmite y vuelven a «Duplicar» al terminar. Para ver en la tele el mismo monitor que ves en el PC, "
        + "desmarca el monitor virtual en «Qué pantalla se transmite».";

    /// <summary>Los modos que ofrece el controlador (los de su configuración), sin repetir, del mayor al menor.</summary>
    public IReadOnlyList<(int Ancho, int Alto, int Hz)> ModosOfrecidos() =>
        LeerModos().Select(m => (Ancho: m.PelsWidth, Alto: m.PelsHeight, Hz: m.DisplayFrequency)).Distinct()
            .OrderByDescending(m => (long)m.Ancho * m.Alto).ThenByDescending(m => m.Hz).ToList();

    private List<Modo> LeerModos()
    {
        var todos = new List<Modo>();
        for (var i = 0; ; i++)
        {
            var m = Modo.Nuevo();
            if (!EnumDisplaySettings(Dispositivo, i, ref m)) break;
            todos.Add(m);
        }
        return todos;
    }

    /// <param name="preferido">
    /// El modo al que se quiere llegar (el de la tele, o el que eligió el usuario): se usa el más cercano que ofrezca el
    /// controlador sin pasarse. Null: 1080p a 60 Hz como siempre.
    /// </param>
    public Resultado Conectar(TimeSpan espera, (int Ancho, int Alto, int Hz)? preferido = null)
    {
        ModoConectado = null;
        ModoAnterior = null;
        AvisoTopologia = null;
        AvisoGrafica = null;
        GraficaQueLoDibuja = null;
        GraficaDePrincipal = null;
        Motivo = null;
        if (EstaConectado())
        {
            AjustarModoDelConectado(preferido);
            EvaluarGrafica();
            return Resultado.YaEstaba;
        }

        var todos = LeerModos();
        var modos = todos.Select(m => (Ancho: m.PelsWidth, Alto: m.PelsHeight, Hz: m.DisplayFrequency)).ToList();
        var elegido = ElegirModo(modos, preferido);
        Registro.Anotar($"monitor virtual {Dispositivo}: se busca {(preferido is { } q ? $"{q.Ancho}×{q.Alto}@{q.Hz}" : "el de siempre")}; modos que ofrece el controlador: "
                        + (modos.Count == 0 ? "ninguno" : string.Join(", ", modos.Distinct().Select(m => $"{m.Ancho}×{m.Alto}@{m.Hz}")))
                        + (elegido is { } e ? $"; elegido {e.Ancho}×{e.Alto}@{e.Hz}" : ""));
        if (elegido is null)
        {
            Registro.Error($"monitor virtual {Dispositivo}: el controlador no ofrece ningún modo de 1080p o 720p");
            return Resultado.Fallo;
        }
        var modo = todos.First(m => m.PelsWidth == elegido.Value.Ancho && m.PelsHeight == elegido.Value.Alto && m.DisplayFrequency == elegido.Value.Hz);

        var otras = Monitores.Pantallas().Where(p => !EsEste(p.Nombre)).Select(p => p.Limites);
        var (x, y) = PosicionADerecha(otras);
        modo.PositionX = x;
        modo.PositionY = y;
        modo.Fields = CampoPosicion | CampoBits | CampoAncho | CampoAlto | CampoFrecuencia;

        // Con «Duplicar» Windows rechaza conectar el monitor (ChangeDisplaySettingsEx -1): se pasa a «Extender», como Win+P,
        // y al soltarlo se vuelve. La marca ya dice que hay que volver, por si el proceso muere antes.
        var duplicado = Topologia.HayQuePasarAExtender(Topologia.Actual());
        EscribirMarca(duplicado);
        if (duplicado && !PasarAExtender()) return Abandonar();
        var cambio = ChangeDisplaySettingsEx(Dispositivo, ref modo, IntPtr.Zero, ActualizarRegistro | SinReiniciar, IntPtr.Zero);
        var aplicar = cambio == 0 ? AplicarCambios() : 0;
        if (cambio != 0 || aplicar != 0)
        {
            // Los códigos son los DISP_CHANGE_* de Windows (-1 falló, -2 modo no soportado, -6 el controlador no
            // pudo, 1 hace falta reiniciar…).
            Registro.Error($"monitor virtual {Dispositivo}: Windows no aceptó el cambio de pantalla (ChangeDisplaySettingsEx {cambio}, aplicar {aplicar})");
            Motivo = "Windows no aceptó conectar el monitor virtual. Pruébalo en Configuración → Pantalla.";
            return Abandonar();
        }

        var limite = DateTime.UtcNow + espera;
        while (DateTime.UtcNow < limite)
        {
            // También DXGI: si el monitor aún no está allí, Monitores.Combinar supondría otra salida sin avisar.
            if (EstaConectado()
                && Monitores.Pantallas().Any(p => EsEste(p.Nombre))
                && Dxgi.Salidas().Any(s => EsEste(s.Dispositivo)))
            {
                ModoConectado = elegido;
                // Lo que quedó de verdad: si Windows no lo respetó, el vídeo saldría de otro tamaño que el pedido.
                if (LimitesActuales(Dispositivo) is { } r && (r.Width, r.Height) != (elegido.Value.Ancho, elegido.Value.Alto))
                    Registro.Error($"monitor virtual {Dispositivo}: se pidió {elegido.Value.Ancho}×{elegido.Value.Alto} y quedó en {r.Width}×{r.Height}");
                EvaluarGrafica();
                return Resultado.Conectado;
            }
            Thread.Sleep(250);
        }
        // Qué faltó: conectarse al escritorio, aparecer como pantalla o aparecer en la gráfica (DXGI). Lo último pasa
        // si el monitor quedó en una gráfica que la captura no ve (por ejemplo, en notebooks con dos gráficas).
        Registro.Error($"monitor virtual {Dispositivo}: no apareció en {espera.TotalSeconds:0} s (en el escritorio: {EstaConectado()}, "
                       + $"como pantalla: {Monitores.Pantallas().Any(p => EsEste(p.Nombre))}, en DXGI: {Dxgi.Salidas().Any(s => EsEste(s.Dispositivo))})");
        return Abandonar();
    }

    /// <summary>Mira qué gráfica dibuja el monitor virtual y cuál la pantalla principal; si son distintas, lo anota y deja el aviso.</summary>
    private void EvaluarGrafica()
    {
        try
        {
            GraficaQueLoDibuja = Dxgi.Salidas().FirstOrDefault(s => EsEste(s.Dispositivo))?.NombreAdaptador.Trim();
            GraficaDePrincipal = Controlador.GraficaDePrincipal();
            AvisoGrafica = TextoAvisoGrafica(GraficaQueLoDibuja, GraficaDePrincipal, Controlador.GraficasDelPc(),
                Controlador.ConfiguracionEsNuestra(), Controlador.RutaConfiguracion());
            if (AvisoGrafica is not null) Registro.Anotar($"monitor virtual {Dispositivo}: {AvisoGrafica}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Registro.Error($"monitor virtual {Dispositivo}: no se pudo saber qué gráfica lo dibuja", e);
        }
    }

    /// <summary>
    /// El aviso de que el monitor virtual y la pantalla principal las dibujan gráficas distintas, o null (iguales, no se sabe, o solo
    /// hay una gráfica de hardware: no hay nada que mover). Distingue si la configuración es la nuestra (se puede mover con un botón)
    /// o la cambió la persona (hay que editarla a mano).
    /// </summary>
    internal static string? TextoAvisoGrafica(string? dibuja, string? principal, IReadOnlyList<Nativa.AdaptadorGpu> graficas,
        bool configuracionNuestra, string rutaConfiguracion)
    {
        if (string.IsNullOrWhiteSpace(dibuja) || string.IsNullOrWhiteSpace(principal)) return null;
        if (string.Equals(dibuja.Trim(), principal.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        if (Controlador.NombresDeHardware(graficas).Count < 2) return null;
        var inicio = $"El monitor virtual lo dibuja la gráfica «{dibuja.Trim()}» y la pantalla la «{principal.Trim()}»: cada cuadro se copia de una a otra (más CPU).";
        return configuracionNuestra
            ? $"{inicio} Se puede mover a la «{principal.Trim()}» con «Mover el monitor virtual a la gráfica de la pantalla», en «Avanzado» (pide permiso de administrador)."
            : $"{inicio} Su configuración no es la de Pantalla TV: para moverlo, pon <friendlyname>{principal.Trim()}</friendlyname> en {rutaConfiguracion} y reinicia el controlador (Administrador de dispositivos: deshabilitar y habilitar).";
    }

    /// <summary>Pasa de «Duplicar» a «Extender». Si no se puede, deja el motivo y devuelve false.</summary>
    private bool PasarAExtender()
    {
        var error = Topologia.PasarA(TopologiaPantalla.Extender);
        if (error != 0)
        {
            Registro.Error($"monitor virtual {Dispositivo}: no se pudo pasar de «Duplicar» a «Extender» ({Topologia.Explicar(error)})");
            Motivo = "Las pantallas están en «Duplicar» y Windows no deja agregar el monitor virtual así. No se pudo pasar a «Extender» solo "
                     + $"({Topologia.Explicar(error)}): hazlo con Win+P → Extender y vuelve a transmitir.";
            EscribirMarca(duplicado: false); // no cambió nada: no hay a qué volver
            return false;
        }
        _restaurarDuplicar = true;
        AvisoTopologia = TextoPasoAExtender;
        Registro.Anotar($"monitor virtual {Dispositivo}: las pantallas estaban en «Duplicar»; se pasó a «Extender» (se vuelve al terminar)");
        // SetDisplayConfig vuelve cuando lo aplicó; por si Windows tarda un instante en reflejarlo.
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (Topologia.Actual() == TopologiaPantalla.Duplicar && DateTime.UtcNow < limite) Thread.Sleep(100);
        return true;
    }

    /// <summary>Al soltar: vuelve a «Duplicar» si lo cambiamos y la persona no lo tocó mientras tanto.</summary>
    private static void RestaurarDuplicar()
    {
        if (Topologia.Actual() != TopologiaPantalla.Extender) return; // ya la cambió la persona: se respeta
        var error = Topologia.PasarA(TopologiaPantalla.Duplicar);
        if (error == 0) Registro.Anotar("monitor virtual: las pantallas vuelven a «Duplicar»");
        else Registro.Error($"monitor virtual: no se pudo volver a «Duplicar» ({Topologia.Explicar(error)})");
    }

    /// <summary>
    /// El monitor ya estaba conectado (lo conectó la persona, o Windows al instalar el controlador: lo deja en el primer
    /// modo del controlador, 3840×2160, que ningún codificador aceptó en un portátil con dos gráficas): se le pone el modo
    /// pedido, sin moverlo. Lo que tenía queda en <see cref="ModoAnterior"/>; <see cref="ModoConectado"/> dice cómo quedó.
    /// </summary>
    private void AjustarModoDelConectado((int Ancho, int Alto, int Hz)? preferido)
    {
        var actual = Modo.Nuevo();
        if (!EnumDisplaySettings(Dispositivo, -1, ref actual)) return;
        var antes = (Ancho: actual.PelsWidth, Alto: actual.PelsHeight, Hz: actual.DisplayFrequency);
        var todos = LeerModos();
        var elegido = ElegirModo(todos.Select(m => (Ancho: m.PelsWidth, Alto: m.PelsHeight, Hz: m.DisplayFrequency)), preferido);
        ModoConectado = antes;
        if (elegido is not { } e || !NecesitaCambioDeModo(antes, e))
        {
            Registro.Anotar($"monitor virtual {Dispositivo}: ya estaba conectado a {antes.Ancho}×{antes.Alto}@{antes.Hz}"
                            + (elegido is null ? " (el controlador no ofrece un modo mejor)" : ""));
            return;
        }
        var modo = todos.First(m => m.PelsWidth == e.Ancho && m.PelsHeight == e.Alto && m.DisplayFrequency == e.Hz);
        modo.Fields = CampoBits | CampoAncho | CampoAlto | CampoFrecuencia; // sin posición: no se mueve
        var cambio = ChangeDisplaySettingsEx(Dispositivo, ref modo, IntPtr.Zero, ActualizarRegistro | SinReiniciar, IntPtr.Zero);
        var aplicar = cambio == 0 ? AplicarCambios() : 0;
        var ahora = LimitesActuales(Dispositivo);
        if (cambio != 0 || aplicar != 0 || ahora is null || (ahora.Value.Width, ahora.Value.Height) != (e.Ancho, e.Alto))
        {
            Registro.Error($"monitor virtual {Dispositivo}: ya estaba conectado a {antes.Ancho}×{antes.Alto}@{antes.Hz} y no se pudo pasar a "
                           + $"{e.Ancho}×{e.Alto}@{e.Hz} (ChangeDisplaySettingsEx {cambio}, aplicar {aplicar}); se transmite como está");
            return;
        }
        ModoAnterior = antes;
        ModoConectado = e;
        Registro.Anotar($"monitor virtual {Dispositivo}: ya estaba conectado a {antes.Ancho}×{antes.Alto}@{antes.Hz}; pasó a {e.Ancho}×{e.Alto}@{e.Hz}");
    }

    /// <summary>¿Hay que cambiar el modo de un monitor ya conectado? Sí si el tamaño o la frecuencia no son los elegidos.</summary>
    internal static bool NecesitaCambioDeModo((int Ancho, int Alto, int Hz) actual, (int Ancho, int Alto, int Hz) elegido) =>
        actual != elegido;

    /// <summary>Devuelve el modo que tenía el monitor antes de <see cref="Conectar"/> (solo si lo cambió).</summary>
    public void RestaurarModoAnterior()
    {
        if (ModoAnterior is not { } a) return;
        ModoAnterior = null;
        var modo = LeerModos().FirstOrDefault(m => m.PelsWidth == a.Ancho && m.PelsHeight == a.Alto && m.DisplayFrequency == a.Hz);
        if (modo.Size == 0 || !EstaConectado()) return;
        modo.Fields = CampoBits | CampoAncho | CampoAlto | CampoFrecuencia;
        var cambio = ChangeDisplaySettingsEx(Dispositivo, ref modo, IntPtr.Zero, ActualizarRegistro | SinReiniciar, IntPtr.Zero);
        var aplicar = cambio == 0 ? AplicarCambios() : 0;
        if (cambio != 0 || aplicar != 0)
            Registro.Error($"monitor virtual {Dispositivo}: no se pudo devolver el modo {a.Ancho}×{a.Alto}@{a.Hz} ({cambio}, {aplicar})");
    }

    private bool EsEste(string nombre) => string.Equals(nombre, Dispositivo, StringComparison.OrdinalIgnoreCase);

    /// <summary>No se pudo conectar: se deshace lo que se alcanzó a hacer y se quita la marca.</summary>
    private Resultado Abandonar()
    {
        Liberar();
        return Resultado.Fallo;
    }

    /// <summary>Lo quita del escritorio (las ventanas que estuvieran en él pasan al monitor principal).</summary>
    public bool Desconectar()
    {
        var m = Modo.Nuevo();
        if (!EnumDisplaySettings(Dispositivo, -1, ref m)) return false;
        m.PelsWidth = 0;
        m.PelsHeight = 0;
        m.Fields = CampoPosicion | CampoAncho | CampoAlto;
        return ChangeDisplaySettingsEx(Dispositivo, ref m, IntPtr.Zero, ActualizarRegistro | SinReiniciar, IntPtr.Zero) == 0
               && AplicarCambios() == 0;
    }

    /// <summary>
    /// Lo desconecta (si lo está), espera a que Windows lo aplique y, solo entonces, borra la marca.
    /// Windows a veces lo vuelve a conectar al instante (pasó la primera vez tras instalar el controlador,
    /// 10/11/2026): se vigila un momento y se vuelve a quitar, igual que al instalar.
    /// </summary>
    public bool Liberar() => Liberar(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));

    internal bool Liberar(TimeSpan espera, TimeSpan quieto)
    {
        var volverADuplicar = _restaurarDuplicar || LeerMarcaDuplicado();
        var limite = DateTime.UtcNow + espera;
        var desde = DateTime.UtcNow;
        while (DateTime.UtcNow < limite)
        {
            if (EstaConectado())
            {
                Desconectar();
                desde = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - desde >= quieto)
            {
                break;
            }
            Thread.Sleep(100);
        }
        if (EstaConectado()) return false; // la marca se queda: el próximo arranque lo reintenta
        BorrarMarca();
        if (volverADuplicar) RestaurarDuplicar();
        _restaurarDuplicar = false;
        return true;
    }

    /// <summary>
    /// Al arrancar: si un proceso anterior dejó el monitor conectado (lo mataron mientras transmitía), lo
    /// desconecta. No toca nada si el dueño de la marca sigue vivo (otra copia del programa transmitiendo).
    /// </summary>
    public static void RecuperarSiQuedoConectado()
    {
        if (!File.Exists(RutaMarca) || DuenoVivo()) return;
        var monitor = Detectar();
        if (monitor is null) BorrarMarca();
        else monitor.Liberar();
    }

    /// <summary>Lo suelta solo si la marca es de este proceso (al salir, por si la tarea no alcanzó a hacerlo).</summary>
    public static void LiberarSiEsNuestro()
    {
        if (LeerMarca() == Environment.ProcessId) Detectar()?.Liberar();
    }

    internal static string RutaMarca => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PantallaTv", "monitor-virtual-conectado.marca");

    /// <summary>La marca: el PID del dueño y, en una segunda línea, «duplicar» si las pantallas estaban en «Duplicar» y hay que volver.</summary>
    private static void EscribirMarca(bool duplicado)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RutaMarca)!);
            File.WriteAllText(RutaMarca, Environment.ProcessId + (duplicado ? "\n" + MarcaDuplicar : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // sin poder escribir la marca se transmite igual; solo se pierde la recuperación tras un cierre brusco
        }
    }

    /// <summary>Para el comando manual <c>conectar</c>: lo conectado a mano se queda, no es de una transmisión.</summary>
    public static void OlvidarMarca() => BorrarMarca();

    private static void BorrarMarca()
    {
        try { File.Delete(RutaMarca); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private const string MarcaDuplicar = "duplicar";

    private static int? LeerMarca() => LeerLineasMarca() is { Length: > 0 } l && int.TryParse(l[0].Trim(), out var pid) ? pid : null;

    private static bool LeerMarcaDuplicado() => LeerLineasMarca() is { Length: > 1 } l && l[1].Trim() == MarcaDuplicar;

    private static string[]? LeerLineasMarca()
    {
        try { return File.ReadAllLines(RutaMarca); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool DuenoVivo()
    {
        if (LeerMarca() is not { } pid || pid == Environment.ProcessId) return false;
        try
        {
            using var otro = System.Diagnostics.Process.GetProcessById(pid);
            using var yo = System.Diagnostics.Process.GetCurrentProcess();
            return !otro.HasExited && string.Equals(otro.ProcessName, yo.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // no existe ese proceso
        }
    }

    /// <summary>Posición y tamaño actuales (en píxeles reales) de una salida conectada; null si no se puede leer.</summary>
    internal static Rectangle? LimitesActuales(string dispositivo)
    {
        var m = Modo.Nuevo();
        return EnumDisplaySettings(dispositivo, -1, ref m) ? new Rectangle(m.PositionX, m.PositionY, m.PelsWidth, m.PelsHeight) : null;
    }

    /// <summary>
    /// De los modos que ofrece el controlador, el que se usa para transmitir: 1920×1080 a 60 Hz; si no
    /// está, el de mayor área que no pase de 1080p a 60 Hz; si tampoco, ninguno.
    /// </summary>
    internal static (int Ancho, int Alto, int Hz)? ElegirModo(IEnumerable<(int Ancho, int Alto, int Hz)> ofrecidos,
        (int Ancho, int Alto, int Hz)? preferido)
    {
        var lista = ofrecidos.Distinct().ToList();
        if (preferido is { } p)
        {
            // Horizontal siempre: una tele o tableta en vertical informa su modo «de pie».
            var (ancho, alto) = (Math.Max(p.Ancho, p.Alto), Math.Min(p.Ancho, p.Alto));
            // 59,94 Hz llega redondeado a 60; se tolera 1 Hz de más.
            var caben = lista.Where(m => m.Ancho <= ancho && m.Alto <= alto && m.Hz <= p.Hz + 1 && m.Hz >= 24).ToList();
            if (caben.Count > 0)
            {
                var proporcion = (double)ancho / alto;
                bool MismaForma((int Ancho, int Alto, int Hz) m) => Math.Abs((double)m.Ancho / m.Alto - proporcion) < 0.03;
                // Primero el tamaño exacto; si no, la misma forma (sin franjas); si no, el más grande. Después, más Hz.
                return caben.OrderByDescending(m => m.Ancho == ancho && m.Alto == alto)
                    .ThenByDescending(MismaForma)
                    .ThenByDescending(m => (long)m.Ancho * m.Alto)
                    .ThenByDescending(m => m.Hz)
                    .First();
            }
        }
        return ElegirModo(lista);
    }

    internal static (int Ancho, int Alto, int Hz)? ElegirModo(IEnumerable<(int Ancho, int Alto, int Hz)> ofrecidos)
    {
        var a60 = ofrecidos.Where(m => m.Hz == 60 && m.Ancho <= 1920 && m.Alto <= 1080).ToList();
        if (a60.Count == 0) return null;
        return a60.OrderByDescending(m => (long)m.Ancho * m.Alto).First();
    }

    /// <summary>Dónde poner el monitor nuevo: pegado al borde derecho de las pantallas que ya hay, arriba.</summary>
    internal static (int X, int Y) PosicionADerecha(IEnumerable<Rectangle> pantallas)
    {
        var lista = pantallas.ToList();
        return lista.Count == 0 ? (0, 0) : (lista.Max(p => p.Right), 0);
    }

    /// <summary>Aplica los cambios acumulados con <see cref="SinReiniciar"/> (llamada con todo nulo).</summary>
    private static int AplicarCambios() => AplicarPendientes(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string dispositivo, int modo, ref Modo dm);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(string dispositivo, ref Modo dm, IntPtr ventana, int banderas, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExA")]
    private static extern int AplicarPendientes(IntPtr dispositivo, IntPtr dm, IntPtr ventana, int banderas, IntPtr param);

    /// <summary>DEVMODEA de Windows.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct Modo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;

        public static Modo Nuevo() => new() { Size = (short)Marshal.SizeOf<Modo>() };
    }
}
