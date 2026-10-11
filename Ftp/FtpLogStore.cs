using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Registro persistente de eventos en archivos CSV diarios (<c>ftp_yyyy-MM-dd.csv</c>).
    /// Es seguro entre hilos, protege contra inyección de fórmulas al abrir el CSV en Excel
    /// y se puede reutilizar en otros proyectos que usen <see cref="FtpLogEntry"/>.
    /// </summary>
    public sealed class FtpLogStore
    {
        private const char Sep = ';'; // Excel en configuración regional española separa con ';'
        private const string Header = "Fecha;Categoria;Sesion;IP;Usuario;Mensaje;Archivo;Bytes;DuracionMs";
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private readonly object _lock = new();

        /// <summary>Carpeta donde se guardan los archivos de registro.</summary>
        public string FolderPath { get; }

        /// <summary>Días que se conservan los archivos antes de borrarlos con <see cref="PurgeOld"/>.</summary>
        public int RetentionDays { get; }

        /// <summary>Crea el almacén y la carpeta si no existe.</summary>
        /// <param name="folder">Carpeta de los registros.</param>
        /// <param name="retentionDays">Días de retención.</param>
        public FtpLogStore(string folder, int retentionDays = 30)
        {
            FolderPath = folder;
            RetentionDays = Math.Max(1, retentionDays);
            try { Directory.CreateDirectory(folder); } catch { /* se reintentará al escribir */ }
        }

        /// <summary>Añade un evento al archivo del día. Devuelve false si no se pudo escribir.</summary>
        public bool Append(FtpLogEntry e)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(FolderPath);
                    string path = FileFor(e.Time.Date);
                    bool isNew = !File.Exists(path);
                    using var sw = new StreamWriter(path, true, new UTF8Encoding(isNew)); // BOM solo en archivos nuevos
                    if (isNew) sw.WriteLine(Header);
                    sw.WriteLine(ToLine(e));
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Lee los eventos de los últimos <paramref name="days"/> días (más antiguos primero).</summary>
        /// <param name="days">Días hacia atrás, contando hoy.</param>
        /// <param name="maxEntries">Máximo de eventos devueltos (se conservan los más recientes).</param>
        public List<FtpLogEntry> LoadRecent(int days, int maxEntries)
        {
            var result = new List<FtpLogEntry>();
            DateTime from = DateTime.Today.AddDays(-(Math.Max(days, 1) - 1));

            string[] files;
            try { files = Directory.GetFiles(FolderPath, "ftp_*.csv"); }
            catch (IOException) { return result; }
            catch (UnauthorizedAccessException) { return result; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                if (!TryDateOf(file, out DateTime day) || day < from) continue;
                try
                {
                    foreach (string line in File.ReadLines(file, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line.StartsWith("Fecha;", StringComparison.Ordinal)) continue;
                        FtpLogEntry? entry = Parse(line);
                        if (entry != null) result.Add(entry);
                    }
                }
                catch (IOException) { /* archivo en uso o dañado: se omite */ }
            }

            result = result.OrderBy(x => x.Time).ToList(); // ordenación estable
            if (result.Count > maxEntries) result.RemoveRange(0, result.Count - maxEntries);
            return result;
        }

        /// <summary>Borra los archivos de registro más antiguos que la retención configurada.</summary>
        public void PurgeOld()
        {
            DateTime limit = DateTime.Today.AddDays(-RetentionDays);
            try
            {
                foreach (string file in Directory.GetFiles(FolderPath, "ftp_*.csv"))
                {
                    if (TryDateOf(file, out DateTime day) && day < limit)
                    {
                        try { File.Delete(file); } catch (IOException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Escribe una colección de eventos en un CSV (por ejemplo, el historial filtrado).</summary>
        /// <param name="path">Ruta del archivo de salida (se sobrescribe).</param>
        /// <param name="entries">Eventos a exportar.</param>
        public static void WriteCsv(string path, IEnumerable<FtpLogEntry> entries)
        {
            using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
            sw.WriteLine(Header);
            foreach (FtpLogEntry e in entries) sw.WriteLine(ToLine(e));
        }

        // ------------------------------------------------------------------ utilidades internas

        private string FileFor(DateTime day) =>
            Path.Combine(FolderPath, "ftp_" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");

        private static bool TryDateOf(string file, out DateTime day)
        {
            string name = Path.GetFileNameWithoutExtension(file); // ftp_2026-10-10
            day = default;
            return name.Length > 4 && DateTime.TryParseExact(name.Substring(4), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        private static string ToLine(FtpLogEntry e) => string.Join(Sep.ToString(), new[]
        {
            e.Time.ToString(TimeFormat, CultureInfo.InvariantCulture),
            e.Category.ToString(),
            e.SessionId.ToString(CultureInfo.InvariantCulture),
            Quote(e.RemoteEndPoint), Quote(e.User), Quote(e.Message), Quote(e.FileName),
            e.Bytes.ToString(CultureInfo.InvariantCulture),
            ((long)e.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        });

        /// <summary>Escapa un campo CSV; los textos que empiezan por = + - @ llevan un apóstrofo delante (anti-fórmulas).</summary>
        private static string Quote(string s)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            if (s.IndexOf(Sep) >= 0 || s.IndexOf('"') >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>Deshace la protección anti-fórmulas de <see cref="Quote"/>.</summary>
        private static string Unquote(string s) =>
            s.Length > 1 && s[0] == '\'' && "=+-@".IndexOf(s[1]) >= 0 ? s.Substring(1) : s;

        /// <summary>Divide una línea CSV respetando las comillas.</summary>
        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == Sep) { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        /// <summary>Convierte una línea CSV en un evento (null si el formato no es válido).</summary>
        private static FtpLogEntry? Parse(string line)
        {
            List<string> f = SplitCsv(line);
            if (f.Count < 9) return null;
            if (!DateTime.TryParseExact(f[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)) return null;
            if (!Enum.TryParse(f[1], out FtpLogCategory cat)) return null;
            int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sid);
            long.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes);
            double.TryParse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms);

            return new FtpLogEntry
            {
                Time = time, Category = cat, SessionId = sid,
                RemoteEndPoint = Unquote(f[3]), User = Unquote(f[4]),
                Message = Unquote(f[5]), FileName = Unquote(f[6]),
                Bytes = bytes, Duration = TimeSpan.FromMilliseconds(ms)
            };
        }
    }
}
