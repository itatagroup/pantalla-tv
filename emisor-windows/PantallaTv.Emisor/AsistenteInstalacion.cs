using System.Diagnostics;

namespace PantallaTv.Emisor;

/// <summary>
/// El asistente de instalación: bienvenida → opciones → resultado. También atiende la instalación
/// y la desinstalación sin ventanas (<c>--silencioso</c>), que es como se prueba.
/// <code>
/// PantallaTvSetup.exe                                   asistente
/// PantallaTvSetup.exe --instalar --actualizar           asistente en modo actualización (lo abre el programa al actualizarse)
/// PantallaTv.exe --instalar --silencioso [--dir C:\ruta] [--escritorio] [--inicio] [--sin-menu] [--con-ffmpeg] [--con-monitor-virtual] [--id Prueba]
/// PantallaTv.exe --desinstalar [--silencioso] [--quitar-monitor-virtual] [--id Prueba]
/// </code>
/// <c>--con-monitor-virtual</c> instala también el controlador del monitor virtual (pide administrador);
/// <c>--quitar-monitor-virtual</c> lo quita al desinstalar sin preguntar, solo si lo instaló Pantalla TV.
/// <c>--actualizar</c> abre el asistente directo en las opciones, con las de la instalación actual, y el botón
/// «Actualizar». Con el programa abierto, el asistente (y también <c>--silencioso</c>) le pide que se cierre antes de copiar.
/// </summary>
internal static class Instalador
{
    public const string ArgInstalar = "--instalar";
    public const string ArgActualizar = "--actualizar";

    private static readonly string TituloDesinstalar = $"Desinstalar {Instalacion.Nombre}";

    public static int Instalar(string[] args)
    {
        var id = Texto(args, "--id") ?? Instalacion.IdPorDefecto;
        if (args.Contains("--silencioso"))
        {
            var op = new OpcionesInstalacion(
                Texto(args, "--dir") ?? Instalacion.CarpetaInstalada(id) ?? Instalacion.CarpetaPorDefecto,
                MenuInicio: !args.Contains("--sin-menu"),
                Escritorio: args.Contains("--escritorio"),
                IniciarConWindows: args.Contains("--inicio"),
                Id: id,
                ConFfmpegAdjunto: args.Contains("--con-ffmpeg"));
            try
            {
                Instalacion.Instalar(op, Environment.ProcessPath!);
            }
            catch (InstalacionException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            if (args.Contains("--con-monitor-virtual") && !Controlador.Instalado())
            {
                var r = Controlador.InstalarConPermiso(id);
                if (r.Detalle is not null) Console.Error.WriteLine(r.Detalle);
                if (r.Resultado != Controlador.Resultado.Hecho) return 4;
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var asistente = new AsistenteInstalacion(id, args.Contains(ArgActualizar));
        Application.Run(asistente);
        return asistente.Instalado ? 0 : 2;
    }

    public static int Desinstalar(string[] args)
    {
        var id = Texto(args, "--id") ?? Instalacion.IdPorDefecto;
        var silencioso = args.Contains("--silencioso");
        if (!silencioso)
        {
            ApplicationConfiguration.Initialize();
            var respuesta = MessageBox.Show(
                $"¿Quitar {Instalacion.Nombre} de este equipo?\n\nSe borran el programa y sus accesos directos. Tus preferencias se conservan.",
                TituloDesinstalar, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (respuesta != DialogResult.Yes) return 2;
        }
        try
        {
            // El controlador va antes que los archivos: el proceso elevado que lo quita es este mismo
            // ejecutable, y la marca que dice que es nuestro vive en la clave que Desinstalar borra.
            Instalacion.ComprobarCerrado(id);
            var avisoControlador = QuitarControladorSiCorresponde(id, silencioso, args.Contains("--quitar-monitor-virtual"));
            Instalacion.Desinstalar(id);
            if (!silencioso)
                MessageBox.Show($"{Instalacion.Nombre} se quitó de este equipo." + (avisoControlador is null ? "" : "\n\n" + avisoControlador),
                    TituloDesinstalar, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (avisoControlador is not null) Console.Error.WriteLine(avisoControlador);
            return 0;
        }
        catch (InstalacionException e)
        {
            if (silencioso) Console.Error.WriteLine(e.Message);
            else MessageBox.Show(e.Message, TituloDesinstalar, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 3;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!silencioso)
                MessageBox.Show("No se pudo borrar todo: " + e.Message + "\n\nCierra Pantalla TV y vuelve a intentarlo.",
                    TituloDesinstalar, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        finally
        {
            Instalacion.TerminarBorrado();
        }
    }

    /// <summary>
    /// Si el controlador lo instaló Pantalla TV, pregunta si quitarlo (por defecto Sí) y lo quita. Si no lo
    /// instalamos nosotros no se pregunta ni se toca. Devuelve un aviso para el mensaje final, o null.
    /// </summary>
    private static string? QuitarControladorSiCorresponde(string id, bool silencioso, bool quitarSinPreguntar)
    {
        if (!DebePreguntarPorElControlador(Controlador.LoInstalamosNosotros(id), Controlador.Instalado())) return null;
        if (silencioso && !quitarSinPreguntar) return null;
        if (!silencioso && MessageBox.Show(
                "¿Quitar también el monitor virtual?\n\nLo instaló Pantalla TV. Quitarlo pide permiso de administrador. " +
                "Si otro programa lo usa, responde No.",
                TituloDesinstalar, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            return null;

        var r = Controlador.QuitarConPermiso(id);
        return r.Resultado switch
        {
            Controlador.Resultado.Hecho => r.Detalle,
            Controlador.Resultado.Rechazado => "El monitor virtual no se quitó (no se dio permiso de administrador). Se puede quitar desde el Administrador de dispositivos › Adaptadores de pantalla › Virtual Display Driver.",
            _ => "El monitor virtual no se pudo quitar: " + r.Detalle,
        };
    }

    /// <summary>Solo se pregunta si lo instalamos nosotros y sigue instalado.</summary>
    internal static bool DebePreguntarPorElControlador(bool loInstalamosNosotros, bool instalado) => loInstalamosNosotros && instalado;

    internal static string? Texto(string[] args, string nombre)
    {
        var i = Array.IndexOf(args, nombre);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

internal sealed class AsistenteInstalacion : Form
{
    private static readonly Color Oliva = Color.FromArgb(0x3F, 0x62, 0x12);
    private static readonly Color Fondo = Color.FromArgb(0xEC, 0xED, 0xE6);
    private static readonly Color Texto = Color.FromArgb(0x1F, 0x23, 0x1A);
    private static readonly Color TextoSuave = Color.FromArgb(0x4B, 0x52, 0x41);

    private readonly string _id;
    private readonly Panel[] _paginas;
    private readonly Label _titulo = new();
    private readonly Button _atras = new() { Text = "Atrás", Width = 96, Height = 32 };
    private readonly Button _siguiente = new() { Text = "Siguiente", Width = 96, Height = 32 };
    private readonly Button _cancelar = new() { Text = "Cancelar", Width = 96, Height = 32 };

    private readonly TextBox _carpeta = new();
    private readonly CheckBox _menuInicio = new() { Text = "Acceso directo en el menú Inicio", Checked = true, AutoSize = true };
    private readonly CheckBox _escritorio = new() { Text = "Acceso directo en el escritorio", AutoSize = true };
    private readonly CheckBox _inicio = new() { Text = "Abrir Pantalla TV al iniciar Windows", Checked = true, AutoSize = true };
    private readonly CheckBox _ffmpegAdjunto = new() { Text = "Instalar también el ffmpeg.exe que acompaña a este instalador", Checked = true, AutoSize = true, Visible = false };
    private readonly CheckBox _monitorVirtual = new() { Text = "Instalar el monitor virtual (para proyectar una ventana y seguir usando el PC)", Checked = true, AutoSize = true };
    private readonly Label _notaMonitorVirtual = new();
    private readonly Label _resultado = new();
    private readonly CheckBox _abrir = new() { Text = "Abrir Pantalla TV ahora", Checked = true, AutoSize = true };

    private int _pagina;
    private bool _ocupado; // cerrando el programa o instalando el controlador: no se puede cerrar a medias
    private string? _instaladoEn;
    /// <summary>Ya estaba instalado: se conservan sus opciones (accesos, inicio con Windows, sin agregar el monitor virtual).</summary>
    private readonly bool _yaInstalado;
    /// <summary>Lo abrió el programa para actualizarse: empieza en las opciones y el botón dice «Actualizar».</summary>
    private readonly bool _actualizar;

    public bool Instalado => _instaladoEn is not null;

    public AsistenteInstalacion(string id, bool actualizar = false)
    {
        _id = id;
        _yaInstalado = Instalacion.CarpetaInstalada(id) is not null;
        _actualizar = actualizar && _yaInstalado;
        Text = $"{(_actualizar ? "Actualizar" : "Instalar")} {Instalacion.Nombre} {Instalacion.Version}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 500);
        BackColor = Color.White;
        ForeColor = Texto;
        Font = new Font("Segoe UI", 9.75f);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        var cabecera = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Oliva };
        _titulo.SetBounds(24, 20, 552, 34);
        _titulo.ForeColor = Color.White;
        _titulo.Font = new Font("Segoe UI Semibold", 15f);
        cabecera.Controls.Add(_titulo);

        var pie = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Fondo };
        _cancelar.SetBounds(600 - 24 - 96, 14, 96, 32);
        _siguiente.SetBounds(_cancelar.Left - 8 - 96, 14, 96, 32);
        _atras.SetBounds(_siguiente.Left - 8 - 96, 14, 96, 32);
        pie.Controls.AddRange([_atras, _siguiente, _cancelar]);
        _atras.Click += (_, _) => Mostrar(_pagina - 1);
        _siguiente.Click += (_, _) => Avanzar();
        _cancelar.Click += (_, _) => Close();
        AcceptButton = _siguiente;
        CancelButton = _cancelar;

        _paginas = [PaginaBienvenida(), PaginaOpciones(), PaginaResultado()];
        foreach (var p in _paginas)
        {
            p.Dock = DockStyle.Fill;
            p.Padding = new Padding(24, 20, 24, 8);
            p.Visible = false;
            Controls.Add(p);
        }
        Controls.Add(pie);
        Controls.Add(cabecera);
        Mostrar(_actualizar ? 1 : 0);
    }

    private static Label Parrafo(string texto, int alto, Color? color = null) => new()
    {
        Text = texto, Dock = DockStyle.Top, Height = alto, ForeColor = color ?? Texto, AutoSize = false,
    };

    private static Panel Apilar(params Control[] deArribaAbajo)
    {
        var p = new Panel();
        // Con Dock = Top, el último control agregado queda arriba: se agregan al revés.
        for (var i = deArribaAbajo.Length - 1; i >= 0; i--) p.Controls.Add(deArribaAbajo[i]);
        return p;
    }

    private static Panel PaginaBienvenida() => Apilar(
        Parrafo("Pantalla TV muestra la pantalla de este PC en tu tele, con su sonido, por la red de tu casa.", 48),
        Parrafo("Qué necesitas:", 24),
        Parrafo("  •  La app «Pantalla» instalada y abierta en la tele o TV Box (Android 8 o superior).\n" +
                "  •  El PC y la tele en la misma red.", 48),
        Parrafo("Se instala solo para tu usuario, sin pedir permisos de administrador, y se puede quitar desde " +
                "«Aplicaciones instaladas» de Windows.", 48, TextoSuave));

    private Panel PaginaOpciones()
    {
        _carpeta.Text = Instalacion.CarpetaInstalada(_id) ?? Instalacion.CarpetaPorDefecto;
        var fila = new Panel { Dock = DockStyle.Top, Height = 34 };
        var examinar = new Button { Text = "Examinar…", Dock = DockStyle.Right, Width = 100 };
        _carpeta.Dock = DockStyle.Fill;
        fila.Controls.Add(_carpeta);
        fila.Controls.Add(examinar);
        examinar.Click += (_, _) =>
        {
            using var dialogo = new FolderBrowserDialog { Description = "Carpeta donde instalar Pantalla TV", UseDescriptionForTitle = true };
            if (dialogo.ShowDialog(this) == DialogResult.OK)
                _carpeta.Text = Path.Combine(dialogo.SelectedPath, "PantallaTv");
        };

        // Desde el 0.7.0 ffmpeg es opcional: la captura propia no lo usa. Si está, queda de respaldo para un PC donde la
        // propia no funcione (por ejemplo, una gráfica sin codificador H.264 por hardware).
        var adjunto = Instalacion.FfmpegAdjunto(Environment.ProcessPath!);
        _ffmpegAdjunto.Visible = adjunto is not null;
        var ffmpeg = adjunto is not null ? "ffmpeg (opcional, de respaldo): este instalador trae uno (" + adjunto + ")."
            : Ffmpeg.Buscar(null) is { } encontrado ? "ffmpeg (opcional, de respaldo): encontrado en " + encontrado + "."
            : "ffmpeg no hace falta. Si en este PC la captura propia no funcionara, se puede instalar como respaldo: " +
              "winget install Gyan.FFmpeg";
        var color = TextoSuave;

        // Al actualizar o reinstalar se conserva lo que hay: los accesos y el inicio con Windows como están, y el
        // monitor virtual sin marcar (si no está, es porque se desmarcó antes; se puede instalar desde el programa).
        if (_yaInstalado)
        {
            _menuInicio.Checked = Instalacion.TieneAccesoMenuInicio(_id);
            _escritorio.Checked = Instalacion.TieneAccesoEscritorio(_id);
            _inicio.Checked = Instalacion.IniciaConWindows(_id);
            _monitorVirtual.Checked = false;
        }

        // Si ya está (lo instaló el usuario o una versión anterior), no hay nada que ofrecer.
        var conMonitorVirtual = !Controlador.Instalado();
        _monitorVirtual.Visible = conMonitorVirtual;
        _notaMonitorVirtual.Visible = conMonitorVirtual;
        _notaMonitorVirtual.Text = "Un segundo monitor que no existe: se conecta solo mientras transmites. Pide permiso de administrador.";
        _notaMonitorVirtual.Dock = DockStyle.Top;
        _notaMonitorVirtual.Height = 24;
        _notaMonitorVirtual.Padding = new Padding(18, 0, 0, 0);
        _notaMonitorVirtual.ForeColor = TextoSuave;

        foreach (var c in new[] { _menuInicio, _escritorio, _inicio, _ffmpegAdjunto, _monitorVirtual })
        {
            c.Dock = DockStyle.Top;
            c.Padding = new Padding(0, 4, 0, 4);
        }
        var abierto = Instalacion.CarpetaInstalada(_id) is { } instalada && Instalacion.AbiertosDesde(instalada) > 0;
        return Apilar(
            Parrafo(abierto ? "Pantalla TV está abierto: se cierra solo al " + (_actualizar ? "actualizar" : "instalar") +
                              " (si estaba transmitiendo, se detiene) y se vuelve a abrir al terminar." : "", abierto ? 44 : 0, TextoSuave),
            Parrafo("Carpeta de instalación:", 24),
            fila,
            new Panel { Dock = DockStyle.Top, Height = 14 },
            _menuInicio, _escritorio, _inicio, _monitorVirtual, _notaMonitorVirtual,
            new Panel { Dock = DockStyle.Top, Height = 14 },
            Parrafo(ffmpeg, 60, color),
            _ffmpegAdjunto);
    }

    private Panel PaginaResultado()
    {
        _resultado.Dock = DockStyle.Top;
        _resultado.Height = 230;
        _abrir.Dock = DockStyle.Top;
        return Apilar(_resultado, _abrir);
    }

    private void Mostrar(int pagina)
    {
        _pagina = pagina;
        for (var i = 0; i < _paginas.Length; i++) _paginas[i].Visible = i == pagina;
        _paginas[pagina].BringToFront();
        _titulo.Text = pagina switch
        {
            0 => $"Instalar {Instalacion.Nombre}",
            1 => _actualizar ? $"Actualizar a la versión {Instalacion.Version}" : "Opciones de instalación",
            _ => _actualizar ? "Actualización terminada" : "Instalación terminada",
        };
        _atras.Visible = pagina == 1 && !_actualizar;
        _siguiente.Text = pagina switch { 1 => _actualizar ? "Actualizar" : "Instalar", 2 => "Finalizar", _ => "Siguiente" };
        _cancelar.Visible = pagina < 2;
    }

    private void Avanzar()
    {
        switch (_pagina)
        {
            case 0:
                Mostrar(1);
                break;
            case 1:
                _ = InstalarAhoraAsync();
                break;
            default:
                Close(); // abre el programa al cerrarse (ver OnFormClosed)
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_ocupado && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <summary>
    /// «Abrir Pantalla TV ahora» vale también si se cierra con la X en la última página: al actualizar, el programa se
    /// cerró para copiarse y tiene que volver.
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (_abrir.Checked && _instaladoEn is not null) Abrir(_instaladoEn);
    }

    private static void Abrir(string ruta)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // se puede abrir desde el menú Inicio
        }
    }

    /// <summary>
    /// Si el programa está abierto desde la carpeta de destino, le pide que se cierre y espera (fuera del hilo de la
    /// ventana: puede tardar unos segundos si está transmitiendo). True si había uno abierto.
    /// </summary>
    private async Task<bool> CerrarElAbiertoAsync(string carpeta)
    {
        string destino;
        try { destino = Path.GetFullPath(carpeta); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (Instalacion.AbiertosDesde(destino) == 0) return false;

        _titulo.Text = "Cerrando Pantalla TV…";
        _atras.Enabled = _siguiente.Enabled = _cancelar.Enabled = false;
        _ocupado = true;
        try { await Task.Run(() => Instalacion.CerrarAbiertos(destino, Instalacion.EsperaCierre)); }
        finally
        {
            _ocupado = false;
            _atras.Enabled = _siguiente.Enabled = _cancelar.Enabled = true;
            Mostrar(_pagina);
        }
        return true; // si no se cerró, Instalar da el mensaje
    }

    private async Task InstalarAhoraAsync()
    {
        var op = new OpcionesInstalacion(_carpeta.Text.Trim(), _menuInicio.Checked, _escritorio.Checked, _inicio.Checked, _id,
            ConFfmpegAdjunto: _ffmpegAdjunto.Visible && _ffmpegAdjunto.Checked);
        UseWaitCursor = true;
        var habiaAbierto = await CerrarElAbiertoAsync(op.Carpeta);
        try
        {
            _instaladoEn = Instalacion.Instalar(op, Environment.ProcessPath!);
        }
        catch (InstalacionException e)
        {
            UseWaitCursor = false;
            // Se cerró para copiar y no se pudo instalar: se vuelve a abrir el que había, para no dejar al usuario sin él.
            if (habiaAbierto && Instalacion.CarpetaInstalada(_id) is { } anterior && Instalacion.AbiertosDesde(anterior) == 0
                && File.Exists(Path.Combine(anterior, Instalacion.Ejecutable)))
                Abrir(Path.Combine(anterior, Instalacion.Ejecutable));
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? monitor = null;
        if (_monitorVirtual.Visible && _monitorVirtual.Checked)
        {
            // Pide UAC y espera a que Windows conecte el monitor nuevo para quitarlo del escritorio:
            // tarda varios segundos, fuera del hilo de la ventana.
            _titulo.Text = "Instalando el monitor virtual…";
            _atras.Enabled = _siguiente.Enabled = _cancelar.Enabled = false;
            _ocupado = true;
            Controlador.Respuesta r;
            try { r = await Task.Run(() => Controlador.InstalarConPermiso(_id)); }
            catch (Exception e)
            {
                // El programa ya quedó instalado: un fallo aquí solo afecta al monitor virtual.
                r = new Controlador.Respuesta(Controlador.Resultado.Fallo, e.Message);
            }
            finally
            {
                _ocupado = false;
                _atras.Enabled = _siguiente.Enabled = _cancelar.Enabled = true;
            }
            monitor = r.Resultado switch
            {
                Controlador.Resultado.Hecho => "Monitor virtual instalado (desconectado: se conecta solo al transmitirlo)." +
                                               (r.Detalle is null ? "" : " " + r.Detalle),
                Controlador.Resultado.Rechazado => "El monitor virtual no se instaló: no se dio permiso de administrador. Puedes " +
                                                   "instalarlo después desde el ícono: «Qué pantalla se transmite» › «Instalar el monitor virtual…».",
                _ => "El monitor virtual no se pudo instalar: " + r.Detalle,
            };
        }
        UseWaitCursor = false;

        _resultado.Text =
            $"Pantalla TV {Instalacion.Version} quedó instalado en:\n{Path.GetDirectoryName(_instaladoEn)}\n\n" +
            (monitor is null ? "" : monitor + "\n\n") +
            "Vive como un ícono junto al reloj: clic derecho → «Transmitir a…».\n\n" +
            "En la tele, abre la app «Pantalla» antes de transmitir. La primera vez Windows puede preguntar si " +
            "permites a Pantalla TV usar la red: acepta para redes privadas.";
        Mostrar(2);
    }
}
