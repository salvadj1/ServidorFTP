using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Guarda y carga la lista de usuarios FTP en un archivo JSON.
    /// Clase estática independiente de la interfaz, reutilizable en otros proyectos.
    /// </summary>
    public static class FtpUserStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        /// <summary>
        /// Carga los usuarios desde el JSON indicado.
        /// Devuelve una lista vacía si el archivo no existe; lanza excepción si está corrupto.
        /// </summary>
        /// <param name="path">Ruta completa del archivo JSON.</param>
        public static List<FtpUser> Load(string path)
        {
            if (!File.Exists(path)) return new List<FtpUser>();
            return JsonSerializer.Deserialize<List<FtpUser>>(File.ReadAllText(path)) ?? new List<FtpUser>();
        }

        /// <summary>
        /// Guarda los usuarios en el JSON indicado (escribe a un temporal y lo reemplaza,
        /// para no dejar el archivo a medias si falla la escritura).
        /// </summary>
        /// <param name="path">Ruta completa del archivo JSON.</param>
        /// <param name="users">Usuarios a guardar.</param>
        public static void Save(string path, IEnumerable<FtpUser> users)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(users, JsonOpts));
            File.Move(tmp, path, true);
        }
    }
}
