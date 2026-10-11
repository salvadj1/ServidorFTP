using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SimpleFtp.Ui
{
    /// <summary>
    /// Gráfica en tiempo real de dos series (subida y descarga) con los últimos 60 muestreos.
    /// Llama a <see cref="AddSample"/> una vez por segundo. Reutilizable en otros proyectos WinForms.
    /// </summary>
    public sealed class SpeedChart : Control
    {
        private const int Samples = 60;
        private readonly double[] _up = new double[Samples];
        private readonly double[] _down = new double[Samples];

        public SpeedChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 150;
        }

        /// <summary>Añade una muestra (bytes por segundo) y desplaza la gráfica una posición.</summary>
        /// <param name="upBytesPerSec">Velocidad de subida.</param>
        /// <param name="downBytesPerSec">Velocidad de descarga.</param>
        public void AddSample(double upBytesPerSec, double downBytesPerSec)
        {
            Array.Copy(_up, 1, _up, 0, Samples - 1);
            Array.Copy(_down, 1, _down, 0, Samples - 1);
            _up[Samples - 1] = Math.Max(0, upBytesPerSec);
            _down[Samples - 1] = Math.Max(0, downBytesPerSec);
            Invalidate();
        }

        /// <summary>Convierte bytes/s a texto (KB/s o MB/s).</summary>
        public static string FormatSpeed(double v) =>
            v >= 1048576 ? $"{v / 1048576:0.#} MB/s" : $"{v / 1024:0} KB/s";

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(FtpTheme.Back)) g.FillRectangle(b, ClientRectangle);

            double peak = 0;
            for (int i = 0; i < Samples; i++) peak = Math.Max(peak, Math.Max(_up[i], _down[i]));

            // ---- cabecera: título y valores actuales
            const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int x = 12;
            x = DrawPart(g, "Actividad en tiempo real", FtpTheme.Bold, FtpTheme.Text, x, tf) + 22;
            x = DrawPart(g, "Subida  " + FormatSpeed(_up[Samples - 1]), FtpTheme.Bold, FtpTheme.UploadColor, x, tf) + 22;
            x = DrawPart(g, "Descarga  " + FormatSpeed(_down[Samples - 1]), FtpTheme.Bold, FtpTheme.DownloadColor, x, tf) + 22;
            DrawPart(g, "Pico (60 s)  " + FormatSpeed(peak), FtpTheme.Base, FtpTheme.Muted, x, tf);

            // ---- área del gráfico
            const int left = 70, top = 30, right = 12, bottom = 20;
            int pw = Width - left - right, ph = Height - top - bottom;
            if (pw < 20 || ph < 20) return;
            double max = Math.Max(1048576, peak) * 1.15;

            using (var grid = new Pen(FtpTheme.Line))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = top + ph * i / 4;
                    g.DrawLine(grid, left, y, left + pw, y);
                    TextRenderer.DrawText(g, FormatSpeed(max * (1 - i / 4.0)), FtpTheme.Base,
                        new Rectangle(0, y - 8, left - 6, 16), FtpTheme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
            TextRenderer.DrawText(g, "hace 60 s", FtpTheme.Base, new Point(left, Height - 17), FtpTheme.Muted, tf);
            TextRenderer.DrawText(g, "ahora", FtpTheme.Base, new Rectangle(left, Height - 17, pw, 16), FtpTheme.Muted,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSeries(g, _down, FtpTheme.DownloadColor, left, top, pw, ph, max);
            DrawSeries(g, _up, FtpTheme.UploadColor, left, top, pw, ph, max);
        }

        /// <summary>Dibuja un texto y devuelve la posición X donde termina.</summary>
        private static int DrawPart(Graphics g, string text, Font font, Color color, int x, TextFormatFlags flags)
        {
            Size s = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, 20), flags);
            TextRenderer.DrawText(g, text, font, new Point(x, 7), color, flags);
            return x + s.Width;
        }

        /// <summary>Dibuja una serie como línea con relleno suave debajo.</summary>
        private static void DrawSeries(Graphics g, double[] data, Color color, int left, int top, int pw, int ph, double max)
        {
            var pts = new PointF[Samples];
            for (int i = 0; i < Samples; i++)
                pts[i] = new PointF(left + pw * (float)i / (Samples - 1), top + ph * (1f - (float)(data[i] / max)));

            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[Samples - 1], new PointF(left + pw, top + ph));
                path.AddLine(new PointF(left + pw, top + ph), new PointF(left, top + ph));
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(45, color));
                g.FillPath(fill, path);
            }
            using var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, pts);
        }
    }
}
