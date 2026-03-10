using System.Text.Json;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Agents;

public sealed class CodeGeneratorAgent(
    IChatClient llm,
    IAgentMessageBus bus,
    ILogger<CodeGeneratorAgent> logger,
    IAnythingLLMClient? rag = null)
    : BaseAgent(llm, bus, logger, rag)
{
    public override string Name => "CodeGeneratorAgent";

    protected override string SystemPrompt => """
        Você é um Desenvolvedor Sênior C# / .NET 9 especializado em código limpo e idiomático.

        RESPONSABILIDADES:
        - Gerar código C# idiomático e moderno
        - Seguir EXATAMENTE a especificação do WorkItem
        - Implementar async/await em toda a stack
        - Usar records, primary constructors, pattern matching quando adequado
        - Incluir nullable reference types (#nullable enable)

        CHECKLIST OBRIGATÓRIO ANTES DE RESPONDER:
        ✓ Todos os métodos async retornam Task/ValueTask?
        ✓ Não há .Result ou .Wait() bloqueantes?
        ✓ Nullable reference types habilitado?
        ✓ Classes seguem Single Responsibility?
        ✓ Injeção de dependência via construtor?
        ✓ Interfaces para todas as dependências externas?

        PROIBIDO ABSOLUTAMENTE:
        - Thread.Sleep (use Task.Delay)
        - Magic strings/numbers sem constante nomeada
        - Catch genérico sem tratamento específico
        - Métodos com mais de 40 linhas sem justificativa
        - Dependência direta entre Domain e Infrastructure

        FORMATO — APENAS JSON, SEM MARKDOWN:
        {
          "files": [
            {
              "path": "src/MyApp.Domain/Entities/Order.cs",
              "content": "// código C# completo aqui",
              "description": "Entidade Order com value objects",
              "target_project": "MyApp.Domain"
            }
          ],
          "notes": "Observações sobre a implementação",
          "requires_packages": []
        }
        """;

    protected override async Task HandleMessageAsync(AgentMessage message, CancellationToken ct)
    {
        // Tenta deserializar como contexto de geração ou como WorkItem direto
        CodeGenerationContext? context = null;
        WorkItem? workItem = null;

        try
        {
            var json = ExtractJson(message.Content);
            context = JsonSerializer.Deserialize<CodeGenerationContext>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            workItem = context?.WorkItem;
        }
        catch
        {
            // Mensagem em texto livre — usa diretamente
        }

        var spec = workItem != null
            ? BuildWorkItemPrompt(workItem, context!)
            : message.Content;

        await PublishStatusAsync($"💻 Gerando código: {workItem?.Name ?? "componente"}...", ct);

        // Busca padrões relevantes no RAG
        var ragQuery = workItem != null
            ? $"C# implementation pattern for {workItem.Type} {workItem.Name}"
            : message.Content[..Math.Min(100, message.Content.Length)];

        var response = await AskLlmWithRagAsync(spec, "dotnet-best-practices", ct);
        var responseJson = ExtractJson(response);

        // Envia para o RoslynValidatorAgent
        await SendToAsync("RoslynValidatorAgent", responseJson, MessageType.Result,
            message.CorrelationId, ct);
    }

    private static string BuildWorkItemPrompt(WorkItem wi, CodeGenerationContext ctx) => $"""
        WORK ITEM: {wi.Id} - {wi.Name}
        Tipo: {wi.Type}
        Projeto alvo: {wi.TargetProject}
        Arquivo alvo: {wi.TargetPath}

        ESPECIFICAÇÃO COMPLETA:
        {wi.FullSpecification}

        CRITÉRIOS DE VALIDAÇÃO (o código DEVE atender):
        {wi.ValidationCriteria}

        CONTRATOS DISPONÍVEIS (já existem, você DEVE usar):
        {string.Join("\n", ctx.AlreadyGeneratedContracts.Select(f => $"// {f.Path}\n{f.Content}"))}

        DECISÕES ARQUITETURAIS:
        {ctx.ArchitectureDecisions}

        IMPACTO: Se este item quebrar, afetará: {string.Join(", ", wi.CanBreakIfMissing)}

        Gere o código completo seguindo o seu papel.
        """;
}
