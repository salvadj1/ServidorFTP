using System;
using System.Collections.Generic;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Estadísticas acumuladas de un usuario FTP (conexiones, subidas, descargas, bytes, archivos, errores).
    /// Se alimentan con <see cref="FtpLogEntry"/>; reutilizable en cualquier proyecto que use esos eventos.
    /// </summary>
    public sealed class FtpUserStats
    {
        /// <summary>Nombre con el que se agrupan los eventos sin usuario (p. ej. logins fallidos).</summary>
        public const string Unknown = "(sin identificar)";

        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Nombre del usuario.</summary>
        public string User { get; }
        /// <summary>Logins correctos.</summary>
        public int Connections { get; private set; }
        /// <summary>Archivos subidos al servidor.</summary>
        public int Uploads { get; private set; }
        /// <summary>Archivos descargados del servidor.</summary>
        public int Downloads { get; private set; }
        /// <summary>Eventos de error atribuidos al usuario.</summary>
        public int Errors { get; private set; }
        /// <summary>Bytes subidos.</summary>
        public long UploadBytes { get; private set; }
        /// <summary>Bytes descargados.</summary>
        public long DownloadBytes { get; private set; }
        /// <summary>Último evento del usuario (default si no hay ninguno).</summary>
        public DateTime LastActivity { get; private set; }
        /// <summary>Archivos distintos transferidos.</summary>
        public int Files => _files.Count;
        /// <summary>Bytes totales (subidos + descargados).</summary>
        public long TotalBytes => UploadBytes + DownloadBytes;

        /// <summary>Crea las estadísticas vacías de un usuario.</summary>
        public FtpUserStats(string user) { User = user; }

        /// <summary>Suma un evento a las estadísticas.</summary>
        public void Add(FtpLogEntry e)
        {
            if (e.Time > LastActivity) LastActivity = e.Time;
            switch (e.Category)
            {
                case FtpLogCategory.Transfer:
                    if (e.Message == "Subida") { Uploads++; UploadBytes += e.Bytes; }
                    else { Downloads++; DownloadBytes += e.Bytes; }
                    if (e.FileName.Length > 0) _files.Add(e.FileName);
                    break;
                case FtpLogCategory.Connection:
                    if (e.Message == "Login correcto") Connections++;
                    break;
                case FtpLogCategory.Error:
                    Errors++;
                    break;
            }
        }

        /// <summary>Suma un evento a la estadística de su usuario dentro de un diccionario (lo crea si no existe).</summary>
        /// <param name="map">Diccionario usuario → estadísticas (usar comparador sin distinguir mayúsculas).</param>
        /// <param name="e">Evento a sumar.</param>
        public static void Accumulate(Dictionary<string, FtpUserStats> map, FtpLogEntry e)
        {
            string key = e.User.Length > 0 ? e.User : Unknown;
            if (!map.TryGetValue(key, out FtpUserStats? s))
            {
                s = new FtpUserStats(key);
                map[key] = s;
            }
            s.Add(e);
        }
    }
}
