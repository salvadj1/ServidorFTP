using System;
using SimpleFtp.Ftp;

namespace SimpleFtp.Services
{
    /// <summary>Evento del historial tal como se envía al navegador.</summary>
    public sealed class LogDto
    {
        public DateTime Time { get; init; }
        public string Category { get; init; } = "";
        public int SessionId { get; init; }
        public string Remote { get; init; } = "";
        public string User { get; init; } = "";
        public string Message { get; init; } = "";
        public string File { get; init; } = "";
        public long Bytes { get; init; }
        public double DurationMs { get; init; }

        /// <summary>Convierte un <see cref="FtpLogEntry"/> en su DTO.</summary>
        /// <param name="e">Evento original.</param>
        public static LogDto From(FtpLogEntry e) => new()
        {
            Time = e.Time, Category = e.Category.ToString(), SessionId = e.SessionId, Remote = e.RemoteEndPoint,
            User = e.User, Message = e.Message, File = e.FileName, Bytes = e.Bytes, DurationMs = e.Duration.TotalMilliseconds
        };
    }

    /// <summary>Sesión FTP activa tal como se envía al navegador.</summary>
    public sealed class SessionDto
    {
        public int Id { get; init; }
        public string Remote { get; init; } = "";
        public string User { get; init; } = "";
        public DateTime ConnectedAt { get; init; }
        public string LastCommand { get; init; } = "";
        public string Directory { get; init; } = "/";
        public long BytesIn { get; init; }
        public long BytesOut { get; init; }

        /// <summary>Convierte un <see cref="FtpSessionInfo"/> en su DTO.</summary>
        /// <param name="i">Instantánea de la sesión.</param>
        public static SessionDto From(FtpSessionInfo i) => new()
        {
            Id = i.Id, Remote = i.RemoteEndPoint, User = i.User, ConnectedAt = i.ConnectedAt,
            LastCommand = i.LastCommand, Directory = i.CurrentDirectory, BytesIn = i.BytesIn, BytesOut = i.BytesOut
        };
    }

    /// <summary>Usuario FTP sin contraseña (nunca se envía el hash al navegador).</summary>
    public sealed class UserDto
    {
        public string Username { get; init; } = "";
        public string RootPath { get; init; } = "";
        public bool ReadOnly { get; init; }
        public bool HasPassword { get; init; }

        /// <summary>Convierte un <see cref="FtpUser"/> en su DTO.</summary>
        /// <param name="u">Usuario original.</param>
        public static UserDto From(FtpUser u) => new()
        {
            Username = u.Username, RootPath = u.RootPath, ReadOnly = u.ReadOnly, HasPassword = u.HasPassword
        };
    }

    /// <summary>Datos recibidos del navegador para crear o editar un usuario.</summary>
    public sealed class UserInput
    {
        public string? Username { get; set; }
        /// <summary>Contraseña nueva; vacía al editar = no cambiarla.</summary>
        public string? Password { get; set; }
        public string? RootPath { get; set; }
        public bool ReadOnly { get; set; }
    }

    /// <summary>Estadísticas de un usuario para la pestaña de estadísticas.</summary>
    public sealed class StatsDto
    {
        public string User { get; init; } = "";
        public int Connections { get; init; }
        public int Uploads { get; init; }
        public int Downloads { get; init; }
        public int Files { get; init; }
        public int Errors { get; init; }
        public long UploadBytes { get; init; }
        public long DownloadBytes { get; init; }
        public DateTime LastActivity { get; init; }

        /// <summary>Convierte un <see cref="FtpUserStats"/> en su DTO.</summary>
        /// <param name="s">Estadísticas originales.</param>
        public static StatsDto From(FtpUserStats s) => new()
        {
            User = s.User, Connections = s.Connections, Uploads = s.Uploads, Downloads = s.Downloads, Files = s.Files,
            Errors = s.Errors, UploadBytes = s.UploadBytes, DownloadBytes = s.DownloadBytes, LastActivity = s.LastActivity
        };
    }

    /// <summary>Ajustes editables desde el panel (sin credenciales).</summary>
    public sealed class SettingsDto
    {
        public int FtpPort { get; set; }
        public int PassivePortMin { get; set; }
        public int PassivePortMax { get; set; }
        public string? PassiveExternalIp { get; set; }
        public bool AutoStartFtp { get; set; }
        public int RetentionDays { get; set; }
        public bool AdvertiseEnabled { get; set; }
        public string AdvertiseName { get; set; } = "";
        public string? BaseFolder { get; set; }
        public bool RestrictToBase { get; set; }
        public string WebBindAddress { get; set; } = "";
        public int WebPort { get; set; }
    }

    /// <summary>Petición de cambio de contraseña del panel.</summary>
    public sealed class PasswordChange
    {
        public string? Current { get; set; }
        public string? New { get; set; }
    }

    /// <summary>Petición de login del panel.</summary>
    public sealed class LoginRequest
    {
        public string? User { get; set; }
        public string? Password { get; set; }
    }
}
