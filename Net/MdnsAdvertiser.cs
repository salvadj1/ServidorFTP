using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFtp.Net
{
    /// <summary>Servicio a anunciar por mDNS (por ejemplo "_ftp._tcp" en el puerto 2121).</summary>
    public sealed class MdnsService
    {
        /// <summary>Tipo DNS-SD, p. ej. "_ftp._tcp" o "_http._tcp".</summary>
        public string Type { get; init; } = "";
        /// <summary>Nombre de la instancia visible en la red (p. ej. "Servidor FTP").</summary>
        public string Name { get; init; } = "";
        /// <summary>Puerto TCP del servicio.</summary>
        public int Port { get; init; }
        /// <summary>Entradas TXT opcionales con formato "clave=valor".</summary>
        public string[] Txt { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Anunciante mDNS / DNS-SD mínimo (RFC 6762/6763) sin dependencias externas.
    /// Responde a consultas y se anuncia periódicamente en 224.0.0.251:5353 para que
    /// el servidor aparezca en el navegador de red (Bonjour/Avahi) y se resuelva como
    /// <c>nombre.local</c>. Reutilizable en cualquier proyecto .NET.
    /// </summary>
    public sealed class MdnsAdvertiser : IDisposable
    {
        private const int MdnsPort = 5353;
        private const uint Ttl = 120;
        private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

        private readonly string _host;
        private readonly List<MdnsService> _services;
        private readonly Action<string>? _log;
        private Socket? _socket;
        private CancellationTokenSource? _cts;

        /// <summary>Crea el anunciante.</summary>
        /// <param name="hostName">Nombre de host sin ".local" (se sanea automáticamente).</param>
        /// <param name="services">Servicios a anunciar.</param>
        /// <param name="log">Callback opcional para mensajes de diagnóstico.</param>
        public MdnsAdvertiser(string hostName, IEnumerable<MdnsService> services, Action<string>? log = null)
        {
            _host = Sanitize(hostName);
            _services = services.Select(s => new MdnsService
            {
                Type = s.Type, Port = s.Port, Txt = s.Txt, Name = s.Name.Replace('.', '-')
            }).ToList();
            _log = log;
        }

        /// <summary>Abre el socket multicast y empieza a responder y anunciar. No lanza excepciones.</summary>
        public void Start()
        {
            if (_socket != null) return;
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                foreach (IPAddress ip in LocalAddresses())
                {
                    try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, ip)); }
                    catch { /* interfaz sin multicast */ }
                }
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                _socket = sock;
                _cts = new CancellationTokenSource();
                CancellationToken ct = _cts.Token;
                _ = Task.Run(() => ReceiveLoopAsync(sock, ct));
                _ = Task.Run(() => AnnounceLoopAsync(ct));
                _log?.Invoke($"mDNS activo: {_host}.local");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"No se pudo iniciar mDNS: {ex.Message}");
                Stop();
            }
        }

        /// <summary>Envía el mensaje de despedida (TTL 0) y cierra el socket.</summary>
        public void Stop()
        {
            Socket? sock = _socket;
            if (sock == null) return;
            try { Send(sock, BuildResponse(0)); } catch { /* ya cerrado */ }
            _cts?.Cancel();
            _socket = null;
            try { sock.Close(); } catch { /* ya cerrado */ }
        }

        /// <summary>Detiene el anuncio y libera recursos.</summary>
        public void Dispose() => Stop();

        // ------------------------------------------------------------------ bucles

        /// <summary>Re-anuncia los servicios al inicio y cada 60 s (las IP pueden cambiar).</summary>
        private async Task AnnounceLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Socket? sock = _socket;
                    if (sock != null) Send(sock, BuildResponse(Ttl));
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                }
            }
            catch { /* cancelado o socket cerrado */ }
        }

        /// <summary>Escucha consultas mDNS y responde si preguntan por nuestros nombres.</summary>
        private async Task ReceiveLoopAsync(Socket sock, CancellationToken ct)
        {
            var buffer = new byte[9000];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SocketReceiveFromResult r = await sock.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, remote);
                    if (MatchesQuery(buffer, r.ReceivedBytes)) Send(sock, BuildResponse(Ttl));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (ct.IsCancellationRequested) break; await Task.Delay(500); }
                catch { /* paquete mal formado: se ignora */ }
            }
        }

        // ------------------------------------------------------------------ consultas

        /// <summary>Indica si el paquete es una consulta que afecta a alguno de nuestros nombres.</summary>
        private bool MatchesQuery(byte[] b, int len)
        {
            if (len < 12 || (b[2] & 0x80) != 0) return false;      // demasiado corto o es una respuesta
            int qd = (b[4] << 8) | b[5];
            int pos = 12;
            for (int i = 0; i < qd; i++)
            {
                string name = ReadName(b, len, ref pos).ToLowerInvariant();
                if (pos + 4 > len) return false;
                pos += 4;                                            // tipo + clase
                if (name == "_services._dns-sd._udp.local" || name == _host.ToLowerInvariant() + ".local") return true;
                foreach (MdnsService s in _services)
                {
                    string type = (s.Type + ".local").ToLowerInvariant();
                    if (name == type || name == (s.Name + "." + s.Type + ".local").ToLowerInvariant()) return true;
                }
            }
            return false;
        }

        /// <summary>Lee un nombre DNS (con soporte de punteros de compresión) y avanza la posición.</summary>
        private static string ReadName(byte[] b, int len, ref int pos)
        {
            var sb = new StringBuilder();
            int p = pos, jumps = 0;
            bool jumped = false;
            while (p < len)
            {
                int l = b[p];
                if (l == 0) { p++; break; }
                if ((l & 0xC0) == 0xC0)
                {
                    if (p + 1 >= len || ++jumps > 16) break;
                    int target = ((l & 0x3F) << 8) | b[p + 1];
                    if (!jumped) pos = p + 2;
                    jumped = true;
                    p = target;
                    continue;
                }
                p++;
                if (p + l > len) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(b, p, l));
                p += l;
            }
            if (!jumped) pos = p;
            return sb.ToString();
        }

        // ------------------------------------------------------------------ respuestas

        /// <summary>Construye un paquete de respuesta con todos nuestros registros.</summary>
        /// <param name="ttl">Tiempo de vida en segundos (0 = despedida).</param>
        private byte[] BuildResponse(uint ttl)
        {
            var records = new List<byte[]>();
            string hostFqdn = _host + ".local";

            foreach (MdnsService s in _services)
            {
                string type = s.Type + ".local";
                string instance = s.Name + "." + type;

                records.Add(Record("_services._dns-sd._udp.local", 12, 1, ttl, NameBytes(type)));
                records.Add(Record(type, 12, 1, ttl, NameBytes(instance)));

                var srv = new List<byte> { 0, 0, 0, 0, (byte)(s.Port >> 8), (byte)s.Port };
                srv.AddRange(NameBytes(hostFqdn));
                records.Add(Record(instance, 33, 0x8001, ttl, srv.ToArray()));

                var txt = new List<byte>();
                string[] entries = s.Txt.Length > 0 ? s.Txt : new[] { "" };
                foreach (string e in entries)
                {
                    byte[] eb = Encoding.UTF8.GetBytes(e);
                    int n = Math.Min(eb.Length, 255);
                    txt.Add((byte)n);
                    txt.AddRange(eb.Take(n));
                }
                records.Add(Record(instance, 16, 0x8001, ttl, txt.ToArray()));
            }

            foreach (IPAddress ip in LocalAddresses())
                records.Add(Record(hostFqdn, 1, 0x8001, ttl, ip.GetAddressBytes()));

            var packet = new List<byte> { 0, 0, 0x84, 0, 0, 0, (byte)(records.Count >> 8), (byte)records.Count, 0, 0, 0, 0 };
            foreach (byte[] r in records) packet.AddRange(r);
            return packet.ToArray();
        }

        /// <summary>Codifica un registro DNS completo (nombre, tipo, clase, TTL y datos).</summary>
        private static byte[] Record(string name, ushort type, ushort cls, uint ttl, byte[] data)
        {
            var r = new List<byte>(NameBytes(name));
            r.AddRange(new[] { (byte)(type >> 8), (byte)type, (byte)(cls >> 8), (byte)cls });
            r.AddRange(new[] { (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl });
            r.AddRange(new[] { (byte)(data.Length >> 8), (byte)data.Length });
            r.AddRange(data);
            return r.ToArray();
        }

        /// <summary>Codifica un nombre DNS como etiquetas con prefijo de longitud (sin compresión).</summary>
        private static byte[] NameBytes(string name)
        {
            var r = new List<byte>();
            foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] lb = Encoding.UTF8.GetBytes(label);
                int n = Math.Min(lb.Length, 63);
                r.Add((byte)n);
                r.AddRange(lb.Take(n));
            }
            r.Add(0);
            return r.ToArray();
        }

        // ------------------------------------------------------------------ red

        /// <summary>Envía el paquete al grupo multicast por cada interfaz IPv4 activa.</summary>
        private static void Send(Socket sock, byte[] data)
        {
            var dest = new IPEndPoint(Group, MdnsPort);
            foreach (IPAddress ip in LocalAddresses())
            {
                try
                {
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                    sock.SendTo(data, dest);
                }
                catch { /* interfaz sin salida multicast */ }
            }
        }

        /// <summary>Direcciones IPv4 de las interfaces activas con multicast (sin loopback ni APIPA).</summary>
        private static List<IPAddress> LocalAddresses()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.Address.ToString().StartsWith("169.254.")) continue;
                        list.Add(ua.Address);
                    }
                }
            }
            catch { /* sin acceso a las interfaces */ }
            return list;
        }

        /// <summary>Deja el nombre de host solo con letras, números y guiones.</summary>
        private static string Sanitize(string name)
        {
            string s = new string(name.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray()).Trim('-');
            return s.Length == 0 ? "ftp-server" : s;
        }
    }
}
