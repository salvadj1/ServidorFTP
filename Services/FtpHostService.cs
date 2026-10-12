using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleFtp.Config;
using SimpleFtp.Ftp;
using SimpleFtp.Net;

namespace SimpleFtp.Services
{
    /// <summary>
    /// Servicio en segundo plano que aloja el servidor FTP: gestiona usuarios, historial, estadísticas,
    /// velocidad en vivo (SignalR) y el anuncio mDNS. Sustituye a la lógica que antes vivía en MainForm.
    /// </summary>
    public sealed class FtpHostService : IHostedService, IDisposable
    {
        private static readonly Regex UserNameRegex = new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$");
        private const int RecentMax = 500;

        private readonly AppSettingsStore _store;
        private readonly IHubContext<LiveHub> _hub;
        private readonly ILogger<FtpHostService> _log;
        private readonly object _lock = new();
        private readonly object _statsLock = new();
        private readonly ConcurrentQueue<LogDto> _recent = new();
        private readonly Dictionary<string, FtpUserStats> _todayStats = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, (long In, long Out)> _prev = new();

        private FtpServerCore? _server;
        private FtpLogStore _logStore;
        private List<FtpUser> _users = new();
        private MdnsAdvertiser? _mdns;
        private Timer? _timer;
        private DateTime _statsDay = DateTime.Today;

        private string UsersFile => Path.Combine(AppPaths.DataDir, "users.json");

        /// <summary>Indica si el servidor FTP está escuchando.</summary>
        public bool IsRunning => _server?.IsRunning == true;

        /// <summary>Crea el servicio (se registra como singleton y como hosted service).</summary>
        public FtpHostService(AppSettingsStore store, IHubContext<LiveHub> hub, ILogger<FtpHostService> log)
        {
            _store = store; _hub = hub; _log = log;
            _logStore = new FtpLogStore(Path.Combine(AppPaths.DataDir, "logs"), store.Current.RetentionDays);
        }

        // ------------------------------------------------------------------ ciclo de vida

        /// <summary>Carga usuarios e historial, arranca el temporizador y, si procede, el servidor FTP.</summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _users = LoadUsers();
            _logStore.PurgeOld();
            foreach (FtpLogEntry e in _logStore.LoadRecent(1, RecentMax))
            {
                Enqueue(LogDto.From(e));
                if (e.Time.Date == _statsDay) lock (_statsLock) FtpUserStats.Accumulate(_todayStats, e);
            }

            _timer = new Timer(_ => Tick(), null, 1000, 1000);
            if (_store.Current.AutoStartFtp)
            {
                try { StartServer(); }
                catch (Exception ex) { _log.LogError("No se pudo iniciar el servidor FTP: {Message}", ex.Message); }
            }
            RefreshAdvertising();
            return Task.CompletedTask;
        }

        /// <summary>Detiene el servidor, el anuncio y el temporizador.</summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Dispose();
            StopServer();
            _mdns?.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>Libera recursos.</summary>
        public void Dispose()
        {
            _timer?.Dispose();
            _server?.Dispose();
            _mdns?.Dispose();
        }

        // ------------------------------------------------------------------ servidor

        /// <summary>Arranca el servidor FTP con la configuración y usuarios actuales (lanza excepción si falla).</summary>
        public void StartServer()
        {
            lock (_lock)
            {
                if (IsRunning) return;
                AppSettings c = _store.Current;
                var opt = new FtpServerOptions
                {
                    ListenAddress = IPAddress.Any,
                    Port = c.FtpPort,
                    Users = _users,
                    PassivePortMin = c.PassivePortMin,
                    PassivePortMax = c.PassivePortMax,
                    PassiveExternalIp = string.IsNullOrWhiteSpace(c.PassiveExternalIp) ? null : c.PassiveExternalIp
                };
                var server = new FtpServerCore(opt);
                server.LogEntryAdded += OnLog;
                server.Start();
                _server = server;
            }
            RefreshAdvertising();
            _ = _hub.Clients.All.SendAsync("status", GetStatus());
        }

        /// <summary>Detiene el servidor FTP y cierra todas las sesiones.</summary>
        public void StopServer()
        {
            lock (_lock)
            {
                if (_server == null) return;
                _server.Stop();
                _server.LogEntryAdded -= OnLog;
                _server.Dispose();
                _server = null;
            }
            RefreshAdvertising();
            _ = _hub.Clients.All.SendAsync("status", GetStatus());
        }

        /// <summary>Reinicia el servidor FTP (aplica puerto y rango pasivo nuevos).</summary>
        public void RestartServer() { StopServer(); StartServer(); }

        /// <summary>Estado general para el panel.</summary>
        public object GetStatus() => new
        {
            running = IsRunning,
            ftpPort = _store.Current.FtpPort,
            webPort = _store.Current.WebPort,
            advertising = _mdns != null,
            hostName = Environment.MachineName,
            sessions = _server?.GetSessions().Count ?? 0
        };

        /// <summary>Aplica los ajustes guardados: nuevo retention, reinicio del FTP si está activo y re-anuncio.</summary>
        public void ApplySettings()
        {
            lock (_lock) _logStore = new FtpLogStore(Path.Combine(AppPaths.DataDir, "logs"), _store.Current.RetentionDays);
            if (IsRunning) RestartServer(); else RefreshAdvertising();
        }

        // ------------------------------------------------------------------ mDNS

        /// <summary>Recrea el anuncio mDNS según el estado actual (FTP si está activo + panel web).</summary>
        private void RefreshAdvertising()
        {
            lock (_lock)
            {
                _mdns?.Dispose();
                _mdns = null;
                AppSettings c = _store.Current;
                if (!c.AdvertiseEnabled) return;

                var list = new List<MdnsService>
                {
                    new() { Type = "_http._tcp", Name = c.AdvertiseName + " (panel)", Port = c.WebPort, Txt = new[] { "path=/" } }
                };
                if (IsRunning)
                    list.Add(new MdnsService { Type = "_ftp._tcp", Name = c.AdvertiseName, Port = c.FtpPort, Txt = new[] { "panel=" + c.WebPort } });

                _mdns = new MdnsAdvertiser(Environment.MachineName, list, m => _log.LogInformation("{Message}", m));
                _mdns.Start();
            }
        }

        // ------------------------------------------------------------------ usuarios

        /// <summary>Devuelve los usuarios actuales sin contraseñas.</summary>
        public List<UserDto> GetUsers() { lock (_lock) return _users.Select(UserDto.From).ToList(); }

        /// <summary>Crea un usuario nuevo. Devuelve null si va bien o el mensaje de error.</summary>
        /// <param name="input">Datos del formulario.</param>
        public string? AddUser(UserInput input)
        {
            string? err = Validate(input, creating: true, currentRoot: null);
            if (err != null) return err;
            lock (_lock)
            {
                if (_users.Any(u => u.Username.Equals(input.Username, StringComparison.OrdinalIgnoreCase))) return "Ya existe un usuario con ese nombre";
                var u = new FtpUser { Username = input.Username!, RootPath = input.RootPath!, ReadOnly = input.ReadOnly };
                if (!u.IsAnonymous) u.SetPassword(input.Password!);
                Commit(new List<FtpUser>(_users) { u });
            }
            return null;
        }

        /// <summary>Edita un usuario existente (contraseña vacía = no cambiarla). Devuelve null si va bien o el error.</summary>
        /// <param name="name">Nombre actual del usuario.</param>
        /// <param name="input">Datos nuevos.</param>
        public string? UpdateUser(string name, UserInput input)
        {
            lock (_lock)
            {
                FtpUser? old = _users.FirstOrDefault(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (old == null) return "Usuario no encontrado";
                input.Username = name; // el nombre no se puede cambiar
                string? err = Validate(input, creating: false, currentRoot: old.RootPath);
                if (err != null) return err;
                var u = new FtpUser { Username = old.Username, RootPath = input.RootPath!, ReadOnly = input.ReadOnly, PasswordHash = old.PasswordHash };
                if (!string.IsNullOrEmpty(input.Password)) u.SetPassword(input.Password);
                var list = _users.Where(x => x != old).ToList();
                list.Add(u);
                Commit(list.OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase).ToList());
            }
            return null;
        }

        /// <summary>Elimina un usuario (no se permite borrar el último). Devuelve null si va bien o el error.</summary>
        /// <param name="name">Nombre del usuario.</param>
        public string? DeleteUser(string name)
        {
            lock (_lock)
            {
                FtpUser? u = _users.FirstOrDefault(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (u == null) return "Usuario no encontrado";
                if (_users.Count == 1) return "No se puede eliminar el último usuario";
                Commit(_users.Where(x => x != u).ToList());
            }
            return null;
        }

        /// <summary>
        /// Valida los datos de un usuario y prepara su carpeta raíz. Si no se indica carpeta usa
        /// "carpeta base\nombre"; la crea si no existe. Con <c>RestrictToBase</c> activo la carpeta debe
        /// estar dentro de la base (salvo que sea la que el usuario ya tenía al editar).
        /// </summary>
        /// <param name="i">Datos recibidos del formulario (se normaliza RootPath).</param>
        /// <param name="creating">true si es un usuario nuevo.</param>
        /// <param name="currentRoot">Carpeta actual del usuario al editar (null al crear).</param>
        /// <returns>null si es válido o el mensaje de error.</returns>
        private string? Validate(UserInput i, bool creating, string? currentRoot)
        {
            if (string.IsNullOrWhiteSpace(i.Username) || !UserNameRegex.IsMatch(i.Username))
                return "Nombre de usuario no válido (1-32 caracteres: letras, números, _ . -; debe empezar por letra, número o _)";

            AppSettings c = _store.Current;
            string baseFolder;
            try { baseFolder = c.ResolveBaseFolder(); }
            catch (Exception ex) { return $"Carpeta base no válida: {ex.Message}"; }

            if (string.IsNullOrWhiteSpace(i.RootPath)) i.RootPath = Path.Combine(baseFolder, i.Username);

            bool anon = i.Username.Equals("anonymous", StringComparison.OrdinalIgnoreCase);
            if (creating && !anon && (i.Password ?? "").Length < 6) return "La contraseña debe tener al menos 6 caracteres";
            if (!creating && !string.IsNullOrEmpty(i.Password) && i.Password.Length < 6) return "La contraseña debe tener al menos 6 caracteres";

            try
            {
                i.RootPath = Path.GetFullPath(i.RootPath);
                StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                bool unchanged = currentRoot != null && string.Equals(Path.GetFullPath(currentRoot), i.RootPath, cmp);
                if (c.RestrictToBase && !unchanged && !PathGuard.IsInside(baseFolder, i.RootPath))
                    return $"La carpeta debe estar dentro de la carpeta base ({baseFolder})";
                Directory.CreateDirectory(i.RootPath);
            }
            catch (Exception ex) { return $"Carpeta no válida: {ex.Message}"; }
            return null;
        }

        /// <summary>Guarda la lista, la aplica al servidor en marcha (copy-on-write) y la persiste en users.json.</summary>
        private void Commit(List<FtpUser> list)
        {
            _users = list;
            if (_server != null) _server.Options.Users = list;
            FtpUserStore.Save(UsersFile, list);
        }

        /// <summary>
        /// Carga users.json, migra contraseñas antiguas en texto plano a hash y, si no hay usuarios,
        /// crea "admin" con contraseña aleatoria (anotada en PRIMER_ARRANQUE.txt).
        /// </summary>
        private List<FtpUser> LoadUsers()
        {
            List<FtpUser> users;
            try { users = FtpUserStore.Load(UsersFile); }
            catch (Exception ex)
            {
                try { File.Copy(UsersFile, UsersFile + ".bak", true); } catch { /* sin permisos */ }
                _log.LogError("users.json no se pudo leer ({Message}); copia en users.json.bak", ex.Message);
                users = new List<FtpUser>();
            }

            bool changed = users.Aggregate(false, (acc, u) => u.MigrateLegacyPassword() | acc);
            if (users.Count == 0)
            {
                string pass = PasswordHasher.GenerateRandom();
                var admin = new FtpUser { Username = "admin", RootPath = Path.Combine(_store.Current.ResolveBaseFolder(), "admin") };
                admin.SetPassword(pass);
                Directory.CreateDirectory(admin.RootPath);
                users.Add(admin);
                changed = true;
                AppSettingsStore.AppendFirstRunNote($"Servidor FTP -> usuario: admin  contraseña: {pass}  carpeta: {admin.RootPath}");
                Console.WriteLine($"[PRIMER ARRANQUE] Servidor FTP -> usuario: admin  contraseña: {pass}");
            }
            if (changed) { try { FtpUserStore.Save(UsersFile, users); } catch (Exception ex) { _log.LogError("No se pudo guardar users.json: {Message}", ex.Message); } }
            return users;
        }

        // ------------------------------------------------------------------ sesiones, logs, estadísticas

        /// <summary>Sesiones activas.</summary>
        public List<SessionDto> GetSessions() =>
            _server?.GetSessions().Select(SessionDto.From).ToList() ?? new List<SessionDto>();

        /// <summary>Expulsa una sesión activa. Devuelve false si no existe.</summary>
        /// <param name="id">Id de sesión.</param>
        public bool Kick(int id) => _server?.KickSession(id) ?? false;

        /// <summary>Eventos más recientes en memoria (para el panel principal).</summary>
        /// <param name="count">Cantidad máxima.</param>
        public List<LogDto> GetRecent(int count) => _recent.Reverse().Take(count).Reverse().ToList();

        /// <summary>Consulta el historial de los últimos días con filtros opcionales.</summary>
        /// <param name="days">Días hacia atrás.</param>
        /// <param name="category">Categoría (vacío = todas).</param>
        /// <param name="user">Usuario (vacío = todos).</param>
        /// <param name="text">Texto a buscar en mensaje, archivo e IP.</param>
        /// <param name="max">Máximo de eventos (los más recientes).</param>
        public List<FtpLogEntry> QueryLogs(int days, string? category, string? user, string? text, int max)
        {
            IEnumerable<FtpLogEntry> q = _logStore.LoadRecent(Math.Clamp(days, 1, 365), 100_000);
            if (Enum.TryParse(category, true, out FtpLogCategory cat)) q = q.Where(e => e.Category == cat);
            if (!string.IsNullOrWhiteSpace(user)) q = q.Where(e => e.User.Equals(user, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(text))
                q = q.Where(e => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                              || e.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                              || e.RemoteEndPoint.Contains(text, StringComparison.OrdinalIgnoreCase));
            return q.Reverse().Take(Math.Clamp(max, 1, 10_000)).Reverse().ToList();
        }

        /// <summary>Estadísticas por usuario de los últimos días (incluye usuarios sin actividad).</summary>
        /// <param name="days">Días hacia atrás.</param>
        public List<StatsDto> GetStats(int days)
        {
            var map = new Dictionary<string, FtpUserStats>(StringComparer.OrdinalIgnoreCase);
            DateTime since = DateTime.Today.AddDays(-(Math.Clamp(days, 1, 365) - 1));
            foreach (FtpLogEntry e in _logStore.LoadRecent(Math.Clamp(days, 1, 365), 200_000))
                if (e.Time >= since) FtpUserStats.Accumulate(map, e);
            lock (_lock)
                foreach (FtpUser u in _users)
                    if (!map.ContainsKey(u.Username)) map[u.Username] = new FtpUserStats(u.Username);
            return map.Values.OrderByDescending(s => s.TotalBytes).Select(StatsDto.From).ToList();
        }

        // ------------------------------------------------------------------ eventos y temporizador

        /// <summary>Recibe cada evento del servidor (hilos secundarios): lo guarda, lo cuenta y lo emite.</summary>
        private void OnLog(object? sender, FtpLogEntry e)
        {
            try { _logStore.Append(e); } catch { /* disco lleno o sin permisos: no debe tumbar el servidor */ }
            LogDto dto = LogDto.From(e);
            Enqueue(dto);
            lock (_statsLock)
            {
                if (e.Time.Date != _statsDay) { _todayStats.Clear(); _statsDay = e.Time.Date; }
                FtpUserStats.Accumulate(_todayStats, e);
            }
            _ = _hub.Clients.All.SendAsync("log", dto);
        }

        /// <summary>Añade un evento a la cola circular de recientes.</summary>
        private void Enqueue(LogDto dto)
        {
            _recent.Enqueue(dto);
            while (_recent.Count > RecentMax) _recent.TryDequeue(out _);
        }

        /// <summary>Cada segundo: calcula la velocidad total y emite sesiones, velocidad y KPIs del día.</summary>
        private void Tick()
        {
            try
            {
                List<FtpSessionInfo> infos = _server?.GetSessions() ?? new List<FtpSessionInfo>();
                long up = 0, down = 0;
                var now = new Dictionary<int, (long, long)>();
                foreach (FtpSessionInfo s in infos)
                {
                    _prev.TryGetValue(s.Id, out var p);
                    up += Math.Max(0, s.BytesIn - p.In);
                    down += Math.Max(0, s.BytesOut - p.Out);
                    now[s.Id] = (s.BytesIn, s.BytesOut);
                }
                _prev.Clear();
                foreach (var kv in now) _prev[kv.Key] = kv.Value;

                object kpi;
                lock (_statsLock)
                {
                    if (DateTime.Today != _statsDay) { _todayStats.Clear(); _statsDay = DateTime.Today; }
                    kpi = new
                    {
                        connections = _todayStats.Values.Sum(s => s.Connections),
                        uploads = _todayStats.Values.Sum(s => s.Uploads),
                        downloads = _todayStats.Values.Sum(s => s.Downloads),
                        uploadBytes = _todayStats.Values.Sum(s => s.UploadBytes),
                        downloadBytes = _todayStats.Values.Sum(s => s.DownloadBytes),
                        errors = _todayStats.Values.Sum(s => s.Errors)
                    };
                }
                _ = _hub.Clients.All.SendAsync("tick", new
                {
                    running = IsRunning,
                    sessions = infos.Select(SessionDto.From).ToList(),
                    upSpeed = up,     // bytes/s recibidos de los clientes (subidas)
                    downSpeed = down, // bytes/s enviados a los clientes (descargas)
                    kpi
                });
            }
            catch (Exception ex) { _log.LogWarning("Error en el temporizador: {Message}", ex.Message); }
        }
    }
}
