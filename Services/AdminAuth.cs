using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using SimpleFtp.Config;
using SimpleFtp.Ftp;

namespace SimpleFtp.Services
{
    /// <summary>
    /// Autenticación del panel web: usuario administrador con hash PBKDF2, sesiones por cookie
    /// HttpOnly en memoria y bloqueo temporal tras varios intentos fallidos desde la misma IP.
    /// </summary>
    public sealed class AdminAuth
    {
        /// <summary>Nombre de la cookie de sesión del panel.</summary>
        public const string CookieName = "ftp_panel";
        private static readonly TimeSpan SessionLife = TimeSpan.FromHours(12);
        private const int MaxFails = 5;
        private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(1);

        private readonly AppSettingsStore _store;
        private readonly ConcurrentDictionary<string, DateTime> _tokens = new();
        private readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> _fails = new();

        /// <summary>Crea el servicio; si no hay contraseña de administrador genera una aleatoria.</summary>
        /// <param name="store">Ajustes persistentes.</param>
        public AdminAuth(AppSettingsStore store)
        {
            _store = store;
            lock (_store.SyncRoot)
            {
                if (string.IsNullOrEmpty(_store.Current.AdminPasswordHash))
                {
                    string pass = PasswordHasher.GenerateRandom();
                    _store.Current.AdminPasswordHash = PasswordHasher.Hash(pass);
                    _store.Save();
                    AppSettingsStore.AppendFirstRunNote($"Panel web -> usuario: {_store.Current.AdminUser}  contraseña: {pass}");
                    Console.WriteLine($"[PRIMER ARRANQUE] Panel web -> usuario: {_store.Current.AdminUser}  contraseña: {pass}");
                }
            }
        }

        /// <summary>Valida credenciales y, si son correctas, crea una sesión.</summary>
        /// <param name="user">Usuario.</param>
        /// <param name="pass">Contraseña.</param>
        /// <param name="ip">IP del cliente (para el bloqueo por intentos).</param>
        /// <param name="token">Token de sesión creado (solo si devuelve true).</param>
        /// <param name="error">Motivo del rechazo (solo si devuelve false).</param>
        public bool Login(string user, string pass, string ip, out string token, out string error)
        {
            token = ""; error = "";
            if (_fails.TryGetValue(ip, out var f) && f.Until > DateTime.UtcNow)
            {
                error = "Demasiados intentos. Espera un minuto.";
                return false;
            }

            bool ok;
            lock (_store.SyncRoot)
            {
                ok = string.Equals(user, _store.Current.AdminUser, StringComparison.Ordinal)
                     && PasswordHasher.Verify(pass, _store.Current.AdminPasswordHash ?? "");
            }

            if (!ok)
            {
                int n = _fails.TryGetValue(ip, out var prev) ? prev.Fails + 1 : 1;
                _fails[ip] = n >= MaxFails ? (0, DateTime.UtcNow + LockTime) : (n, DateTime.MinValue);
                error = "Usuario o contraseña incorrectos";
                return false;
            }

            _fails.TryRemove(ip, out _);
            var raw = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            token = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _tokens[token] = DateTime.UtcNow + SessionLife;
            return true;
        }

        /// <summary>Indica si la petición trae una cookie de sesión válida y no caducada.</summary>
        /// <param name="ctx">Contexto HTTP.</param>
        public bool IsAuthenticated(HttpContext ctx)
        {
            if (!ctx.Request.Cookies.TryGetValue(CookieName, out string? token) || string.IsNullOrEmpty(token)) return false;
            if (!_tokens.TryGetValue(token, out DateTime exp)) return false;
            if (exp > DateTime.UtcNow) return true;
            _tokens.TryRemove(token, out _);
            return false;
        }

        /// <summary>Cierra la sesión de la cookie recibida.</summary>
        /// <param name="ctx">Contexto HTTP.</param>
        public void Logout(HttpContext ctx)
        {
            if (ctx.Request.Cookies.TryGetValue(CookieName, out string? token) && token != null) _tokens.TryRemove(token, out _);
        }

        /// <summary>Cambia la contraseña del administrador y cierra todas las sesiones abiertas.</summary>
        /// <param name="current">Contraseña actual.</param>
        /// <param name="newPass">Contraseña nueva (mínimo 8 caracteres).</param>
        /// <param name="error">Motivo del rechazo.</param>
        public bool ChangePassword(string current, string newPass, out string error)
        {
            error = "";
            if (newPass.Length < 8) { error = "La contraseña nueva debe tener al menos 8 caracteres"; return false; }
            lock (_store.SyncRoot)
            {
                if (!PasswordHasher.Verify(current, _store.Current.AdminPasswordHash ?? "")) { error = "La contraseña actual no es correcta"; return false; }
                _store.Current.AdminPasswordHash = PasswordHasher.Hash(newPass);
                _store.Save();
            }
            _tokens.Clear();
            return true;
        }
    }
}
