using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SimpleFtp.Ftp;

namespace SimpleFtp.Ui
{
    /// <summary>
    /// Paleta de colores, fuentes y utilidades de estilo para una interfaz WinForms oscura.
    /// Es independiente de la aplicación: se puede copiar a otros proyectos WinForms.
    /// </summary>
    public static class FtpTheme
    {
        // ------------------------------------------------------------ colores base
        public static readonly Color Back = Color.FromArgb(16, 23, 42);
        public static readonly Color Surface = Color.FromArgb(23, 32, 58);
        public static readonly Color Surface2 = Color.FromArgb(31, 42, 74);
        public static readonly Color Line = Color.FromArgb(43, 56, 96);
        public static readonly Color Text = Color.FromArgb(230, 236, 251);
        public static readonly Color Muted = Color.FromArgb(139, 154, 192);
        public static readonly Color RowAlt = Color.FromArgb(19, 27, 51);
        public static readonly Color Selected = Color.FromArgb(34, 53, 102);

        // ------------------------------------------------------------ colores por tipo de evento
        /// <summary>Subidas (verde).</summary>
        public static readonly Color UploadColor = Color.FromArgb(52, 211, 153);
        /// <summary>Descargas (azul).</summary>
        public static readonly Color DownloadColor = Color.FromArgb(74, 168, 255);
        /// <summary>Operaciones sobre archivos (ámbar).</summary>
        public static readonly Color FileColor = Color.FromArgb(251, 191, 36);
        /// <summary>Errores (rojo suave).</summary>
        public static readonly Color ErrorColor = Color.FromArgb(255, 107, 122);
        /// <summary>Conexiones y logins (violeta).</summary>
        public static readonly Color ConnColor = Color.FromArgb(183, 148, 246);

        /// <summary>Colores asignados a los usuarios (el mismo nombre siempre da el mismo color).</summary>
        private static readonly Color[] UserPalette =
        {
            Color.FromArgb(183, 148, 246), Color.FromArgb(74, 168, 255), Color.FromArgb(251, 191, 36),
            Color.FromArgb(52, 211, 153), Color.FromArgb(255, 138, 101), Color.FromArgb(244, 114, 182),
            Color.FromArgb(45, 212, 191), Color.FromArgb(163, 230, 53)
        };

        /// <summary>Color estable de un usuario (gris para los eventos sin identificar).</summary>
        public static Color ForUser(string name)
        {
            if (name == FtpUserStats.Unknown) return Muted;
            int h = 17;
            foreach (char c in name.ToLowerInvariant()) h = unchecked(h * 31 + c);
            return UserPalette[(h & 0x7fffffff) % UserPalette.Length];
        }

        // ------------------------------------------------------------ fuentes
        public static readonly Font Base = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font SmallBold = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        public static readonly Font Big = new Font("Segoe UI", 17f, FontStyle.Bold);

        /// <summary>Devuelve el color que representa un evento del servidor (subida, descarga, error...).</summary>
        public static Color ColorOf(FtpLogEntry e) => e.Category switch
        {
            FtpLogCategory.Error => ErrorColor,
            FtpLogCategory.Connection => ConnColor,
            FtpLogCategory.File => FileColor,
            _ => e.Message == "Subida" ? UploadColor : DownloadColor
        };

        /// <summary>Rellena un rectángulo con las esquinas redondeadas (suavizado activado).</summary>
        /// <param name="g">Superficie donde dibujar.</param>
        /// <param name="r">Rectángulo a rellenar.</param>
        /// <param name="radius">Radio de las esquinas en píxeles.</param>
        /// <param name="color">Color de relleno.</param>
        public static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
        {
            if (r.Width < 2 || r.Height < 2) return;
            radius = System.Math.Max(1, System.Math.Min(radius, System.Math.Min(r.Width, r.Height) / 2));
            int d = radius * 2;
            using var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        /// <summary>Aplica estilo plano de color sólido a un botón.</summary>
        public static void StyleButton(Button b, Color back, Color fore)
        {
            b.UseVisualStyleBackColor = false;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = Bold;
            b.Cursor = Cursors.Hand;
            b.Height = 30;
        }

        /// <summary>Aplica estilo oscuro a un TextBox o ComboBox.</summary>
        public static void StyleInput(Control c)
        {
            c.BackColor = Surface2;
            c.ForeColor = Text;
            c.Font = Base;
            if (c is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
            if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
        }

        /// <summary>
        /// Convierte un CheckBox en un "chip" de filtro: contorno del color indicado y,
        /// cuando está marcado, relleno sólido. Queda marcado por defecto.
        /// </summary>
        public static void StyleChip(CheckBox cb, Color accent)
        {
            cb.Appearance = Appearance.Button;
            cb.FlatStyle = FlatStyle.Flat;
            cb.AutoSize = true;
            cb.Cursor = Cursors.Hand;
            cb.Font = Base;
            cb.BackColor = Surface;
            cb.FlatAppearance.BorderColor = accent;
            cb.FlatAppearance.CheckedBackColor = accent;
            cb.CheckedChanged += (_, _) => cb.ForeColor = cb.Checked ? Back : accent;
            cb.Checked = true;
            cb.ForeColor = Back;
        }
    }
}
