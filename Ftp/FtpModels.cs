using System;
using System.Collections.Generic;
using System.Net;

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
        /// <summary>Contraseña (texto plano).</summary>
        public string Password { get; set; } = "";
        /// <summary>Carpeta física raíz de este usuario; no puede salir de ella.</summary>
        public string RootPath { get; set; } = "";
        /// <summary>Si es true, el usuario solo puede listar y descargar.</summary>
        public bool ReadOnly { get; set; }
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
