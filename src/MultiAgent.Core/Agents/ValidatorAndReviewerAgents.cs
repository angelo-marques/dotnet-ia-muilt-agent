using System.Text.Json;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using MultiAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Agents;

// ─── RoslynValidatorAgent ─────────────────────────────────────────────────────

public sealed class RoslynValidatorAgent(
    IChatClient llm,
    IAgentMessageBus bus,
    ILogger<RoslynValidatorAgent> logger,
    IRoslynCodeValidator roslynValidator,
    IAnythingLLMClient? rag = null)
    : BaseAgent(llm, bus, logger, rag)
{
    public override string Name => "RoslynValidatorAgent";

    protected override string SystemPrompt => """
        Você é um Especialista em Análise Estática com acesso ao compilador Roslyn.

        VOCÊ RECEBE:
        - Código gerado com resultado REAL da compilação Roslyn
        - Análise de AST (classes, métodos, dependências detectadas)

        SUAS RESPONSABILIDADES:
        - Interpretar erros de compilação e gerar correções PRECISAS
        - Identificar code smells via análise da AST
        - Detectar dependências circulares
        - Verificar cobertura de casos null

        FORMATO — APENAS JSON, SEM MARKDOWN:
        {
          "compilation_status": "success",
          "errors_analyzed": [],
          "corrections": [
            {
              "file": "Order.cs",
              "line": 42,
              "original": "var status = OrderStatus",
              "corrected": "var status = OrderStatus.Pending",
              "reason": "Enum value não especificado"
            }
          ],
          "code_smells": [],
          "approved_for_review": true,
          "corrected_files": []
        }
        """;

    protected override async Task HandleMessageAsync(AgentMessage message, CancellationToken ct)
    {
        await PublishStatusAsync("🔬 Compilando e analisando código com Roslyn...", ct);

        CodePayload? payload = null;
        try
        {
            var json = ExtractJson(message.Content);
            payload = JsonSerializer.Deserialize<CodePayload>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { /* mensagem livre */ }

        if (payload?.Files == null || payload.Files.Count == 0)
        {
            await SendToAsync("ReviewerAgent", message.Content, MessageType.Result,
                message.CorrelationId, ct);
            return;
        }

        var files = payload.Files.ToDictionary(f => f.Path, f => f.Content);
        var validation = await roslynValidator.ValidateAsync(files);

        // Analisa estrutura do primeiro arquivo
        var astContext = payload.Files.Count > 0
            ? roslynValidator.AnalyzeDeep(payload.Files[0].Content)
            : null;

        if (validation.IsValid)
        {
            await PublishStatusAsync(
                $"✅ Compilação OK — {validation.Metrics.ClassCount} classes, " +
                $"{validation.Metrics.MethodCount} métodos, " +
                $"complexidade: {validation.Metrics.CyclomaticComplexity}", ct);

            // Passa para o reviewer
            await SendToAsync("ReviewerAgent", message.Content, MessageType.Result,
                message.CorrelationId, ct);
            return;
        }

        await PublishStatusAsync($"❌ {validation.Errors.Count} erro(s) de compilação. Analisando...", ct);

        // Pede ao LLM que interprete os erros e corrija
        var prompt = $"""
            RESULTADO REAL DA COMPILAÇÃO ROSLYN:
            Erros: {JsonSerializer.Serialize(validation.Errors)}

            ESTRUTURA AST DETECTADA:
            Classes: {string.Join(", ", astContext?.Classes ?? [])}
            Interfaces: {string.Join(", ", astContext?.Interfaces ?? [])}
            Dependências: {JsonSerializer.Serialize(astContext?.Dependencies ?? [])}

            CÓDIGO COM PROBLEMAS:
            {string.Join("\n\n", payload.Files.Select(f => $"// {f.Path}\n{f.Content}"))}

            Analise os erros e gere as CORREÇÕES PRECISAS com o código corrigido.
            """;

        var response = await AskLlmAsync(prompt, ct);
        var responseJson = ExtractJson(response);

        // Devolve para o CodeGeneratorAgent corrigir
        await SendToAsync("CodeGeneratorAgent",
            $"CORREÇÃO NECESSÁRIA (erros Roslyn):\n{responseJson}\n\nCÓDIGO ORIGINAL:\n{message.Content}",
            MessageType.Correction, message.CorrelationId, ct);
    }

    private sealed record CodePayload(List<GeneratedFile> Files);
}

// ─── ReviewerAgent ────────────────────────────────────────────────────────────

public sealed class ReviewerAgent(
    IChatClient llm,
    IAgentMessageBus bus,
    ILogger<ReviewerAgent> logger,
    IAnythingLLMClient? rag = null)
    : BaseAgent(llm, bus, logger, rag)
{
    public override string Name => "ReviewerAgent";

    protected override string SystemPrompt => """
        Você é um Tech Lead fazendo Code Review rigoroso em C# / .NET 9.

        VOCÊ AVALIA (nota 0-10 cada):
        1. SOLID Principles — cada princípio separadamente
        2. Performance — alocações, LINQ em hot paths, async correto
        3. Segurança — injection, exposição de dados, validações
        4. Testabilidade — acoplamento, mocks, side effects
        5. Legibilidade — nomes, complexidade ciclomática

        CRITÉRIO DE APROVAÇÃO:
        - overall_score >= 8.0 E zero critical_issues → APROVADO
        - Qualquer critical_issue OU score < 8.0 → REPROVADO (feedback detalhado)

        FORMATO — APENAS JSON, SEM MARKDOWN:
        {
          "overall_score": 8.5,
          "scores": {
            "solid": 9,
            "performance": 8,
            "security": 9,
            "testability": 8,
            "readability": 8
          },
          "critical_issues": [],
          "suggestions": ["Extraia lógica X para método privado"],
          "approved": true,
          "feedback_for_generator": null
        }
        """;

    protected override async Task HandleMessageAsync(AgentMessage message, CancellationToken ct)
    {
        await PublishStatusAsync("🧐 Realizando code review...", ct);

        var prompt = $"""
            Faça um code review rigoroso do código abaixo.
            Seja específico nos problemas críticos.

            CÓDIGO:
            {message.Content}
            """;

        var response = await AskLlmWithRagAsync(prompt, "design-patterns", ct);
        var json = ExtractJson(response);

        ReviewResponse? review = null;
        try
        {
            review = JsonSerializer.Deserialize<ReviewResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { /* formato livre */ }

        var approved = review?.Approved ?? true;
        var score = review?.Overall_Score ?? 8.0;

        await PublishStatusAsync(
            $"{(approved ? "✅" : "❌")} Review: {score:F1}/10 " +
            $"| Issues críticos: {review?.Critical_Issues?.Count ?? 0}", ct);

        if (approved)
        {
            // Envia para geração de testes
            await SendToAsync("TestAgent", message.Content, MessageType.Task,
                message.CorrelationId, ct);
        }
        else
        {
            // Devolve para correção com feedback
            await SendToAsync("CodeGeneratorAgent",
                $"REPROVADO NO REVIEW:\n{json}\n\nCÓDIGO ORIGINAL:\n{message.Content}",
                MessageType.Correction, message.CorrelationId, ct);
        }
    }

    private sealed record ReviewResponse(
        double Overall_Score,
        Dictionary<string, double>? Scores,
        List<string>? Critical_Issues,
        List<string>? Suggestions,
        bool Approved,
        string? Feedback_For_Generator
    );
}
