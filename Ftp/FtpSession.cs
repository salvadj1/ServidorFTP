using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFtp.Ftp
{
    /// <summary>
    /// Gestiona una conexión de cliente FTP: lee comandos, responde y mueve datos
    /// (modo pasivo PASV/EPSV y activo PORT). Todas las rutas quedan confinadas a la carpeta raíz.
    /// </summary>
    internal sealed class FtpSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly FtpServerCore _server;
        private readonly FtpServerOptions _opt;
        private StreamReader _reader = null!;
        private StreamWriter _writer = null!;

        private string _cwd = "/";            // directorio actual (virtual)
        private string? _pendingUser;         // usuario enviado con USER
        private FtpUser? _account;            // cuenta autenticada (define raíz y permisos)
        private bool _authenticated;
        private string? _renameFrom;          // ruta física pendiente de RNTO
        private long _restOffset;             // offset indicado por REST
        private TcpListener? _pasv;           // listener de datos pasivo
        private IPEndPoint? _activeEp;        // destino de datos activo
        private long _bytesIn, _bytesOut;
        private bool _disposed;
        private static readonly Random Rng = new Random(); // compartido entre sesiones (siempre con lock)

        public int Id { get; }
        public string RemoteEndPoint { get; }
        public string User { get; private set; } = "";
        public DateTime ConnectedAt { get; } = DateTime.Now;
        public string LastCommand { get; private set; } = "";

        /// <summary>Crea la sesión para un cliente ya conectado.</summary>
        public FtpSession(int id, TcpClient client, FtpServerCore server)
        {
            Id = id;
            _client = client;
            _server = server;
            _opt = server.Options;
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        }

        /// <summary>Devuelve una instantánea del estado actual de la sesión.</summary>
        public FtpSessionInfo GetInfo() => new()
        {
            Id = Id, RemoteEndPoint = RemoteEndPoint, User = User, ConnectedAt = ConnectedAt,
            LastCommand = LastCommand, CurrentDirectory = _cwd,
            BytesIn = Interlocked.Read(ref _bytesIn), BytesOut = Interlocked.Read(ref _bytesOut)
        };

        /// <summary>Bucle principal: lee y ejecuta comandos hasta QUIT o desconexión.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            _server.Log(this, FtpLogCategory.Connection, "Cliente conectado");
            try
            {
                NetworkStream ns = _client.GetStream();
                _reader = new StreamReader(ns, new UTF8Encoding(false));
                _writer = new StreamWriter(ns, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

                await ReplyAsync(220, "Servidor FTP listo");
                while (!ct.IsCancellationRequested)
                {
                    string? line = await _reader.ReadLineAsync(); // se interrumpe al cerrar el cliente (Dispose)
                    if (line == null) break;
                    if (!await HandleCommandAsync(line, ct)) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _server.Log(this, FtpLogCategory.Error, ex.Message); }
            finally
            {
                Dispose();
                _server.Log(this, FtpLogCategory.Connection, "Cliente desconectado");
            }
        }

        // ------------------------------------------------------------------ comandos

        /// <summary>Interpreta una línea de comando. Devuelve false para cerrar la sesión.</summary>
        private async Task<bool> HandleCommandAsync(string line, CancellationToken ct)
        {
            string cmd = line, arg = "";
            int sp = line.IndexOf(' ');
            if (sp > 0) { cmd = line[..sp]; arg = line[(sp + 1)..].Trim(); }
            cmd = cmd.ToUpperInvariant();
            LastCommand = cmd;

            // Sin login solo se permiten unos pocos comandos
            if (!_authenticated && cmd is not ("USER" or "PASS" or "QUIT" or "FEAT" or "SYST" or "OPTS" or "NOOP"))
            {
                await ReplyAsync(530, "Debe iniciar sesion");
                return true;
            }

            // Los usuarios de solo lectura no pueden modificar nada
            if (_account is { ReadOnly: true } &&
                cmd is "STOR" or "APPE" or "DELE" or "MKD" or "XMKD" or "RMD" or "XRMD" or "RNFR" or "RNTO")
            {
                _server.Log(this, FtpLogCategory.Error, $"Operación denegada (solo lectura): {cmd} {arg}");
                await ReplyAsync(550, "Usuario de solo lectura");
                return true;
            }

            try
            {
                switch (cmd)
                {
                    case "USER":
                        _pendingUser = arg;
                        await ReplyAsync(331, "Usuario correcto, envie la contrasena");
                        break;

                    case "PASS":
                        if (_pendingUser == null) { await ReplyAsync(503, "Envie USER primero"); break; }
                        FtpUser? account = FindAccount(_pendingUser, arg);
                        if (account != null)
                        {
                            Directory.CreateDirectory(account.RootPath);
                            _account = account;
                            _authenticated = true;
                            User = account.Username;
                            _cwd = "/";
                            _server.Log(this, FtpLogCategory.Connection, "Login correcto");
                            await ReplyAsync(230, "Sesion iniciada");
                        }
                        else
                        {
                            _server.Log(this, FtpLogCategory.Error, $"Login fallido (usuario '{_pendingUser}')");
                            await ReplyAsync(530, "Credenciales incorrectas");
                        }
                        break;

                    case "QUIT": await ReplyAsync(221, "Hasta luego"); return false;
                    case "SYST": await ReplyAsync(215, "UNIX Type: L8"); break;
                    case "NOOP": case "OPTS": await ReplyAsync(200, "OK"); break;
                    case "TYPE": case "MODE": case "STRU": await ReplyAsync(200, "OK"); break;
                    case "FEAT":
                        await _writer.WriteLineAsync("211-Extensiones:\r\n UTF8\r\n SIZE\r\n REST STREAM\r\n EPSV\r\n211 Fin");
                        break;

                    case "PWD": case "XPWD":
                        await ReplyAsync(257, $"\"{_cwd.Replace("\"", "\"\"")}\" es el directorio actual");
                        break;

                    case "CWD": case "XCWD":
                    {
                        string v = ToVirtual(arg);
                        if (Directory.Exists(ToPhysical(v))) { _cwd = v; await ReplyAsync(250, "Directorio cambiado"); }
                        else await ReplyAsync(550, "El directorio no existe");
                        break;
                    }
                    case "CDUP": case "XCUP":
                        _cwd = ToVirtual("..");
                        await ReplyAsync(250, "Directorio cambiado");
                        break;

                    case "PASV":
                    {
                        int port = StartPassive();
                        byte[] ip = GetPassiveAddress().GetAddressBytes();
                        await ReplyAsync(227, $"Modo pasivo ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{port / 256},{port % 256})");
                        break;
                    }
                    case "EPSV":
                    {
                        int port = StartPassive();
                        await ReplyAsync(229, $"Modo pasivo extendido (|||{port}|)");
                        break;
                    }
                    case "PORT":
                        await HandlePortAsync(arg);
                        break;

                    case "LIST": await SendListingAsync(arg, false, ct); break;
                    case "NLST": await SendListingAsync(arg, true, ct); break;
                    case "RETR": await RetrieveAsync(arg, ct); break;
                    case "STOR": await StoreAsync(arg, false, ct); break;
                    case "APPE": await StoreAsync(arg, true, ct); break;

                    case "REST":
                        if (long.TryParse(arg, out long off) && off >= 0) { _restOffset = off; await ReplyAsync(350, "Offset aceptado"); }
                        else await ReplyAsync(501, "Offset invalido");
                        break;

                    case "SIZE":
                    {
                        string p = ToPhysical(ToVirtual(arg));
                        if (File.Exists(p)) await ReplyAsync(213, new FileInfo(p).Length.ToString());
                        else await ReplyAsync(550, "Archivo no encontrado");
                        break;
                    }
                    case "DELE":
                    {
                        string v = ToVirtual(arg), p = ToPhysical(v);
                        if (!File.Exists(p)) { await ReplyAsync(550, "Archivo no encontrado"); break; }
                        File.Delete(p);
                        _server.Log(this, FtpLogCategory.File, "Archivo eliminado", v);
                        await ReplyAsync(250, "Archivo eliminado");
                        break;
                    }
                    case "MKD": case "XMKD":
                    {
                        string v = ToVirtual(arg);
                        Directory.CreateDirectory(ToPhysical(v));
                        _server.Log(this, FtpLogCategory.File, "Carpeta creada", v);
                        await ReplyAsync(257, $"\"{v}\" creado");
                        break;
                    }
                    case "RMD": case "XRMD":
                    {
                        string v = ToVirtual(arg), p = ToPhysical(v);
                        if (!Directory.Exists(p)) { await ReplyAsync(550, "Carpeta no encontrada"); break; }
                        Directory.Delete(p, false);
                        _server.Log(this, FtpLogCategory.File, "Carpeta eliminada", v);
                        await ReplyAsync(250, "Carpeta eliminada");
                        break;
                    }
                    case "RNFR":
                    {
                        string p = ToPhysical(ToVirtual(arg));
                        if (File.Exists(p) || Directory.Exists(p)) { _renameFrom = p; await ReplyAsync(350, "Indique el nuevo nombre"); }
                        else await ReplyAsync(550, "No existe");
                        break;
                    }
                    case "RNTO":
                    {
                        if (_renameFrom == null) { await ReplyAsync(503, "Envie RNFR primero"); break; }
                        string v = ToVirtual(arg), dest = ToPhysical(v);
                        if (File.Exists(_renameFrom)) File.Move(_renameFrom, dest);
                        else Directory.Move(_renameFrom, dest);
                        _server.Log(this, FtpLogCategory.File, "Renombrado", v);
                        _renameFrom = null;
                        await ReplyAsync(250, "Renombrado");
                        break;
                    }
                    default:
                        await ReplyAsync(502, "Comando no implementado");
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException)
            {
                _server.Log(this, FtpLogCategory.Error, $"Acceso denegado: {cmd} {arg}");
                await ReplyAsync(550, "Acceso denegado");
            }
            catch (Exception ex) when (ex is not IOException { InnerException: SocketException })
            {
                _server.Log(this, FtpLogCategory.Error, $"{cmd} {arg}: {ex.Message}");
                await ReplyAsync(550, "Error al ejecutar el comando");
            }
            return true;
        }

        /// <summary>
        /// Busca la cuenta que coincide con usuario/contraseña. El usuario "anonymous"
        /// (si existe en la lista) acepta cualquier contraseña. Devuelve null si no hay coincidencia.
        /// </summary>
        private FtpUser? FindAccount(string user, string pass)
        {
            foreach (FtpUser u in _opt.Users)
            {
                if (u.Username.Length == 0 || !u.Username.Equals(user, StringComparison.OrdinalIgnoreCase)) continue;
                if (u.Username.Equals("anonymous", StringComparison.OrdinalIgnoreCase) || u.Password == pass) return u;
            }
            return null;
        }

        // ------------------------------------------------------------------ rutas

        /// <summary>Convierte un argumento (relativo o absoluto) en una ruta virtual normalizada.</summary>
        private string ToVirtual(string arg)
        {
            string path = arg.StartsWith('/') ? arg : _cwd.TrimEnd('/') + "/" + arg;
            var parts = new List<string>();
            foreach (string seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            return "/" + string.Join('/', parts);
        }

        /// <summary>Convierte una ruta virtual en física, impidiendo salir de la carpeta raíz.</summary>
        private string ToPhysical(string virtualPath)
        {
            char sep = Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(_account!.RootPath).TrimEnd(sep);
            string full = Path.GetFullPath(Path.Combine(root, virtualPath.TrimStart('/').Replace('/', sep)));
            if (!(full + sep).StartsWith(root + sep, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Ruta fuera de la raiz");
            return full;
        }

        // ------------------------------------------------------------------ canal de datos

        /// <summary>Abre un listener pasivo en el rango configurado y devuelve su puerto.</summary>
        private int StartPassive()
        {
            CloseDataSetup();
            int min = _opt.PassivePortMin, range = _opt.PassivePortMax - min + 1;
            int start;
            lock (Rng) { start = Rng.Next(range); }
            for (int i = 0; i < range; i++)
            {
                int port = min + (start + i) % range;
                try
                {
                    var l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    _pasv = l;
                    return port;
                }
                catch (SocketException) { /* puerto ocupado, probar el siguiente */ }
            }
            throw new IOException("No hay puertos pasivos libres");
        }

        /// <summary>IP que se anuncia al cliente en PASV.</summary>
        private IPAddress GetPassiveAddress()
        {
            if (!string.IsNullOrWhiteSpace(_opt.PassiveExternalIp)) return IPAddress.Parse(_opt.PassiveExternalIp);
            return ((IPEndPoint)_client.Client.LocalEndPoint!).Address.MapToIPv4();
        }

        /// <summary>Procesa PORT (modo activo); solo acepta conectar a la IP del propio cliente.</summary>
        private async Task HandlePortAsync(string arg)
        {
            string[] p = arg.Split(',');
            if (p.Length != 6 || !p.All(x => int.TryParse(x, out _)))
            {
                await ReplyAsync(501, "Parametros invalidos");
                return;
            }
            var ip = IPAddress.Parse($"{p[0]}.{p[1]}.{p[2]}.{p[3]}");
            int port = int.Parse(p[4]) * 256 + int.Parse(p[5]);
            var remote = (IPEndPoint)_client.Client.RemoteEndPoint!;
            if (!ip.Equals(remote.Address.MapToIPv4()))
            {
                await ReplyAsync(504, "Solo se permite la IP del cliente");
                return;
            }
            CloseDataSetup();
            _activeEp = new IPEndPoint(ip, port);
            await ReplyAsync(200, "PORT correcto");
        }

        /// <summary>Indica si el cliente ya preparó un canal de datos (PASV/EPSV/PORT).</summary>
        private bool HasDataSetup => _pasv != null || _activeEp != null;

        /// <summary>Cancela la preparación del canal de datos actual.</summary>
        private void CloseDataSetup()
        {
            try { _pasv?.Stop(); } catch { }
            _pasv = null;
            _activeEp = null;
        }

        /// <summary>Establece la conexión de datos (espera 15 s como máximo). Devuelve null si falla.</summary>
        private async Task<TcpClient?> OpenDataAsync(CancellationToken ct)
        {
            try
            {
                if (_pasv != null) return await WithTimeoutAsync(_pasv.AcceptTcpClientAsync(), 15000, ct);
                if (_activeEp != null) return await WithTimeoutAsync(ConnectDataAsync(_activeEp), 15000, ct);
            }
            finally { CloseDataSetup(); }
            return null;
        }

        /// <summary>Conecta con el cliente (modo activo); libera el socket si la conexión falla.</summary>
        private static async Task<TcpClient> ConnectDataAsync(IPEndPoint ep)
        {
            var c = new TcpClient();
            try { await c.ConnectAsync(ep.Address, ep.Port); return c; }
            catch { c.Dispose(); throw; }
        }

        /// <summary>
        /// Espera una tarea con límite de tiempo (en .NET 5 Accept/Connect no admiten CancellationToken).
        /// Devuelve null si vence el tiempo; lanza OperationCanceledException si se cancela <paramref name="ct"/>.
        /// </summary>
        private static async Task<T?> WithTimeoutAsync<T>(Task<T> task, int milliseconds, CancellationToken ct) where T : class
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task delay = Task.Delay(milliseconds, delayCts.Token);
            Task finished = await Task.WhenAny(task, delay);
            if (finished == task)
            {
                delayCts.Cancel();
                return await task;
            }
            ct.ThrowIfCancellationRequested();
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); // evita excepciones sin observar
            return null;
        }

        /// <summary>Copia un flujo a otro actualizando los contadores de bytes de la sesión.</summary>
        private async Task<long> CopyAsync(Stream src, Stream dst, bool outgoing, CancellationToken ct)
        {
            byte[] buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                if (outgoing) Interlocked.Add(ref _bytesOut, n); else Interlocked.Add(ref _bytesIn, n);
            }
            return total;
        }

        // ------------------------------------------------------------------ transferencias

        /// <summary>LIST / NLST: envía el listado del directorio por el canal de datos.</summary>
        private async Task SendListingAsync(string arg, bool namesOnly, CancellationToken ct)
        {
            while (arg.StartsWith('-')) { int i = arg.IndexOf(' '); arg = i < 0 ? "" : arg[(i + 1)..].Trim(); }
            string phys = ToPhysical(ToVirtual(arg == "" ? "." : arg));

            var lines = new List<string>();
            if (Directory.Exists(phys))
                foreach (FileSystemInfo e in new DirectoryInfo(phys).EnumerateFileSystemInfos())
                    lines.Add(namesOnly ? e.Name : FormatUnix(e));
            else if (File.Exists(phys))
                lines.Add(namesOnly ? Path.GetFileName(phys) : FormatUnix(new FileInfo(phys)));
            else { await ReplyAsync(550, "No existe"); return; }

            if (!HasDataSetup) { await ReplyAsync(425, "Use PASV o PORT primero"); return; }
            await ReplyAsync(150, "Enviando listado");
            using TcpClient? data = await OpenDataAsync(ct);
            if (data == null) { await ReplyAsync(425, "No se pudo abrir la conexion de datos"); return; }

            await using (var w = new StreamWriter(data.GetStream(), new UTF8Encoding(false)) { NewLine = "\r\n" })
                foreach (string l in lines) await w.WriteLineAsync(l);
            await ReplyAsync(226, "Listado enviado");
        }

        /// <summary>Formatea una entrada al estilo "ls -l" de Unix (lo entienden todos los clientes).</summary>
        private static string FormatUnix(FileSystemInfo e)
        {
            bool dir = (e.Attributes & FileAttributes.Directory) != 0;
            long size = dir ? 0 : ((FileInfo)e).Length;
            string date = e.LastWriteTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
            return $"{(dir ? "drwxr-xr-x" : "-rw-r--r--")} 1 ftp ftp {size,12} {date} {e.Name}";
        }

        /// <summary>RETR: el cliente descarga un archivo.</summary>
        private async Task RetrieveAsync(string arg, CancellationToken ct)
        {
            string v = ToVirtual(arg), phys = ToPhysical(v);
            if (!File.Exists(phys)) { await ReplyAsync(550, "Archivo no encontrado"); return; }
            if (!HasDataSetup) { await ReplyAsync(425, "Use PASV o PORT primero"); return; }

            long offset = _restOffset; _restOffset = 0;
            await using var fs = new FileStream(phys, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);

            await ReplyAsync(150, "Iniciando descarga");
            using TcpClient? data = await OpenDataAsync(ct);
            if (data == null) { await ReplyAsync(425, "No se pudo abrir la conexion de datos"); return; }

            var sw = Stopwatch.StartNew();
            long total = 0;
            try
            {
                await using NetworkStream ns = data.GetStream();
                total = await CopyAsync(fs, ns, true, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                _server.Log(this, FtpLogCategory.Error, $"Descarga interrumpida: {ex.Message}", v);
                await ReplyAsync(426, "Transferencia interrumpida");
                return;
            }
            sw.Stop();
            _server.Log(this, FtpLogCategory.Transfer, "Descarga", v, total, sw.Elapsed);
            await ReplyAsync(226, "Descarga completada");
        }

        /// <summary>STOR / APPE: el cliente sube un archivo (o añade datos al final).</summary>
        private async Task StoreAsync(string arg, bool append, CancellationToken ct)
        {
            string v = ToVirtual(arg), phys = ToPhysical(v);
            if (!HasDataSetup) { await ReplyAsync(425, "Use PASV o PORT primero"); return; }

            long offset = _restOffset; _restOffset = 0;
            FileMode mode = append ? FileMode.Append : offset > 0 ? FileMode.OpenOrCreate : FileMode.Create;
            await using var fs = new FileStream(phys, mode, FileAccess.Write, FileShare.None, 81920, true);
            if (!append && offset > 0) fs.Seek(offset, SeekOrigin.Begin);

            await ReplyAsync(150, "Iniciando subida");
            using TcpClient? data = await OpenDataAsync(ct);
            if (data == null) { await ReplyAsync(425, "No se pudo abrir la conexion de datos"); return; }

            var sw = Stopwatch.StartNew();
            long total = 0;
            try
            {
                await using NetworkStream ns = data.GetStream();
                total = await CopyAsync(ns, fs, false, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                _server.Log(this, FtpLogCategory.Error, $"Subida interrumpida: {ex.Message}", v);
                await ReplyAsync(426, "Transferencia interrumpida");
                return;
            }
            sw.Stop();
            _server.Log(this, FtpLogCategory.Transfer, "Subida", v, total, sw.Elapsed);
            await ReplyAsync(226, "Subida completada");
        }

        // ------------------------------------------------------------------ utilidades

        /// <summary>Envía una respuesta de una línea al cliente.</summary>
        private Task ReplyAsync(int code, string text) => _writer.WriteLineAsync($"{code} {text}");

        /// <summary>Cierra la conexión de control y de datos (puede llamarse varias veces).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseDataSetup();
            try { _client.Close(); } catch { }
        }
    }
}
