using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleFtp.Api;
using SimpleFtp.Services;

namespace SimpleFtp
{
    /// <summary>Configuración de servicios y del pipeline HTTP del panel web.</summary>
    public sealed class Startup
    {
        /// <summary>Registra los servicios (ajustes, autenticación, servidor FTP y SignalR).</summary>
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
            services.AddSignalR();
            services.AddSingleton<FtpHostService>();
            services.AddHostedService(sp => sp.GetRequiredService<FtpHostService>());
            services.AddSingleton<AdminAuth>();
        }

        /// <summary>Define el pipeline: archivos estáticos, protección por login y endpoints.</summary>
        public void Configure(IApplicationBuilder app)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseRouting();

            // Todo /api/* y /hub exige sesión, salvo el login.
            app.Use(async (ctx, next) =>
            {
                PathString p = ctx.Request.Path;
                bool guarded = p.StartsWithSegments("/api") || p.StartsWithSegments("/hub");
                bool open = p.StartsWithSegments("/api/login");
                if (guarded && !open && !ctx.RequestServices.GetRequiredService<AdminAuth>().IsAuthenticated(ctx))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["X-Frame-Options"] = "DENY";
                await next();
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHub<LiveHub>("/hub");
                ApiEndpoints.Map(endpoints);
            });
        }
    }
}
