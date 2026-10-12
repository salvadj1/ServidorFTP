using System;
using System.Security.Cryptography;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Hash de contraseñas con PBKDF2-SHA256 y sal aleatoria. Clase estática sin dependencias,
    /// reutilizable en cualquier proyecto. Formato: <c>pbkdf2$iteraciones$sal$hash</c> (Base64).
    /// </summary>
    public static class PasswordHasher
    {
        private const int Iterations = 100_000;
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        /// <summary>Genera el hash de una contraseña con una sal nueva.</summary>
        /// <param name="password">Contraseña en texto plano.</param>
        /// <returns>Cadena con iteraciones, sal y hash lista para guardar.</returns>
        public static string Hash(string password)
        {
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash = Derive(password, salt, Iterations);
            return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>Comprueba una contraseña contra un hash guardado (comparación en tiempo constante).</summary>
        /// <param name="password">Contraseña en texto plano.</param>
        /// <param name="stored">Cadena generada por <see cref="Hash"/>.</param>
        /// <returns>true si coincide; false si no coincide o el formato es inválido.</returns>
        public static bool Verify(string password, string stored)
        {
            try
            {
                string[] p = stored.Split('$');
                if (p.Length != 4 || p[0] != "pbkdf2") return false;
                int iterations = int.Parse(p[1]);
                byte[] salt = Convert.FromBase64String(p[2]);
                byte[] expected = Convert.FromBase64String(p[3]);
                byte[] actual = Derive(password, salt, iterations, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch { return false; }
        }

        /// <summary>Genera una contraseña aleatoria legible (sin caracteres ambiguos como 0/O o l/1).</summary>
        /// <param name="length">Longitud deseada.</param>
        public static string GenerateRandom(int length = 14)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            return new string(chars);
        }

        /// <summary>Deriva la clave PBKDF2-SHA256.</summary>
        private static byte[] Derive(string password, byte[] salt, int iterations, int size = HashSize)
        {
            using var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
            return kdf.GetBytes(size);
        }
    }
}
