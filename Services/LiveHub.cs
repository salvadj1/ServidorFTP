using Microsoft.AspNetCore.SignalR;

namespace SimpleFtp.Services
{
    /// <summary>
    /// Hub de SignalR solo de salida: el servidor emite los eventos "tick" (sesiones, velocidad y KPIs, cada segundo),
    /// "log" (cada evento nuevo) y "status" (arranque/parada). El acceso lo protege el middleware de autenticación.
    /// </summary>
    public sealed class LiveHub : Hub
    {
    }
}
