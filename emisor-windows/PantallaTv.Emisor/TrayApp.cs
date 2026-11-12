using System.Drawing.Drawing2D;

namespace PantallaTv.Emisor;

/// <summary>
/// El programa de bandeja: el ícono junto al reloj, su menú y la ventana principal. El estado y las acciones
/// viven en <see cref="Mando"/>, que comparten el menú y la ventana: aquí solo se dibujan.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icono;
    private readonly ContextMenuStrip _menu = new();
    private readonly Mando _mando;
    private readonly Icon _iconoReposo = CrearIcono(Color.FromArgb(120, 120, 120));
    private readonly Icon _iconoActivo = CrearIcono(Color.FromArgb(34, 160, 80));
    private readonly Icon _iconoReconectando = CrearIcono(Color.FromArgb(214, 140, 20));
    private readonly IDisposable _pedidos;
    private readonly IDisposable _cierre;
    private VentanaPrincipal? _ventana;
    private readonly Font _negrita;

    /// <param name="mostrar">Abrir la ventana al empezar (no al iniciar con Windows).</param>
    /// <param name="manifiesto">Otra dirección para el <c>version.json</c> (<c>--manifiesto-url</c>, para pruebas).</param>
    public TrayApp(bool mostrar, Uri? manifiesto = null)
    {
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _mando = Mando.Crear(ui, out var problemaRed, manifiesto);
        _icono = new NotifyIcon
        {
            Icon = _iconoReposo,
            Text = "Pantalla TV",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _negrita = new Font(_menu.Font, FontStyle.Bold);
        _menu.Opening += (_, _) => ConstruirMenu();
        _icono.DoubleClick += (_, _) => MostrarVentana();
        // El globo de una versión nueva no tiene botones: hacer clic en él abre la ventana, que tiene la banda.
        _icono.BalloonTipClicked += (_, _) => MostrarVentana();
        _mando.Cambio += AlCambiar;
        _mando.Aviso += (texto, icono) => _icono.ShowBalloonTip(icono == ToolTipIcon.Error ? 6000 : 4000, "Pantalla TV", texto, icono);
        // Otra instancia (abrir el programa desde el menú Inicio con este ya corriendo) pide mostrar la ventana.
        _pedidos = Arranque.EscucharPedidos(() => ui.Post(_ => MostrarVentana(), null));
        // El instalador (al actualizar o reinstalar) pide que se cierre para poder reemplazar el ejecutable.
        _cierre = Arranque.EscucharCierre(() => ui.Post(_ => Salir(), null));

        if (mostrar) MostrarVentana();
        if (problemaRed is { } p)
            _icono.ShowBalloonTip(6000, "Pantalla TV", p + " Puedes escribir la IP a mano.", ToolTipIcon.Warning);
    }

    private void MostrarVentana()
    {
        if (_ventana is null || _ventana.IsDisposed)
        {
            _ventana = new VentanaPrincipal(_mando, Salir);
            _ventana.PrimeraVezEscondida += () => _icono.ShowBalloonTip(5000, "Pantalla TV",
                "Pantalla TV sigue abierto aquí, junto al reloj. Para cerrarlo del todo: «Salir».", ToolTipIcon.Info);
        }
        _ventana.Mostrar();
    }

    private void AlCambiar()
    {
        _icono.Text = _mando.Fase == Fase.Inactivo ? "Pantalla TV" : Recortar("Pantalla TV: " + _mando.Estado);
        _icono.Icon = _mando.Fase switch
        {
            Fase.Transmitiendo when _mando.Transmitiendo => _iconoActivo,
            Fase.Reconectando when _mando.Transmitiendo => _iconoReconectando,
            _ => _iconoReposo,
        };
    }

    /// <summary>El menú se arma de nuevo cada vez que se abre, con lo que diga el mando en ese momento.</summary>
    private void ConstruirMenu()
    {
        var m = _mando;
        var editable = m.PuedeCambiarAjustes;
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("Abrir Pantalla TV", null, (_, _) => MostrarVentana()) { Font = _negrita });
        _menu.Items.Add(new ToolStripMenuItem(m.Estado) { Enabled = false });
        if (m.NovedadOfrecida is { } novedad)
        {
            _menu.Items.Add(new ToolStripMenuItem($"Actualizar ahora a la versión {novedad.Texto}", null, (_, _) => m.ActualizarAhora()) { Font = _negrita });
            _menu.Items.Add($"Después (no volver a avisar de la {novedad.Texto} hasta el próximo inicio)", null, (_, _) => m.PosponerActualizacion());
        }
        else if (m.AvanceDescarga is { } avance)
        {
            _menu.Items.Add(new ToolStripMenuItem($"Descargando la versión nueva… {avance} %") { Enabled = false });
        }
        _menu.Items.Add(new ToolStripSeparator());

        if (m.Transmitiendo)
        {
            _menu.Items.Add("Detener transmisión", null, (_, _) => m.Detener());
        }
        else
        {
            var destinos = m.Destinos();
            if (!Mando.HayTvAnunciada(destinos))
                _menu.Items.Add(new ToolStripMenuItem("No se ve ninguna TV (abre «Pantalla» en ella)") { Enabled = false });
            foreach (var d in destinos)
            {
                var receptor = d.Receptor;
                _menu.Items.Add(d.TextoMenu, null, (_, _) => m.Iniciar(receptor));
            }
            _menu.Items.Add("Escribir IP…", null, (_, _) =>
            {
                if (DialogoIp.Pedir(null, m.Ajustes.UltimaIp ?? "") is { } ip) m.IniciarConIp(ip);
            });
        }

        _menu.Items.Add(new ToolStripSeparator());

        m.ActualizarPantallas();
        var pantallas = m.OpcionesPantalla();
        var menuMonitor = new ToolStripMenuItem("Qué pantalla se transmite") { Enabled = editable };
        Opciones(menuMonitor, pantallas.Opciones, v => m.Cambiar(a => a.MonitorNombre = v));
        if (!pantallas.HayVirtual)
        {
            // Para quien lo desmarcó al instalar o viene de una versión anterior.
            menuMonitor.DropDownItems.Add(new ToolStripMenuItem(
                m.TextoInstalarVirtual, null, (_, _) => m.InstalarMonitorVirtual())
            {
                Enabled = !m.InstalandoControlador,
            });
        }
        _menu.Items.Add(menuMonitor);

        var retardo = new ToolStripMenuItem("Retardo (el monitor virtual va siempre sin retardo)") { Enabled = editable };
        Opciones(retardo, m.OpcionesRetardo(), v => m.Cambiar(a => a.RetardoMs = v));
        _menu.Items.Add(retardo);

        var forma = new ToolStripMenuItem("Forma de capturar") { Enabled = editable };
        Opciones(forma, m.OpcionesForma(), v => m.Cambiar(a => a.Tuberia = v));
        _menu.Items.Add(forma);

        Alternable("Enviar también el sonido", m.Ajustes.ConAudio, v => m.Cambiar(a => a.ConAudio = v), editable);
        Alternable("Silenciar el PC mientras transmite", m.Ajustes.SilenciarPc, v => m.Cambiar(a => a.SilenciarPc = v), m.PuedeSilenciar);
        Alternable("Reconectar solo si se corta", m.Ajustes.Reconectar, v => m.Cambiar(a => a.Reconectar = v), editable);

        _menu.Items.Add(new ToolStripSeparator());
        Alternable("Guardar telemetría (CSV)", m.Ajustes.GuardarTelemetria, v => m.Cambiar(a => a.GuardarTelemetria = v), editable);
        _menu.Items.Add("Abrir carpeta de telemetría", null, (_, _) => Mando.AbrirTelemetria());
        _menu.Items.Add(new ToolStripMenuItem("Diagnóstico para enviar…", null, (_, _) => m.Diagnosticar())
        {
            Enabled = m.PuedeDiagnosticar,
        });

        // «Abrir al iniciar Windows» solo se ofrece al programa instalado (en la ventana aparece bloqueado, con el porqué).
        if (m.PuedeIniciarConWindows)
            Alternable("Abrir al iniciar Windows", m.IniciaConWindows, m.CambiarInicioConWindows);
        Alternable("Buscar actualizaciones (una vez al día, en GitHub)", m.Ajustes.BuscarActualizaciones, m.CambiarBuscarActualizaciones);
        _menu.Items.Add(new ToolStripMenuItem(m.BuscandoActualizaciones ? "Buscando actualizaciones…" : "Buscar actualizaciones ahora", null,
            (_, _) => m.BuscarActualizacionesAhora()) { Enabled = m.PuedeBuscarActualizaciones });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem($"Pantalla TV {Instalacion.Version}") { Enabled = false });
        _menu.Items.Add("Salir", null, (_, _) => Salir());
    }

    private static void Opciones<T>(ToolStripMenuItem padre, IReadOnlyList<Opcion<T>> opciones, Action<T> elegir)
    {
        foreach (var o in opciones)
        {
            var valor = o.Valor;
            padre.DropDownItems.Add(new ToolStripMenuItem(o.Texto, null, (_, _) => elegir(valor)) { Checked = o.Elegida });
        }
    }

    private void Alternable(string texto, bool valor, Action<bool> cambiar, bool habilitado = true) =>
        _menu.Items.Add(new ToolStripMenuItem(texto, null, (_, _) => cambiar(!valor)) { Checked = valor, Enabled = habilitado });

    private void Salir()
    {
        _mando.Terminar();
        _ventana?.Cerrar();
        _icono.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pedidos.Dispose();
            _cierre.Dispose();
            _ventana?.Dispose();
            _icono.Dispose();
            _menu.Dispose();
            _negrita.Dispose();
            _mando.Dispose();
            _iconoReposo.Dispose();
            _iconoActivo.Dispose();
            _iconoReconectando.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>NotifyIcon.Text admite como máximo 63 caracteres.</summary>
    private static string Recortar(string texto) => texto.Length <= 63 ? texto : texto[..60] + "…";

    /// <summary>Un televisor dibujado en el color del estado (gris en reposo, verde al transmitir, ámbar al reconectar).</summary>
    private static Icon CrearIcono(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var relleno = new SolidBrush(color);
            using var pantalla = new SolidBrush(Color.FromArgb(25, 25, 25));
            g.FillRectangle(relleno, 2, 5, 28, 19);
            g.FillRectangle(pantalla, 5, 8, 22, 13);
            g.FillRectangle(relleno, 10, 26, 12, 3);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temporal = Icon.FromHandle(handle);
            return (Icon)temporal.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
