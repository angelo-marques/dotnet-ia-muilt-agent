using MultiAgent.Core.Bus;
using MultiAgent.Core.Models;
using MultiAgent.Core.Validation.Gates;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.Validation;

public interface IValidationPipeline
{
    Task<PipelineResult> RunAsync(AgentResult result, CancellationToken ct);
}

public record PipelineResult(
    bool Passed,
    string? FailedGate,
    string? Feedback,
    AgentResult Result,
    List<GateResult> GateResults
);

public sealed class ValidationPipeline : IValidationPipeline
{
    private readonly IReadOnlyList<IValidationGate> _gates;
    private readonly IAgentMessageBus _bus;
    private readonly ILogger<ValidationPipeline> _logger;

    public ValidationPipeline(
        IAgentMessageBus bus,
        ILogger<ValidationPipeline> logger,
        IRoslynCodeValidator roslynValidator,
        Microsoft.Extensions.AI.IChatClient llm)
    {
        _bus = bus;
        _logger = logger;
        _gates =
        [
            new CompilationGate(roslynValidator),
            new NullSafetyGate(roslynValidator),
            new CodeReviewGate(llm),
            new TestCoverageGate(llm),
            new ArchitecturalGate(llm)
        ];
    }

    public async Task<PipelineResult> RunAsync(AgentResult result, CancellationToken ct)
    {
        var gateResults = new List<GateResult>();

        await _bus.PublishAsync(AgentMessage.Broadcast(
            "Pipeline", $"▶️ Iniciando pipeline de validação para [{result.AgentName}]",
            MessageType.Status), ct);

        foreach (var gate in _gates)
        {
            ct.ThrowIfCancellationRequested();

            var gateResult = await gate.ValidateAsync(result, ct);
            gateResults.Add(gateResult);

            var icon = gateResult.Passed ? "✅" : "❌";
            await _bus.PublishAsync(AgentMessage.Broadcast(
                "Pipeline",
                $"Gate [{gate.Name}]: {icon} {gateResult.Summary}",
                MessageType.Status), ct);

            if (!gateResult.Passed)
            {
                _logger.LogWarning("Pipeline FALHOU no gate [{Gate}]: {Summary}",
                    gate.Name, gateResult.Summary);

                return new PipelineResult(
                    Passed: false,
                    FailedGate: gate.Name,
                    Feedback: gateResult.Feedback,
                    Result: result,
                    GateResults: gateResults);
            }
        }

        await _bus.PublishAsync(AgentMessage.Broadcast(
            "Pipeline", "🎉 Todos os gates passaram! Código aprovado.",
            MessageType.Complete), ct);

        return new PipelineResult(
            Passed: true,
            FailedGate: null,
            Feedback: null,
            Result: result,
            GateResults: gateResults);
    }
}
