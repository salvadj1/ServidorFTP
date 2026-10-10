using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SimpleFtp.Ftp;

namespace SimpleFtp
{
    /// <summary>
    /// Ventana principal: configuración del servidor, lista de conexiones activas
    /// e historiales (archivos, conexiones, transferencias y errores).
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int MaxRows = 5000; // máximo de filas por historial

        private FtpServerCore? _server;

        private readonly TextBox _txtPort = new() { Text = "2121", Width = 55 };
        private readonly Button _btnAddUser = new() { Text = "Añadir", Width = 80 };
        private readonly Button _btnEditUser = new() { Text = "Editar", Width = 80 };
        private readonly Button _btnDelUser = new() { Text = "Quitar", Width = 80 };
        private readonly Button _btnStart = new() { Text = "Iniciar", Width = 80 };
        private readonly Label _lblStatus = new() { Text = "Detenido", AutoSize = true, ForeColor = Color.Firebrick };

        private readonly ListView _lvSessions;
        private readonly ListView _lvFiles;
        private readonly ListView _lvConn;
        private readonly ListView _lvTransfers;
        private readonly ListView _lvErrors;
        private readonly ListView _lvUsers;
        private static readonly string UsersFile = Path.Combine(AppContext.BaseDirectory, "users.json");
        private List<FtpUser> _users = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

        /// <summary>Construye la interfaz completa por código (sin diseñador).</summary>
        public MainForm()
        {
            Text = "Servidor FTP";
            Width = 1000;
            Height = 650;
            StartPosition = FormStartPosition.CenterScreen;

            _users = LoadUsers();
            _lvUsers = CreateListView(("Usuario", 150), ("Carpeta raíz", 500), ("Solo lectura", 90));

            _lvSessions = CreateListView(("ID", 40), ("IP remota", 140), ("Usuario", 100), ("Conectado", 70),
                                         ("Directorio", 160), ("Último comando", 110), ("Recibido", 90), ("Enviado", 90));
            _lvFiles = CreateListView(("Hora", 70), ("Sesión", 55), ("Usuario", 100), ("Operación", 110), ("Archivo", 500));
            _lvConn = CreateListView(("Hora", 70), ("Sesión", 55), ("IP remota", 140), ("Usuario", 100), ("Evento", 400));
            _lvTransfers = CreateListView(("Hora", 70), ("Sesión", 55), ("IP remota", 130), ("Usuario", 90), ("Tipo", 80),
                                          ("Archivo", 300), ("Tamaño", 80), ("Duración", 70), ("Velocidad", 90));
            _lvErrors = CreateListView(("Hora", 70), ("Sesión", 55), ("IP remota", 140), ("Usuario", 100), ("Error", 500));

            BuildLayout();
            WireEvents();
            RefreshUsersList();
        }

        /// <summary>Crea un ListView en modo detalle con las columnas indicadas (título, ancho).</summary>
        private static ListView CreateListView(params (string Title, int Width)[] columns)
        {
            var lv = new ListView
            {
                View = View.Details, FullRowSelect = true, GridLines = true,
                HideSelection = false, Dock = DockStyle.Fill
            };
            foreach (var (title, width) in columns) lv.Columns.Add(title, width);
            return lv;
        }

        /// <summary>Coloca los controles en la ventana.</summary>
        private void BuildLayout()
        {
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            top.Controls.AddRange(new Control[]
            {
                new Label { Text = "Puerto:", AutoSize = true, Margin = new Padding(3, 7, 0, 0) }, _txtPort,
                _btnStart, _lblStatus
            });
            _btnStart.Margin = new Padding(10, 3, 0, 0);
            _lblStatus.Margin = new Padding(10, 7, 0, 0);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            var grpSessions = new GroupBox { Text = "Conexiones activas", Dock = DockStyle.Fill };
            grpSessions.Controls.Add(_lvSessions);
            split.Panel1.Controls.Add(grpSessions);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            AddTab(tabs, "Archivos", _lvFiles);
            AddTab(tabs, "Conexiones", _lvConn);
            AddTab(tabs, "Transferencias", _lvTransfers);
            AddTab(tabs, "Errores", _lvErrors);

            // Pestaña de usuarios: botones arriba y lista a pantalla completa
            var usersPage = new TabPage("Usuarios");
            var userButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
            userButtons.Controls.AddRange(new Control[] { _btnAddUser, _btnEditUser, _btnDelUser });
            usersPage.Controls.Add(_lvUsers);
            usersPage.Controls.Add(userButtons);
            tabs.TabPages.Add(usersPage);
            split.Panel2.Controls.Add(tabs);

            // El control Fill se añade antes que el Top para que el docking sea correcto
            Controls.Add(split);
            Controls.Add(top);
            Load += (_, _) => split.SplitterDistance = Math.Max(60, split.Height / 3);
        }

        /// <summary>Añade una pestaña con un ListView a pantalla completa.</summary>
        private static void AddTab(TabControl tabs, string title, ListView lv)
        {
            var page = new TabPage(title);
            page.Controls.Add(lv);
            tabs.TabPages.Add(page);
        }

        /// <summary>Conecta los eventos de los controles.</summary>
        private void WireEvents()
        {
            _btnAddUser.Click += (_, _) => AddUser();
            _btnEditUser.Click += (_, _) => EditUser();
            _btnDelUser.Click += (_, _) => DeleteUser();
            _lvUsers.DoubleClick += (_, _) => { if (_btnEditUser.Enabled) EditUser(); };
            _btnStart.Click += (_, _) => ToggleServer();
            _timer.Tick += (_, _) => RefreshSessions();
            FormClosing += (_, _) => { _timer.Stop(); _server?.Dispose(); };
        }

        // ------------------------------------------------------------------ servidor

        /// <summary>Inicia o detiene el servidor según su estado actual.</summary>
        private void ToggleServer()
        {
            if (_server is { IsRunning: true })
            {
                _server.Stop();
                SetRunningState(false);
                return;
            }

            if (!int.TryParse(_txtPort.Text, out int port) || port is < 1 or > 65535)
            {
                MessageBox.Show(this, "Puerto no válido (1-65535).", "Servidor FTP");
                return;
            }

            if (_users.Count == 0)
            {
                MessageBox.Show(this, "Añade al menos un usuario en la pestaña Usuarios.", "Servidor FTP");
                return;
            }

            var options = new FtpServerOptions { Port = port, Users = _users };

            try
            {
                if (_server != null) _server.LogEntryAdded -= OnLogEntry;
                _server = new FtpServerCore(options);
                _server.LogEntryAdded += OnLogEntry;
                _server.Start();
                SetRunningState(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo iniciar: {ex.Message}", "Servidor FTP");
                SetRunningState(false);
            }
        }

        /// <summary>Actualiza botón, estado y habilita/deshabilita la configuración.</summary>
        private void SetRunningState(bool running)
        {
            _btnStart.Text = running ? "Detener" : "Iniciar";
            _lblStatus.Text = running ? $"Escuchando en el puerto {_txtPort.Text}" : "Detenido";
            _lblStatus.ForeColor = running ? Color.ForestGreen : Color.Firebrick;
            foreach (Control c in new Control[] { _txtPort, _btnAddUser, _btnEditUser, _btnDelUser })
                c.Enabled = !running;
            _timer.Enabled = running;
            RefreshSessions();
        }

        // ------------------------------------------------------------------ usuarios

        /// <summary>
        /// Carga los usuarios de users.json. Si no hay ninguno crea "admin/admin" con carpeta
        /// Documentos\FtpRoot; si el archivo está corrupto lo copia a .bak y empieza de nuevo.
        /// </summary>
        private List<FtpUser> LoadUsers()
        {
            List<FtpUser> users;
            try { users = FtpUserStore.Load(UsersFile); }
            catch (Exception ex)
            {
                try { File.Copy(UsersFile, UsersFile + ".bak", true); } catch { }
                MessageBox.Show($"users.json no se pudo leer ({ex.Message}). Se guardó una copia como users.json.bak.", "Servidor FTP");
                users = new List<FtpUser>();
            }

            if (users.Count == 0)
            {
                users.Add(new FtpUser
                {
                    Username = "admin",
                    Password = "admin",
                    RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FtpRoot")
                });
                try { FtpUserStore.Save(UsersFile, users); } catch { }
            }
            return users;
        }

        /// <summary>Guarda la lista de usuarios en users.json y avisa si falla.</summary>
        private void SaveUsers()
        {
            try { FtpUserStore.Save(UsersFile, _users); }
            catch (Exception ex) { MessageBox.Show(this, $"No se pudo guardar users.json: {ex.Message}", "Servidor FTP"); }
        }

        /// <summary>Redibuja la lista de usuarios a partir de <c>_users</c>.</summary>
        private void RefreshUsersList()
        {
            _lvUsers.BeginUpdate();
            _lvUsers.Items.Clear();
            foreach (FtpUser u in _users)
                _lvUsers.Items.Add(new ListViewItem(new[] { u.Username, u.RootPath, u.ReadOnly ? "Sí" : "No" }) { Tag = u });
            _lvUsers.EndUpdate();
        }

        /// <summary>Devuelve el usuario seleccionado en la lista (o null).</summary>
        private FtpUser? SelectedUser() =>
            _lvUsers.SelectedItems.Count > 0 ? _lvUsers.SelectedItems[0].Tag as FtpUser : null;

        /// <summary>Abre el diálogo para crear un usuario nuevo.</summary>
        private void AddUser()
        {
            using var dlg = new UserEditDialog(null, _users.Select(u => u.Username));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _users.Add(dlg.Result);
            SaveUsers();
            RefreshUsersList();
        }

        /// <summary>Abre el diálogo para editar el usuario seleccionado.</summary>
        private void EditUser()
        {
            FtpUser? old = SelectedUser();
            if (old == null) return;
            using var dlg = new UserEditDialog(old, _users.Where(u => u != old).Select(u => u.Username));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _users[_users.IndexOf(old)] = dlg.Result;
            SaveUsers();
            RefreshUsersList();
        }

        /// <summary>Elimina el usuario seleccionado tras pedir confirmación.</summary>
        private void DeleteUser()
        {
            FtpUser? u = SelectedUser();
            if (u == null) return;
            if (MessageBox.Show(this, $"¿Quitar al usuario '{u.Username}'? (no se borra su carpeta)", "Servidor FTP",
                                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _users.Remove(u);
            SaveUsers();
            RefreshUsersList();
        }

        // ------------------------------------------------------------------ historiales

        /// <summary>Recibe eventos desde hilos secundarios y los pasa al hilo de la interfaz.</summary>
        private void OnLogEntry(object? sender, FtpLogEntry e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => AddEntry(e))); } catch (InvalidOperationException) { }
        }

        /// <summary>Reparte un evento en los historiales correspondientes.</summary>
        private void AddEntry(FtpLogEntry e)
        {
            string t = e.Time.ToString("HH:mm:ss");
            string id = e.SessionId == 0 ? "-" : e.SessionId.ToString();

            switch (e.Category)
            {
                case FtpLogCategory.Connection:
                    AddRow(_lvConn, new[] { t, id, e.RemoteEndPoint, e.User, e.Message });
                    break;
                case FtpLogCategory.Transfer:
                    double secs = Math.Max(e.Duration.TotalSeconds, 0.001);
                    AddRow(_lvTransfers, new[]
                    {
                        t, id, e.RemoteEndPoint, e.User, e.Message, e.FileName, FormatBytes(e.Bytes),
                        $"{e.Duration.TotalSeconds:0.0} s", FormatBytes((long)(e.Bytes / secs)) + "/s"
                    });
                    AddRow(_lvFiles, new[] { t, id, e.User, e.Message, e.FileName });
                    break;
                case FtpLogCategory.File:
                    AddRow(_lvFiles, new[] { t, id, e.User, e.Message, e.FileName });
                    break;
                case FtpLogCategory.Error:
                    string text = e.FileName.Length > 0 ? $"{e.Message} ({e.FileName})" : e.Message;
                    AddRow(_lvErrors, new[] { t, id, e.RemoteEndPoint, e.User, text }, true);
                    break;
            }
        }

        /// <summary>Añade una fila al historial respetando el máximo de filas.</summary>
        private static void AddRow(ListView lv, string[] cells, bool isError = false)
        {
            var item = new ListViewItem(cells);
            if (isError) item.ForeColor = Color.Firebrick;
            lv.Items.Add(item);
            if (lv.Items.Count > MaxRows) lv.Items.RemoveAt(0);
            item.EnsureVisible();
        }

        // ------------------------------------------------------------------ conexiones activas

        /// <summary>Sincroniza la lista de conexiones activas con el estado del servidor.</summary>
        private void RefreshSessions()
        {
            List<FtpSessionInfo> sessions = _server is { IsRunning: true } ? _server.GetSessions() : new List<FtpSessionInfo>();
            var keys = new HashSet<string>(sessions.Select(s => s.Id.ToString()));

            _lvSessions.BeginUpdate();
            for (int i = _lvSessions.Items.Count - 1; i >= 0; i--)
                if (!keys.Contains(_lvSessions.Items[i].Name)) _lvSessions.Items.RemoveAt(i);

            foreach (FtpSessionInfo s in sessions)
            {
                string key = s.Id.ToString();
                string[] cells =
                {
                    key, s.RemoteEndPoint, s.User, (DateTime.Now - s.ConnectedAt).ToString(@"hh\:mm\:ss"),
                    s.CurrentDirectory, s.LastCommand, FormatBytes(s.BytesIn), FormatBytes(s.BytesOut)
                };
                if (_lvSessions.Items.ContainsKey(key))
                {
                    ListViewItem item = _lvSessions.Items[key]!;
                    for (int c = 0; c < cells.Length; c++) item.SubItems[c].Text = cells[c];
                }
                else _lvSessions.Items.Add(new ListViewItem(cells) { Name = key });
            }
            _lvSessions.EndUpdate();
        }

        /// <summary>Convierte bytes a un texto legible (KB, MB, GB...).</summary>
        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
        }
    }
}
