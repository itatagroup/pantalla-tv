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
    private readonly Label _notaTvs = new() { Text = "No se ve ninguna TV: abre «Pantalla» en ella o escribe su IP.", UseMnemonic = false };
    private readonly Label _etiquetaPantalla = new() { Text = "Qué &pantalla" };
    private readonly Panel _pantallas = new() { AccessibleName = "Qué pantalla", AccessibleRole = AccessibleRole.Grouping };
    private readonly Boton _instalarVirtual = new() { Text = "Instalar el &monitor virtual…", Estilo = EstiloBoton.Secundario };
    private readonly Label _notaVirtual = new() { Text = "Para usar la tele como un segundo monitor (pide permiso de administrador).", UseMnemonic = false };
    private readonly Tarjeta _vivo = new() { Hundida = true, AccessibleName = "Transmisión en curso", AccessibleRole = AccessibleRole.Grouping };
    private readonly Label[] _vivoEtiquetas = [.. new[] { "TV", "Pantalla", "Tiempo", "Cuadros por segundo", "Mb/s", "Forma de capturar" }.Select(t => new Label { Text = t, UseMnemonic = false })];
    private readonly Label[] _vivoValores = [.. Enumerable.Range(0, 6).Select(_ => new Label { UseMnemonic = false, Text = "—" })];
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
    private readonly Casilla _reconectar = new() { Text = "Re&conectar solo si se corta" };
    private readonly Casilla _telemetria = new() { Text = "&Guardar telemetría (CSV)" };
    private readonly Boton _abrirTelemetria = new() { Text = "Abrir carp&eta de telemetría", Estilo = EstiloBoton.Secundario };
    private readonly Boton _diagnostico = new() { Text = "Diag&nóstico para enviar…", Estilo = EstiloBoton.Secundario };
    private readonly Label _notaDiagnostico = new() { UseMnemonic = false };
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
        _principal.Controls.AddRange([_etiquetaTv, _tvs, _escribirIp, _notaTvs, _etiquetaPantalla, _pantallas, _instalarVirtual, _notaVirtual, _vivo, _boton]);
        _vivo.Controls.AddRange([.. _vivoEtiquetas, .. _vivoValores]);
        _ajustes.Controls.AddRange([_notaBloqueo, _sonido, _silenciar, _notaSilenciar, _inicio, _notaInicio]);
        _avanzado.Controls.AddRange([_notaBloqueoAvanzado, _etiquetaForma, _forma, _reconectar, _telemetria, _abrirTelemetria, _diagnostico, _notaDiagnostico,
            _buscarAhora, _notaActualizaciones]);

        // Orden de tabulación = orden en pantalla.
        OrdenTab(_banda, _principal, _ajustes, _expansor, _avanzado, _botonSalir);
        OrdenTab(_actualizar, _despues);
        OrdenTab(_etiquetaTv, _tvs, _escribirIp, _etiquetaPantalla, _pantallas, _instalarVirtual, _boton);
        OrdenTab(_sonido, _silenciar, _inicio);
        OrdenTab(_etiquetaForma, _forma, _reconectar, _telemetria, _abrirTelemetria, _diagnostico, _buscarAhora);
        foreach (var l in Notas().Append(_version).Append(_estado).Append(_textoBanda)) l.TabStop = false;
        foreach (var l in new[] { _etiquetaTv, _etiquetaPantalla, _etiquetaForma, _version }) l.TextAlign = ContentAlignment.MiddleLeft;
        _avanzado.Visible = false;
        AcceptButton = _boton;

        _boton.Click += (_, _) => AlBotonPrincipal();
        _escribirIp.Click += (_, _) => EscribirIp();
        _instalarVirtual.Click += (_, _) => _mando.InstalarMonitorVirtual();
        _tvs.SelectionChangeCommitted += (_, _) => { if (_tvs.Valor is Destino d) _elegido = d.Clave; };
        _forma.SelectionChangeCommitted += (_, _) => _mando.Cambiar(a => a.Tuberia = (string?)_forma.Valor);
        Alternar(_sonido, (a, v) => a.ConAudio = v);
        Alternar(_silenciar, (a, v) => a.SilenciarPc = v);
        Alternar(_reconectar, (a, v) => a.Reconectar = v);
        Alternar(_telemetria, (a, v) => a.GuardarTelemetria = v);
        _inicio.CheckedChanged += (_, _) => { if (!_refrescando) _mando.CambiarInicioConWindows(_inicio.Checked); };
        _expansor.Click += (_, _) => { _abierto = !_abierto; Refrescar(); if (_abierto) _contenido.ScrollControlIntoView(_avanzado); };
        _abrirTelemetria.Click += (_, _) => Mando.AbrirTelemetria();
        _diagnostico.Click += (_, _) => _mando.Diagnosticar();
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
        if (_mando.Transmitiendo)
        {
            _mando.Detener();
            return;
        }
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

            // Destinos.
            var destinos = m.Destinos();
            _tvs.Cargar([.. destinos.Select(d => new ElementoLista(d.TextoLista, d))],
                destinos.FirstOrDefault(d => d.Clave == _elegido) ?? Mando.Preferido(destinos, m.Ajustes.UltimaIp));
            if (_tvs.Valor is Destino actual) _elegido = actual.Clave;
            _tvs.Enabled = destinos.Count > 0;
            _notaTvs.Visible = !transmitiendo && !Mando.HayTvAnunciada(destinos);

            // Pantallas: se rehacen solo si cambió la lista.
            var pantallas = m.OpcionesPantalla();
            CargarPantallas(pantallas.Opciones);
            _instalarVirtual.Visible = _notaVirtual.Visible = !transmitiendo && !pantallas.HayVirtual;
            _instalarVirtual.Enabled = !m.InstalandoControlador;
            _instalarVirtual.Text = m.TextoInstalarVirtual.Replace(" monitor", " &monitor"); // tecla de acceso Alt+M

            // Panel en vivo en lugar de la elección de TV y pantalla.
            foreach (var c in new Control[] { _etiquetaTv, _tvs, _escribirIp, _etiquetaPantalla, _pantallas })
                c.Visible = !transmitiendo;
            _vivo.Visible = transmitiendo;
            if (m.EnVivo is { } vivo)
            {
                var e = vivo.Ultimas;
                _vivoValores[0].Text = vivo.Tv;
                _vivoValores[1].Text = vivo.Pantalla;
                _vivoValores[2].Text = vivo.Desde is { } desde ? Mando.TextoTiempo(DateTime.UtcNow - desde) : "—";
                _vivoValores[3].Text = Mando.TextoFps(e);
                _vivoValores[4].Text = Mando.TextoMbps(e);
                _vivoValores[5].Text = Mando.TextoForma(e);
                _vivoValores[3].AccessibleName = $"Cuadros por segundo: {_vivoValores[3].Text} de {vivo.FpsPedidos}";
            }

            _boton.Text = transmitiendo ? "&Detener" : "&Transmitir";
            _boton.Estilo = transmitiendo ? EstiloBoton.Peligro : EstiloBoton.Primario;
            _boton.Icono = transmitiendo ? IconoBoton.Detener : IconoBoton.Reproducir;

            // Ajustes.
            _notaBloqueo.Visible = _notaBloqueoAvanzado.Visible = !editable;
            _forma.Cargar([.. m.OpcionesForma().Select(o => new ElementoLista(o.Texto, o.Valor))], m.Ajustes.Tuberia);
            _sonido.Checked = m.Ajustes.ConAudio;
            _silenciar.Checked = m.Ajustes.SilenciarPc;
            _reconectar.Checked = m.Ajustes.Reconectar;
            _telemetria.Checked = m.Ajustes.GuardarTelemetria;
            foreach (var c in new Control[] { _sonido, _reconectar, _telemetria, _forma }) c.Enabled = editable;
            _silenciar.Enabled = m.PuedeSilenciar;
            _notaSilenciar.Visible = editable && !m.Ajustes.ConAudio;

            var puedeInicio = m.PuedeIniciarConWindows;
            _inicio.Enabled = puedeInicio;
            _inicio.Checked = puedeInicio && m.IniciaConWindows;
            _notaInicio.Visible = !puedeInicio;

            _buscarAhora.Enabled = m.PuedeBuscarActualizaciones;
            _buscarAhora.Text = m.BuscandoActualizaciones ? "Buscando…" : "B&uscar actualizaciones";
            _notaActualizaciones.Text = m.EstadoActualizaciones ??
                $"Solo cuando lo pulsas: mira en GitHub si hay una versión publicada, sin enviar nada. Versión instalada: {Instalacion.Version}.";

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
        var clave = string.Join("|", DeviceDpi, _abierto, _vivo.Visible, _estado.Text, _vivoValores[0].Text, _vivoValores[1].Text,
            _vivoValores[5].Text, _pantallas.Controls.Count, _instalarVirtual.Text, _notaDiagnostico.Text,
            _banda.Visible, _actualizar.Visible, _textoBanda.Text, _buscarAhora.Text, _notaActualizaciones.Text,
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
                    if (!_refrescando && opcion.Checked) _mando.Cambiar(a => a.MonitorNombre = valor);
                };
                _pantallas.Controls.Add(opcion);
            }
            _pantallas.ResumeLayout(false);
        }
        var editable = _mando.PuedeCambiarAjustes;
        for (var i = 0; i < opciones.Count; i++)
        {
            var opcion = (Opcion)_pantallas.Controls[i];
            opcion.Checked = opciones[i].Elegida;
            opcion.Enabled = editable;
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
        foreach (var l in _vivoEtiquetas) l.Font = _fuenteChica;
        _version.Font = _fuenteChica;
        _estado.Font = _fuenteEstado;
        foreach (var l in _vivoValores) l.Font = _fuente;
        foreach (var i in new[] { 2, 3, 4 }) _vivoValores[i].Font = _fuenteDato;
        _boton.Font = _fuenteBoton;
        _expansor.Font = _fuenteFuerte;
        foreach (var l in new[] { _tvs, _forma }) l.ItemHeight = S(24);
    }

    private IEnumerable<Label> Notas() =>
        [_notaTvs, _notaVirtual, _notaBloqueo, _notaSilenciar, _notaInicio, _notaDiagnostico, _notaBloqueoAvanzado, _notaActualizaciones];

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
        }
        else
        {
            var anchoIp = TextRenderer.MeasureText(_escribirIp.Text, _escribirIp.Font).Width + S(28);
            Etiqueta(_etiquetaTv, relleno, cy, etiquetas, altoCampo);
            Poner(_tvs, relleno + etiquetas, cy, campo - anchoIp - S(8), altoCampo);
            Poner(_escribirIp, relleno + interior - anchoIp - S(3), cy - S(3), anchoIp + S(6), altoCampo + S(6));
            cy += altoCampo;
            if (_notaTvs.Visible) cy = Nota(_notaTvs, relleno + etiquetas, cy + S(4), campo);
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
        cy += S(18);
        Poner(_boton, relleno - S(3), cy - S(3), interior + S(6), S(46) + S(6));
        cy += S(46) + relleno;
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
            cy += altoCampo + S(10);
            cy = Casilla(_reconectar, relleno, cy, interior);
            cy = Casilla(_telemetria, relleno, cy, interior);
            cy += S(8);
            var mitad = (interior - S(8)) / 2;
            Poner(_abrirTelemetria, relleno - S(3), cy - S(3), mitad + S(6), altoBoton + S(6));
            Poner(_diagnostico, relleno + mitad + S(8) - S(3), cy - S(3), interior - mitad - S(8) + S(6), altoBoton + S(6));
            cy += altoBoton;
            cy = Nota(_notaDiagnostico, relleno + mitad + S(8), cy + S(6), interior - mitad - S(8));
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

    private int AcomodarVivo(int cy, int relleno, int interior)
    {
        var r = S(14);
        var ancho = interior - 2 * r;
        var etiquetas = S(ColumnaEtiquetas) - r;
        var vy = r;
        // TV y pantalla: etiqueta a la izquierda, valor a la derecha.
        foreach (var i in new[] { 0, 1 })
        {
            var alto = Math.Max(S(22), AltoTexto(_vivoValores[i], ancho - etiquetas));
            Poner(_vivoEtiquetas[i], r, vy + S(2), etiquetas, S(20));
            Poner(_vivoValores[i], r + etiquetas, vy, ancho - etiquetas, alto);
            vy += alto + S(6);
        }
        vy += S(6);
        // Tiempo, fps y Mb/s: tres números grandes.
        var tercio = ancho / 3;
        for (var k = 0; k < 3; k++)
        {
            Poner(_vivoValores[2 + k], r + k * tercio, vy, tercio, S(30));
            Poner(_vivoEtiquetas[2 + k], r + k * tercio, vy + S(30), tercio, S(18));
        }
        vy += S(30) + S(18) + S(12);
        var altoForma = Math.Max(S(22), AltoTexto(_vivoValores[5], ancho - etiquetas));
        Poner(_vivoEtiquetas[5], r, vy + S(2), etiquetas, S(20));
        Poner(_vivoValores[5], r + etiquetas, vy, ancho - etiquetas, altoForma);
        vy += altoForma + r;
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
        foreach (var l in _vivoEtiquetas) l.ForeColor = p.Secundario;
        foreach (var l in _vivoValores) l.ForeColor = p.TextoFuerte;
        _version.ForeColor = p.Secundario;
        foreach (var l in new[] { _tvs, _forma }) l.BackColor = p.Superficie;
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
        foreach (var l in new[] { _tvs, _forma }) _ = SetWindowTheme(l.Handle, p.Oscura ? "DarkMode_CFD" : "CFD", null);
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
