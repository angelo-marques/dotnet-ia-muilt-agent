using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Bus;

public interface IAgentMessageBus
{
    Task PublishAsync(AgentMessage message, CancellationToken ct = default);
    IAsyncEnumerable<AgentMessage> SubscribeAsync(string agentName, CancellationToken ct);
    event Func<AgentMessage, Task>? OnMessage;
}

public sealed class AgentMessageBus : IAgentMessageBus, IAsyncDisposable
{
    private readonly Channel<AgentMessage> _channel =
        Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false
        });

    private readonly Dictionary<string, Channel<AgentMessage>> _agentChannels = new();
    private readonly Lock _lock = new();
    private readonly ILogger<AgentMessageBus> _logger;

    public event Func<AgentMessage, Task>? OnMessage;

    public AgentMessageBus(ILogger<AgentMessageBus> logger)
    {
        _logger = logger;
        _ = ProcessMessagesAsync();
    }

    public async Task PublishAsync(AgentMessage message, CancellationToken ct = default)
    {
        _logger.LogDebug("[BUS] {From} → {To}: {Type}", message.FromAgent, message.ToAgent, message.Type);
        await _channel.Writer.WriteAsync(message, ct);
    }

    public async IAsyncEnumerable<AgentMessage> SubscribeAsync(
        string agentName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var agentChannel = GetOrCreateAgentChannel(agentName);

        await foreach (var msg in agentChannel.Reader.ReadAllAsync(ct))
            yield return msg;
    }

    private Channel<AgentMessage> GetOrCreateAgentChannel(string agentName)
    {
        lock (_lock)
        {
            if (!_agentChannels.TryGetValue(agentName, out var ch))
            {
                ch = Channel.CreateUnbounded<AgentMessage>();
                _agentChannels[agentName] = ch;
            }
            return ch;
        }
    }

    private async Task ProcessMessagesAsync()
    {
        await foreach (var message in _channel.Reader.ReadAllAsync())
        {
            // Notifica listeners externos (SignalR, console, etc.)
            if (OnMessage != null)
                await OnMessage.Invoke(message);

            // Roteia para agente específico ou broadcast
            lock (_lock)
            {
                if (message.ToAgent == "broadcast")
                {
                    foreach (var ch in _agentChannels.Values)
                        ch.Writer.TryWrite(message);
                }
                else if (_agentChannels.TryGetValue(message.ToAgent, out var targetCh))
                {
                    targetCh.Writer.TryWrite(message);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        foreach (var ch in _agentChannels.Values)
            ch.Writer.Complete();
        await Task.CompletedTask;
    }
}
