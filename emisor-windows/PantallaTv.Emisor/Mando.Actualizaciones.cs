using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// Las actualizaciones, vistas desde la ventana y la bandeja: buscar (al abrir, con
/// <see cref="EsperaInicial"/>, y cada <see cref="Intervalo"/>), avisar, descargar con avance y abrir el instalador.
/// Reglas: apagado no hay ninguna conexión; mientras se transmite no se ofrece ni se instala nada (lo que aparezca
/// espera a que termine); «Después» no vuelve a avisar de esa versión hasta el próximo inicio.
/// </summary>
internal sealed partial class Mando
{
    /// <summary>Al abrir, se espera esto antes de buscar: no compite con el arranque (ni con el de Windows).</summary>
    public static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Intervalo = TimeSpan.FromHours(24);

    private readonly ComprobadorActualizaciones? _comprobador;
    private readonly TimeProvider _reloj;
    /// <summary>Se cancela al salir: corta una búsqueda o una descarga en curso.</summary>
    private readonly CancellationTokenSource _cancelarActualizacion = new();
    /// <summary>La de la descarga en curso: también se cancela al apagar la búsqueda.</summary>
    private CancellationTokenSource? _cancelarDescarga;
    private ITimer? _temporizador;
    private int _buscando; // 1 mientras hay una búsqueda en curso (la del temporizador corre en otro hilo)

    /// <summary>La última versión nueva válida que se encontró (aunque se haya pospuesto).</summary>
    private Novedad? _novedad;
    /// <summary>La que se pospuso con «Después»: no se vuelve a avisar hasta el próximo inicio.</summary>
    private Version? _pospuesta;
    /// <summary>De qué versión ya salió el globo (una vez por versión en cada inicio).</summary>
    private Version? _avisada;
    /// <summary>Se encontró mientras se transmitía: el globo sale al terminar.</summary>
    private bool _avisarAlTerminar;
    /// <summary>Se terminó de descargar mientras se transmitía: el instalador se abre al terminar.</summary>
    private (string Version, string Ruta)? _instalarAlTerminar;

    /// <summary>Abre el instalador descargado. Las pruebas lo cambian para no ejecutar nada.</summary>
    internal Action<string> LanzarInstalador { get; set; } = AbrirInstalador;

    public bool BuscandoActualizaciones { get; private set; }

    /// <summary>Porcentaje de la descarga en curso; null si no se está descargando.</summary>
    public int? AvanceDescarga { get; private set; }

    /// <summary>Qué pasó con la última búsqueda, para la nota de «Avanzado». Null antes de la primera.</summary>
    public string? EstadoActualizaciones { get; private set; }

    /// <summary>La versión que se ofrece ahora (banda de la ventana y menú de la bandeja), o null.</summary>
    public Novedad? NovedadOfrecida =>
        _novedad is { } n && n.Version != _pospuesta && !Transmitiendo && AvanceDescarga is null && _instalarAlTerminar is null ? n : null;

    /// <summary>«Buscar ahora» se puede si está activado y no hay una búsqueda o una descarga en curso.</summary>
    public bool PuedeBuscarActualizaciones =>
        _comprobador is not null && Ajustes.BuscarActualizaciones && !BuscandoActualizaciones && AvanceDescarga is null;

    /// <summary>La primera búsqueda a los <see cref="EsperaInicial"/> y después cada <see cref="Intervalo"/>.</summary>
    public void IniciarActualizaciones()
    {
        if (_comprobador is null) return;
        _comprobador.Limpiar(); // el instalador de la actualización anterior, que ya no está en uso
        _temporizador = _reloj.CreateTimer(_ => _ = ComprobarAsync(manual: false), null, EsperaInicial, Intervalo);
    }

    /// <summary>
    /// Activa o apaga la búsqueda. No es un ajuste de la emisión: se puede cambiar transmitiendo. Apagar corta una
    /// descarga en curso y olvida lo encontrado (también un instalador que esperaba el fin de la transmisión).
    /// </summary>
    public void CambiarBuscarActualizaciones(bool activar)
    {
        Ajustes.BuscarActualizaciones = activar;
        if (!activar)
        {
            _cancelarDescarga?.Cancel();
            _novedad = null;
            _instalarAlTerminar = null;
            _avisarAlTerminar = false;
            EstadoActualizaciones = null;
        }
        Guardar();
    }

    public void BuscarActualizacionesAhora() => _ = ComprobarAsync(manual: true);

    /// <summary>
    /// Una búsqueda. La automática no cuenta nada si falla (solo queda en el registro); la manual («Buscar ahora»)
    /// siempre dice el resultado y vuelve a ofrecer una versión pospuesta.
    /// </summary>
    internal async Task ComprobarAsync(bool manual)
    {
        // Apagado = cero conexiones. Se mira aquí, en cada vuelta del temporizador.
        if (_comprobador is null || !Ajustes.BuscarActualizaciones) return;
        if (Interlocked.Exchange(ref _buscando, 1) == 1) return;
        EnLaInterfaz(() => { BuscandoActualizaciones = true; Cambio?.Invoke(); });
        Revision r;
        try
        {
            r = await _comprobador.BuscarAsync(_cancelarActualizacion.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // BuscarAsync no lanza por la red; algo inesperado (cerrando el programa) no debe dejar «Buscando…» pegado.
            r = new Revision(null, Rechazo.Red, e.Message);
        }
        finally
        {
            Volatile.Write(ref _buscando, 0);
        }
        EnLaInterfaz(() => AlRevisar(r, manual));
    }

    private void EnLaInterfaz(Action accion) => _ui.Post(_ => accion(), null);

    private void AlRevisar(Revision r, bool manual)
    {
        BuscandoActualizaciones = false;
        // Se apagó mientras se buscaba: lo encontrado no se ofrece.
        if (!Ajustes.BuscarActualizaciones)
        {
            Cambio?.Invoke();
            return;
        }
        var hora = _reloj.GetLocalNow().ToString("dd-MM-yyyy HH:mm");
        if (r.Novedad is { } n)
        {
            _novedad = n;
            if (manual) _pospuesta = null;
            EstadoActualizaciones = $"Hay una versión nueva: {n.Texto} (comprobado el {hora}).";
            if (Transmitiendo)
            {
                _avisarAlTerminar = true;
                if (manual) Aviso?.Invoke($"Hay una versión nueva ({n.Texto}): se ofrece al terminar de transmitir.", ToolTipIcon.Info);
            }
            else
            {
                if (manual) _avisada = null; // «Buscar ahora» siempre contesta
                AvisarNovedad();
            }
        }
        else if (r.Rechazo == Rechazo.NoEsNueva)
        {
            // Lo publicado ya no es más nuevo (se retiró un release): tampoco se instala lo que esperaba.
            _novedad = null;
            _instalarAlTerminar = null;
            EstadoActualizaciones = $"Tienes la última versión (comprobado el {hora}).";
            if (manual) Aviso?.Invoke($"Tienes la última versión ({Instalacion.Version}).", ToolTipIcon.Info);
        }
        else
        {
            // Sin internet, una firma que no cuadra…: no se ofrece nada nuevo y queda en el registro. Una versión ya
            // encontrada (y verificada) sigue ofreciéndose.
            EstadoActualizaciones = $"No se pudo comprobar el {hora}; se vuelve a intentar mañana.";
            if (manual) Aviso?.Invoke("No se pudo comprobar si hay una versión nueva. Vuelve a intentarlo más tarde.", ToolTipIcon.Info);
        }
        Cambio?.Invoke();
    }

    /// <summary>
    /// El globo, una vez por versión: las búsquedas diarias no lo repiten (la banda de la ventana sigue). No mira
    /// <see cref="Transmitiendo"/>: lo deciden quienes lo llaman (al terminar, la tarea todavía no acabó del todo).
    /// </summary>
    private void AvisarNovedad()
    {
        if (_novedad is not { } n || n.Version == _pospuesta || n.Version == _avisada || AvanceDescarga is not null || _instalarAlTerminar is not null) return;
        _avisada = n.Version;
        Aviso?.Invoke($"Hay una versión nueva ({n.Texto})" + (n.Notas.Length > 0 ? $": {n.Notas}" : ".") +
                      " Ábrelo para actualizar.", ToolTipIcon.Info);
    }

    /// <summary>«Después»: la banda y el menú dejan de ofrecer esta versión hasta el próximo inicio (o «Buscar ahora»).</summary>
    public void PosponerActualizacion()
    {
        if (_novedad is { } n) _pospuesta = n.Version;
        Cambio?.Invoke();
    }

    public void ActualizarAhora() => _ = ActualizarAhoraAsync();

    /// <summary>
    /// Descarga (con avance), verifica y abre el instalador, que es visible: el usuario confirma ahí y el instalador le
    /// pide a este programa que se cierre antes de copiar. Nunca mientras se transmite.
    /// </summary>
    internal async Task ActualizarAhoraAsync()
    {
        if (_comprobador is null || NovedadOfrecida is not { } n) return;
        AvanceDescarga = 0;
        EstadoActualizaciones = $"Descargando la versión {n.Texto}…";
        _cancelarDescarga?.Dispose();
        var cancelar = _cancelarDescarga = CancellationTokenSource.CreateLinkedTokenSource(_cancelarActualizacion.Token);
        Cambio?.Invoke();

        var ultimo = 0;
        void Avance(int porcentaje)
        {
            // Viene del hilo de la descarga; solo los cambios, y solo si sigue descargando.
            if (Interlocked.Exchange(ref ultimo, porcentaje) == porcentaje) return;
            EnLaInterfaz(() => { if (AvanceDescarga is not null) { AvanceDescarga = porcentaje; Cambio?.Invoke(); } });
        }

        string? ruta;
        try
        {
            ruta = await Task.Run(() => _comprobador.DescargarAsync(n, Avance, cancelar.Token)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // DescargarAsync no lanza por la red ni por el disco; algo inesperado no debe dejar «Descargando…» pegado.
            Registro.Anotar($"actualizaciones: descarga de la {n.Texto} interrumpida: {e.Message}");
            ruta = null;
        }
        var hecho = new TaskCompletionSource();
        EnLaInterfaz(() =>
        {
            try { AlDescargar(n, ruta, cancelar.IsCancellationRequested); }
            finally { hecho.TrySetResult(); }
        });
        await hecho.Task.ConfigureAwait(false);
    }

    private void AlDescargar(Novedad n, string? ruta, bool cancelada)
    {
        AvanceDescarga = null;
        if (cancelada || !Ajustes.BuscarActualizaciones)
        {
            // Se apagó la búsqueda (o se está saliendo): no se instala nada.
            if (ruta is not null) _comprobador?.Limpiar();
        }
        else if (ruta is null)
        {
            // No cuadró (tamaño, huella, red): no se instala nada. Ya quedó en el registro.
            _novedad = null;
            EstadoActualizaciones = "La descarga no se pudo completar o verificar; se vuelve a intentar mañana.";
            Aviso?.Invoke("No se pudo descargar la versión nueva. Se vuelve a intentar mañana.", ToolTipIcon.Info);
        }
        else if (Transmitiendo)
        {
            _instalarAlTerminar = (n.Texto, ruta);
            EstadoActualizaciones = $"La versión {n.Texto} está lista: se abre el instalador al terminar de transmitir.";
        }
        else
        {
            Instalar(n.Texto, ruta);
        }
        Cambio?.Invoke();
    }

    private void Instalar(string version, string ruta)
    {
        _instalarAlTerminar = null;
        try
        {
            LanzarInstalador(ruta);
            Registro.Anotar($"actualizaciones: abierto el instalador de la {version}");
            _novedad = null;
            EstadoActualizaciones = $"Se abrió el instalador de la versión {version}.";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Registro.Anotar($"actualizaciones: no se pudo abrir el instalador: {e.Message}");
            EstadoActualizaciones = "No se pudo abrir el instalador descargado.";
            Aviso?.Invoke("No se pudo abrir el instalador de la versión nueva: " + e.Message, ToolTipIcon.Warning);
        }
    }

    /// <summary>Lo que esperaba a que terminara la transmisión: abrir el instalador o el globo de una versión nueva.</summary>
    private void AlTerminarTransmision()
    {
        if (_instalarAlTerminar is { } pendiente)
        {
            Instalar(pendiente.Version, pendiente.Ruta);
            return;
        }
        if (!_avisarAlTerminar) return;
        _avisarAlTerminar = false;
        AvisarNovedad();
    }

    /// <summary>
    /// El instalador visible (el asistente, nunca <c>--silencioso</c>) en modo actualización: conserva las opciones
    /// de la instalación y le pide a este programa que se cierre recién cuando el usuario confirma.
    /// </summary>
    private static void AbrirInstalador(string ruta) =>
        Process.Start(new ProcessStartInfo(ruta)
        {
            Arguments = $"{Instalador.ArgInstalar} {Instalador.ArgActualizar}",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ruta)!,
        })?.Dispose();

    private void LiberarActualizaciones()
    {
        _temporizador?.Dispose();
        try { _cancelarActualizacion.Cancel(); } catch (ObjectDisposedException) { return; } // ya liberado
        _comprobador?.Dispose();
        _cancelarDescarga?.Dispose();
        _cancelarActualizacion.Dispose();
    }
}
