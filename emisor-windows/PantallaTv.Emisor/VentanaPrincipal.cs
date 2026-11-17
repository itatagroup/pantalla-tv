using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PantallaTv.Emisor;

/// <summary>
/// La ventana principal: lo mismo que el menú de la bandeja, todo a la vista.
/// No guarda estado propio: lee y cambia el <see cref="Mando"/> y se repinta cuando este avisa un cambio, así
/// lo que se toque en el menú de la bandeja se ve aquí al instante y al revés.
/// </summary>
internal sealed class VentanaPrincipal : Form
{
    /// <summary>Ancho del contenido en píxeles lógicos (a 100 %).</summary>
    private const int Ancho = 560;
    /// <summary>Columna de las etiquetas («Transmitir a», «Pantalla»…).</summary>
    private const int ColumnaEtiquetas = 128;

    private readonly Mando _mando;
    private readonly Action _salir;
    private readonly System.Windows.Forms.Timer _reloj = new() { Interval = 1000 };

    // Contenido con barra de desplazamiento por si no cabe (pantallas chicas al 150 %).
    private readonly Panel _contenido = new() { Dock = DockStyle.Fill, AutoScroll = true };

    private readonly Punto _punto = new();
    private readonly Label _estado = new() { UseMnemonic = false, AutoSize = false };

    // Banda de versión nueva (o de su descarga). Lee Mando.NovedadOfrecida y Mando.AvanceDescarga.
    private readonly Tarjeta _banda = new() { Hundida = true, AccessibleName = "Versión nueva", AccessibleRole = AccessibleRole.Grouping };
    private readonly Label _textoBanda = new() { UseMnemonic = false, AutoSize = false };
    private readonly Boton _actualizar = new() { Text = "Actuali&zar ahora", Estilo = EstiloBoton.Primario };
    private readonly Boton _despues = new() { Text = "Después", Estilo = EstiloBoton.Secundario, UseMnemonic = false };

    private readonly Tarjeta _principal = new() { AccessibleName = "Transmisión", AccessibleRole = AccessibleRole.Grouping };
    private readonly Label _etiquetaTv = new() { Text = "Transmitir &a" };
    private readonly Lista _tvs = new() { AccessibleName = "Transmitir a" };
    private readonly Boton _escribirIp = new() { Text = "Escribir &IP…", Estilo = EstiloBoton.Secundario };
    private const string SinTvs = "No se ve ninguna TV: abre «Pantalla» en ella o escribe su IP.";
    private const string TodasOcupadas = "Las TV que se ven ya reciben una transmisión: abre «Pantalla» en otra o escribe su IP.";
    private readonly Label _notaTvs = new() { Text = SinTvs, UseMnemonic = false };
    private readonly Boton _abrirRed = new() { Text = "Abrir Configuración de &red", Estilo = EstiloBoton.Secundario };
    private readonly Label _etiquetaPantalla = new() { Text = "Qué &pantalla" };
    private readonly Panel _pantallas = new() { AccessibleName = "Qué pantalla", AccessibleRole = AccessibleRole.Grouping };
    private readonly Boton _instalarVirtual = new() { Text = "Instalar el &monitor virtual…", Estilo = EstiloBoton.Secundario };
    private readonly Label _notaVirtual = new() { Text = "Para usar la tele como un segundo monitor (pide permiso de administrador).", UseMnemonic = false };
    private readonly Tarjeta _vivo = new() { Hundida = true, AccessibleName = "Transmisión en curso", AccessibleRole = AccessibleRole.Grouping };
    /// <summary>Una fila por transmisión (hasta <see cref="Mando.MaxTransmisiones"/>): «TV ← pantalla», su estado y su botón «Detener».</summary>
    private sealed class FilaVivo
    {
        public readonly Label Titulo = new() { UseMnemonic = false };
        public readonly Label Estado = new() { UseMnemonic = false };
        public readonly Label Nota = new() { UseMnemonic = false };
        public readonly Boton Detener = new() { Text = "Detener", Estilo = EstiloBoton.Peligro, Icono = IconoBoton.Detener, UseMnemonic = false };
        public Transmision? T;
        public IEnumerable<Control> Controles => [Titulo, Estado, Nota, Detener];
    }

    private readonly FilaVivo[] _filas = [.. Enumerable.Range(0, Mando.MaxTransmisiones).Select(_ => new FilaVivo())];
    /// <summary>Por qué no se puede sumar otra transmisión (ya hay 4, o no queda TV o pantalla libre); se ve mientras se transmite.</summary>
    private readonly Label _notaOtra = new() { UseMnemonic = false };
    /// <summary>Se ven la TV y la pantalla a elegir: sin transmitir, o transmitiendo con lugar para otra.</summary>
    private bool _seleccion = true;
    private readonly Boton _boton = new() { Estilo = EstiloBoton.Primario };

    private readonly Tarjeta _ajustes = new() { AccessibleName = "Ajustes", AccessibleRole = AccessibleRole.Grouping };
    private readonly Label _notaBloqueo = new() { Text = Mando.PorQueBloqueado, UseMnemonic = false };
    private readonly Casilla _sonido = new() { Text = "Enviar también el &sonido" };
    private readonly Casilla _silenciar = new() { Text = "Si&lenciar el PC mientras transmite" };
    private readonly Label _notaSilenciar = new() { Text = "Solo si se envía el sonido.", UseMnemonic = false };
    private readonly Casilla _inicio = new() { Text = "Abrir al iniciar &Windows" };
    private readonly Label _notaInicio = new() { Text = "Solo en el programa instalado (no en una copia suelta).", UseMnemonic = false };

    private readonly Boton _expansor = new() { Text = "A&vanzado", Estilo = EstiloBoton.Expansor, AccessibleRole = AccessibleRole.ButtonDropDown };
    private readonly Tarjeta _avanzado = new() { AccessibleName = "Avanzado", AccessibleRole = AccessibleRole.Grouping };
    private readonly Label _etiquetaForma = new() { Text = "&Forma de capturar" };
    private readonly Lista _forma = new() { AccessibleName = "Forma de capturar" };
    private readonly Label _etiquetaModoVirtual = new() { Text = "M&onitor virtual" };
    private readonly Lista _modoVirtual = new() { AccessibleName = "Tamaño y frecuencia del monitor virtual" };
    private readonly Label _notaModoVirtual = new() { UseMnemonic = false };
    private readonly Boton _reinstalarVirtual = new() { Estilo = EstiloBoton.Secundario };
    private readonly Boton _quitarVirtual = new() { Estilo = EstiloBoton.Secundario };
    private readonly Boton _reubicarVirtual = new() { Estilo = EstiloBoton.Secundario, Visible = false };
    private readonly Casilla _reconectar = new() { Text = "Re&conectar solo si se corta" };
    private readonly Casilla _telemetria = new() { Text = "&Guardar telemetría (CSV)" };
    private readonly Boton _abrirTelemetria = new() { Text = "Abrir carp&eta de telemetría", Estilo = EstiloBoton.Secundario };
    private readonly Boton _diagnostico = new() { Text = "Diag&nóstico para enviar…", Estilo = EstiloBoton.Secundario };
    private readonly Label _notaDiagnostico = new() { UseMnemonic = false };
    private readonly Boton _ffmpeg = new() { Text = "Descargar ff&mpeg…", Estilo = EstiloBoton.Secundario };
    private readonly Label _notaFfmpeg = new() { UseMnemonic = false };
    private readonly Boton _vbCable = new() { Text = "Instalar &VB-CABLE…", Estilo = EstiloBoton.Secundario };
    /// <summary>Con un enlace para donar a VB-Audio (lo pide su licencia).</summary>
    private readonly LinkLabel _notaVbCable = new() { UseMnemonic = false, LinkBehavior = LinkBehavior.AlwaysUnderline };
    private readonly Label _notaBloqueoAvanzado = new() { Text = Mando.PorQueBloqueado, UseMnemonic = false };
    private readonly Boton _buscarAhora = new() { Text = "B&uscar actualizaciones", Estilo = EstiloBoton.Secundario };
    private readonly Label _notaActualizaciones = new() { UseMnemonic = false };

    private readonly Label _version = new() { Text = $"Pantalla TV {Instalacion.Version}", UseMnemonic = false };
    private readonly Boton _botonSalir = new() { Text = "Salir", Estilo = EstiloBoton.Secundario, UseMnemonic = false };

    private Font? _fuente, _fuenteChica, _fuenteEstado, _fuenteDato, _fuenteFuerte, _fuenteBoton;
    private bool _centrada;
    private bool _abierto; // «Avanzado» desplegado
    private bool _refrescando; // al cargar los controles desde el mando no hay que escribir de vuelta
    private bool _saliendo;

    public VentanaPrincipal(Mando mando, Action salir)
    {
        _mando = mando;
        _salir = salir;
        Text = Instalacion.Nombre;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None; // las medidas se escalan a mano con DeviceDpi (ver Acomodar)
        KeyPreview = true;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

        Armar();
        AplicarTema();

        _mando.Cambio += Refrescar;
        _reloj.Tick += (_, _) => Refrescar(); // las TV aparecen y desaparecen solas; el tiempo de la transmisión corre
        SystemEvents.UserPreferenceChanged += AlCambiarPreferencias;
        SystemEvents.DisplaySettingsChanged += AlCambiarPantallas;
    }

    // ── Construcción ───────────────────────────────────────────────────────────────────────────

    private void Armar()
    {
        SuspendLayout();
        Controls.Add(_contenido);
        _contenido.Controls.AddRange([_punto, _estado, _banda, _principal, _ajustes, _expansor, _avanzado, _version, _botonSalir]);
        _banda.Controls.AddRange([_textoBanda, _actualizar, _despues]);
        _principal.Controls.AddRange([_etiquetaTv, _tvs, _escribirIp, _notaTvs, _abrirRed, _etiquetaPantalla, _pantallas, _instalarVirtual, _notaVirtual, _vivo, _notaOtra, _boton]);
        _vivo.Controls.AddRange([.. _filas.SelectMany(f => f.Controles)]);
        foreach (var f in _filas)
        {
            var fila = f;
            fila.Detener.Click += (_, _) => { if (fila.T is { } t) _mando.Detener(t); };
        }
        _ajustes.Controls.AddRange([_notaBloqueo, _sonido, _silenciar, _notaSilenciar, _inicio, _notaInicio]);
        _avanzado.Controls.AddRange([_notaBloqueoAvanzado, _etiquetaForma, _forma, _etiquetaModoVirtual, _modoVirtual, _notaModoVirtual,
            _reinstalarVirtual, _quitarVirtual, _reubicarVirtual, _reconectar, _telemetria, _abrirTelemetria, _diagnostico, _notaDiagnostico,
            _ffmpeg, _notaFfmpeg, _vbCable, _notaVbCable, _buscarAhora, _notaActualizaciones]);

        // Orden de tabulación = orden en pantalla.
        OrdenTab(_banda, _principal, _ajustes, _expansor, _avanzado, _botonSalir);
        OrdenTab(_actualizar, _despues);
        OrdenTab(_vivo, _etiquetaTv, _tvs, _escribirIp, _abrirRed, _etiquetaPantalla, _pantallas, _instalarVirtual, _boton);
        OrdenTab(_filas.Select(f => f.Detener).ToArray());
        OrdenTab(_sonido, _silenciar, _inicio);
        OrdenTab(_etiquetaForma, _forma, _etiquetaModoVirtual, _modoVirtual, _reinstalarVirtual, _quitarVirtual, _reubicarVirtual, _reconectar, _telemetria, _abrirTelemetria, _diagnostico, _ffmpeg, _vbCable, _buscarAhora);
        foreach (var l in Notas().Append(_version).Append(_estado).Append(_textoBanda)) l.TabStop = false;
        foreach (var l in new[] { _etiquetaTv, _etiquetaPantalla, _etiquetaForma, _etiquetaModoVirtual, _version }) l.TextAlign = ContentAlignment.MiddleLeft;
        _avanzado.Visible = false;
        AcceptButton = _boton;

        foreach (var l in _filas.SelectMany(f => new[] { f.Titulo, f.Estado, f.Nota }).Append(_notaOtra)) l.TabStop = false;
        _boton.Click += (_, _) => AlBotonPrincipal();
        _escribirIp.Click += (_, _) => EscribirIp();
        _abrirRed.Click += (_, _) => Red.AbrirConfiguracion();
        _instalarVirtual.Click += (_, _) => _mando.InstalarMonitorVirtual();
        _tvs.SelectionChangeCommitted += (_, _) => { if (_tvs.Valor is Destino d) _elegido = d.Clave; };
        _forma.SelectionChangeCommitted += (_, _) => _mando.Cambiar(a => a.Tuberia = (string?)_forma.Valor);
        _modoVirtual.SelectionChangeCommitted += (_, _) => _mando.Cambiar(a => a.ModoMonitorVirtual = (string?)_modoVirtual.Valor);
        _reinstalarVirtual.Click += (_, _) => _mando.InstalarMonitorVirtual();
        _quitarVirtual.Click += (_, _) => _mando.QuitarMonitorVirtual();
        _reubicarVirtual.Click += (_, _) => _mando.ReubicarMonitorVirtual();
        Alternar(_sonido, (a, v) => a.ConAudio = v);
        Alternar(_silenciar, (a, v) => a.SilenciarPc = v);
        Alternar(_reconectar, (a, v) => a.Reconectar = v);
        Alternar(_telemetria, (a, v) => a.GuardarTelemetria = v);
        _inicio.CheckedChanged += (_, _) => { if (!_refrescando) _mando.CambiarInicioConWindows(_inicio.Checked); };
        _expansor.Click += (_, _) => { _abierto = !_abierto; Refrescar(); if (_abierto) _contenido.ScrollControlIntoView(_avanzado); };
        _abrirTelemetria.Click += (_, _) => Mando.AbrirTelemetria();
        _diagnostico.Click += (_, _) => _mando.Diagnosticar();
        _ffmpeg.Click += (_, _) => AlPulsarFfmpeg();
        _vbCable.Click += (_, _) => AlPulsarVbCable();
        _notaVbCable.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(DescargadorVbCable.UrlDonar) { UseShellExecute = true })?.Dispose(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Registro.Error("no se pudo abrir la página para donar", e); }
        };
        _actualizar.Click += (_, _) => _mando.ActualizarAhora();
        _despues.Click += (_, _) => _mando.PosponerActualizacion();
        _buscarAhora.Click += (_, _) => _mando.BuscarActualizacionesAhora();
        _botonSalir.Click += (_, _) => _salir();
        ResumeLayout(false);
    }

    private static void OrdenTab(params Control[] controles)
    {
        for (var i = 0; i < controles.Length; i++) controles[i].TabIndex = i;
    }

    private void Alternar(Casilla casilla, Action<Ajustes, bool> guardar) =>
        casilla.CheckedChanged += (_, _) => { if (!_refrescando) _mando.Cambiar(a => guardar(a, casilla.Checked)); };

    /// <summary>La TV elegida en la lista (su clave), para no perderla cuando la lista se refresca.</summary>
    private string? _elegido;

    // ── Acciones ───────────────────────────────────────────────────────────────────────────────

    private void AlBotonPrincipal()
    {
        // Con transmisiones en curso este botón suma otra (cada fila tiene su «Detener»).
        if (_tvs.Valor is Destino d) _mando.Iniciar(d.Receptor);
        else EscribirIp();
    }

    private void EscribirIp()
    {
        var ip = DialogoIp.Pedir(this, _mando.Ajustes.UltimaIp ?? "");
        if (ip is not null && _mando.IniciarConIp(ip)) _elegido = null;
    }

    /// <summary>Muestra la ventana y la trae al frente (menú Inicio, doble clic en la bandeja, segunda instancia).</summary>
    public void Mostrar()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>Cerrar de verdad (al salir del programa); la X solo la esconde.</summary>
    public void Cerrar()
    {
        _saliendo = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_saliendo && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Esconder();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>Se avisa la primera vez que la X no cierra el programa (lo hace la bandeja).</summary>
    public event Action? PrimeraVezEscondida;

    private void Esconder()
    {
        Hide();
        if (_mando.MarcarAvisoBandeja()) PrimeraVezEscondida?.Invoke();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Escape esconde la ventana, salvo que esté cerrando una lista desplegada.
        if (keyData == Keys.Escape && !(ActiveControl is ComboBox { DroppedDown: true }))
        {
            Esconder();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _reloj.Enabled = Visible;
        if (Visible)
        {
            _mando.ActualizarPantallas();
            Refrescar();
        }
    }

    // ── Refresco ───────────────────────────────────────────────────────────────────────────────

    private void AlCambiarPantallas(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { _mando.ActualizarPantallas(); Refrescar(); });
    }

    /// <summary>Lleva el estado del mando a los controles. Se llama en cada cambio y cada segundo.</summary>
    private void Refrescar()
    {
        // Escondida no se ve nada: al mostrarse (OnVisibleChanged) se refresca todo de una vez.
        if (IsDisposed || !Visible) return;
        _refrescando = true;
        try
        {
            var p = Dibujo.Vigente;
            var m = _mando;
            var transmitiendo = m.Transmitiendo;
            var editable = m.PuedeCambiarAjustes;

            _estado.Text = m.Fase == Fase.Inactivo && !transmitiendo ? "Lista para transmitir" : m.Estado;
            _estado.ForeColor = m.Fase == Fase.Inactivo ? p.Texto : p.DeFase(m.Fase);
            _punto.ForeColor = p.DeFase(m.Fase);
            _estado.AccessibleName = "Estado: " + _estado.Text;

            // Versión nueva: se ofrece (nunca mientras se transmite) o se ve el avance de su descarga.
            var novedad = m.NovedadOfrecida;
            _banda.Visible = novedad is not null || m.AvanceDescarga is not null;
            _actualizar.Visible = _despues.Visible = novedad is not null;
            _textoBanda.Text = novedad is { } n ? $"Hay una versión nueva ({n.Texto})" + (n.Notas.Length > 0 ? $": {n.Notas}" : ".")
                : m.AvanceDescarga is { } avance ? $"Descargando la versión nueva… {avance} %"
                : "";
            _banda.AccessibleName = _textoBanda.Text;

            // Mientras se transmite, la TV y la pantalla de abajo son para sumar otra: solo las libres (hasta 4 transmisiones).
            var motivoNoOtra = transmitiendo ? m.MotivoNoOtra() : null;
            _seleccion = motivoNoOtra is null;
            _notaOtra.Text = motivoNoOtra ?? "";
            _notaOtra.Visible = motivoNoOtra is not null;

            // Destinos.
            var todos = m.Destinos();
            var destinos = m.DestinosLibres();
            _tvs.Cargar([.. destinos.Select(d => new ElementoLista(d.TextoLista, d))],
                destinos.FirstOrDefault(d => d.Clave == _elegido) ?? Mando.Preferido(destinos, m.Ajustes.UltimaIp));
            if (_tvs.Valor is Destino actual) _elegido = actual.Clave;
            _tvs.Enabled = destinos.Count > 0;
            // Transmitiendo y sin TV libre a la vista, la lista queda vacía: se dice por qué y que queda la IP.
            var todasOcupadas = transmitiendo && destinos.Count == 0;
            _notaTvs.Visible = todasOcupadas || (!transmitiendo && !Mando.HayTvAnunciada(todos));
            var avisoRed = _notaTvs.Visible && !transmitiendo ? m.AvisoRed() : null;
            _notaTvs.Text = todasOcupadas ? TodasOcupadas : avisoRed ?? SinTvs;
            _abrirRed.Visible = avisoRed is not null;

            // Pantallas: se rehacen solo si cambió la lista.
            var pantallas = m.OpcionesPantalla();
            CargarPantallas(pantallas.Opciones);
            _instalarVirtual.Visible = _notaVirtual.Visible = !transmitiendo && !pantallas.HayVirtual;
            _instalarVirtual.Enabled = !m.InstalandoControlador;
            _instalarVirtual.Text = m.TextoInstalarVirtual.Replace(" monitor", " &monitor"); // tecla de acceso Alt+M

            // Una fila por transmisión arriba; debajo, la elección de TV y pantalla si falta o si se puede sumar otra.
            foreach (var c in new Control[] { _etiquetaTv, _tvs, _escribirIp, _etiquetaPantalla, _pantallas })
                c.Visible = _seleccion;
            var transmisiones = m.Transmisiones;
            _vivo.Visible = transmisiones.Count > 0;
            for (var i = 0; i < _filas.Length; i++)
            {
                var f = _filas[i];
                f.T = i < transmisiones.Count ? transmisiones[i] : null;
                foreach (var c in f.Controles) c.Visible = f.T is not null;
                if (f.T is not { } t) continue;
                f.Titulo.Text = Mando.TituloFila(t);
                f.Estado.Text = Mando.EstadoFila(t);
                f.Nota.Text = m.NotaFila(t);
                f.Nota.Visible = f.Nota.Text.Length > 0;
                f.Detener.Text = Mando.TextoBotonFila(t);
                f.Detener.AccessibleName = (t.EnCurso ? "Detener la transmisión a " : "Quitar la transmisión con error a ") + t.Receptor;
                f.Estado.AccessibleName = $"Estado de {t.Receptor}: {f.Estado.Text}";
                f.Estado.ForeColor = p.TextoFuerte; // el contraste medido es el del texto fuerte sobre la tarjeta; el error se lee por su texto
            }

            _boton.Visible = _seleccion;
            _boton.Text = transmitiendo ? "&Transmitir otra pantalla…" : "&Transmitir";
            _boton.Estilo = EstiloBoton.Primario;
            _boton.Icono = IconoBoton.Reproducir;

            // Ajustes.
            _notaBloqueo.Visible = _notaBloqueoAvanzado.Visible = !editable;
            _forma.Cargar([.. m.OpcionesForma().Select(o => new ElementoLista(o.Texto, o.Valor))], m.Ajustes.Tuberia);
            if (_abierto) RefrescarMonitorVirtual(m, editable);
            _sonido.Checked = m.Ajustes.ConAudio;
            _silenciar.Checked = m.Ajustes.SilenciarPc;
            _reconectar.Checked = m.Ajustes.Reconectar;
            _telemetria.Checked = m.Ajustes.GuardarTelemetria;
            foreach (var c in new Control[] { _sonido, _reconectar, _telemetria, _forma }) c.Enabled = editable;
            _silenciar.Enabled = m.PuedeSilenciar;
            // «Solo si se envía el sonido» o, si haría falta, el aviso de que falta VB-CABLE.
            var faltaCable = m.AvisoFaltaVbCable();
            _notaSilenciar.Text = faltaCable ?? "Solo si se envía el sonido.";
            _notaSilenciar.Visible = faltaCable is not null || (editable && !m.Ajustes.ConAudio);

            var puedeInicio = m.PuedeIniciarConWindows;
            _inicio.Enabled = puedeInicio;
            _inicio.Checked = puedeInicio && m.IniciaConWindows;
            _notaInicio.Visible = !puedeInicio;

            _buscarAhora.Enabled = m.PuedeBuscarActualizaciones;
            _buscarAhora.Text = m.BuscandoActualizaciones ? "Buscando…" : "B&uscar actualizaciones";
            _notaActualizaciones.Text = m.EstadoActualizaciones ??
                $"Solo cuando lo pulsas: mira en GitHub si hay una versión publicada, sin enviar nada. Versión instalada: {Instalacion.Version}.";

            if (_abierto) RefrescarFfmpeg(m);
            if (_abierto) RefrescarVbCable(m);
            _diagnostico.Enabled = m.PuedeDiagnosticar;
            _notaDiagnostico.Text = m.Diagnosticando ? "Preparando el diagnóstico (unos segundos)…"
                : transmitiendo ? "Detén la transmisión para hacer el diagnóstico: prueba la captura."
                : "Un informe de este PC para mandar si algo no funciona.";

            _expansor.Abierto = _abierto;
            _expansor.AccessibleName = _abierto ? "Avanzado, desplegado" : "Avanzado, plegado";
            _avanzado.Visible = _abierto;
        }
        finally
        {
            _refrescando = false;
        }
        AcomodarSiCambio();
    }

    private string? _distribucion;

    /// <summary>
    /// Acomodar mide textos y mueve ~40 controles: solo si cambió algo que mueve la distribución (qué se ve, los textos
    /// que pueden ocupar varias líneas, las pantallas, el DPI), no cada segundo por el reloj o los números en vivo.
    /// </summary>
    private void AcomodarSiCambio()
    {
        // De cada fila solo lo que cambia su alto: el estado cuando no es «Transmitiendo» (esos números cambian cada 2 s sin mover nada).
        var filas = string.Join(";", _filas.Select(f => f.T is null ? "-" : string.Join("/", f.Titulo.Text, f.T.Fase == Fase.Transmitiendo ? "T" : f.Estado.Text, f.Nota.Text)));
        var clave = string.Join("|", DeviceDpi, _abierto, _vivo.Visible, _estado.Text, filas, _seleccion, _boton.Visible, _notaOtra.Text, _pantallas.Controls.Count, _instalarVirtual.Text, _notaDiagnostico.Text,
            _banda.Visible, _actualizar.Visible, _textoBanda.Text, _buscarAhora.Text, _notaActualizaciones.Text, _notaModoVirtual.Text, _ffmpeg.Visible, _ffmpeg.Text, _notaFfmpeg.Text, _modoVirtual.Visible, _quitarVirtual.Visible, _reubicarVirtual.Visible, _notaTvs.Text, _abrirRed.Visible,
            string.Concat(Notas().Select(n => n.Visible ? "1" : "0")));
        if (clave == _distribucion) return;
        _distribucion = clave;
        Acomodar();
    }

    private void CargarPantallas(IReadOnlyList<Opcion<string?>> opciones)
    {
        var textos = opciones.Select(o => o.Texto).ToList();
        var actuales = _pantallas.Controls.Cast<Opcion>().Select(o => o.Text).ToList();
        if (!textos.SequenceEqual(actuales))
        {
            _pantallas.SuspendLayout();
            foreach (Control c in _pantallas.Controls) c.Dispose();
            _pantallas.Controls.Clear();
            for (var i = 0; i < opciones.Count; i++)
            {
                var valor = opciones[i].Valor;
                var opcion = new Opcion { Text = opciones[i].Texto, TabIndex = i, Font = _fuente, BackColor = _principal.BackColor };
                opcion.CheckedChanged += (_, _) =>
                {
                    if (!_refrescando && opcion.Checked) _mando.ElegirPantalla(valor); // también mientras se transmite: es para la próxima
                };
                _pantallas.Controls.Add(opcion);
            }
            _pantallas.ResumeLayout(false);
            _distribucion = null; // las opciones nuevas nacen sin tamaño ni lugar: acomodar aunque sean las mismas en número
        }
        for (var i = 0; i < opciones.Count; i++)
        {
            var opcion = (Opcion)_pantallas.Controls[i];
            opcion.Checked = opciones[i].Elegida;
            opcion.Enabled = opciones[i].Disponible; // las que ya van a una TV no se pueden elegir
            opcion.TabStop = opcion.Checked; // como en Windows: Tab entra a la elegida y las flechas recorren el grupo
        }
    }

    // ── Medidas y posiciones ───────────────────────────────────────────────────────────────────

    private int S(int logicos) => this.Escalar(logicos);

    private void CrearFuentes()
    {
        LiberarFuentes();
        // En píxeles a 100 % (escalados): nada baja de 12 px. Segoe UI Semibold en lugar de negrita (pesos 600).
        _fuente = new Font("Segoe UI", S(14), GraphicsUnit.Pixel);
        _fuenteChica = new Font("Segoe UI", S(12), GraphicsUnit.Pixel);
        _fuenteEstado = new Font("Segoe UI Semibold", S(17), GraphicsUnit.Pixel);
        _fuenteDato = new Font("Segoe UI Semibold", S(20), GraphicsUnit.Pixel);
        _fuenteFuerte = new Font("Segoe UI Semibold", S(14), GraphicsUnit.Pixel);
        _fuenteBoton = new Font("Segoe UI Semibold", S(15), GraphicsUnit.Pixel);
        AsignarFuentes();
    }

    /// <summary>
    /// Pone a cada control su fuente. Va también en cada <see cref="Acomodar"/>: WinForms reescala por su cuenta, al
    /// hacerse visibles, los controles que estaban ocultos cuando la ventana cambió de monitor, y su fuente quedaba
    /// escalada dos veces (visto al 150 % con «Avanzado» plegado al mover la ventana).
    /// </summary>
    private void AsignarFuentes()
    {
        foreach (var c in Todos(this)) c.Font = _fuente;
        foreach (var l in Notas()) l.Font = _fuenteChica;
        _version.Font = _fuenteChica;
        _estado.Font = _fuenteEstado;
        foreach (var f in _filas)
        {
            f.Titulo.Font = _fuenteFuerte;
            f.Estado.Font = _fuente;
        }
        _boton.Font = _fuenteBoton;
        _expansor.Font = _fuenteFuerte;
        foreach (var l in new[] { _tvs, _forma, _modoVirtual }) l.ItemHeight = S(24);
    }

    private IEnumerable<Label> Notas() =>
        [_notaTvs, _notaVirtual, _notaOtra, .. _filas.Select(f => f.Nota), _notaBloqueo, _notaSilenciar, _notaInicio, _notaDiagnostico, _notaBloqueoAvanzado, _notaActualizaciones, _notaModoVirtual, _notaFfmpeg, _notaVbCable];

    private static IEnumerable<Control> Todos(Control raiz)
    {
        foreach (Control c in raiz.Controls)
        {
            yield return c;
            foreach (var h in Todos(c)) yield return h;
        }
    }

    /// <summary>Alto de un texto que puede ocupar varias líneas en ese ancho.</summary>
    private static int AltoTexto(Label l, int ancho) =>
        TextRenderer.MeasureText(l.Text, l.Font, new Size(ancho, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

    private static void Poner(Control c, int x, int y, int ancho, int alto) => c.SetBounds(x, y, ancho, alto);

    /// <summary>Coloca todo (a mano: así se escala bien con el DPI y las secciones aparecen y desaparecen sin huecos).</summary>
    private void Acomodar()
    {
        if (_fuente is null) return;
        AsignarFuentes();
        SuspendLayout();
        _contenido.SuspendLayout();
        var margen = S(16);
        var ancho = S(Ancho);
        var anchoTarjeta = ancho - 2 * margen;
        var relleno = S(16);
        var interior = anchoTarjeta - 2 * relleno;
        var etiquetas = S(ColumnaEtiquetas);
        var campo = interior - etiquetas;
        var altoCampo = _tvs.Height; // lo fija la fuente y ItemHeight
        var altoFila = S(30);
        var altoBoton = S(40);
        var y0 = -_contenido.VerticalScroll.Value;
        var y = y0 + margen;

        // Estado.
        var anchoEstado = ancho - 2 * margen - S(22);
        var altoEstado = Math.Max(S(26), AltoTexto(_estado, anchoEstado));
        Poner(_punto, margen + S(2), y + S(8), S(12), S(12));
        Poner(_estado, margen + S(22), y, anchoEstado, altoEstado);
        y += altoEstado + S(12);

        // Banda de versión nueva.
        if (_banda.Visible)
        {
            var by = relleno;
            var altoTextoBanda = Math.Max(S(22), AltoTexto(_textoBanda, interior));
            Poner(_textoBanda, relleno, by, interior, altoTextoBanda);
            by += altoTextoBanda;
            if (_actualizar.Visible)
            {
                by += S(10);
                var anchoActualizar = TextRenderer.MeasureText(_actualizar.Text.Replace("&", ""), _actualizar.Font).Width + S(34);
                var anchoDespues = TextRenderer.MeasureText(_despues.Text, _despues.Font).Width + S(34);
                Poner(_actualizar, relleno - S(3), by - S(3), anchoActualizar + S(6), altoBoton + S(6));
                Poner(_despues, relleno + anchoActualizar + S(8) - S(3), by - S(3), anchoDespues + S(6), altoBoton + S(6));
                by += altoBoton;
            }
            by += relleno;
            Poner(_banda, margen, y, anchoTarjeta, by);
            y += by + S(12);
        }

        // Tarjeta principal.
        var cy = relleno;
        if (_vivo.Visible)
        {
            cy = AcomodarVivo(cy, relleno, interior);
            if (_notaOtra.Visible) cy = Nota(_notaOtra, relleno, cy + S(10), interior) - S(8);
            cy += S(14);
        }
        if (_seleccion)
        {
            var anchoIp = TextRenderer.MeasureText(_escribirIp.Text, _escribirIp.Font).Width + S(28);
            Etiqueta(_etiquetaTv, relleno, cy, etiquetas, altoCampo);
            Poner(_tvs, relleno + etiquetas, cy, campo - anchoIp - S(8), altoCampo);
            Poner(_escribirIp, relleno + interior - anchoIp - S(3), cy - S(3), anchoIp + S(6), altoCampo + S(6));
            cy += altoCampo;
            if (_notaTvs.Visible) cy = Nota(_notaTvs, relleno + etiquetas, cy + S(4), campo);
            if (_abrirRed.Visible)
            {
                var anchoRed = TextRenderer.MeasureText(_abrirRed.Text.Replace("&", ""), _abrirRed.Font).Width + S(34);
                Poner(_abrirRed, relleno + etiquetas - S(3), cy + S(6) - S(3), Math.Min(campo, anchoRed) + S(6), altoBoton + S(6));
                cy += S(6) + altoBoton;
            }
            cy += S(14);

            Etiqueta(_etiquetaPantalla, relleno, cy, etiquetas, altoFila);
            var n = _pantallas.Controls.Count;
            for (var i = 0; i < n; i++) Poner(_pantallas.Controls[i], 0, i * altoFila, campo, altoFila);
            Poner(_pantallas, relleno + etiquetas, cy, campo, Math.Max(1, n) * altoFila);
            cy += n * altoFila;
            if (_instalarVirtual.Visible)
            {
                var anchoInstalar = TextRenderer.MeasureText(_instalarVirtual.Text.Replace("&", ""), _instalarVirtual.Font).Width + S(34);
                Poner(_instalarVirtual, relleno + etiquetas - S(3), cy + S(4), Math.Min(campo + S(6), anchoInstalar), altoBoton);
                cy += S(4) + altoBoton;
                cy = Nota(_notaVirtual, relleno + etiquetas, cy + S(2), campo);
            }
        }
        if (_boton.Visible)
        {
            cy += S(18);
            Poner(_boton, relleno - S(3), cy - S(3), interior + S(6), S(46) + S(6));
            cy += S(46);
        }
        else
        {
            cy -= S(14); // sin botón debajo de las filas: queda solo su separación
        }
        cy += relleno;
        Poner(_principal, margen, y, anchoTarjeta, cy);
        y += cy + S(12);

        // Tarjeta de ajustes.
        cy = relleno;
        if (_notaBloqueo.Visible) cy = Nota(_notaBloqueo, relleno, cy, interior) + S(10);
        cy = Casilla(_sonido, relleno, cy, interior);
        cy = Casilla(_silenciar, relleno, cy, interior);
        if (_notaSilenciar.Visible) cy = Nota(_notaSilenciar, relleno + S(31), cy - S(4), interior - S(31)) + S(4);
        cy = Casilla(_inicio, relleno, cy, interior);
        if (_notaInicio.Visible) cy = Nota(_notaInicio, relleno + S(31), cy - S(4), interior - S(31)) + S(4);
        cy += relleno - S(4);
        Poner(_ajustes, margen, y, anchoTarjeta, cy);
        y += cy + S(8);

        // Avanzado.
        var anchoExpansor = TextRenderer.MeasureText(_expansor.Text.Replace("&", ""), _expansor.Font).Width + S(48);
        Poner(_expansor, margen - S(3), y, anchoExpansor, S(38));
        y += S(38) + S(6);
        if (_avanzado.Visible)
        {
            cy = relleno;
            if (_notaBloqueoAvanzado.Visible) cy = Nota(_notaBloqueoAvanzado, relleno, cy, interior) + S(10);
            Etiqueta(_etiquetaForma, relleno, cy, etiquetas, altoCampo);
            Poner(_forma, relleno + etiquetas, cy, campo, altoCampo);
            cy += altoCampo + S(14);
            Etiqueta(_etiquetaModoVirtual, relleno, cy, etiquetas, altoCampo);
            if (_modoVirtual.Visible)
            {
                Poner(_modoVirtual, relleno + etiquetas, cy, campo, altoCampo);
                cy += altoCampo + S(4);
            }
            cy = Nota(_notaModoVirtual, relleno + etiquetas, cy + (_modoVirtual.Visible ? 0 : S(4)), campo);
            var mitadVirtual = (campo - S(8)) / 2;
            var anchoReinstalar = _quitarVirtual.Visible ? mitadVirtual : campo;
            Poner(_reinstalarVirtual, relleno + etiquetas - S(3), cy + S(6) - S(3), anchoReinstalar + S(6), altoBoton + S(6));
            if (_quitarVirtual.Visible)
                Poner(_quitarVirtual, relleno + etiquetas + mitadVirtual + S(8) - S(3), cy + S(6) - S(3), campo - mitadVirtual - S(8) + S(6), altoBoton + S(6));
            cy += S(6) + altoBoton;
            if (_reubicarVirtual.Visible)
            {
                Poner(_reubicarVirtual, relleno + etiquetas - S(3), cy + S(8) - S(3), campo + S(6), altoBoton + S(6));
                cy += S(8) + altoBoton;
            }
            cy += S(14);
            cy = Casilla(_reconectar, relleno, cy, interior);
            cy = Casilla(_telemetria, relleno, cy, interior);
            cy += S(8);
            var mitad = (interior - S(8)) / 2;
            Poner(_abrirTelemetria, relleno - S(3), cy - S(3), mitad + S(6), altoBoton + S(6));
            Poner(_diagnostico, relleno + mitad + S(8) - S(3), cy - S(3), interior - mitad - S(8) + S(6), altoBoton + S(6));
            cy += altoBoton;
            cy = Nota(_notaDiagnostico, relleno + mitad + S(8), cy + S(6), interior - mitad - S(8));
            cy += S(14);
            var anchoFfmpeg = TextRenderer.MeasureText(_ffmpeg.Text.Replace("&", ""), _ffmpeg.Font).Width + S(34);
            if (_ffmpeg.Visible)
            {
                Poner(_ffmpeg, relleno - S(3), cy - S(3), anchoFfmpeg + S(6), altoBoton + S(6));
                cy += altoBoton;
            }
            cy = Nota(_notaFfmpeg, relleno, cy + S(6), interior);
            cy += S(14);
            var anchoVbCable = TextRenderer.MeasureText(_vbCable.Text.Replace("&", ""), _vbCable.Font).Width + S(34);
            if (_vbCable.Visible)
            {
                Poner(_vbCable, relleno - S(3), cy - S(3), anchoVbCable + S(6), altoBoton + S(6));
                cy += altoBoton;
            }
            cy = Nota(_notaVbCable, relleno, cy + S(6), interior);
            cy += S(14);
            var anchoBuscar = TextRenderer.MeasureText(_buscarAhora.Text.Replace("&", ""), _buscarAhora.Font).Width + S(34);
            Poner(_buscarAhora, relleno - S(3), cy + S(4) - S(3), anchoBuscar + S(6), altoBoton + S(6));
            cy += S(4) + altoBoton;
            cy = Nota(_notaActualizaciones, relleno, cy + S(6), interior);
            cy += relleno;
            Poner(_avanzado, margen, y, anchoTarjeta, cy);
            y += cy + S(12);
        }

        // Pie.
        var anchoSalir = TextRenderer.MeasureText(_botonSalir.Text, _botonSalir.Font).Width + S(40);
        Poner(_botonSalir, ancho - margen - anchoSalir + S(3), y - S(3), anchoSalir, altoBoton + S(6));
        Poner(_version, margen, y, ancho - 2 * margen - anchoSalir, altoBoton);
        y += altoBoton + margen;

        // La ventana crece y se achica con el contenido, sin pasarse del área de trabajo del monitor.
        var alto = y - y0;
        var area = Screen.FromControl(this).WorkingArea;
        var marco = Height - ClientSize.Height;
        var maximo = area.Height - marco - S(16);
        var barra = alto > maximo ? SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) : 0;
        _contenido.AutoScrollMinSize = new Size(0, alto);
        ClientSize = new Size(ancho + barra, Math.Min(alto, maximo));
        if (Bottom > area.Bottom && Visible) Top = Math.Max(area.Top, area.Bottom - Height);
        _contenido.ResumeLayout(false);
        ResumeLayout(true); // el panel con Dock = Fill tiene que seguir al nuevo alto
    }

    /// <summary>
    /// ffmpeg de respaldo: el botón para bajarlo (con confirmación) o para cancelar la descarga, y la nota de cómo está.
    /// Solo con «Avanzado» abierto: buscarlo mira el disco y el PATH.
    /// </summary>
    private void RefrescarFfmpeg(Mando m)
    {
        var ruta = m.RutaFfmpeg;
        var bajando = m.AvanceFfmpeg is not null;
        _ffmpeg.Visible = bajando || ruta is null;
        _ffmpeg.Enabled = bajando || m.PuedeDescargarFfmpeg;
        _ffmpeg.Text = m.AvanceFfmpeg is { } p ? $"Cancelar la descarga ({p} %)" : "Descargar ff&mpeg…";
        _notaFfmpeg.Text = m.EstadoFfmpeg ?? (ruta is not null
            ? $"ffmpeg (respaldo de la captura): encontrado en {ruta}."
            : "ffmpeg no está instalado. No hace falta salvo que la captura propia no funcione en este PC. Se descarga solo con tu confirmación.");
    }

    /// <summary>VB-CABLE: el botón para instalarlo (con confirmación) y la nota con los créditos y el enlace para donar.</summary>
    private void RefrescarVbCable(Mando m)
    {
        var hay = m.HayVbCable;
        _vbCable.Visible = !hay;
        _vbCable.Enabled = m.PuedeInstalarVbCable;
        _vbCable.Text = m.InstalandoVbCable ? "Instalando VB-CABLE…" : "Instalar &VB-CABLE…";
        var estado = m.EstadoVbCable ?? (hay
            ? "VB-CABLE está instalado: lo del monitor virtual suena solo en la tele y el PC sigue sonando."
            : "VB-CABLE (opcional) permite que el PC siga sonando mientras la tele oye solo lo del monitor virtual. Se instala solo con tu confirmación.");
        const string donar = "Donar a VB-Audio";
        var texto = $"{estado} {DescargadorVbCable.Creditos} {donar}.";
        if (_notaVbCable.Text != texto)
        {
            _notaVbCable.Text = texto;
            _notaVbCable.LinkArea = new LinkArea(texto.Length - donar.Length - 1, donar.Length);
        }
        _notaVbCable.LinkColor = _notaVbCable.ActiveLinkColor = _notaVbCable.VisitedLinkColor = _notaVbCable.ForeColor;
    }

    private void AlPulsarVbCable()
    {
        var r = MessageBox.Show(this, Mando.TextoConfirmacionVbCable(), "Instalar VB-CABLE", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r == DialogResult.OK) _mando.InstalarVbCableAhora();
    }

    private void AlPulsarFfmpeg()
    {
        if (_mando.AvanceFfmpeg is not null)
        {
            _mando.CancelarDescargaFfmpeg();
            return;
        }
        var r = MessageBox.Show(this, Mando.TextoConfirmacionFfmpeg(), "Descargar ffmpeg", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r == DialogResult.OK) _mando.DescargarFfmpegAhora();
    }

    /// <summary>
    /// La sección del monitor virtual en «Avanzado». Solo con «Avanzado» abierto: leer sus modos y su configuración
    /// toca el registro y el disco.
    /// </summary>
    private void RefrescarMonitorVirtual(Mando m, bool editable)
    {
        var hay = Mando.HayMonitorVirtual;
        if (hay) _modoVirtual.Cargar([.. m.OpcionesModoVirtual().Select(o => new ElementoLista(o.Texto, o.Valor))], m.Ajustes.ModoMonitorVirtual);
        _modoVirtual.Visible = hay;
        _modoVirtual.Enabled = editable;
        _notaModoVirtual.Text = m.NotaMonitorVirtual(_tvs.Valor is Destino d && !d.UltimaIp ? d.Receptor : null, hay);
        _reinstalarVirtual.Text = m.TextoReinstalarVirtual(hay);
        _quitarVirtual.Text = m.TextoQuitarVirtual;
        _reinstalarVirtual.AccessibleName = _reinstalarVirtual.Text.Replace("&", "").TrimEnd('…') + " el monitor virtual";
        _quitarVirtual.AccessibleName = _quitarVirtual.Text.Replace("&", "").TrimEnd('…') + " el monitor virtual";
        _quitarVirtual.Visible = hay || m.InstalandoControlador;
        _reinstalarVirtual.Enabled = _quitarVirtual.Enabled = editable && !m.InstalandoControlador;
        // Solo cuando el monitor virtual lo dibuja otra gráfica que la pantalla (y su configuración es la nuestra).
        _reubicarVirtual.Visible = hay && m.PuedeReubicarMonitorVirtual;
        _reubicarVirtual.Text = m.TextoReubicarVirtual;
        _reubicarVirtual.AccessibleName = "Mover el monitor virtual a la gráfica de la pantalla";
        _reubicarVirtual.Enabled = editable && !m.InstalandoControlador;
    }

    /// <summary>Las filas de las transmisiones: título y botón «Detener» arriba, el estado debajo y, si hay, una nota chica.</summary>
    private int AcomodarVivo(int cy, int relleno, int interior)
    {
        var r = S(14);
        var ancho = interior - 2 * r;
        var anchoDetener = TextRenderer.MeasureText("Detener", _filas[0].Detener.Font).Width + S(56);
        var anchoTexto = ancho - anchoDetener - S(8);
        var altoBoton = S(36);
        var vy = r;
        foreach (var f in _filas)
        {
            if (f.T is null) continue;
            var altoTitulo = Math.Max(S(22), AltoTexto(f.Titulo, anchoTexto));
            Poner(f.Titulo, r, vy, anchoTexto, altoTitulo);
            Poner(f.Detener, r + ancho - anchoDetener, vy - S(3), anchoDetener, altoBoton);
            var fy = vy + altoTitulo + S(2);
            var altoEstado = Math.Max(S(22), AltoTexto(f.Estado, anchoTexto));
            Poner(f.Estado, r, fy, anchoTexto, altoEstado);
            fy += altoEstado;
            if (f.Nota.Visible)
            {
                var altoNota = AltoTexto(f.Nota, anchoTexto);
                Poner(f.Nota, r, fy + S(2), anchoTexto, altoNota);
                fy += S(2) + altoNota;
            }
            vy = Math.Max(fy, vy - S(3) + altoBoton) + S(14);
        }
        vy += r - S(14);
        Poner(_vivo, relleno, cy, interior, vy);
        return cy + vy;
    }
    private static void Etiqueta(Label l, int x, int y, int ancho, int alto) => Poner(l, x, y, ancho, alto);

    private int Nota(Label l, int x, int y, int ancho)
    {
        var alto = AltoTexto(l, ancho);
        Poner(l, x, y, ancho, alto);
        return y + alto;
    }

    private int Casilla(Casilla c, int x, int y, int ancho)
    {
        Poner(c, x - S(3), y, ancho + S(3), S(32));
        return y + S(32);
    }

    // ── Tema y DPI ─────────────────────────────────────────────────────────────────────────────

    private void AplicarTema()
    {
        var p = Tema.Actual();
        Dibujo.Vigente = p;
        BackColor = _contenido.BackColor = p.Fondo;
        foreach (var t in new[] { _principal, _ajustes, _avanzado }) t.BackColor = p.Superficie;
        _vivo.BackColor = _banda.BackColor = p.SuperficieSuave;
        foreach (var c in Todos(_contenido))
        {
            if (c is Tarjeta) continue;
            c.BackColor = c.Parent?.BackColor ?? p.Fondo;
            c.ForeColor = p.Texto;
        }
        foreach (var l in Notas()) l.ForeColor = p.Secundario;
        foreach (var f in _filas)
        {
            f.Titulo.ForeColor = f.Estado.ForeColor = p.TextoFuerte;
            f.Nota.ForeColor = p.Secundario;
        }
        _version.ForeColor = p.Secundario;
        foreach (var l in new[] { _tvs, _forma, _modoVirtual }) l.BackColor = p.Superficie;
        if (IsHandleCreated) AplicarTemaVentana(p);
        CrearFuentes();
        Refrescar();
        Invalidate(true);
    }

    private void AplicarTemaVentana(Paleta p)
    {
        Tema.BarraDeTitulo(Handle, p.Oscura);
        // La barra de desplazamiento (si hace falta) y las listas desplegadas, oscuras en el tema oscuro.
        var tema = p.Oscura ? "DarkMode_Explorer" : "Explorer";
        _ = SetWindowTheme(_contenido.Handle, tema, null);
        foreach (var l in new[] { _tvs, _forma, _modoVirtual }) _ = SetWindowTheme(l.Handle, p.Oscura ? "DarkMode_CFD" : "CFD", null);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AplicarTemaVentana(Dibujo.Vigente);
    }

    private void AlCambiarPreferencias(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (Tema.Actual() != Dibujo.Vigente) AplicarTema();
        });
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        CrearFuentes();
        Acomodar();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Acomodar();
        if (_centrada) return;
        _centrada = true; // la primera vez; después se queda donde el usuario la deje
        CenterToScreen();
    }

    private void LiberarFuentes()
    {
        foreach (var f in new[] { _fuente, _fuenteChica, _fuenteEstado, _fuenteDato, _fuenteFuerte, _fuenteBoton }) f?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mando.Cambio -= Refrescar;
            SystemEvents.UserPreferenceChanged -= AlCambiarPreferencias;
            SystemEvents.DisplaySettingsChanged -= AlCambiarPantallas;
            _reloj.Dispose();
            LiberarFuentes();
        }
        base.Dispose(disposing);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
