using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SimpleFtp.Ftp;

namespace SimpleFtp
{
    /// <summary>Diálogo para crear o editar un usuario FTP (nombre, clave, carpeta raíz, solo lectura).</summary>
    public sealed class UserEditDialog : Form
    {
        private readonly TextBox _txtName = new() { Left = 100, Top = 15, Width = 250 };
        private readonly TextBox _txtPass = new() { Left = 100, Top = 50, Width = 250, UseSystemPasswordChar = true };
        private readonly TextBox _txtRoot = new() { Left = 100, Top = 85, Width = 290 };
        private readonly CheckBox _chkReadOnly = new() { Left = 100, Top = 120, Text = "Solo lectura", AutoSize = true };
        private readonly Button _btnBrowse = new() { Left = 395, Top = 84, Width = 30, Text = "..." };
        private readonly Button _btnOk = new() { Left = 260, Top = 150, Width = 80, Text = "Aceptar" };
        private readonly Button _btnCancel = new() { Left = 350, Top = 150, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        private readonly HashSet<string> _otherNames;

        /// <summary>Usuario resultante (válido cuando el diálogo se cierra con Aceptar).</summary>
        public FtpUser Result { get; private set; } = new();

        /// <summary>Crea el diálogo.</summary>
        /// <param name="existing">Usuario a editar, o null para crear uno nuevo.</param>
        /// <param name="otherNames">Nombres ya usados por otros usuarios (para evitar duplicados).</param>
        public UserEditDialog(FtpUser? existing, IEnumerable<string> otherNames)
        {
            _otherNames = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);

            Text = existing == null ? "Añadir usuario" : "Editar usuario";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 190);
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Controls.Add(new Label { Left = 10, Top = 18, AutoSize = true, Text = "Usuario:" });
            Controls.Add(new Label { Left = 10, Top = 53, AutoSize = true, Text = "Clave:" });
            Controls.Add(new Label { Left = 10, Top = 88, AutoSize = true, Text = "Carpeta raíz:" });
            Controls.AddRange(new Control[] { _txtName, _txtPass, _txtRoot, _btnBrowse, _chkReadOnly, _btnOk, _btnCancel });

            if (existing != null)
            {
                _txtName.Text = existing.Username;
                _txtPass.Text = existing.Password;
                _txtRoot.Text = existing.RootPath;
                _chkReadOnly.Checked = existing.ReadOnly;
            }

            _btnBrowse.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = _txtRoot.Text };
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtRoot.Text = dlg.SelectedPath;
            };
            _btnOk.Click += (_, _) => Accept();
        }

        /// <summary>Valida los datos y cierra el diálogo con éxito si son correctos.</summary>
        private void Accept()
        {
            string name = _txtName.Text.Trim();
            string root = _txtRoot.Text.Trim();

            if (name.Length == 0) { Warn("Indica un nombre de usuario."); return; }
            if (_otherNames.Contains(name)) { Warn("Ya existe un usuario con ese nombre."); return; }
            if (root.Length == 0 || !Path.IsPathRooted(root)) { Warn("Indica una carpeta raíz con ruta completa."); return; }

            Result = new FtpUser
            {
                Username = name,
                Password = _txtPass.Text,
                RootPath = Path.GetFullPath(root),
                ReadOnly = _chkReadOnly.Checked
            };
            DialogResult = DialogResult.OK;
        }

        /// <summary>Muestra un aviso de validación.</summary>
        private void Warn(string text) => MessageBox.Show(this, text, Text);
    }
}
