using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PantallaTv.Emisor;

// Controles de WinForms con dibujo propio para la ventana principal. Heredan de los de Windows para quedarse con
// el teclado, el foco y la accesibilidad (lectores de pantalla); solo cambia cómo se pintan. Los colores salen de
// Tema.Vigente y las medidas se escalan con el DPI del monitor (DeviceDpi).

internal static class Dibujo
{
    /// <summary>La paleta con que se pinta todo; la cambia la ventana al seguir el tema de Windows.</summary>
    public static Paleta Vigente { get; set; } = Paleta.Clara;

    public static int Escalar(this Control c, int logicos) => (int)Math.Round(logicos * c.DeviceDpi / 96.0);

    public static GraphicsPath Redondeado(RectangleF r, float radio)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radio * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>El «&amp;» de las teclas de acceso: se subraya la letra solo si el usuario usa el teclado (Alt).</summary>
    public static TextFormatFlags Prefijo(ButtonBase b) =>
        !b.UseMnemonic ? TextFormatFlags.NoPrefix : b is IMuestraTeclas { MuestraTeclas: true } ? 0 : TextFormatFlags.HidePrefix;
}

/// <summary>Expone <c>ShowKeyboardCues</c> (protegido) para decidir si se subraya la tecla de acceso.</summary>
internal interface IMuestraTeclas
{
    bool MuestraTeclas { get; }
}

internal enum EstiloBoton { Primario, Secundario, Peligro, Expansor }

internal enum IconoBoton { Ninguno, Reproducir, Detener }

/// <summary>Botón con radio chico: principal (oliva), secundario (con borde), peligro (detener) o expansor (texto con flecha).</summary>
internal sealed class Boton : Button, IMuestraTeclas
{
    private bool _encima, _apretado;

    public Boton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EstiloBoton Estilo { get; set { if (field == value) return; field = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IconoBoton Icono { get; set { if (field == value) return; field = value; Invalidate(); } }
    /// <summary>Solo para <see cref="EstiloBoton.Expansor"/>: la flecha apunta hacia abajo.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Abierto { get; set { if (field == value) return; field = value; Invalidate(); } }

    public bool MuestraTeclas => ShowKeyboardCues;

    protected override void OnMouseEnter(EventArgs e) { _encima = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _encima = false; _apretado = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _apretado = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _apretado = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Dibujo.Vigente;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Superficie);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var anillo = this.Escalar(2);
        var margen = anillo + this.Escalar(1); // el anillo de foco va por fuera del botón, sin moverlo
        var caja = new RectangleF(margen, margen, Width - 2 * margen, Height - 2 * margen);
        var radio = this.Escalar(6);
        var activo = _encima || _apretado;

        Color texto;
        if (Estilo == EstiloBoton.Expansor)
        {
            texto = Enabled ? p.Texto : p.Secundario;
            if (activo && Enabled)
            {
                using var hover = new SolidBrush(p.SuperficieApagada);
                using var r = Dibujo.Redondeado(caja, radio);
                g.FillPath(hover, r);
            }
        }
        else
        {
            Color fondo, borde;
            (fondo, borde, texto) = (Estilo, Enabled) switch
            {
                (_, false) => (p.SuperficieApagada, p.SuperficieApagada, p.Secundario),
                (EstiloBoton.Primario, _) => (activo ? p.PrimarioFuerte : p.Primario, activo ? p.PrimarioFuerte : p.Primario, p.SobrePrimario),
                (EstiloBoton.Peligro, _) => (p.Error, p.Error, p.SobrePrimario),
                _ => (activo ? p.SuperficieApagada : p.Superficie, p.BordeControl, p.Texto),
            };
            using var relleno = new SolidBrush(fondo);
            using var camino = Dibujo.Redondeado(caja, radio);
            g.FillPath(relleno, camino);
            using var lapiz = new Pen(borde, 1);
            using var bordeCamino = Dibujo.Redondeado(new RectangleF(caja.X + 0.5f, caja.Y + 0.5f, caja.Width - 1, caja.Height - 1), radio);
            g.DrawPath(lapiz, bordeCamino);
        }

        // Texto (con el ícono antes, si tiene).
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | Dibujo.Prefijo(this);
        var lado = this.Escalar(Estilo == EstiloBoton.Primario || Estilo == EstiloBoton.Peligro ? 14 : 10);
        var anchoTexto = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags & ~TextFormatFlags.EndEllipsis).Width; // con EndEllipsis y sin ancho, mide casi nada
        var ancho = anchoTexto + (Icono != IconoBoton.Ninguno || Estilo == EstiloBoton.Expansor ? lado + this.Escalar(8) : 0);
        var x = Estilo == EstiloBoton.Expansor ? caja.X + this.Escalar(8) : caja.X + (caja.Width - ancho) / 2f;
        var centroY = caja.Y + caja.Height / 2f;
        using (var pincel = new SolidBrush(texto))
        using (var trazo = new Pen(texto, Math.Max(1.5f, this.Escalar(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            switch (Icono)
            {
                case IconoBoton.Reproducir:
                    g.FillPolygon(pincel, [new PointF(x + lado * 0.15f, centroY - lado / 2f), new PointF(x + lado * 0.95f, centroY), new PointF(x + lado * 0.15f, centroY + lado / 2f)]);
                    break;
                case IconoBoton.Detener:
                    using (var cuadro = Dibujo.Redondeado(new RectangleF(x + lado * 0.1f, centroY - lado * 0.4f, lado * 0.8f, lado * 0.8f), this.Escalar(2)))
                        g.FillPath(pincel, cuadro);
                    break;
            }
            if (Estilo == EstiloBoton.Expansor)
            {
                var m = lado / 2f;
                var cx = x + m;
                PointF[] flecha = Abierto
                    ? [new(cx - m * 0.6f, centroY - m * 0.3f), new(cx, centroY + m * 0.35f), new(cx + m * 0.6f, centroY - m * 0.3f)]
                    : [new(cx - m * 0.3f, centroY - m * 0.6f), new(cx + m * 0.35f, centroY), new(cx - m * 0.3f, centroY + m * 0.6f)];
                g.DrawLines(trazo, flecha);
            }
        }
        var xTexto = (int)x + (ancho - anchoTexto);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(xTexto, (int)caja.Y, (int)caja.Right - xTexto, (int)caja.Height), texto, flags);

        if (Focused && ShowFocusCues)
        {
            using var foco = new Pen(p.Texto, anillo);
            using var aro = Dibujo.Redondeado(new RectangleF(anillo / 2f, anillo / 2f, Width - anillo, Height - anillo), radio + margen / 2f);
            g.DrawPath(foco, aro);
        }
    }
}

/// <summary>Casilla de verificación oliva. El texto va en una sola línea; las explicaciones van en una nota aparte.</summary>
internal sealed class Casilla : CheckBox, IMuestraTeclas
{
    public Casilla()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
    }

    public bool MuestraTeclas => ShowKeyboardCues;

    protected override void OnPaint(PaintEventArgs e) =>
        Marca.Pintar(this, e.Graphics, Checked, redonda: false);
}

/// <summary>Botón de opción oliva (una de varias, con las flechas del teclado para moverse entre ellas).</summary>
internal sealed class Opcion : RadioButton, IMuestraTeclas
{
    public Opcion()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        UseMnemonic = false; // son nombres de monitores
    }

    public bool MuestraTeclas => ShowKeyboardCues;

    protected override void OnPaint(PaintEventArgs e) =>
        Marca.Pintar(this, e.Graphics, Checked, redonda: true);
}

/// <summary>Lo común de <see cref="Casilla"/> y <see cref="Opcion"/>: la marca a la izquierda y el texto.</summary>
internal static class Marca
{
    public static void Pintar(ButtonBase c, Graphics g, bool marcada, bool redonda)
    {
        var p = Dibujo.Vigente;
        g.Clear(c.Parent?.BackColor ?? p.Superficie);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var lado = c.Escalar(18);
        var y = (c.Height - lado) / 2f;
        var x = c.Escalar(3);
        var caja = new RectangleF(x, y, lado, lado);
        var radio = redonda ? lado / 2f : c.Escalar(4);

        Color relleno, borde, marca;
        if (!c.Enabled) (relleno, borde, marca) = (p.SuperficieApagada, p.LineaFuerte, p.Secundario);
        else if (marcada && !redonda) (relleno, borde, marca) = (p.Primario, p.Primario, p.SobrePrimario);
        else (relleno, borde, marca) = (p.Superficie, marcada ? p.Primario : p.BordeControl, p.Primario);

        using (var r = Dibujo.Redondeado(caja, radio))
        using (var pincel = new SolidBrush(relleno))
            g.FillPath(pincel, r);
        var grosor = Math.Max(1.5f, c.Escalar(2) * 0.75f);
        using (var r = Dibujo.Redondeado(new RectangleF(caja.X + grosor / 2, caja.Y + grosor / 2, caja.Width - grosor, caja.Height - grosor), radio))
        using (var lapiz = new Pen(borde, grosor))
            g.DrawPath(lapiz, r);

        if (marcada && redonda)
        {
            var d = lado * 0.5f;
            using var pincel = new SolidBrush(c.Enabled ? p.Primario : p.Secundario);
            g.FillEllipse(pincel, caja.X + (lado - d) / 2, caja.Y + (lado - d) / 2, d, d);
        }
        else if (marcada)
        {
            using var lapiz = new Pen(marca, Math.Max(2f, c.Escalar(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(lapiz, [
                new PointF(caja.X + lado * 0.24f, caja.Y + lado * 0.52f),
                new PointF(caja.X + lado * 0.43f, caja.Y + lado * 0.70f),
                new PointF(caja.X + lado * 0.77f, caja.Y + lado * 0.32f)]);
        }

        if (c.Focused && ((IMuestraTeclas)c).MuestraTeclas)
        {
            var anillo = c.Escalar(2);
            var fuera = RectangleF.Inflate(caja, anillo + 1, anillo + 1);
            using var r = Dibujo.Redondeado(fuera, redonda ? fuera.Width / 2 : radio + anillo);
            using var lapiz = new Pen(p.Texto, anillo);
            g.DrawPath(lapiz, r);
        }

        var xTexto = (int)(caja.Right + c.Escalar(10));
        TextRenderer.DrawText(g, c.Text, c.Font, new Rectangle(xTexto, 0, c.Width - xTexto, c.Height),
            c.Enabled ? p.Texto : p.Secundario,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | Dibujo.Prefijo(c));
    }
}

/// <summary>Un elemento de <see cref="Lista"/>: lo que se muestra y lo que vale.</summary>
internal sealed record ElementoLista(string Texto, object? Valor)
{
    public override string ToString() => Texto;
}

/// <summary>Lista desplegable oliva (solo para elegir, no se escribe en ella).</summary>
internal sealed class Lista : ComboBox
{
    public Lista()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
    }

    /// <summary>Carga los elementos sin perder la selección si no cambiaron (se refresca cada segundo).</summary>
    public void Cargar(IReadOnlyList<ElementoLista> elementos, object? elegido)
    {
        var iguales = Items.Count == elementos.Count && elementos.Select((el, i) => Equals(Items[i], el)).All(x => x);
        if (!iguales)
        {
            BeginUpdate();
            Items.Clear();
            foreach (var el in elementos) Items.Add(el);
            EndUpdate();
        }
        var indice = elementos.ToList().FindIndex(el => Equals(el.Valor, elegido));
        if (indice >= 0 && SelectedIndex != indice) SelectedIndex = indice;
        else if (indice < 0 && SelectedIndex < 0 && Items.Count > 0) SelectedIndex = 0;
    }

    public object? Valor => (SelectedItem as ElementoLista)?.Valor;

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var p = Dibujo.Vigente;
        var enCaja = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var elegido = (e.State & DrawItemState.Selected) != 0 && !enCaja;
        var (fondo, texto) = !Enabled ? (p.SuperficieApagada, p.Secundario)
            : elegido ? (p.Primario, p.SobrePrimario)
            : (p.Superficie, p.Texto);
        using (var pincel = new SolidBrush(fondo))
            e.Graphics.FillRectangle(pincel, e.Bounds);
        if (e.Index >= 0)
        {
            var r = Rectangle.FromLTRB(e.Bounds.Left + this.Escalar(6), e.Bounds.Top, e.Bounds.Right - this.Escalar(4), e.Bounds.Bottom);
            TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString(), Font, r, texto,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if (enCaja && Focused)
        {
            // Con el foco, un subrayado grueso además del borde (que dibuja WndProc).
            using var foco = new SolidBrush(p.Primario);
            e.Graphics.FillRectangle(foco, e.Bounds.Left, e.Bounds.Bottom - this.Escalar(2), e.Bounds.Width, this.Escalar(2));
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    /// <summary>El borde y la flecha los pinta Windows en gris claro: se pintan encima con los de la paleta.</summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F /* WM_PAINT */) return;
        var p = Dibujo.Vigente;
        using var g = CreateGraphics();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ancho = SystemInformation.VerticalScrollBarWidth * DeviceDpi / 96;
        var flecha = new Rectangle(Width - ancho - 3, 1, ancho + 2, Height - 2); // tapa también la raya que Windows pone junto a la flecha
        using (var fondo = new SolidBrush(Enabled ? p.Superficie : p.SuperficieApagada))
            g.FillRectangle(fondo, flecha);
        var m2 = this.Escalar(4);
        var cx = flecha.Left + flecha.Width / 2f;
        var cy = Height / 2f;
        using (var lapiz = new Pen(Enabled ? p.Texto : p.Secundario, Math.Max(1.5f, this.Escalar(2) * 0.75f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(lapiz, [new PointF(cx - m2, cy - m2 / 2f), new PointF(cx, cy + m2 / 2f), new PointF(cx + m2, cy - m2 / 2f)]);
        g.SmoothingMode = SmoothingMode.None;
        using var borde = new Pen(Focused ? p.Primario : Enabled ? p.BordeControl : p.LineaFuerte, 1);
        g.DrawRectangle(borde, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Un recuadro con bordes redondeados que agrupa una sección de la ventana.</summary>
internal sealed class Tarjeta : Panel
{
    public Tarjeta()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Sin borde ni redondeo: un panel liso del color de la superficie hundida (el panel en vivo).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Hundida { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Dibujo.Vigente;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Fondo);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var radio = this.Escalar(8);
        using var camino = Dibujo.Redondeado(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radio);
        using var relleno = new SolidBrush(BackColor);
        g.FillPath(relleno, camino);
        if (Hundida) return;
        using var borde = new Pen(p.Linea, 1);
        g.DrawPath(borde, camino);
    }
}

/// <summary>El punto de color del estado (siempre junto al texto del estado: nunca dice nada solo).</summary>
internal sealed class Punto : Control
{
    public Punto()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Dibujo.Vigente.Fondo);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pincel = new SolidBrush(ForeColor);
        e.Graphics.FillEllipse(pincel, 0.5f, 0.5f, Width - 1, Height - 1);
    }
}
