using System;
using System.IO;
using System.Text.Json;

namespace SimpleFtp.Config
{
    /// <summary>Rutas de datos de la aplicación (users.json, settings.json, logs, carpeta raíz FTP).</summary>
    public static class AppPaths
    {
        /// <summary>
        /// Carpeta de datos. Se puede cambiar con la variable de entorno FTPPANEL_DATA;
        /// por defecto es la carpeta del ejecutable.
        /// </summary>
        public static string DataDir
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("FTPPANEL_DATA");
                string dir = string.IsNullOrWhiteSpace(env) ? AppContext.BaseDirectory : env;
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }

    /// <summary>Configuración persistente del servicio (se guarda en settings.json).</summary>
    public sealed class AppSettings
    {
        /// <summary>Puerto de control FTP.</summary>
        public int FtpPort { get; set; } = 2121;
        /// <summary>Primer puerto del rango pasivo.</summary>
        public int PassivePortMin { get; set; } = 50000;
        /// <summary>Último puerto del rango pasivo.</summary>
        public int PassivePortMax { get; set; } = 50100;
        /// <summary>IP pública a anunciar en modo pasivo (vacío = IP local).</summary>
        public string? PassiveExternalIp { get; set; }
        /// <summary>Arrancar el servidor FTP automáticamente al iniciar el servicio.</summary>
        public bool AutoStartFtp { get; set; } = true;
        /// <summary>Días que se conservan los historiales CSV.</summary>
        public int RetentionDays { get; set; } = 30;
        /// <summary>Dirección donde escucha el panel web (0.0.0.0 = toda la red).</summary>
        public string WebBindAddress { get; set; } = "0.0.0.0";
        /// <summary>Puerto del panel web.</summary>
        public int WebPort { get; set; } = 8080;
        /// <summary>Anunciar el servicio en la red local (mDNS / DNS-SD).</summary>
        public bool AdvertiseEnabled { get; set; } = true;
        /// <summary>Nombre con el que aparece el servidor en la red.</summary>
        public string AdvertiseName { get; set; } = "Servidor FTP";
        /// <summary>Carpeta base de los usuarios FTP (vacío = "FtpRoot" dentro de la carpeta de datos).</summary>
        public string? BaseFolder { get; set; }
        /// <summary>Si es true, la carpeta raíz de cada usuario debe estar dentro de <see cref="BaseFolder"/>.</summary>
        public bool RestrictToBase { get; set; } = true;
        /// <summary>Usuario administrador del panel.</summary>
        public string AdminUser { get; set; } = "admin";
        /// <summary>Hash PBKDF2 de la contraseña del administrador.</summary>
        public string? AdminPasswordHash { get; set; }

        /// <summary>Devuelve la ruta completa de la carpeta base (la de por defecto si no se configuró).</summary>
        public string ResolveBaseFolder() =>
            Path.GetFullPath(string.IsNullOrWhiteSpace(BaseFolder) ? Path.Combine(AppPaths.DataDir, "FtpRoot") : BaseFolder);
    }

    /// <summary>Carga y guarda <see cref="AppSettings"/> en settings.json (escritura atómica).</summary>
    public sealed class AppSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private readonly object _lock = new();
        private readonly string _path;

        /// <summary>Configuración actual en memoria.</summary>
        public AppSettings Current { get; private set; }

        /// <summary>Objeto de sincronización para modificar <see cref="Current"/> de forma segura.</summary>
        public object SyncRoot => _lock;

        /// <summary>Carga settings.json (o crea valores por defecto si no existe o está corrupto).</summary>
        public AppSettingsStore()
        {
            _path = Path.Combine(AppPaths.DataDir, "settings.json");
            Current = Load();
        }

        /// <summary>Lee el archivo; si está corrupto lo copia a .bak y empieza con valores por defecto.</summary>
        private AppSettings Load()
        {
            if (!File.Exists(_path)) return new AppSettings();
            try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings(); }
            catch
            {
                try { File.Copy(_path, _path + ".bak", true); } catch { /* sin permisos */ }
                return new AppSettings();
            }
        }

        /// <summary>Guarda la configuración actual en disco.</summary>
        public void Save()
        {
            lock (_lock)
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
                File.Move(tmp, _path, true);
            }
        }

        /// <summary>Añade una línea al archivo PRIMER_ARRANQUE.txt (credenciales generadas automáticamente).</summary>
        /// <param name="line">Texto a añadir.</param>
        public static void AppendFirstRunNote(string line)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "PRIMER_ARRANQUE.txt"), line + Environment.NewLine); }
            catch { /* no es crítico */ }
        }
    }
}
