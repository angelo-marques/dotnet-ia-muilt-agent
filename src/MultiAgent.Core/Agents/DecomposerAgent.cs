using System.Text.Json;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using MultiAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Agents;

public sealed class DecomposerAgent(
    IChatClient llm,
    IAgentMessageBus bus,
    ILogger<DecomposerAgent> logger,
    IRoslynCodeValidator roslynValidator,
    IAnythingLLMClient? rag = null)
    : BaseAgent(llm, bus, logger, rag)
{
    public override string Name => "DecomposerAgent";

    protected override string SystemPrompt => """
        Você é um Especialista em Decomposição de Sistemas e Análise de Dependências.

        DADO um arquivo grande, especificação ou arquitetura:

        1. IDENTIFIQUE todos os conceitos/entidades
        2. MAPEIE dependências (A usa B → A depende de B)
        3. DETECTE bounded contexts
        4. ORDENE em camadas topológicas (sem ciclos)
        5. GERE WorkItems para cada camada com contexto COMPLETO

        REGRAS ABSOLUTAS:
        - Uma camada NUNCA referencia uma camada acima dela
        - Interfaces ficam ANTES das implementações
        - Value Objects e Enums são SEMPRE Layer 0
        - Cada WorkItem deve ser implementável INDEPENDENTEMENTE
        - Inclua os contratos que cada WorkItem vai consumir

        FORMATO — APENAS JSON, SEM MARKDOWN:
        {
          "system_name": "OrderManagement",
          "total_layers": 5,
          "dependency_graph": {
            "Order": ["Customer", "Product", "OrderStatus"],
            "OrderService": ["IOrderRepository", "Order"]
          },
          "layers": [
            {
              "layer_number": 0,
              "name": "Primitives",
              "reason": "Sem dependências externas",
              "work_items": [
                {
                  "id": "WI-001",
                  "name": "OrderStatus",
                  "type": "enum",
                  "target_project": "MyApp.Domain",
                  "target_path": "Enums/OrderStatus.cs",
                  "dependencies": [],
                  "contracts_needed": [],
                  "full_specification": "Enum com: Pending, Confirmed, Shipped, Delivered, Cancelled",
                  "validation_criteria": "Todos os valores presentes e compilando",
                  "can_break_if_missing": ["Order", "OrderService"]
                }
              ]
            }
          ],
          "execution_plan": {
            "parallel_groups": [
              ["WI-001", "WI-002"],
              ["WI-003"],
              ["WI-004", "WI-005"]
            ],
            "reason": "Grupos sem dependência entre si podem rodar em paralelo"
          }
        }
        """;

    protected override async Task HandleMessageAsync(AgentMessage message, CancellationToken ct)
    {
        await PublishStatusAsync("📐 Analisando e decompondo o sistema em camadas...", ct);

        var content = message.Content;

        // Se é código C#, usa Roslyn para análise precisa
        var roslynContext = string.Empty;
        if (IsCSharpCode(content))
        {
            await PublishStatusAsync("🔬 Analisando estrutura com Roslyn...", ct);
            var structure = roslynValidator.AnalyzeDeep(content);
            roslynContext = $"""

                ANÁLISE ROSLYN DO CÓDIGO:
                Classes detectadas: {string.Join(", ", structure.Classes)}
                Interfaces detectadas: {string.Join(", ", structure.Interfaces)}
                Enums detectados: {string.Join(", ", structure.Enums)}
                Dependências reais: {JsonSerializer.Serialize(structure.Dependencies)}
                Ciclos potenciais detectados: {JsonSerializer.Serialize(structure.PotentialCycles)}
                """;

            if (structure.PotentialCycles.Count > 0)
                await PublishStatusAsync($"⚠️ {structure.PotentialCycles.Count} ciclo(s) potencial(is) detectado(s)!", ct);
        }

        var prompt = $"""
            {roslynContext}

            ESPECIFICAÇÃO / CÓDIGO / ARQUITETURA PARA DECOMPOR:
            {content}

            Decomponha em WorkItems ordenados topologicamente.
            Garanta que cada WorkItem possa ser implementado sem quebrar os outros.
            """;

        var response = await AskLlmWithRagAsync(prompt, "design-patterns", ct);
        var json = ExtractJson(response);

        // Valida o JSON antes de prosseguir
        DecompositionResult decomposition;
        try
        {
            decomposition = JsonSerializer.Deserialize<DecompositionResult>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Falha ao parsear decomposição");
            await PublishStatusAsync("❌ Erro na decomposição — retentando com formato simplificado...", ct);
            decomposition = BuildFallbackDecomposition(content);
        }

        var totalItems = decomposition.Layers.Sum(l => l.WorkItems.Count);
        await PublishStatusAsync(
            $"✅ Decomposição: {decomposition.TotalLayers} camadas, {totalItems} work items", ct);

        // Publica cada layer em sequência, grupos paralelos dentro de cada layer
        await Bus.PublishAsync(new AgentMessage(
            Guid.NewGuid().ToString(),
            Name,
            "Orchestrator",
            json,
            MessageType.Task,
            DateTime.UtcNow,
            message.CorrelationId), ct);
    }

    private static bool IsCSharpCode(string content) =>
        content.Contains("namespace ") ||
        content.Contains("public class ") ||
        content.Contains("using System") ||
        content.Contains("public interface ");

    private static DecompositionResult BuildFallbackDecomposition(string content)
    {
        // Decomposição mínima de fallback
        return new DecompositionResult(
            SystemName: "GeneratedSystem",
            TotalLayers: 3,
            DependencyGraph: new Dictionary<string, List<string>>(),
            Layers:
            [
                new DecompositionLayer(0, "Primitives", "Enums e value objects", [
                    new WorkItem("WI-001", "Domain Primitives", "classlib", 0,
                        "MyApp.Domain", "Primitives.cs",
                        [], [], content, "Compila sem erros", [])
                ]),
                new DecompositionLayer(1, "Domain", "Entidades do domínio", []),
                new DecompositionLayer(2, "Application", "Casos de uso", [])
            ],
            ExecutionPlan: new ExecutionPlan([["WI-001"]], "Fallback sequencial")
        );
    }
}
