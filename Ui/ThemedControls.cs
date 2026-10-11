using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace SimpleFtp.Ui
{
    /// <summary>Panel con fondo en degradado horizontal (útil para cabeceras).</summary>
    public sealed class GradientPanel : Panel
    {
        /// <summary>Color del extremo izquierdo.</summary>
        public Color Color1 { get; set; } = Color.FromArgb(20, 48, 79);
        /// <summary>Color del extremo derecho.</summary>
        public Color Color2 { get; set; } = Color.FromArgb(42, 29, 82);

        public GradientPanel() { DoubleBuffered = true; ResizeRedraw = true; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0) return;
            using var brush = new LinearGradientBrush(ClientRectangle, Color1, Color2, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    /// <summary>Tarjeta de indicador: un valor grande de color con su título debajo.</summary>
    public sealed class KpiCard : Control
    {
        private string _value = "0";
        private string _title = "";
        private (Color Color, double Fraction)[] _segments = Array.Empty<(Color, double)>();

        /// <summary>Texto descriptivo bajo el valor.</summary>
        public string Title
        {
            get => _title;
            set { if (_title == value) return; _title = value; Invalidate(); }
        }

        /// <summary>Barra inferior repartida por colores (p. ej. el reparto por usuario); las fracciones suman como máximo 1.</summary>
        public void SetSegments(IEnumerable<(Color Color, double Fraction)> segments)
        {
            _segments = segments.ToArray();
            Invalidate();
        }
        /// <summary>Color del valor y de la barra lateral.</summary>
        public Color Accent { get; set; } = FtpTheme.DownloadColor;
        /// <summary>Valor mostrado (se redibuja al cambiarlo).</summary>
        public string Value
        {
            get => _value;
            set { if (_value == value) return; _value = value; Invalidate(); }
        }

        public KpiCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(180, 62);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(FtpTheme.Back)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(FtpTheme.Line)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 0, 4, Height);

            const TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Value, FtpTheme.Big, new Rectangle(12, 1, Math.Max(0, Width - 16), 30), Accent, f);
            TextRenderer.DrawText(g, Title, FtpTheme.Base, new Rectangle(12, 30, Math.Max(0, Width - 16), 18), FtpTheme.Muted, f);

            // Barra de reparto en la parte inferior
            var track = new Rectangle(12, Height - 11, Math.Max(0, Width - 22), 5);
            using (var b = new SolidBrush(FtpTheme.Surface2)) g.FillRectangle(b, track);
            int x = track.X;
            foreach (var (color, fraction) in _segments)
            {
                int w = (int)Math.Round(track.Width * Math.Max(0, Math.Min(1, fraction)));
                if (w <= 0) continue;
                w = Math.Min(w, track.Right - x);
                using (var b = new SolidBrush(color)) g.FillRectangle(b, x, track.Y, w, track.Height);
                x += w;
            }
        }
    }

    /// <summary>Pestaña dibujada a medida: texto, contador opcional y subrayado de color si está activa.</summary>
    public sealed class TabButton : Control
    {
        private string _caption = "";
        private int _count = -1;
        private bool _selected;

        /// <summary>Color del subrayado de la pestaña activa.</summary>
        public Color Accent { get; set; } = FtpTheme.DownloadColor;

        /// <summary>Título de la pestaña.</summary>
        public string Caption { get => _caption; set { _caption = value; Relayout(); } }

        /// <summary>Número mostrado junto al título (-1 para ocultarlo).</summary>
        public int Count { get => _count; set { if (_count == value) return; _count = value; Relayout(); } }

        /// <summary>Indica si es la pestaña activa.</summary>
        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Invalidate(); } }

        public TabButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 2, 0);
            Height = 34;
        }

        private string Label() => _count >= 0 ? $"{_caption}   {_count:N0}" : _caption;

        /// <summary>Recalcula el ancho según el texto y redibuja.</summary>
        private void Relayout()
        {
            Width = TextRenderer.MeasureText(Label(), FtpTheme.Bold).Width + 24;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(_selected ? FtpTheme.Back : FtpTheme.Surface)) g.FillRectangle(b, ClientRectangle);
            if (_selected)
                using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, Height - 3, Width, 3);

            TextRenderer.DrawText(g, Label(), _selected ? FtpTheme.Bold : FtpTheme.Base, ClientRectangle,
                _selected ? FtpTheme.Text : FtpTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
