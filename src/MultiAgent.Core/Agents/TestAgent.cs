using System.Text.Json;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using MultiAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Agents;

public sealed class TestAgent(
    IChatClient llm,
    IAgentMessageBus bus,
    ILogger<TestAgent> logger,
    IRoslynCodeValidator roslynValidator,
    IAnythingLLMClient? rag = null)
    : BaseAgent(llm, bus, logger, rag)
{
    public override string Name => "TestAgent";

    protected override string SystemPrompt => """
        Você é um Especialista em Testes automatizados para .NET 9 / xUnit / NSubstitute.

        ESTRATÉGIA DE TESTES:
        - Unit Tests: toda lógica de domínio e application layer
        - Integration Tests: repositórios e infraestrutura
        - NÃO teste o framework (não teste EF Core em si)

        ESTRUTURA OBRIGATÓRIA (padrão AAA):
        [Fact]
        public async Task NomeMetodo_Cenario_ResultadoEsperado()
        {
            // Arrange — setup mínimo necessário
            // Act — UMA única ação sendo testada
            // Assert — verificação clara e específica
        }

        COBERTURA MÍNIMA:
        ✓ Happy path de cada método público
        ✓ Casos de erro e exceções esperadas
        ✓ Edge cases: null, lista vazia, valores limite

        MOCKING:
        - Use NSubstitute para todas as dependências externas
        - NUNCA mock da classe sendo testada
        - Prefira fakes simples a mocks complexos

        FORMATO — APENAS JSON, SEM MARKDOWN:
        {
          "test_files": [
            {
              "path": "tests/MyApp.Tests/Domain/OrderTests.cs",
              "content": "// código de teste xUnit",
              "description": "Testes da entidade Order",
              "target_project": "MyApp.Tests"
            }
          ],
          "coverage_estimate": 85,
          "scenarios_covered": ["happy path", "null input", "invalid state"],
          "packages_needed": ["xunit", "NSubstitute", "FluentAssertions"]
        }
        """;

    protected override async Task HandleMessageAsync(AgentMessage message, CancellationToken ct)
    {
        await PublishStatusAsync("🧪 Gerando testes unitários...", ct);

        var prompt = $"""
            Gere testes unitários completos para o código abaixo.
            Cubra happy paths, casos de erro e edge cases.

            CÓDIGO A TESTAR:
            {message.Content}
            """;

        var response = await AskLlmWithRagAsync(prompt, "testing-patterns", ct);
        var json = ExtractJson(response);

        // Valida se os testes compilam com Roslyn
        TestPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<TestPayload>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { /* formato livre */ }

        if (payload?.Test_Files != null)
        {
            var testFiles = payload.Test_Files.ToDictionary(f => f.Path, f => f.Content);
            var validation = await roslynValidator.ValidateAsync(testFiles);

            if (!validation.IsValid)
            {
                await PublishStatusAsync(
                    $"⚠️ Testes têm {validation.Errors.Count} erro(s) de compilação — corrigindo...", ct);

                // Pede correção dos testes
                var fixPrompt = $"""
                    Os testes gerados têm erros de compilação:
                    {JsonSerializer.Serialize(validation.Errors)}

                    TESTES COM ERRO:
                    {json}

                    CÓDIGO ORIGINAL:
                    {message.Content}

                    Corrija os testes mantendo a cobertura.
                    """;

                response = await AskLlmAsync(fixPrompt, ct);
                json = ExtractJson(response);
            }
            else
            {
                await PublishStatusAsync(
                    $"✅ Testes compilando — cobertura estimada: {payload.Coverage_Estimate}%", ct);
            }
        }

        // Combina código original + testes e envia como resultado final
        var finalResult = new
        {
            source_code = message.Content,
            test_code = json,
            status = "complete"
        };

        await SendToAsync("Orchestrator",
            JsonSerializer.Serialize(finalResult),
            MessageType.Complete,
            message.CorrelationId, ct);
    }

    private sealed record TestPayload(
        List<GeneratedFile> Test_Files,
        int Coverage_Estimate,
        List<string> Scenarios_Covered
    );
}
