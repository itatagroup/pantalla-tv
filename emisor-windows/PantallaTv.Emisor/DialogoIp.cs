namespace PantallaTv.Emisor;

/// <summary>Pide la IP de la TV (desde la ventana o desde el menú de la bandeja), con los colores de la ventana.</summary>
internal static class DialogoIp
{
    public static string? Pedir(IWin32Window? duena, string inicial)
    {
        var p = Tema.Actual();
        Dibujo.Vigente = p;
        using var f = new Form
        {
            Text = "Dirección IP de la TV",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = duena is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = duena is null,
            TopMost = duena is null,
            AutoScaleMode = AutoScaleMode.None,
            BackColor = p.Superficie,
        };
        int S(int v) => f.Escalar(v);
        using var fuente = new Font("Segoe UI", S(14), GraphicsUnit.Pixel);
        f.Font = fuente;
        f.ClientSize = new Size(S(380), S(148));
        var etiqueta = new Label { Text = "&IP de la TV (por ejemplo 10.0.0.20):", ForeColor = p.Texto, BackColor = p.Superficie, Bounds = new Rectangle(S(16), S(14), S(348), S(22)) };
        var caja = new TextBox
        {
            Text = inicial, ForeColor = p.Texto, BackColor = p.Superficie, BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(S(16), S(42), S(348), S(28)), AccessibleName = "IP de la TV",
        };
        var ok = new Boton { Text = "Transmitir", Estilo = EstiloBoton.Primario, DialogResult = DialogResult.OK, BackColor = p.Superficie, Bounds = new Rectangle(S(126), S(92), S(124), S(44)) };
        var cancelar = new Boton { Text = "Cancelar", Estilo = EstiloBoton.Secundario, DialogResult = DialogResult.Cancel, BackColor = p.Superficie, Bounds = new Rectangle(S(256), S(92), S(111), S(44)) };
        f.Controls.AddRange([etiqueta, caja, ok, cancelar]);
        f.AcceptButton = ok;
        f.CancelButton = cancelar;
        f.HandleCreated += (_, _) => Tema.BarraDeTitulo(f.Handle, p.Oscura);
        return f.ShowDialog(duena) == DialogResult.OK ? caja.Text : null;
    }
}
