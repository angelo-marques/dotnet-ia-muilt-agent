namespace MultiAgent.Core.Bus;

public record AgentMessage(
    string Id,
    string FromAgent,
    string ToAgent,
    string Content,
    MessageType Type,
    DateTime Timestamp,
    string? CorrelationId = null,
    Dictionary<string, string>? Metadata = null
)
{
    public static AgentMessage Create(
        string from,
        string to,
        string content,
        MessageType type = MessageType.Task,
        string? correlationId = null) =>
        new(
            Guid.NewGuid().ToString(),
            from,
            to,
            content,
            type,
            DateTime.UtcNow,
            correlationId
        );

    public static AgentMessage Broadcast(string from, string content, MessageType type = MessageType.Status) =>
        Create(from, "broadcast", content, type);
}

public enum MessageType
{
    Task,
    Result,
    Error,
    Status,
    Review,
    Correction,
    Escalation,
    Complete
}
