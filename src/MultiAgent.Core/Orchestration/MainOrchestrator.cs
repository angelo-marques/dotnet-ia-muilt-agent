using System.Text.Json;
using System.Threading.Tasks.Dataflow;
using MultiAgent.Core.Agents;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Orchestration;

public interface IMainOrchestrator
{
    Task<SystemGenerationResult> GenerateSystemAsync(string input, CancellationToken ct);
    Task<SystemGenerationResult> GenerateFromFileAsync(string filePath, CancellationToken ct);
}

public sealed class MainOrchestrator(
    IAgentMessageBus bus,
    ArchitectAgent architectAgent,
    DecomposerAgent decomposerAgent,
    CodeGeneratorAgent codeGeneratorAgent,
    RoslynValidatorAgent roslynAgent,
    ReviewerAgent reviewerAgent,
    TestAgent testAgent,
    SmartRetryOrchestrator retryOrchestrator,
    IValidationPipeline pipeline,
    ILogger<MainOrchestrator> logger) : IMainOrchestrator
{
    private readonly List<GeneratedFile> _allGeneratedFiles = [];
    private string _architectureDecisions = string.Empty;
    private DecompositionResult? _decomposition;

    public async Task<SystemGenerationResult> GenerateFromFileAsync(
        string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Arquivo não encontrado: {filePath}");

        var content = await File.ReadAllTextAsync(filePath, ct);
        var fileName = Path.GetFileName(filePath);

        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator",
            $"📄 Arquivo carregado: {fileName} ({content.Length:N0} chars)",
            MessageType.Status), ct);

        return await GenerateSystemAsync(content, ct);
    }

    public async Task<SystemGenerationResult> GenerateSystemAsync(
        string input, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var results = new List<OrchestratorResult>();

        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator", "🎬 Sistema multi-agente iniciado", MessageType.Status), ct);

        // ─── FASE 1: Arquitetura ───────────────────────────────────────────────
        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator", "📋 FASE 1: Definindo arquitetura...", MessageType.Status), ct);

        var archResult = await RunAgentWithRetryAsync(
            "ArchitectAgent", input, "DecomposerAgent", ct);
        _architectureDecisions = archResult;

        // ─── FASE 2: Decomposição ─────────────────────────────────────────────
        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator", "📐 FASE 2: Decompondo em work items...", MessageType.Status), ct);

        var decompositionJson = await RunAgentWithRetryAsync(
            "DecomposerAgent", $"ARQUITETURA:\n{_architectureDecisions}\n\nINPUT:\n{input}",
            "Orchestrator", ct);

        try
        {
            _decomposition = JsonSerializer.Deserialize<DecompositionResult>(
                ExtractJson(decompositionJson),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao parsear decomposição");
            _decomposition = BuildMinimalDecomposition(input);
        }

        var totalItems = _decomposition!.Layers.Sum(l => l.WorkItems.Count);
        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator",
            $"✅ Decomposição: {_decomposition.TotalLayers} camadas, {totalItems} work items",
            MessageType.Status), ct);

        // ─── FASE 3: Geração por camadas ──────────────────────────────────────
        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator", "⚙️ FASE 3: Gerando código por camadas...", MessageType.Status), ct);

        foreach (var layer in _decomposition.Layers.OrderBy(l => l.LayerNumber))
        {
            await bus.PublishAsync(AgentMessage.Broadcast(
                "Orchestrator",
                $"🔨 Layer {layer.LayerNumber}: {layer.Name} ({layer.WorkItems.Count} items)",
                MessageType.Status), ct);

            // WorkItems dentro de uma layer podem rodar em paralelo (se não têm deps entre si)
            var parallelGroups = GroupItemsForParallelExecution(layer.WorkItems);

            foreach (var group in parallelGroups)
            {
                var groupTasks = group.Select(wi =>
                    retryOrchestrator.ExecuteWorkItemAsync(
                        wi,
                        new List<GeneratedFile>(_allGeneratedFiles),
                        _architectureDecisions,
                        null!, // IChatClient vem da injeção de dependência
                        ct)).ToArray();

                var groupResults = await Task.WhenAll(groupTasks);

                foreach (var result in groupResults)
                {
                    results.Add(result);
                    if (result.Result.Files != null)
                        _allGeneratedFiles.AddRange(result.Result.Files);
                }
            }
        }

        // ─── FASE 4: Validação final do sistema completo ──────────────────────
        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator", "🔍 FASE 4: Validação final do sistema completo...", MessageType.Status), ct);

        var finalValidation = await pipeline.RunAsync(
            new AgentResult(true, "Orchestrator", "Sistema completo",
                Files: _allGeneratedFiles), ct);

        var duration = DateTime.UtcNow - startTime;
        var successCount = results.Count(r => r.Success);

        await bus.PublishAsync(AgentMessage.Broadcast(
            "Orchestrator",
            $"🏁 Geração concluída em {duration.TotalMinutes:F1} min — " +
            $"{successCount}/{results.Count} work items bem-sucedidos",
            MessageType.Complete), ct);

        return new SystemGenerationResult(
            Success: finalValidation.Passed,
            SystemName: _decomposition.SystemName,
            GeneratedFiles: _allGeneratedFiles,
            WorkItemResults: results,
            Duration: duration,
            TotalAttempts: results.Sum(r => r.AttemptsUsed)
        );
    }

    private async Task<string> RunAgentWithRetryAsync(
        string agentName, string input, string expectedResponseFrom, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>();
        var correlationId = Guid.NewGuid().ToString();

        Func<AgentMessage, Task> listener = msg =>
        {
            if (msg.CorrelationId == correlationId &&
                (msg.ToAgent == "Orchestrator" || msg.ToAgent == expectedResponseFrom) &&
                msg.Type is MessageType.Task or MessageType.Result or MessageType.Complete)
            {
                tcs.TrySetResult(msg.Content);
            }
            return Task.CompletedTask;
        };

        bus.OnMessage += listener;

        try
        {
            await bus.PublishAsync(AgentMessage.Create(
                "Orchestrator", agentName, input, MessageType.Task, correlationId), ct);

            var timeout = Task.Delay(TimeSpan.FromMinutes(3), ct);
            var completed = await Task.WhenAny(tcs.Task, timeout);

            return completed == tcs.Task ? await tcs.Task : $"Timeout aguardando {agentName}";
        }
        finally
        {
            bus.OnMessage -= listener;
        }
    }

    private static List<List<WorkItem>> GroupItemsForParallelExecution(List<WorkItem> items)
    {
        // Itens sem dependências entre si dentro da mesma layer → paralelo
        var groups = new List<List<WorkItem>>();
        var remaining = new List<WorkItem>(items);

        while (remaining.Count > 0)
        {
            var group = remaining
                .Where(item => !item.Dependencies.Any(dep =>
                    remaining.Any(r => r.Id == dep)))
                .ToList();

            if (group.Count == 0)
            {
                // Dependência circular ou todos dependem de algo — processa um por vez
                groups.Add([remaining[0]]);
                remaining.RemoveAt(0);
            }
            else
            {
                groups.Add(group);
                foreach (var item in group)
                    remaining.Remove(item);
            }
        }

        return groups;
    }

    private static string ExtractJson(string text)
    {
        text = text.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static DecompositionResult BuildMinimalDecomposition(string input) =>
        new("GeneratedSystem", 1, new Dictionary<string, List<string>>(),
        [
            new DecompositionLayer(0, "Core", "Implementação principal", [
                new WorkItem("WI-001", "Core Implementation", "classlib", 0,
                    "MyApp.Core", "Core.cs", [], [], input,
                    "Compila sem erros", [])
            ])
        ],
        new ExecutionPlan([["WI-001"]], "Sequencial"));
}

public record SystemGenerationResult(
    bool Success,
    string SystemName,
    List<GeneratedFile> GeneratedFiles,
    List<OrchestratorResult> WorkItemResults,
    TimeSpan Duration,
    int TotalAttempts
);
