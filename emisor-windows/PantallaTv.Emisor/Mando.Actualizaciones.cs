using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// Las actualizaciones, vistas desde la ventana y la bandeja: buscar (solo cuando el usuario pulsa «Buscar
/// actualizaciones»: el programa nunca se conecta solo), ofrecer, descargar con avance y abrir el instalador.
/// Reglas: mientras se transmite no se ofrece ni se instala nada (lo que aparezca espera a que termine); «Después»
/// esconde esa versión hasta la próxima búsqueda.
/// </summary>
internal sealed partial class Mando
{
    private readonly ComprobadorActualizaciones? _comprobador;
    private readonly TimeProvider _reloj;
    /// <summary>Se cancela al salir: corta una búsqueda o una descarga en curso.</summary>
    private readonly CancellationTokenSource _cancelarActualizacion = new();
    private CancellationTokenSource? _cancelarDescarga;
    private int _buscando; // 1 mientras hay una búsqueda en curso

    /// <summary>La última versión nueva válida que se encontró (aunque se haya pospuesto).</summary>
    private Novedad? _novedad;
    /// <summary>La que se pospuso con «Después»: no se vuelve a ofrecer hasta la próxima búsqueda.</summary>
    private Version? _pospuesta;
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

    /// <summary>«Buscar actualizaciones» se puede si no hay una búsqueda o una descarga en curso.</summary>
    public bool PuedeBuscarActualizaciones => _comprobador is not null && !BuscandoActualizaciones && AvanceDescarga is null;

    /// <summary>Al abrir: borra el instalador de la actualización anterior (que ya no está en uso). No se conecta a nada.</summary>
    public void IniciarActualizaciones() => _comprobador?.Limpiar();

    public void BuscarActualizacionesAhora() => _ = ComprobarAsync();

    /// <summary>Una búsqueda, pedida por el usuario: siempre dice el resultado y vuelve a ofrecer una versión pospuesta.</summary>
    internal async Task ComprobarAsync()
    {
        if (_comprobador is null) return;
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
        EnLaInterfaz(() => AlRevisar(r));
    }

    /// <summary>
    /// Pasa <paramref name="accion"/> al hilo de la interfaz. False si ya se está saliendo (no queda bucle de mensajes
    /// que la corra): lo pendiente se descarta.
    /// </summary>
    private bool EnLaInterfaz(Action accion)
    {
        if (_cancelarActualizacion.IsCancellationRequested) return false;
        try
        {
            _ui.Post(_ => accion(), null);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void AlRevisar(Revision r)
    {
        BuscandoActualizaciones = false;
        var hora = _reloj.GetLocalNow().ToString("dd-MM-yyyy HH:mm");
        if (r.Novedad is { } n)
        {
            _novedad = n;
            _pospuesta = null;
            EstadoActualizaciones = $"Hay una versión nueva: {n.Texto} (comprobado el {hora}).";
            Aviso?.Invoke(Transmitiendo
                ? $"Hay una versión nueva ({n.Texto}): se ofrece al terminar de transmitir."
                : $"Hay una versión nueva ({n.Texto})" + (n.Notas.Length > 0 ? $": {n.Notas}" : ".") + " Ábrelo para actualizar.",
                ToolTipIcon.Info);
        }
        else if (r.Rechazo == Rechazo.NoEsNueva)
        {
            // Lo publicado ya no es más nuevo (se retiró una versión): tampoco se instala lo que esperaba.
            _novedad = null;
            _instalarAlTerminar = null;
            EstadoActualizaciones = $"Tienes la última versión (comprobado el {hora}).";
            Aviso?.Invoke($"Tienes la última versión ({Instalacion.Version}).", ToolTipIcon.Info);
        }
        else
        {
            // Sin internet, una firma que no cuadra…: no se ofrece nada nuevo y el detalle queda en el registro. Una
            // versión ya encontrada (y verificada) sigue ofreciéndose.
            EstadoActualizaciones = $"No se pudo comprobar el {hora}.";
            Aviso?.Invoke("No se pudo comprobar si hay una versión nueva. Vuelve a intentarlo más tarde.", ToolTipIcon.Info);
        }
        Cambio?.Invoke();
    }

    /// <summary>«Después»: la banda y el menú dejan de ofrecer esta versión hasta la próxima búsqueda.</summary>
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
        var enviado = EnLaInterfaz(() =>
        {
            try { AlDescargar(n, ruta, cancelar.IsCancellationRequested); }
            finally { hecho.TrySetResult(); }
        });
        if (!enviado) hecho.TrySetResult(); // saliendo: no se instala nada
        await hecho.Task.ConfigureAwait(false);
    }

    private void AlDescargar(Novedad n, string? ruta, bool cancelada)
    {
        AvanceDescarga = null;
        if (cancelada)
        {
            // Se está saliendo: no se instala nada.
            if (ruta is not null) _comprobador?.Limpiar();
        }
        else if (ruta is null)
        {
            // No cuadró (tamaño, huella, red): no se instala nada. Ya quedó en el registro.
            _novedad = null;
            EstadoActualizaciones = "La descarga no se pudo completar o verificar. Vuelve a buscar más tarde.";
            Aviso?.Invoke("No se pudo descargar la versión nueva. Vuelve a buscar más tarde.", ToolTipIcon.Info);
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

    /// <summary>
    /// Lo que esperaba a que terminara la transmisión: abrir el instalador ya descargado. No si la transmisión terminó
    /// porque se está saliendo del programa (sería un instalador que aparece solo al cerrar).
    /// </summary>
    private void AlTerminarTransmision()
    {
        if (_cancelarActualizacion.IsCancellationRequested) return;
        if (_instalarAlTerminar is { } pendiente) Instalar(pendiente.Version, pendiente.Ruta);
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
        try { _cancelarActualizacion.Cancel(); } catch (ObjectDisposedException) { return; } // ya liberado
        _comprobador?.Dispose();
        _cancelarDescarga?.Dispose();
        _cancelarActualizacion.Dispose();
    }
}
