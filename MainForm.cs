using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SimpleFtp.Ftp;
using SimpleFtp.Ui;

namespace SimpleFtp
{
    /// <summary>
    /// Ventana principal: cabecera con estado del servidor, indicadores del día, conexiones activas
    /// e historiales (transferencias, registro completo, archivos, conexiones y errores) con filtros,
    /// detalle por evento, registro persistente en disco y exportación a CSV.
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int MaxRows = 5000;       // máximo de filas visibles por historial
        private const int MaxEntries = 20000;   // máximo de eventos en memoria
        private const int RetentionDays = 30;   // días de registros conservados en disco

        private FtpServerCore? _server;
        private readonly FtpLogStore _logStore;
        private readonly List<FtpLogEntry> _all;   // todos los eventos (hilo de interfaz)

        // ---- cabecera
        private readonly TextBox _txtPort = new() { Text = "2121", Width = 60 };
        private readonly Button _btnStart = new() { Text = "Iniciar", Width = 90 };
        private readonly Label _lblStatus = new() { Text = "● Detenido", AutoSize = true };
        private readonly Label _lblAddr = new() { Text = "Elige el puerto y pulsa Iniciar", AutoSize = true };

        // ---- indicadores del día
        private readonly KpiCard _kpiSessions = new() { Title = "Sesiones activas", Accent = FtpTheme.ConnColor };
        private readonly KpiCard _kpiUp = new() { Title = "Subido hoy", Accent = FtpTheme.UploadColor };
        private readonly KpiCard _kpiDown = new() { Title = "Descargado hoy", Accent = FtpTheme.DownloadColor };
        private readonly KpiCard _kpiTransfers = new() { Title = "Transferencias hoy", Accent = FtpTheme.FileColor };
        private readonly KpiCard _kpiErrors = new() { Title = "Errores hoy", Accent = FtpTheme.ErrorColor };
        private readonly Dictionary<string, FtpUserStats> _todayStats = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _kpiDay = DateTime.Today;                 // día al que corresponden los contadores
        private readonly ComboBox _cmbKpiUser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };

        // ---- gráfica en tiempo real
        private readonly SpeedChart _chart = new() { Dock = DockStyle.Top, Height = 150 };
        private List<FtpSessionInfo> _lastSessions = new();
        private Dictionary<int, (long In, long Out)> _prevBytes = new();   // bytes de cada sesión en el muestreo anterior
        private readonly Stopwatch _sampleWatch = Stopwatch.StartNew();

        // ---- estadísticas por usuario
        private readonly ThemedListView _lvStats;
        private readonly ComboBox _cmbStatsRange = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private TabDef? _statsTab;

        // ---- usuarios
        private readonly Button _btnAddUser = new() { Text = "Añadir", Width = 80 };
        private readonly Button _btnEditUser = new() { Text = "Editar", Width = 80 };
        private readonly Button _btnDelUser = new() { Text = "Quitar", Width = 80 };
        private readonly ThemedListView _lvUsers;
        private readonly Button _btnUsers = new() { Text = "Usuarios", Width = 90 };
        private readonly Panel _usersPanel = new();   // lista y botones; se muestra en una ventana modal
        private static readonly string UsersFile = Path.Combine(AppContext.BaseDirectory, "users.json");
        private List<FtpUser> _users = new();

        // ---- conexiones activas
        private readonly ThemedListView _lvSessions;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

        // ---- pestañas e historiales
        private readonly List<TabDef> _tabs = new();
        private TabDef? _current;
        private readonly FlowLayoutPanel _tabStrip = new();
        private readonly Panel _content = new();
        private readonly Label _lblDetail = new();
        private readonly Label _lblFooter = new();
        private SplitContainer _split = new();

        // ---- filtros
        private readonly FlowLayoutPanel _filterBar = new();
        private readonly TextBox _txtSearch = new() { Width = 210, PlaceholderText = "Buscar archivo, usuario o IP" };
        private readonly ComboBox _cmbUser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly ComboBox _cmbRange = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly CheckBox[] _chips = new CheckBox[5];  // subidas, descargas, archivos, conexiones, errores
        private readonly Button _btnExport = new() { Text = "Exportar CSV", Width = 110 };
        private readonly Button _btnOpenLogs = new() { Text = "Abrir carpeta", Width = 110 };
        private readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 300 };
        private string _fltText = "";
        private string? _fltUser;
        private DateTime _fltStart = DateTime.MinValue;
        private readonly bool[] _fltKinds = { true, true, true, true, true };

        /// <summary>Un historial: lista, pestaña, qué eventos acepta y cómo construye cada fila.</summary>
        private sealed class HistoryView
        {
            public ThemedListView List { get; }
            public TabButton Tab { get; }
            public Func<FtpLogEntry, bool> Accepts { get; }
            public Func<FtpLogEntry, LogItem> Build { get; }
            public int Count;
            public bool Stale = true;   // true = la lista no refleja el filtro actual y hay que reconstruirla

            public HistoryView(ThemedListView list, TabButton tab, Func<FtpLogEntry, bool> accepts, Func<FtpLogEntry, LogItem> build)
            {
                List = list; Tab = tab; Accepts = accepts; Build = build;
            }
        }

        /// <summary>Una pestaña: botón, panel que muestra y (si es un historial) su vista.</summary>
        private sealed class TabDef
        {
            public TabButton Button { get; }
            public Control Pane { get; }
            public HistoryView? View { get; }
            public TabDef(TabButton button, Control pane, HistoryView? view) { Button = button; Pane = pane; View = view; }
        }

        /// <summary>Construye la interfaz completa por código (sin diseñador).</summary>
        public MainForm()
        {
            Text = "Servidor FTP";
            Width = 1120;
            Height = 820;
            MinimumSize = new Size(920, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = FtpTheme.Back;
            ForeColor = FtpTheme.Text;
            Font = FtpTheme.Base;

            _users = LoadUsers();

            // Registro persistente: se limpian los antiguos y se recupera el historial reciente
            _logStore = new FtpLogStore(Path.Combine(AppContext.BaseDirectory, "logs"), RetentionDays);
            _logStore.PurgeOld();
            _all = _logStore.LoadRecent(RetentionDays, MaxEntries);

            _lvUsers = new ThemedListView(("Usuario", 150), ("Carpeta raíz", 500), ("Solo lectura", 90));
            _lvSessions = new ThemedListView(("ID", 45), ("IP remota", 140), ("Usuario", 110), ("Conectado", 80),
                                             ("Directorio", 200), ("Último comando", 120), ("Recibido", 90), ("Enviado", 90));

            _lvStats = new ThemedListView(("Usuario", 140), ("Activas", 65), ("Conexiones", 90), ("Subidas", 70), ("Descargas", 85),
                                          ("Subido", 95), ("Descargado", 100), ("Archivos", 80), ("Errores", 70),
                                          ("Última actividad", 130), ("Parte del tráfico", 160))
            { BadgeColumn = 0, BarColumn = 10 };
            for (int i = 1; i <= 8; i++) _lvStats.Columns[i].TextAlign = HorizontalAlignment.Right;

            CreateHistoryTabs();
            BuildLayout();
            WireEvents();
            RefreshUsersList();
            ComputeKpis();
            CaptureFilter();
        }

        // ------------------------------------------------------------------ construcción de la interfaz

        /// <summary>Registra un historial: crea su lista, su pestaña y la añade a <c>_tabs</c>.</summary>
        /// <param name="caption">Título de la pestaña.</param>
        /// <param name="accent">Color del subrayado.</param>
        /// <param name="accepts">Qué eventos pertenecen a este historial.</param>
        /// <param name="build">Cómo se convierte un evento en fila.</param>
        /// <param name="badgeCol">Columna dibujada como píldora (-1 = ninguna).</param>
        /// <param name="barCol">Columna con barra de velocidad (-1 = ninguna).</param>
        /// <param name="cols">Columnas (título, ancho).</param>
        private void AddHistoryTab(string caption, Color accent, Func<FtpLogEntry, bool> accepts,
                                   Func<FtpLogEntry, LogItem> build, int badgeCol, int barCol,
                                   params (string Title, int Width)[] cols)
        {
            var lv = new ThemedListView(cols) { BadgeColumn = badgeCol, BarColumn = barCol, Visible = false };
            var btn = new TabButton { Caption = caption, Accent = accent };
            var view = new HistoryView(lv, btn, accepts, build);
            var def = new TabDef(btn, lv, view);
            lv.SelectedIndexChanged += (_, _) => ShowDetail(lv);
            btn.Click += (_, _) => SelectTab(def);
            _tabs.Add(def);
        }

        /// <summary>Define los cinco historiales de la aplicación.</summary>
        private void CreateHistoryTabs()
        {
            AddHistoryTab("Transferencias", FtpTheme.DownloadColor, e => e.Category == FtpLogCategory.Transfer,
                BuildTransferRow, 4, 8,
                ("Hora", 120), ("Sesión", 55), ("IP remota", 130), ("Usuario", 90), ("Tipo", 90),
                ("Archivo", 300), ("Tamaño", 85), ("Duración", 75), ("Velocidad", 140));

            AddHistoryTab("Registro completo", FtpTheme.ConnColor, e => true, BuildFullRow, 1, -1,
                ("Hora", 120), ("Evento", 150), ("Sesión", 55), ("IP remota", 130), ("Usuario", 90),
                ("Detalle", 450), ("Tamaño", 85));

            AddHistoryTab("Archivos", FtpTheme.FileColor,
                e => e.Category == FtpLogCategory.File || e.Category == FtpLogCategory.Transfer,
                BuildFileRow, 3, -1,
                ("Hora", 120), ("Sesión", 55), ("Usuario", 100), ("Operación", 140), ("Archivo", 450));

            AddHistoryTab("Conexiones", FtpTheme.ConnColor, e => e.Category == FtpLogCategory.Connection,
                BuildConnRow, -1, -1,
                ("Hora", 120), ("Sesión", 55), ("IP remota", 140), ("Usuario", 100), ("Evento", 400));

            AddHistoryTab("Errores", FtpTheme.ErrorColor, e => e.Category == FtpLogCategory.Error,
                BuildErrorRow, -1, -1,
                ("Hora", 120), ("Sesión", 55), ("IP remota", 140), ("Usuario", 100), ("Error", 500));
        }

        /// <summary>Coloca los controles en la ventana.</summary>
        private void BuildLayout()
        {
            // ---------- cabecera con degradado
            var header = new GradientPanel { Dock = DockStyle.Top, Height = 66 };
            _lblStatus.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            _lblStatus.BackColor = Color.Transparent;
            _lblStatus.ForeColor = FtpTheme.ErrorColor;
            _lblStatus.Location = new Point(16, 9);
            _lblAddr.BackColor = Color.Transparent;
            _lblAddr.ForeColor = Color.FromArgb(170, 185, 220);
            _lblAddr.Location = new Point(19, 38);

            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
                BackColor = Color.Transparent, Padding = new Padding(0, 15, 14, 0)
            };
            var lblPort = new Label
            {
                Text = "Puerto:", AutoSize = true, BackColor = Color.Transparent,
                ForeColor = FtpTheme.Text, Margin = new Padding(0, 8, 6, 0)
            };
            FtpTheme.StyleInput(_txtPort);
            _txtPort.Margin = new Padding(0, 6, 12, 0);
            FtpTheme.StyleButton(_btnStart, FtpTheme.UploadColor, FtpTheme.Back);
            FtpTheme.StyleButton(_btnUsers, FtpTheme.Surface2, FtpTheme.Text);
            _btnUsers.Margin = new Padding(3, 3, 8, 3);
            right.Controls.AddRange(new Control[] { lblPort, _txtPort, _btnUsers, _btnStart });
            header.Controls.Add(_lblStatus);
            header.Controls.Add(_lblAddr);
            header.Controls.Add(right);

            // ---------- indicadores
            var kpis = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 80, WrapContents = false,
                BackColor = FtpTheme.Surface, Padding = new Padding(10, 8, 0, 0)
            };
            foreach (KpiCard k in new[] { _kpiSessions, _kpiUp, _kpiDown, _kpiTransfers, _kpiErrors })
            {
                k.Margin = new Padding(0, 0, 10, 0);
                kpis.Controls.Add(k);
            }

            // ---------- zona dividida: conexiones activas (arriba) e historiales (abajo)
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                BackColor = FtpTheme.Line, SplitterWidth = 4
            };
            _split.Panel1.BackColor = FtpTheme.Back;
            _split.Panel2.BackColor = FtpTheme.Back;

            var sessCaption = new Label
            {
                Text = "Conexiones activas", Dock = DockStyle.Top, Height = 26, Font = FtpTheme.Bold,
                BackColor = FtpTheme.Surface, ForeColor = FtpTheme.Text,
                Padding = new Padding(10, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft
            };
            _split.Panel1.Controls.Add(_lvSessions);
            _split.Panel1.Controls.Add(sessCaption);

            // ---------- pestaña de usuarios
            var usersPanel = _usersPanel;
            usersPanel.Dock = DockStyle.Fill;
            usersPanel.BackColor = FtpTheme.Back;
            var userButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), BackColor = FtpTheme.Surface
            };
            FtpTheme.StyleButton(_btnAddUser, FtpTheme.UploadColor, FtpTheme.Back);
            FtpTheme.StyleButton(_btnEditUser, FtpTheme.DownloadColor, FtpTheme.Back);
            FtpTheme.StyleButton(_btnDelUser, FtpTheme.ErrorColor, FtpTheme.Back);
            userButtons.Controls.AddRange(new Control[] { _btnAddUser, _btnEditUser, _btnDelUser });
            usersPanel.Controls.Add(_lvUsers);
            usersPanel.Controls.Add(userButtons);

            // ---------- pestaña de estadísticas por usuario (la primera)
            var statsPane = new Panel { Dock = DockStyle.Fill, BackColor = FtpTheme.Back, Visible = false };
            var statsBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, BackColor = FtpTheme.Surface, Padding = new Padding(8, 6, 8, 4)
            };
            var lblPeriod = new Label
            {
                Text = "Periodo:", AutoSize = true, ForeColor = FtpTheme.Muted, Margin = new Padding(0, 7, 6, 0)
            };
            FtpTheme.StyleInput(_cmbStatsRange);
            _cmbStatsRange.Margin = new Padding(0, 2, 0, 2);
            _cmbStatsRange.Items.AddRange(new object[] { "Última hora", "Hoy", "7 días", "Todo" });
            _cmbStatsRange.SelectedIndex = 1;
            statsBar.Controls.AddRange(new Control[] { lblPeriod, _cmbStatsRange });
            statsPane.Controls.Add(_lvStats);
            statsPane.Controls.Add(statsBar);

            var statsBtn = new TabButton { Caption = "Estadísticas por usuario", Accent = FtpTheme.ConnColor };
            var statsDef = new TabDef(statsBtn, statsPane, null);
            statsBtn.Click += (_, _) => SelectTab(statsDef);
            _statsTab = statsDef;
            _tabs.Insert(0, statsDef);

            // ---------- selector de usuario para las tarjetas
            var kbar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = FtpTheme.Surface };
            kbar.Controls.Add(new Label
            {
                Text = "Tarjetas de:", AutoSize = true, ForeColor = FtpTheme.Muted, Location = new Point(14, 9)
            });
            FtpTheme.StyleInput(_cmbKpiUser);
            _cmbKpiUser.Location = new Point(92, 5);
            _cmbKpiUser.Items.Add("Todos los usuarios");
            foreach (string name in _users.Select(x => x.Username).Concat(_all.Select(x => x.User))
                                          .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                _cmbKpiUser.Items.Add(name);
            _cmbKpiUser.SelectedIndex = 0;
            kbar.Controls.Add(_cmbKpiUser);

            // ---------- barra de pestañas
            _tabStrip.Dock = DockStyle.Top;
            _tabStrip.Height = 34;
            _tabStrip.WrapContents = false;
            _tabStrip.BackColor = FtpTheme.Surface;
            _tabStrip.Padding = new Padding(8, 0, 0, 0);
            foreach (TabDef t in _tabs)
            {
                _tabStrip.Controls.Add(t.Button);
                if (t.View != null) t.View.Tab.Count = 0;
                _content.Controls.Add(t.Pane);
            }
            _content.Dock = DockStyle.Fill;

            // ---------- barra de filtros
            BuildFilterBar();

            // ---------- detalle del evento seleccionado
            _lblDetail.Dock = DockStyle.Bottom;
            _lblDetail.Height = 52;
            _lblDetail.BackColor = FtpTheme.Surface;
            _lblDetail.ForeColor = FtpTheme.Text;
            _lblDetail.Padding = new Padding(10, 6, 10, 4);
            _lblDetail.AutoEllipsis = true;

            // Orden de adición: primero el Fill y después los Top/Bottom (así el docking es correcto)
            _split.Panel2.Controls.Add(_content);
            _split.Panel2.Controls.Add(_lblDetail);
            _split.Panel2.Controls.Add(_filterBar);
            _split.Panel2.Controls.Add(_tabStrip);

            // ---------- pie
            _lblFooter.Dock = DockStyle.Bottom;
            _lblFooter.Height = 24;
            _lblFooter.BackColor = Color.FromArgb(11, 16, 32);
            _lblFooter.ForeColor = FtpTheme.Muted;
            _lblFooter.Padding = new Padding(10, 0, 0, 0);
            _lblFooter.TextAlign = ContentAlignment.MiddleLeft;
            _lblFooter.AutoEllipsis = true;

            Controls.Add(_split);
            Controls.Add(_lblFooter);
            Controls.Add(_chart);
            Controls.Add(kpis);
            Controls.Add(kbar);
            Controls.Add(header);

            Load += (_, _) =>
            {
                _split.SplitterDistance = Math.Max(60, _split.Height / 4);
                CaptureFilter();
                _current = _tabs[0];
                ApplyFilters();
                SelectTab(_tabs[0]);
            };
        }

        /// <summary>Crea la barra de filtros (búsqueda, usuario, rango, tipos, exportar).</summary>
        private void BuildFilterBar()
        {
            _filterBar.Dock = DockStyle.Top;
            _filterBar.AutoSize = true;
            _filterBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _filterBar.BackColor = FtpTheme.Surface;
            _filterBar.Padding = new Padding(8, 6, 8, 4);

            FtpTheme.StyleInput(_txtSearch);
            FtpTheme.StyleInput(_cmbUser);
            FtpTheme.StyleInput(_cmbRange);
            _txtSearch.Margin = new Padding(0, 2, 8, 2);
            _cmbUser.Margin = new Padding(0, 2, 8, 2);
            _cmbRange.Margin = new Padding(0, 2, 12, 2);

            _cmbUser.Items.Add("Todos los usuarios");
            foreach (string u in _all.Select(x => x.User).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                _cmbUser.Items.Add(u);
            _cmbUser.SelectedIndex = 0;

            _cmbRange.Items.AddRange(new object[] { "Última hora", "Hoy", "7 días", "Todo" });
            _cmbRange.SelectedIndex = 1;

            _filterBar.Controls.AddRange(new Control[] { _txtSearch, _cmbUser, _cmbRange });

            string[] names = { "Subidas", "Descargas", "Archivos", "Conexiones", "Errores" };
            Color[] colors = { FtpTheme.UploadColor, FtpTheme.DownloadColor, FtpTheme.FileColor, FtpTheme.ConnColor, FtpTheme.ErrorColor };
            for (int i = 0; i < _chips.Length; i++)
            {
                _chips[i] = new CheckBox { Text = names[i], Margin = new Padding(0, 0, 6, 0) };
                FtpTheme.StyleChip(_chips[i], colors[i]);
                _filterBar.Controls.Add(_chips[i]);
            }

            FtpTheme.StyleButton(_btnExport, FtpTheme.Surface2, FtpTheme.Text);
            FtpTheme.StyleButton(_btnOpenLogs, FtpTheme.Surface2, FtpTheme.Text);
            _btnExport.Margin = new Padding(10, 0, 4, 0);
            _btnOpenLogs.Margin = new Padding(0);
            _filterBar.Controls.Add(_btnExport);
            _filterBar.Controls.Add(_btnOpenLogs);
        }

        /// <summary>Conecta los eventos de los controles.</summary>
        private void WireEvents()
        {
            _btnUsers.Click += (_, _) => ShowUsersDialog();
            _btnAddUser.Click += (_, _) => AddUser();
            _btnEditUser.Click += (_, _) => EditUser();
            _btnDelUser.Click += (_, _) => DeleteUser();
            _lvUsers.DoubleClick += (_, _) => { if (_btnEditUser.Enabled) EditUser(); };
            _btnStart.Click += (_, _) => ToggleServer();
            _timer.Tick += (_, _) => { RefreshSessions(); if (_current == _statsTab) RefreshStats(); };

            // Filtros: la búsqueda espera 300 ms tras la última tecla para no reconstruir en cada pulsación
            _txtSearch.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
            _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilters(); };
            _cmbUser.SelectedIndexChanged += (_, _) => ApplyFilters();
            _cmbRange.SelectedIndexChanged += (_, _) => ApplyFilters();
            foreach (CheckBox c in _chips) c.CheckedChanged += (_, _) => ApplyFilters();
            _btnExport.Click += (_, _) => ExportCsv();
            _btnOpenLogs.Click += (_, _) => OpenLogsFolder();
            _cmbKpiUser.SelectedIndexChanged += (_, _) => ShowKpis();
            _cmbStatsRange.SelectedIndexChanged += (_, _) => RefreshStats();

            FormClosing += (_, _) => { _timer.Stop(); _filterTimer.Stop(); _server?.Dispose(); };
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
                MessageBox.Show(this, "Añade al menos un usuario con el botón Usuarios.", "Servidor FTP");
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
                _lblAddr.Text = $"Escuchando en 0.0.0.0:{port}     pasivo {options.PassivePortMin}-{options.PassivePortMax}";
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
            FtpTheme.StyleButton(_btnStart, running ? FtpTheme.ErrorColor : FtpTheme.UploadColor, FtpTheme.Back);
            _lblStatus.Text = running ? $"● Servidor en marcha (puerto {_txtPort.Text})" : "● Detenido";
            _lblStatus.ForeColor = running ? FtpTheme.UploadColor : FtpTheme.ErrorColor;
            if (!running) _lblAddr.Text = "Elige el puerto y pulsa Iniciar";
            foreach (Control c in new Control[] { _txtPort, _btnAddUser, _btnEditUser, _btnDelUser })
                c.Enabled = !running;
            _timer.Enabled = running;
            RefreshSessions();
            if (_current == _statsTab) RefreshStats();
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

        /// <summary>Abre la gestión de usuarios (lista + Añadir/Editar/Quitar) en una ventana modal.</summary>
        private void ShowUsersDialog()
        {
            using var dlg = new Form
            {
                Text = "Usuarios", StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(720, 380), MinimumSize = new Size(560, 300),
                MinimizeBox = false, ShowInTaskbar = false, BackColor = FtpTheme.Back
            };
            _usersPanel.Visible = true;
            dlg.Controls.Add(_usersPanel);
            dlg.ShowDialog(this);
            dlg.Controls.Remove(_usersPanel); // se retira para que no se destruya junto con la ventana
        }

        /// <summary>Redibuja la lista de usuarios a partir de <c>_users</c>.</summary>
        private void RefreshUsersList()
        {
            _lvUsers.BeginUpdate();
            _lvUsers.Items.Clear();
            foreach (FtpUser u in _users)
                _lvUsers.Items.Add(new ListViewItem(new[] { u.Username, u.RootPath, u.ReadOnly ? "Sí" : "No" }) { Tag = u });
            _lvUsers.EndUpdate();
            foreach (FtpUser nu in _users)
                if (!_cmbKpiUser.Items.Contains(nu.Username)) _cmbKpiUser.Items.Add(nu.Username);
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

        // ------------------------------------------------------------------ historiales y filtros

        /// <summary>Recibe eventos desde hilos secundarios: los guarda en disco y los pasa al hilo de la interfaz.</summary>
        private void OnLogEntry(object? sender, FtpLogEntry e)
        {
            _logStore.Append(e);
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => AddEntry(e))); } catch (InvalidOperationException) { }
        }

        /// <summary>Añade un evento a la memoria, a los indicadores y a los historiales que lo muestran.</summary>
        private void AddEntry(FtpLogEntry e)
        {
            _all.Add(e);
            if (_all.Count > MaxEntries + 1000) _all.RemoveRange(0, 1000);

            if (e.User.Length > 0 && !_cmbUser.Items.Contains(e.User)) _cmbUser.Items.Add(e.User);
            if (e.User.Length > 0 && !_cmbKpiUser.Items.Contains(e.User)) _cmbKpiUser.Items.Add(e.User);
            if (e.Time.Date == _kpiDay) FtpUserStats.Accumulate(_todayStats, e);
            ShowKpis();

            if (!Passes(e)) return;
            foreach (TabDef t in _tabs)
            {
                HistoryView? v = t.View;
                if (v == null || !v.Accepts(e)) continue;
                v.Count++;
                v.Tab.Count = v.Count;
                if (!v.Stale) v.List.AddRow(v.Build(e), MaxRows);
            }
            UpdateFooter();
        }

        /// <summary>Lee los controles de filtro y guarda sus valores para comprobar eventos rápidamente.</summary>
        private void CaptureFilter()
        {
            _fltText = _txtSearch.Text.Trim();
            _fltUser = _cmbUser.SelectedIndex > 0 ? _cmbUser.SelectedItem as string : null;
            _fltStart = _cmbRange.SelectedIndex switch
            {
                0 => DateTime.Now.AddHours(-1),
                1 => DateTime.Today,
                2 => DateTime.Today.AddDays(-6),
                _ => DateTime.MinValue
            };
            for (int i = 0; i < _chips.Length && i < _fltKinds.Length; i++)
                if (_chips[i] != null) _fltKinds[i] = _chips[i].Checked;
        }

        /// <summary>Tipo de evento para los chips: 0 subida, 1 descarga, 2 archivo, 3 conexión, 4 error.</summary>
        private static int KindOf(FtpLogEntry e) => e.Category switch
        {
            FtpLogCategory.Transfer => e.Message == "Subida" ? 0 : 1,
            FtpLogCategory.File => 2,
            FtpLogCategory.Connection => 3,
            _ => 4
        };

        private static bool Has(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Indica si un evento cumple los filtros actuales.</summary>
        private bool Passes(FtpLogEntry e)
        {
            if (!_fltKinds[KindOf(e)]) return false;
            if (e.Time < _fltStart) return false;
            if (_fltUser != null && !string.Equals(e.User, _fltUser, StringComparison.OrdinalIgnoreCase)) return false;
            if (_fltText.Length > 0 &&
                !(Has(e.FileName, _fltText) || Has(e.User, _fltText) || Has(e.RemoteEndPoint, _fltText) || Has(e.Message, _fltText)))
                return false;
            return true;
        }

        /// <summary>Reaplica los filtros: reconstruye la lista visible y marca las demás para hacerlo al abrirlas.</summary>
        private void ApplyFilters()
        {
            CaptureFilter();
            foreach (TabDef t in _tabs)
            {
                HistoryView? v = t.View;
                if (v == null) continue;
                if (t == _current) RebuildView(v);
                else
                {
                    v.Stale = true;
                    v.Count = _all.Count(e => v.Accepts(e) && Passes(e));
                    v.Tab.Count = v.Count;
                }
            }
            UpdateFooter();
        }

        /// <summary>Vuelve a llenar un historial con los eventos que cumplen el filtro (máximo <c>MaxRows</c> filas).</summary>
        private void RebuildView(HistoryView v)
        {
            var items = new List<ListViewItem>();
            int count = 0;
            for (int i = _all.Count - 1; i >= 0; i--)   // de más reciente a más antiguo
            {
                FtpLogEntry e = _all[i];
                if (!v.Accepts(e) || !Passes(e)) continue;
                count++;
                if (items.Count < MaxRows) items.Add(v.Build(e));
            }
            items.Reverse();

            v.List.BeginUpdate();
            v.List.Items.Clear();
            v.List.Items.AddRange(items.ToArray());
            v.List.EndUpdate();
            if (v.List.Items.Count > 0) v.List.Items[v.List.Items.Count - 1].EnsureVisible();

            v.Count = count;
            v.Tab.Count = count;
            v.Stale = false;
        }

        /// <summary>Activa una pestaña, muestra su panel y la barra de filtros si es un historial.</summary>
        private void SelectTab(TabDef t)
        {
            _current = t;
            foreach (TabDef d in _tabs)
            {
                d.Button.Selected = d == t;
                d.Pane.Visible = d == t;
            }
            if (t == _statsTab) RefreshStats();
            bool isHistory = t.View != null;
            _filterBar.Visible = isHistory;
            _lblDetail.Visible = isHistory;
            if (t.View != null)
            {
                if (t.View.Stale) RebuildView(t.View);
                ShowDetail(t.View.List);
            }
        }

        /// <summary>Muestra en el panel inferior el detalle del evento seleccionado.</summary>
        private void ShowDetail(ThemedListView lv)
        {
            if (lv.SelectedItems.Count == 0 || lv.SelectedItems[0] is not LogItem li || li.Entry == null)
            {
                _lblDetail.Text = "Selecciona una fila para ver el detalle completo del evento.";
                return;
            }

            FtpLogEntry e = li.Entry;
            double secs = e.Duration.TotalSeconds;
            var sb = new StringBuilder();
            sb.Append($"Hora: {e.Time:dd/MM/yyyy HH:mm:ss}      Evento: {EventLabel(e)}      Usuario: {OrDash(e.User)}");
            sb.Append($"      IP: {OrDash(e.RemoteEndPoint)}      Sesión: {IdText(e)}");
            sb.AppendLine();
            sb.Append($"Archivo: {OrDash(e.FileName)}");
            if (e.Category == FtpLogCategory.Transfer)
                sb.Append($"      Tamaño: {FormatBytes(e.Bytes)}      Duración: {secs:0.0} s      Velocidad: {FormatBytes((long)(e.Bytes / Math.Max(secs, 0.001)))}/s");
            else if (e.Category == FtpLogCategory.Error || e.Category == FtpLogCategory.Connection)
                sb.Append($"      Mensaje: {e.Message}");
            _lblDetail.Text = sb.ToString();
        }

        private static string OrDash(string s) => s.Length > 0 ? s : "-";
        private static string IdText(FtpLogEntry e) => e.SessionId == 0 ? "-" : e.SessionId.ToString();

        /// <summary>Hora corta si es de hoy; con fecha si es de otro día.</summary>
        private static string TimeText(FtpLogEntry e) =>
            e.Time.Date == DateTime.Today ? e.Time.ToString("HH:mm:ss") : e.Time.ToString("dd/MM HH:mm:ss");

        /// <summary>Etiqueta corta del tipo de evento (para píldoras y detalle).</summary>
        private static string EventLabel(FtpLogEntry e) => e.Category switch
        {
            FtpLogCategory.Transfer => e.Message,
            FtpLogCategory.File => e.Message,
            FtpLogCategory.Connection => "Conexión",
            _ => "Error"
        };

        // ---- constructores de filas (uno por historial)

        private static LogItem BuildTransferRow(FtpLogEntry e)
        {
            double secs = Math.Max(e.Duration.TotalSeconds, 0.001);
            long bps = (long)(e.Bytes / secs);
            double fraction = e.Bytes > 0 ? Math.Max(0.04, Math.Sqrt(bps / (100.0 * 1024 * 1024))) : 0; // escala suave hasta 100 MB/s
            return new LogItem(new[]
            {
                TimeText(e), IdText(e), e.RemoteEndPoint, e.User, e.Message, e.FileName,
                FormatBytes(e.Bytes), $"{e.Duration.TotalSeconds:0.0} s", FormatBytes(bps) + "/s"
            })
            { Accent = FtpTheme.ColorOf(e), Entry = e, Fraction = fraction };
        }

        private static LogItem BuildFullRow(FtpLogEntry e)
        {
            string detail = e.Category switch
            {
                FtpLogCategory.Connection => e.Message,
                FtpLogCategory.Error => e.FileName.Length > 0 ? $"{e.Message} ({e.FileName})" : e.Message,
                _ => e.FileName
            };
            return new LogItem(new[]
            {
                TimeText(e), EventLabel(e), IdText(e), e.RemoteEndPoint, e.User, detail,
                e.Bytes > 0 ? FormatBytes(e.Bytes) : ""
            })
            {
                Accent = FtpTheme.ColorOf(e), Entry = e,
                TextColor = e.Category == FtpLogCategory.Error ? FtpTheme.ErrorColor : null
            };
        }

        private static LogItem BuildFileRow(FtpLogEntry e) =>
            new LogItem(new[] { TimeText(e), IdText(e), e.User, e.Message, e.FileName })
            { Accent = FtpTheme.ColorOf(e), Entry = e };

        private static LogItem BuildConnRow(FtpLogEntry e) =>
            new LogItem(new[] { TimeText(e), IdText(e), e.RemoteEndPoint, e.User, e.Message })
            { Accent = FtpTheme.ConnColor, Entry = e };

        private static LogItem BuildErrorRow(FtpLogEntry e) =>
            new LogItem(new[]
            {
                TimeText(e), IdText(e), e.RemoteEndPoint, e.User,
                e.FileName.Length > 0 ? $"{e.Message} ({e.FileName})" : e.Message
            })
            { Accent = FtpTheme.ErrorColor, Entry = e, TextColor = FtpTheme.ErrorColor };

        // ------------------------------------------------------------------ indicadores, exportación y pie

        // ------------------------------------------------------------------ tarjetas, estadísticas y gráfica

        /// <summary>Reconstruye las estadísticas de hoy a partir del historial cargado.</summary>
        private void ComputeKpis()
        {
            _todayStats.Clear();
            _kpiDay = DateTime.Today;
            foreach (FtpLogEntry e in _all)
                if (e.Time.Date == _kpiDay) FtpUserStats.Accumulate(_todayStats, e);
            ShowKpis();
        }

        /// <summary>Usuario elegido para las tarjetas (null = todos).</summary>
        private string? SelectedKpiUser() =>
            _cmbKpiUser.SelectedIndex > 0 ? _cmbKpiUser.SelectedItem as string : null;

        /// <summary>Actualiza las cinco tarjetas: totales o del usuario elegido, con barra de reparto por usuario.</summary>
        private void ShowKpis()
        {
            if (_kpiDay != DateTime.Today) { ComputeKpis(); return; }   // cambio de día: se reinician los contadores

            string? sel = SelectedKpiUser();
            List<string> users = _todayStats.Keys
                .Union(_lastSessions.Select(x => x.User).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase)
                .ToList();

            long Stat(string user, Func<FtpUserStats, long> pick) =>
                _todayStats.TryGetValue(user, out FtpUserStats? st) ? pick(st) : 0;
            long Sessions(string user) =>
                _lastSessions.Count(x => string.Equals(x.User, user, StringComparison.OrdinalIgnoreCase));

            SetCard(_kpiSessions, "Sesiones activas", users, sel, Sessions, v => v.ToString("N0"));
            if (sel == null) _kpiSessions.Value = _lastSessions.Count.ToString("N0"); // incluye sesiones aún sin login
            SetCard(_kpiUp, "Subido hoy", users, sel, user => Stat(user, x => x.UploadBytes), v => FormatBytes(v));
            SetCard(_kpiDown, "Descargado hoy", users, sel, user => Stat(user, x => x.DownloadBytes), v => FormatBytes(v));
            SetCard(_kpiTransfers, "Transferencias hoy", users, sel, user => Stat(user, x => x.Uploads + x.Downloads), v => v.ToString("N0"));
            SetCard(_kpiErrors, "Errores hoy", users, sel, user => Stat(user, x => x.Errors), v => v.ToString("N0"));
        }

        /// <summary>
        /// Rellena una tarjeta: suma la métrica de todos los usuarios (o solo del elegido) y dibuja
        /// la barra con el reparto por usuario. Con un usuario elegido el título añade su porcentaje.
        /// </summary>
        private static void SetCard(KpiCard card, string title, List<string> users, string? sel,
                                    Func<string, long> metric, Func<long, string> format)
        {
            long total = 0, value = 0;
            var values = new List<(string User, long Value)>();
            foreach (string name in users)
            {
                long m = metric(name);
                values.Add((name, m));
                total += m;
            }

            var segments = new List<(Color Color, double Fraction)>();
            foreach (var (name, m) in values)
            {
                if (sel != null && !string.Equals(name, sel, StringComparison.OrdinalIgnoreCase)) continue;
                value += m;
                if (total > 0) segments.Add((FtpTheme.ForUser(name), (double)m / total));
            }

            card.Value = format(value);
            card.Title = sel != null && total > 0 ? $"{title} · {100.0 * value / total:0} %" : title;
            card.SetSegments(segments);
        }

        /// <summary>Recalcula y muestra la tabla de estadísticas por usuario para el periodo elegido.</summary>
        private void RefreshStats()
        {
            DateTime since = _cmbStatsRange.SelectedIndex switch
            {
                0 => DateTime.Now.AddHours(-1),
                1 => DateTime.Today,
                2 => DateTime.Today.AddDays(-6),
                _ => DateTime.MinValue
            };

            var map = new Dictionary<string, FtpUserStats>(StringComparer.OrdinalIgnoreCase);
            foreach (FtpLogEntry e in _all)
                if (e.Time >= since) FtpUserStats.Accumulate(map, e);
            foreach (FtpUser u in _users)                       // los usuarios sin actividad también aparecen
                if (!map.ContainsKey(u.Username)) map[u.Username] = new FtpUserStats(u.Username);

            long traffic = map.Values.Sum(x => x.TotalBytes);
            string? keep = _lvStats.SelectedItems.Count > 0 ? _lvStats.SelectedItems[0].Name : null;

            _lvStats.BeginUpdate();
            _lvStats.Items.Clear();
            foreach (FtpUserStats st in map.Values.OrderByDescending(x => x.TotalBytes)
                                                  .ThenBy(x => x.User, StringComparer.OrdinalIgnoreCase))
            {
                int active = _lastSessions.Count(x => string.Equals(x.User, st.User, StringComparison.OrdinalIgnoreCase));
                double share = traffic > 0 ? (double)st.TotalBytes / traffic : 0;
                var item = new LogItem(new[]
                {
                    st.User, active.ToString(), st.Connections.ToString("N0"), st.Uploads.ToString("N0"),
                    st.Downloads.ToString("N0"), FormatBytes(st.UploadBytes), FormatBytes(st.DownloadBytes),
                    st.Files.ToString("N0"), st.Errors.ToString("N0"), AgoText(st.LastActivity), $"{share * 100:0} %"
                })
                { Accent = FtpTheme.ForUser(st.User), Fraction = share, Name = st.User };
                _lvStats.Items.Add(item);
                if (keep != null && st.User == keep) item.Selected = true;
            }
            _lvStats.EndUpdate();
        }

        /// <summary>Texto relativo de una fecha ("ahora", "hace 5 min"...).</summary>
        private static string AgoText(DateTime t)
        {
            if (t == default) return "-";
            TimeSpan d = DateTime.Now - t;
            if (d.TotalSeconds < 10) return "ahora";
            if (d.TotalMinutes < 1) return $"hace {(int)d.TotalSeconds} s";
            if (d.TotalHours < 1) return $"hace {(int)d.TotalMinutes} min";
            if (d.TotalDays < 1) return $"hace {(int)d.TotalHours} h";
            return t.ToString("dd/MM HH:mm");
        }

        /// <summary>
        /// Toma una muestra de velocidad para la gráfica: compara los bytes de cada sesión con los del
        /// muestreo anterior (BytesIn = subidas, BytesOut = descargas) y lo divide por el tiempo transcurrido.
        /// </summary>
        private void SampleSpeed(List<FtpSessionInfo> sessions)
        {
            double secs = Math.Max(0.2, _sampleWatch.Elapsed.TotalSeconds);
            _sampleWatch.Restart();

            long dIn = 0, dOut = 0;
            var current = new Dictionary<int, (long In, long Out)>();
            foreach (FtpSessionInfo s in sessions)
            {
                current[s.Id] = (s.BytesIn, s.BytesOut);
                (long In, long Out) prev = _prevBytes.TryGetValue(s.Id, out var p) ? p : (0L, 0L);
                dIn += Math.Max(0, s.BytesIn - prev.In);
                dOut += Math.Max(0, s.BytesOut - prev.Out);
            }
            _prevBytes = current;
            _chart.AddSample(dIn / secs, dOut / secs);
        }

        private void UpdateFooter() =>
            _lblFooter.Text = $"Registro en disco: {_logStore.FolderPath}     |     Retención: {RetentionDays} días     |     {_all.Count:N0} eventos cargados";

        /// <summary>Exporta a CSV los eventos que cumplen el filtro actual.</summary>
        private void ExportCsv()
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Exportar historial",
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"ftp_historial_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                List<FtpLogEntry> list = _all.Where(Passes).ToList();
                FtpLogStore.WriteCsv(dlg.FileName, list);
                MessageBox.Show(this, $"{list.Count:N0} eventos exportados.", "Servidor FTP");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo exportar: {ex.Message}", "Servidor FTP");
            }
        }

        /// <summary>Abre la carpeta de registros en el Explorador de Windows.</summary>
        private void OpenLogsFolder()
        {
            try
            {
                Directory.CreateDirectory(_logStore.FolderPath);
                Process.Start(new ProcessStartInfo(_logStore.FolderPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo abrir la carpeta: {ex.Message}", "Servidor FTP");
            }
        }

        // ------------------------------------------------------------------ conexiones activas

        /// <summary>Sincroniza la lista de conexiones activas con el estado del servidor.</summary>
        private void RefreshSessions()
        {
            List<FtpSessionInfo> sessions = _server is { IsRunning: true } ? _server.GetSessions() : new List<FtpSessionInfo>();
            var keys = new HashSet<string>(sessions.Select(s => s.Id.ToString()));
            _lastSessions = sessions;
            SampleSpeed(sessions);
            ShowKpis();

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
