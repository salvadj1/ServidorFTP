using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Servidor FTP mínimo y reutilizable (sin dependencias de interfaz gráfica).
    /// Acepta clientes, crea una <see cref="FtpSession"/> por cada uno y publica
    /// eventos mediante <see cref="LogEntryAdded"/>.
    /// </summary>
    public sealed class FtpServerCore : IDisposable
    {
        private readonly ConcurrentDictionary<int, FtpSession> _sessions = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private int _nextSessionId;

        /// <summary>Configuración activa del servidor.</summary>
        public FtpServerOptions Options { get; }

        /// <summary>Indica si el servidor está escuchando.</summary>
        public bool IsRunning => _listener != null;

        /// <summary>
        /// Se dispara por cada evento. OJO: se invoca desde hilos secundarios;
        /// la interfaz debe sincronizar con su hilo (Control.BeginInvoke).
        /// </summary>
        public event EventHandler<FtpLogEntry>? LogEntryAdded;

        /// <summary>Crea el servidor con la configuración indicada.</summary>
        public FtpServerCore(FtpServerOptions options) => Options = options;

        /// <summary>Empieza a escuchar (requiere al menos un usuario configurado).</summary>
        public void Start()
        {
            if (IsRunning) return;
            if (Options.Users.Count == 0) throw new InvalidOperationException("No hay usuarios configurados");

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(Options.ListenAddress, Options.Port);
            _listener.Start();

            CancellationToken token = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(_listener, token));
            Log(null, FtpLogCategory.Connection, $"Servidor iniciado en el puerto {Options.Port}");
        }

        /// <summary>Detiene el servidor y cierra todas las sesiones.</summary>
        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { /* ya cerrado */ }
            _listener = null;

            foreach (FtpSession s in _sessions.Values) s.Dispose();
            _sessions.Clear();
            Log(null, FtpLogCategory.Connection, "Servidor detenido");
        }

        /// <summary>Devuelve una copia del estado de las sesiones activas.</summary>
        public List<FtpSessionInfo> GetSessions() =>
            _sessions.Values.Select(s => s.GetInfo()).OrderBy(i => i.Id).ToList();

        /// <summary>Expulsa una sesión activa cerrando su conexión. Devuelve false si no existe.</summary>
        /// <param name="id">Id de la sesión (ver <see cref="FtpSessionInfo.Id"/>).</param>
        public bool KickSession(int id)
        {
            if (!_sessions.TryRemove(id, out FtpSession? s)) return false;
            s.Dispose();
            return true;
        }

        /// <summary>Bucle que acepta clientes hasta que se cancela el token.</summary>
        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    // En .NET 5 no admite CancellationToken: Stop() del listener aborta la espera
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Log(null, FtpLogCategory.Error, $"Error al aceptar cliente: {ex.Message}");
                    await Task.Delay(200);
                    continue;
                }

                int id = Interlocked.Increment(ref _nextSessionId);
                var session = new FtpSession(id, client, this);
                _sessions[id] = session;

                _ = Task.Run(async () =>
                {
                    try { await session.RunAsync(ct); }
                    finally { _sessions.TryRemove(id, out _); }
                }, ct);
            }
        }

        /// <summary>Registra un evento y lo notifica a los suscriptores.</summary>
        internal void Log(FtpSession? s, FtpLogCategory category, string message,
                          string file = "", long bytes = 0, TimeSpan duration = default)
        {
            var entry = new FtpLogEntry
            {
                Category = category,
                SessionId = s?.Id ?? 0,
                RemoteEndPoint = s?.RemoteEndPoint ?? "",
                User = s?.User ?? "",
                Message = message,
                FileName = file,
                Bytes = bytes,
                Duration = duration
            };
            try { LogEntryAdded?.Invoke(this, entry); } catch { /* un suscriptor no debe tumbar el servidor */ }
        }

        /// <summary>Detiene el servidor y libera recursos.</summary>
        public void Dispose() => Stop();
    }
}
