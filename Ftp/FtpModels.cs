using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Serialization;

namespace SimpleFtp.Ftp
{
    /// <summary>Categoría de un evento registrado por el servidor FTP.</summary>
    public enum FtpLogCategory
    {
        /// <summary>Conexiones, desconexiones y logins.</summary>
        Connection,
        /// <summary>Descargas y subidas de archivos completadas.</summary>
        Transfer,
        /// <summary>Operaciones sobre archivos/carpetas (borrar, crear, renombrar).</summary>
        File,
        /// <summary>Errores de cualquier tipo.</summary>
        Error
    }

    /// <summary>Evento inmutable generado por el servidor (se usa para los historiales).</summary>
    public sealed class FtpLogEntry
    {
        /// <summary>Momento en que se generó el evento.</summary>
        public DateTime Time { get; init; } = DateTime.Now;
        /// <summary>Tipo de evento.</summary>
        public FtpLogCategory Category { get; init; }
        /// <summary>Id de la sesión (0 si es un evento del servidor).</summary>
        public int SessionId { get; init; }
        /// <summary>IP:puerto del cliente (vacío si es del servidor).</summary>
        public string RemoteEndPoint { get; init; } = "";
        /// <summary>Usuario autenticado (puede estar vacío).</summary>
        public string User { get; init; } = "";
        /// <summary>Descripción legible del evento.</summary>
        public string Message { get; init; } = "";
        /// <summary>Ruta virtual del archivo implicado, si aplica.</summary>
        public string FileName { get; init; } = "";
        /// <summary>Bytes transferidos, si aplica.</summary>
        public long Bytes { get; init; }
        /// <summary>Duración de la transferencia, si aplica.</summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>Instantánea del estado de una sesión activa (para mostrar en una lista).</summary>
    public sealed class FtpSessionInfo
    {
        public int Id { get; init; }
        public string RemoteEndPoint { get; init; } = "";
        public string User { get; init; } = "";
        public DateTime ConnectedAt { get; init; }
        public string LastCommand { get; init; } = "";
        public string CurrentDirectory { get; init; } = "/";
        public long BytesIn { get; init; }
        public long BytesOut { get; init; }
    }

    /// <summary>Usuario FTP con su propia carpeta raíz.</summary>
    public sealed class FtpUser
    {
        /// <summary>Nombre de usuario (sin distinguir mayúsculas). "anonymous" acepta cualquier clave.</summary>
        public string Username { get; set; } = "";
        /// <summary>
        /// Contraseña en texto plano. OBSOLETO: solo se lee de users.json antiguos para migrarla
        /// a <see cref="PasswordHash"/>; nunca se vuelve a escribir.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Password { get; set; }
        /// <summary>Hash PBKDF2 de la contraseña (formato de <see cref="PasswordHasher"/>).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PasswordHash { get; set; }
        /// <summary>Carpeta física raíz de este usuario; no puede salir de ella.</summary>
        public string RootPath { get; set; } = "";
        /// <summary>Si es true, el usuario solo puede listar y descargar.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Indica si el usuario es la cuenta anónima (acepta cualquier contraseña).</summary>
        [JsonIgnore]
        public bool IsAnonymous => Username.Equals("anonymous", StringComparison.OrdinalIgnoreCase);

        /// <summary>Indica si el usuario tiene una contraseña definida.</summary>
        [JsonIgnore]
        public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

        /// <summary>Comprueba una contraseña contra el hash guardado (cuenta anónima: siempre true).</summary>
        /// <param name="plain">Contraseña recibida del cliente.</param>
        public bool CheckPassword(string plain) =>
            IsAnonymous || (!string.IsNullOrEmpty(PasswordHash) && PasswordHasher.Verify(plain, PasswordHash));

        /// <summary>Establece una contraseña nueva (se guarda solo su hash).</summary>
        /// <param name="plain">Contraseña en texto plano.</param>
        public void SetPassword(string plain)
        {
            PasswordHash = PasswordHasher.Hash(plain);
            Password = null;
        }

        /// <summary>
        /// Convierte una contraseña antigua en texto plano a hash.
        /// Devuelve true si hubo algo que migrar (hay que guardar users.json).
        /// </summary>
        public bool MigrateLegacyPassword()
        {
            if (string.IsNullOrEmpty(Password)) return false;
            SetPassword(Password);
            return true;
        }
    }

    /// <summary>Configuración del servidor FTP.</summary>
    public sealed class FtpServerOptions
    {
        /// <summary>Dirección local donde escuchar (Any = todas las interfaces).</summary>
        public IPAddress ListenAddress { get; set; } = IPAddress.Any;
        /// <summary>Puerto de control (21 requiere permisos de administrador).</summary>
        public int Port { get; set; } = 2121;
        /// <summary>Usuarios permitidos (cada uno con su carpeta raíz).</summary>
        public List<FtpUser> Users { get; set; } = new();
        /// <summary>Primer puerto del rango pasivo (abrir en el firewall/router).</summary>
        public int PassivePortMin { get; set; } = 50000;
        /// <summary>Último puerto del rango pasivo.</summary>
        public int PassivePortMax { get; set; } = 50100;
        /// <summary>IP pública a anunciar en modo pasivo (null = IP local de la conexión).</summary>
        public string? PassiveExternalIp { get; set; }
    }
}
