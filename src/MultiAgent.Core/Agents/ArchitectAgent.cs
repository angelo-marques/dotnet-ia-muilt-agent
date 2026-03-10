using System.Text.Json;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.RAG;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Agents;

public sealed class ArchitectAgent(
    IChatClient llm,
    IAgentMessageBus bus,
    ILogger<ArchitectAgent> logger,
    IAnythingLLMClient? rag = null)
    : BaseAgent(llm, bus, logger, rag)
{
    public override string Name => "ArchitectAgent";

    protected override string SystemPrompt => """
        Você é um Arquiteto de Software Sênior especializado em .NET 9/10 e C#.

        RESPONSABILIDADES:
        - Definir arquitetura (Clean Architecture, DDD, CQRS quando adequado)
        - Identificar bounded contexts e separação de responsabilidades
        - Definir contratos entre camadas (interfaces, DTOs)
        - Decidir padrões de projeto adequados

        PRINCÍPIOS:
        - Prefira simplicidade a over-engineering
        - Use .NET 9 minimal APIs para APIs simples
        - Interfaces ANTES de implementações nas dependências
        - Domain layer NUNCA depende de Infrastructure
        - Justifique todas as decisões arquiteturais

        FORMATO DE RESPOSTA — APENAS JSON, SEM MARKDOWN:
        {
          "system_name": "OrderManagement",
          "architecture_type": "CleanArchitecture",
          "description": "Sistema de gerenciamento de pedidos",
          "layers": [
            {
              "name": "Domain",
              "responsibility": "Entidades, value objects, regras de negócio",
              "dependencies": []
            },
            {
              "name": "Application",
              "responsibility": "Casos de uso, interfaces de repositório",
              "dependencies": ["Domain"]
            },
            {
              "name": "Infrastructure",
              "responsibility": "EF Core, repositórios, serviços externos",
              "dependencies": ["Domain", "Application"]
            },
            {
              "name": "API",
              "responsibility": "Controllers, minimal API endpoints",
              "dependencies": ["Application", "Infrastructure"]
            }
          ],
          "design_patterns": ["Repository", "CQRS", "Mediator"],
          "projects": [
            { "name": "MyApp.Domain", "type": "classlib" },
            { "name": "MyApp.Application", "type": "classlib" },
            { "name": "MyApp.Infrastructure", "type": "classlib" },
            { "name": "MyApp.API", "type": "webapi" }
          ],
          "key_decisions": [
            "MediatR para desacoplamento de handlers",
            "EF Core com repositórios genéricos"
          ],
          "instructions_for_decomposer": "Comece pelos value objects e enums, depois entidades..."
        }
        """;

    protected override async Task HandleMessageAsync(AgentMessage message, CancellationToken ct)
    {
        await PublishStatusAsync($"🏛️ Analisando requisitos e definindo arquitetura...", ct);

        var response = await AskLlmWithRagAsync(
            message.Content,
            "design-patterns",
            ct);

        var json = ExtractJson(response);

        // Tenta parsear para validar
        try
        {
            using var doc = JsonDocument.Parse(json);
            await PublishStatusAsync("✅ Arquitetura definida com sucesso", ct);
        }
        catch
        {
            await PublishStatusAsync("⚠️ Arquitetura gerada em formato livre", ct);
        }

        // Envia para o DecomposerAgent processar
        await SendToAsync("DecomposerAgent", json, MessageType.Task, message.CorrelationId, ct);
    }
}
