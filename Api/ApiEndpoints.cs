using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SimpleFtp.Config;
using SimpleFtp.Ftp;
using SimpleFtp.Services;

namespace SimpleFtp.Api
{
    /// <summary>API REST del panel (todas las rutas /api/* exigen sesión salvo /api/login).</summary>
    public static class ApiEndpoints
    {
        /// <summary>Registra todas las rutas de la API.</summary>
        /// <param name="e">Constructor de endpoints de ASP.NET Core.</param>
        public static void Map(IEndpointRouteBuilder e)
        {
            // ---- sesión del panel
            e.MapPost("/api/login", async ctx =>
            {
                var auth = ctx.RequestServices.GetRequiredService<AdminAuth>();
                var req = await ctx.Request.ReadFromJsonAsync<LoginRequest>() ?? new LoginRequest();
                string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                if (!auth.Login(req.User ?? "", req.Password ?? "", ip, out string token, out string error))
                {
                    await Task.Delay(400); // frena la fuerza bruta
                    await Fail(ctx, 401, error);
                    return;
                }
                ctx.Response.Cookies.Append(AdminAuth.CookieName, token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddHours(12)
                });
                await ctx.Response.WriteAsJsonAsync(new { ok = true });
            });
            e.MapPost("/api/logout", async ctx =>
            {
                ctx.RequestServices.GetRequiredService<AdminAuth>().Logout(ctx);
                ctx.Response.Cookies.Delete(AdminAuth.CookieName);
                await ctx.Response.WriteAsJsonAsync(new { ok = true });
            });
            e.MapGet("/api/me", ctx => ctx.Response.WriteAsJsonAsync(new { ok = true })); // 200 si hay sesión (si no, el middleware da 401)

            // ---- estado y control del servidor FTP
            e.MapGet("/api/status", ctx => ctx.Response.WriteAsJsonAsync(Host(ctx).GetStatus()));
            e.MapPost("/api/server/{action}", async ctx =>
            {
                FtpHostService host = Host(ctx);
                try
                {
                    switch ((string?)ctx.Request.RouteValues["action"])
                    {
                        case "start": host.StartServer(); break;
                        case "stop": host.StopServer(); break;
                        case "restart": host.RestartServer(); break;
                        default: await Fail(ctx, 404, "Acción desconocida"); return;
                    }
                    await ctx.Response.WriteAsJsonAsync(host.GetStatus());
                }
                catch (Exception ex) { await Fail(ctx, 500, ex.Message); }
            });

            // ---- usuarios FTP
            e.MapGet("/api/users", ctx => ctx.Response.WriteAsJsonAsync(Host(ctx).GetUsers()));
            e.MapPost("/api/users", async ctx =>
            {
                var input = await ctx.Request.ReadFromJsonAsync<UserInput>() ?? new UserInput();
                await Result(ctx, Host(ctx).AddUser(input));
            });
            e.MapPut("/api/users/{name}", async ctx =>
            {
                var input = await ctx.Request.ReadFromJsonAsync<UserInput>() ?? new UserInput();
                await Result(ctx, Host(ctx).UpdateUser((string)ctx.Request.RouteValues["name"]!, input));
            });
            e.MapDelete("/api/users/{name}", ctx => Result(ctx, Host(ctx).DeleteUser((string)ctx.Request.RouteValues["name"]!)));

            // ---- sesiones
            e.MapGet("/api/sessions", ctx => ctx.Response.WriteAsJsonAsync(Host(ctx).GetSessions()));
            e.MapDelete("/api/sessions/{id:int}", async ctx =>
            {
                int id = int.Parse((string)ctx.Request.RouteValues["id"]!);
                await Result(ctx, Host(ctx).Kick(id) ? null : "La sesión ya no existe");
            });

            // ---- historial y estadísticas
            e.MapGet("/api/logs/recent", ctx => ctx.Response.WriteAsJsonAsync(Host(ctx).GetRecent(Int(ctx, "count", 50))));
            e.MapGet("/api/logs", ctx =>
            {
                var q = ctx.Request.Query;
                var list = Host(ctx).QueryLogs(Int(ctx, "days", 1), q["category"], q["user"], q["q"], Int(ctx, "max", 1000));
                return ctx.Response.WriteAsJsonAsync(list.Select(LogDto.From));
            });
            e.MapGet("/api/logs/export", async ctx =>
            {
                var q = ctx.Request.Query;
                var list = Host(ctx).QueryLogs(Int(ctx, "days", 1), q["category"], q["user"], q["q"], 10_000);
                string tmp = Path.Combine(Path.GetTempPath(), $"ftp_export_{Guid.NewGuid():N}.csv");
                try
                {
                    FtpLogStore.WriteCsv(tmp, list);
                    ctx.Response.ContentType = "text/csv; charset=utf-8";
                    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"ftp_{DateTime.Now:yyyy-MM-dd}.csv\"";
                    await ctx.Response.SendFileAsync(tmp);
                }
                finally { try { File.Delete(tmp); } catch { /* temporal */ } }
            });
            e.MapGet("/api/stats", ctx => ctx.Response.WriteAsJsonAsync(Host(ctx).GetStats(Int(ctx, "days", 1))));

            // ---- ajustes
            e.MapGet("/api/settings", ctx =>
            {
                AppSettings c = ctx.RequestServices.GetRequiredService<AppSettingsStore>().Current;
                return ctx.Response.WriteAsJsonAsync(new SettingsDto
                {
                    FtpPort = c.FtpPort, PassivePortMin = c.PassivePortMin, PassivePortMax = c.PassivePortMax,
                    PassiveExternalIp = c.PassiveExternalIp, AutoStartFtp = c.AutoStartFtp, RetentionDays = c.RetentionDays,
                    AdvertiseEnabled = c.AdvertiseEnabled, AdvertiseName = c.AdvertiseName,
                    BaseFolder = c.ResolveBaseFolder(), RestrictToBase = c.RestrictToBase, WebBindAddress = c.WebBindAddress, WebPort = c.WebPort
                });
            });
            e.MapPut("/api/settings", async ctx =>
            {
                var store = ctx.RequestServices.GetRequiredService<AppSettingsStore>();
                var s = await ctx.Request.ReadFromJsonAsync<SettingsDto>() ?? new SettingsDto();
                string? err = ValidateSettings(s);
                if (err != null) { await Fail(ctx, 400, err); return; }
                string? baseTxt = string.IsNullOrWhiteSpace(s.BaseFolder) ? null : s.BaseFolder.Trim();
                try { Directory.CreateDirectory(Path.GetFullPath(baseTxt ?? Path.Combine(AppPaths.DataDir, "FtpRoot"))); }
                catch (Exception ex) { await Fail(ctx, 400, "No se pudo crear la carpeta base: " + ex.Message); return; }
                bool webChanged;
                lock (store.SyncRoot)
                {
                    AppSettings c = store.Current;
                    webChanged = c.WebPort != s.WebPort || c.WebBindAddress != s.WebBindAddress;
                    c.FtpPort = s.FtpPort; c.PassivePortMin = s.PassivePortMin; c.PassivePortMax = s.PassivePortMax;
                    c.PassiveExternalIp = string.IsNullOrWhiteSpace(s.PassiveExternalIp) ? null : s.PassiveExternalIp.Trim();
                    c.AutoStartFtp = s.AutoStartFtp; c.RetentionDays = s.RetentionDays;
                    c.AdvertiseEnabled = s.AdvertiseEnabled; c.AdvertiseName = s.AdvertiseName.Trim();
                    c.BaseFolder = baseTxt; c.RestrictToBase = s.RestrictToBase;
                    c.WebBindAddress = s.WebBindAddress.Trim(); c.WebPort = s.WebPort;
                    store.Save();
                }
                try { Host(ctx).ApplySettings(); }
                catch (Exception ex) { await Fail(ctx, 500, "Ajustes guardados, pero el servidor FTP no pudo reiniciarse: " + ex.Message); return; }
                await ctx.Response.WriteAsJsonAsync(new { ok = true, restartRequired = webChanged });
            });
            e.MapPost("/api/password", async ctx =>
            {
                var p = await ctx.Request.ReadFromJsonAsync<PasswordChange>() ?? new PasswordChange();
                var auth = ctx.RequestServices.GetRequiredService<AdminAuth>();
                if (!auth.ChangePassword(p.Current ?? "", p.New ?? "", out string error)) { await Fail(ctx, 400, error); return; }
                ctx.Response.Cookies.Delete(AdminAuth.CookieName);
                await ctx.Response.WriteAsJsonAsync(new { ok = true }); // las sesiones se cierran: hay que volver a entrar
            });
        }

        /// <summary>Obtiene el servicio FTP del contenedor de dependencias.</summary>
        private static FtpHostService Host(HttpContext ctx) => ctx.RequestServices.GetRequiredService<FtpHostService>();

        /// <summary>Lee un parámetro entero de la query con valor por defecto.</summary>
        private static int Int(HttpContext ctx, string key, int def) => int.TryParse(ctx.Request.Query[key], out int v) ? v : def;

        /// <summary>Responde con un error JSON {error}.</summary>
        private static Task Fail(HttpContext ctx, int status, string message)
        {
            ctx.Response.StatusCode = status;
            return ctx.Response.WriteAsJsonAsync(new { error = message });
        }

        /// <summary>Responde ok si el mensaje es null; si no, 400 con el error.</summary>
        private static Task Result(HttpContext ctx, string? error) =>
            error == null ? ctx.Response.WriteAsJsonAsync(new { ok = true }) : Fail(ctx, 400, error);

        /// <summary>Valida los ajustes recibidos. Devuelve null si son correctos o el mensaje de error.</summary>
        private static string? ValidateSettings(SettingsDto s)
        {
            if (s.FtpPort is < 1 or > 65535 || s.WebPort is < 1 or > 65535) return "Puerto fuera de rango (1-65535)";
            if (s.FtpPort == s.WebPort) return "El puerto FTP y el del panel no pueden ser el mismo";
            if (s.PassivePortMin is < 1 or > 65535 || s.PassivePortMax is < 1 or > 65535 || s.PassivePortMin > s.PassivePortMax) return "Rango de puertos pasivos no válido";
            if (s.RetentionDays is < 1 or > 3650) return "Los días de retención deben estar entre 1 y 3650";
            if (!IPAddress.TryParse(s.WebBindAddress, out _)) return "Dirección de escucha del panel no válida";
            if (!string.IsNullOrWhiteSpace(s.PassiveExternalIp) && !IPAddress.TryParse(s.PassiveExternalIp, out _)) return "IP pasiva externa no válida";
            if (string.IsNullOrWhiteSpace(s.AdvertiseName) || s.AdvertiseName.Length > 40) return "Nombre de anuncio no válido (1-40 caracteres)";
            return null;
        }
    }
}
