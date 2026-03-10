using System.Text.Json;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Agents;

public abstract class BaseAgent
{
    protected readonly IChatClient Llm;
    protected readonly IAgentMessageBus Bus;
    protected readonly IAnythingLLMClient? Rag;
    protected readonly ILogger Logger;
    protected readonly List<ChatMessage> History = [];

    protected BaseAgent(
        IChatClient llm,
        IAgentMessageBus bus,
        ILogger logger,
        IAnythingLLMClient? rag = null)
    {
        Llm = llm;
        Bus = bus;
        Logger = logger;
        Rag = rag;
    }

    public abstract string Name { get; }
    protected abstract string SystemPrompt { get; }

    public async Task RunAsync(CancellationToken ct)
    {
        Logger.LogInformation("[{Agent}] Iniciado e aguardando mensagens...", Name);
        await Bus.PublishAsync(AgentMessage.Broadcast(Name, $"🤖 {Name} online", MessageType.Status), ct);

        await foreach (var message in Bus.SubscribeAsync(Name, ct))
        {
            try
            {
                await HandleMessageAsync(message, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Agent}] Erro ao processar mensagem", Name);
                await Bus.PublishAsync(AgentMessage.Create(
                    Name, message.FromAgent,
                    $"Erro: {ex.Message}", MessageType.Error, message.CorrelationId), ct);
            }
        }
    }

    protected abstract Task HandleMessageAsync(AgentMessage message, CancellationToken ct);

    protected async Task<string> AskLlmAsync(string userPrompt, CancellationToken ct)
    {
        List<ChatMessage> listaMensagens = History.Take(10).ToList(); // Limita contexto para últimas 10 mensagens
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
          //  AddMessages.Add(listaMensagens), // contexto das últimas mensagens
            new(ChatRole.User, userPrompt)
        };

        var response = await Llm.GetResponseAsync(messages, cancellationToken: ct);
        var text = response.Text ?? string.Empty;

        // Mantém histórico para contexto
        History.Add(new ChatMessage(ChatRole.User, userPrompt));
        History.Add(new ChatMessage(ChatRole.Assistant, text));

        return text;
    }

    protected async Task<string> AskLlmWithRagAsync(
        string userPrompt,
        string ragWorkspace,
        CancellationToken ct)
    {
        var ragContext = string.Empty;
        if (Rag != null)
        {
            try
            {
                ragContext = await Rag.QueryWorkspaceAsync(ragWorkspace, userPrompt);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{Agent}] RAG indisponível, continuando sem contexto", Name);
            }
        }

        var enrichedPrompt = string.IsNullOrEmpty(ragContext)
            ? userPrompt
            : $"""
               CONTEXTO DA BASE DE CONHECIMENTO:
               {ragContext}

               TAREFA:
               {userPrompt}
               """;

        return await AskLlmAsync(enrichedPrompt, ct);
    }

    protected static string ExtractJson(string text)
    {
        // Remove markdown code blocks
        text = text.Replace("```json", "").Replace("```", "").Trim();

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        // Tenta array
        start = text.IndexOf('[');
        end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    protected async Task PublishStatusAsync(string message, CancellationToken ct) =>
        await Bus.PublishAsync(AgentMessage.Broadcast(Name, message, MessageType.Status), ct);

    protected async Task SendToAsync(
        string targetAgent,
        string content,
        MessageType type,
        string? correlationId,
        CancellationToken ct) =>
        await Bus.PublishAsync(AgentMessage.Create(Name, targetAgent, content, type, correlationId), ct);
}
