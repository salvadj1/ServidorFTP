using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleFtp.Config;
using SimpleFtp.Services;

namespace SimpleFtp
{
    /// <summary>
    /// Punto de entrada. Se ejecuta como aplicación de consola, como servicio de Windows
    /// (sc create) o como servicio systemd en Linux; el panel web se sirve con Kestrel.
    /// </summary>
    internal static class Program
    {
        /// <summary>Crea y ejecuta el host.</summary>
        public static void Main(string[] args)
        {
            var settings = new AppSettingsStore();
            IHost host = CreateHostBuilder(args, settings).Build();
            // Crea AdminAuth ya (no en la primera petición) para generar y anotar la contraseña inicial al arrancar.
            host.Services.GetRequiredService<AdminAuth>();
            host.Run();
        }

        /// <summary>Configura el host (servicio Windows/systemd, Kestrel y Startup).</summary>
        private static IHostBuilder CreateHostBuilder(string[] args, AppSettingsStore settings) =>
            Host.CreateDefaultBuilder(args)
                .UseContentRoot(AppContext.BaseDirectory)
                .UseWindowsService()
                .UseSystemd()
                .ConfigureServices(s => s.AddSingleton(settings))
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseStartup<Startup>();
                    web.UseUrls($"http://{settings.Current.WebBindAddress}:{settings.Current.WebPort}");
                });
    }
}
