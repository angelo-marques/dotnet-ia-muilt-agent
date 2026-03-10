using Microsoft.AspNetCore.SignalR;

namespace MultiAgent.Host.Hubs;

public sealed class AgentHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("AgentMessage", new
        {
            from = "System",
            to = "broadcast",
            content = "Conectado ao hub de agentes! Aguardando mensagens...",
            type = "Status",
            timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff")
        });

        await base.OnConnectedAsync();
    }
}
