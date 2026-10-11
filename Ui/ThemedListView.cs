using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SimpleFtp.Ftp;

namespace SimpleFtp.Ui
{
    /// <summary>
    /// Fila de historial: además del texto guarda el color del evento, el evento original
    /// y (opcional) una fracción 0..1 para dibujar una barra (p. ej. la velocidad).
    /// </summary>
    public sealed class LogItem : ListViewItem
    {
        /// <summary>Color de la "píldora" o de la barra.</summary>
        public Color Accent { get; set; } = FtpTheme.Text;
        /// <summary>Color del texto de la fila (null = color normal del tema).</summary>
        public Color? TextColor { get; set; }
        /// <summary>Evento de origen, para mostrar su detalle.</summary>
        public FtpLogEntry? Entry { get; set; }
        /// <summary>Valor 0..1 de la barra de la columna <see cref="ThemedListView.BarColumn"/>.</summary>
        public double Fraction { get; set; }

        public LogItem(string[] cells) : base(cells) { }
    }

    /// <summary>
    /// ListView en modo detalle con tema oscuro: cabeceras propias, filas alternas, selección,
    /// "píldoras" de color en una columna y barras de progreso en otra. Reutilizable en otros proyectos.
    /// </summary>
    public sealed class ThemedListView : ListView
    {
        private readonly ImageList _rowHeight = new() { ImageSize = new Size(1, 26) }; // fuerza filas más altas
        private bool _fitting;

        /// <summary>Índice de la columna que se dibuja como píldora de color (-1 = ninguna).</summary>
        public int BadgeColumn { get; set; } = -1;
        /// <summary>Índice de la columna que muestra una barra además del texto (-1 = ninguna).</summary>
        public int BarColumn { get; set; } = -1;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

        /// <summary>Crea la lista con las columnas indicadas (título, ancho).</summary>
        public ThemedListView(params (string Title, int Width)[] columns)
        {
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = false;
            Dock = DockStyle.Fill;
            BorderStyle = BorderStyle.None;
            BackColor = FtpTheme.Back;
            ForeColor = FtpTheme.Text;
            Font = FtpTheme.Base;
            OwnerDraw = true;
            DoubleBuffered = true;
            SmallImageList = _rowHeight;

            foreach (var (title, width) in columns) Columns.Add(title, width);

            DrawColumnHeader += OnDrawHeader;
            DrawItem += OnDrawItem;
            DrawSubItem += OnDrawSubItem;
            Resize += (_, _) => FitLastColumn();
            ColumnWidthChanged += (_, _) => FitLastColumn();
        }

        /// <summary>Añade una fila, descarta las más antiguas si se supera el máximo y la deja visible.</summary>
        /// <param name="item">Fila a añadir.</param>
        /// <param name="maxRows">Número máximo de filas conservadas.</param>
        public void AddRow(ListViewItem item, int maxRows)
        {
            Items.Add(item);
            if (Items.Count > maxRows) Items.RemoveAt(0);
            if (SelectedItems.Count == 0) item.EnsureVisible(); // si el usuario ha seleccionado algo no se mueve la vista
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch { /* versiones antiguas de Windows: se ignora */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _rowHeight.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Estira la última columna para ocupar el ancho restante.</summary>
        private void FitLastColumn()
        {
            if (_fitting || Columns.Count == 0) return;
            _fitting = true;
            try
            {
                int used = 0;
                for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
                int w = ClientSize.Width - used;
                if (w > 80) Columns[Columns.Count - 1].Width = w;
            }
            finally { _fitting = false; }
        }

        private void OnDrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(FtpTheme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(FtpTheme.Line))
                e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", FtpTheme.Bold, Rectangle.Inflate(e.Bounds, -6, 0),
                FtpTheme.Muted, AlignFlag(e.ColumnIndex) | TextFormatFlags.VerticalCenter |
                                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>Alineación del texto de una columna (la columna 0 siempre va a la izquierda).</summary>
        private TextFormatFlags AlignFlag(int col) =>
            col > 0 && col < Columns.Count && Columns[col].TextAlign == HorizontalAlignment.Right
                ? TextFormatFlags.Right : TextFormatFlags.Left;

        private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
        {
            if (View != View.Details) e.DrawDefault = true; // en Details todo se dibuja en DrawSubItem
        }

        private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            ListViewItem? item = e.Item;
            if (item == null) return;

            Graphics g = e.Graphics;
            Color bg = item.Selected ? FtpTheme.Selected : (e.ItemIndex % 2 == 1 ? FtpTheme.RowAlt : FtpTheme.Back);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);

            string text = e.SubItem?.Text ?? "";
            var logItem = item as LogItem;
            Color fore = logItem?.TextColor ?? FtpTheme.Text;
            Rectangle cell = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
            TextFormatFlags flags = AlignFlag(e.ColumnIndex) | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            if (logItem != null && e.ColumnIndex == BadgeColumn && text.Length > 0)
            {
                // Píldora de color con el texto en oscuro
                Size ts = TextRenderer.MeasureText(text, FtpTheme.SmallBold);
                var pill = new Rectangle(cell.X, e.Bounds.Y + 4, Math.Min(ts.Width + 8, Math.Max(10, cell.Width)), e.Bounds.Height - 8);
                FtpTheme.FillRounded(g, pill, 4, logItem.Accent);
                TextRenderer.DrawText(g, text, FtpTheme.SmallBold, pill, FtpTheme.Back,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            else if (logItem != null && e.ColumnIndex == BarColumn)
            {
                // Barra proporcional (velocidad) + texto
                const int trackW = 56;
                var track = new Rectangle(cell.X, e.Bounds.Y + e.Bounds.Height / 2 - 3, trackW, 6);
                if (logItem.Fraction > 0)
                {
                    FtpTheme.FillRounded(g, track, 3, FtpTheme.Surface2);
                    int fillW = Math.Max(6, (int)(track.Width * Math.Min(1.0, logItem.Fraction)));
                    FtpTheme.FillRounded(g, new Rectangle(track.X, track.Y, fillW, track.Height), 3, logItem.Accent);
                }
                var textRect = new Rectangle(cell.X + trackW + 6, cell.Y, Math.Max(0, cell.Width - trackW - 6), cell.Height);
                TextRenderer.DrawText(g, text, Font, textRect, fore, flags);
            }
            else
            {
                TextRenderer.DrawText(g, text, Font, cell, fore, flags);
            }
        }
    }
}
