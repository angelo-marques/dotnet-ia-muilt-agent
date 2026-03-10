using System.Text.Json;
using MultiAgent.Core.Agents;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using MultiAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Orchestration;

public enum EscalationLevel
{
    Normal,       // Tentativa 1-2: corrige normalmente
    Rewrite,      // Tentativa 3-4: reescreve do zero com outra abordagem
    Simplify,     // Tentativa 5-6: simplifica, remove abstrações
    RagAssisted,  // Tentativa 7-8: busca exemplos no AnythingLLM
    ArchitectHelp // Tentativa 9+: redesenha a estrutura
}

public class SmartRetryOrchestrator(
    IAgentMessageBus bus,
    IValidationPipeline pipeline,
    ILogger<SmartRetryOrchestrator> logger,
    IAnythingLLMClient? rag = null)
{
    private const int MaxAttempts = 10;

    public async Task<OrchestratorResult> ExecuteWorkItemAsync(
        WorkItem workItem,
        List<GeneratedFile> existingContracts,
        string architectureDecisions,
        IChatClient llm,
        CancellationToken ct)
    {
        var attemptHistory = new List<AttemptRecord>();
        var errorOccurrences = new Dictionary<string, int>();
        AgentResult? lastResult = null;
        AgentResult? bestResult = null;
        double bestScore = -1;

        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator",
            $"🚀 Iniciando [{workItem.Id}] {workItem.Name} — máximo {MaxAttempts} tentativas",
            MessageType.Status), ct);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var escalation = DetermineEscalation(attempt, errorOccurrences);

            await bus.PublishAsync(AgentMessage.Broadcast(
                "Orchestrator",
                $"🔄 [{workItem.Id}] Tentativa {attempt}/{MaxAttempts} — Estratégia: {escalation}",
                MessageType.Status), ct);

            // Monta contexto com estratégia de escalada
            var context = new CodeGenerationContext(
                WorkItem: workItem,
                AlreadyGeneratedContracts: existingContracts,
                ArchitectureDecisions: architectureDecisions,
                RagContext: await GetRagContextAsync(workItem, escalation, lastResult, ct)
            );

            var enrichedContent = BuildEnrichedPrompt(context, escalation, lastResult, attempt);

            // Dispara geração via bus (CodeGenerator → Roslyn → Reviewer → Test)
            var tcs = new TaskCompletionSource<AgentResult>();
            var correlationId = $"{workItem.Id}-attempt-{attempt}";

            // Listener temporário para capturar resultado desta tentativa
            Func<AgentMessage, Task> listener = msg =>
            {
                if (msg.CorrelationId == correlationId && msg.Type == MessageType.Complete)
                {
                    var result = new AgentResult(true, msg.FromAgent, msg.Content,
                        Files: TryExtractFiles(msg.Content));
                    tcs.TrySetResult(result);
                }
                else if (msg.CorrelationId == correlationId && msg.Type == MessageType.Error)
                {
                    tcs.TrySetResult(AgentResult.Fail("Agent", msg.Content));
                }
                return Task.CompletedTask;
            };

            bus.OnMessage += listener;

            try
            {
                await bus.PublishAsync(AgentMessage.Create(
                    "Orchestrator", "CodeGeneratorAgent",
                    enrichedContent, MessageType.Task, correlationId), ct);

                // Timeout por tentativa para evitar travar
                var timeout = TimeSpan.FromMinutes(5);
                var resultTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));

                if (resultTask == tcs.Task)
                    lastResult = await tcs.Task;
                else
                {
                    lastResult = AgentResult.Fail("Orchestrator", "Timeout na tentativa");
                    logger.LogWarning("[{WI}] Timeout na tentativa {Attempt}", workItem.Id, attempt);
                }
            }
            finally
            {
                bus.OnMessage -= listener;
            }

            // Valida com pipeline completo
            var pipelineResult = await pipeline.RunAsync(lastResult!, ct);

            attemptHistory.Add(new AttemptRecord(attempt, escalation, pipelineResult.Passed,
                pipelineResult.FailedGate, pipelineResult.Feedback));

            if (pipelineResult.Passed)
            {
                await bus.PublishAsync(AgentMessage.Broadcast(
                    "Orchestrator",
                    $"✅ [{workItem.Id}] SUCESSO na tentativa {attempt}!",
                    MessageType.Complete), ct);

                return new OrchestratorResult(true, lastResult!, attempt, attemptHistory);
            }

            // Rastreia erros para detectar loop
            if (pipelineResult.FailedGate == "Compilation" && lastResult?.Errors != null)
            {
                foreach (var err in lastResult.Errors)
                {
                    errorOccurrences.TryGetValue(err.Code, out var count);
                    errorOccurrences[err.Code] = count + 1;
                }
            }

            // Guarda melhor resultado parcial
            if (lastResult?.ReviewScore > bestScore)
            {
                bestScore = lastResult.ReviewScore;
                bestResult = lastResult;
            }

            // Se preso no mesmo erro 3x → força escalada imediata
            if (errorOccurrences.Any(e => e.Value >= 3))
            {
                await bus.PublishAsync(AgentMessage.Broadcast(
                    "Orchestrator",
                    $"⚠️ [{workItem.Id}] Detectado loop! Forçando redesenho arquitetural...",
                    MessageType.Escalation), ct);
                errorOccurrences.Clear();
            }

            // Aguarda um breve intervalo antes da próxima tentativa
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }

        // Esgotou tentativas — usa o melhor resultado
        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator",
            $"⚠️ [{workItem.Id}] Máximo de tentativas atingido. Usando melhor resultado (score: {bestScore:F1})",
            MessageType.Status), ct);

        return new OrchestratorResult(false, bestResult ?? lastResult!, MaxAttempts, attemptHistory);
    }

    private EscalationLevel DetermineEscalation(
        int attempt,
        Dictionary<string, int> errorOccurrences)
    {
        if (errorOccurrences.Any(e => e.Value >= 3))
            return EscalationLevel.ArchitectHelp;

        return attempt switch
        {
            <= 2 => EscalationLevel.Normal,
            <= 4 => EscalationLevel.Rewrite,
            <= 6 => EscalationLevel.Simplify,
            <= 8 => EscalationLevel.RagAssisted,
            _    => EscalationLevel.ArchitectHelp
        };
    }

    private string BuildEnrichedPrompt(
        CodeGenerationContext ctx,
        EscalationLevel escalation,
        AgentResult? lastResult,
        int attempt)
    {
        var strategyNote = escalation switch
        {
            EscalationLevel.Rewrite => """
                ⚠️ ATENÇÃO ESCALADA: Sua implementação anterior FALHOU.
                REESCREVA COMPLETAMENTE com uma abordagem DIFERENTE.
                NÃO repita os mesmos padrões. Use uma estratégia alternativa.
                """,
            EscalationLevel.Simplify => """
                ⚠️ ATENÇÃO ESCALADA: Simplifique drasticamente.
                Implemente APENAS o núcleo funcional essencial.
                Remova todas as abstrações e otimizações.
                Faça funcionar primeiro, otimize depois.
                """,
            EscalationLevel.RagAssisted => """
                ⚠️ ATENÇÃO ESCALADA: Use os exemplos da base de conhecimento.
                Siga EXATAMENTE os padrões encontrados para este tipo de componente.
                """,
            EscalationLevel.ArchitectHelp => """
                ⚠️ REDESENHO ARQUITETURAL: A abordagem atual não funciona.
                Repense a estrutura do componente do zero.
                Considere uma implementação muito mais simples e direta.
                Use o mínimo de dependências possível.
                """,
            _ => string.Empty
        };

        var lastErrors = lastResult?.Errors != null && lastResult.Errors.Count > 0
            ? $"\nERROS DA TENTATIVA ANTERIOR:\n{JsonSerializer.Serialize(lastResult.Errors)}"
            : string.Empty;

        return $"""
            {strategyNote}
            TENTATIVA: {attempt}/{MaxAttempts}
            {lastErrors}

            CONTEXTO:
            {JsonSerializer.Serialize(ctx)}
            """;
    }

    private async Task<string> GetRagContextAsync(
        WorkItem workItem,
        EscalationLevel escalation,
        AgentResult? lastResult,
        CancellationToken ct)
    {
        if (rag == null || escalation < EscalationLevel.RagAssisted)
            return string.Empty;

        try
        {
            var query = escalation == EscalationLevel.ArchitectHelp
                ? $"simple implementation of {workItem.Type}"
                : $"C# {workItem.Type} implementation {workItem.Name} error: {lastResult?.LastError}";

            return await rag.QueryWorkspaceAsync("dotnet-best-practices", query);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<GeneratedFile>? TryExtractFiles(string content)
    {
        try
        {
            var json = content[content.IndexOf('{')..];
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("files", out var filesEl))
            {
                return JsonSerializer.Deserialize<List<GeneratedFile>>(filesEl.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch { }
        return null;
    }
}

public record OrchestratorResult(
    bool Success,
    AgentResult Result,
    int AttemptsUsed,
    List<AttemptRecord> History
);

public record AttemptRecord(
    int Attempt,
    EscalationLevel Escalation,
    bool Passed,
    string? FailedGate,
    string? Feedback
);
