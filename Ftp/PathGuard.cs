using System;
using System.IO;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Utilidades para comprobar que una ruta física queda dentro de una carpeta base.
    /// Clase estática sin dependencias, reutilizable en cualquier proyecto.
    /// </summary>
    public static class PathGuard
    {
        /// <summary>
        /// Indica si <paramref name="path"/> es la carpeta base o está dentro de ella.
        /// Normaliza las rutas (resuelve ".." y barras) y compara sin distinguir mayúsculas en Windows.
        /// </summary>
        /// <param name="baseFolder">Carpeta base permitida.</param>
        /// <param name="path">Ruta a comprobar.</param>
        /// <returns>true si la ruta está dentro de la base; false si queda fuera.</returns>
        public static bool IsInside(string baseFolder, string path)
        {
            char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string b = Path.GetFullPath(baseFolder).TrimEnd(seps);
            string p = Path.GetFullPath(path).TrimEnd(seps);
            StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return p.Equals(b, cmp) || p.StartsWith(b + Path.DirectorySeparatorChar, cmp);
        }
    }
}
