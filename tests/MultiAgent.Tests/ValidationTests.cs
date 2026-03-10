using FluentAssertions;
using MultiAgent.Core.Validation;
using Xunit;

namespace MultiAgent.Tests;

public class RoslynCodeValidatorTests
{
    private readonly RoslynCodeValidator _sut = new();

    [Fact]
    public async Task ValidateAsync_ValidCode_ReturnsSuccess()
    {
        // Arrange
        var files = new Dictionary<string, string>
        {
            ["Order.cs"] = """
                namespace MyApp.Domain;
                public record Order(Guid Id, string CustomerName, decimal Total)
                {
                    public bool IsValid() => Total > 0 && !string.IsNullOrEmpty(CustomerName);
                }
                """
        };

        // Act
        var result = await _sut.ValidateAsync(files);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_InvalidCode_ReturnsErrors()
    {
        // Arrange
        var files = new Dictionary<string, string>
        {
            ["Broken.cs"] = """
                namespace MyApp.Domain;
                public class Broken
                {
                    public void Method()
                    {
                        var x = NonExistentType.Create();
                    }
                }
                """
        };

        // Act
        var result = await _sut.ValidateAsync(files);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(e => e.Code.StartsWith("CS"));
    }

    [Fact]
    public void AnalyzeDeep_ExtractsStructureCorrectly()
    {
        // Arrange
        var code = """
            namespace MyApp.Domain;

            public interface IOrderRepository
            {
                Task<Order?> GetByIdAsync(Guid id);
            }

            public class OrderService(IOrderRepository repository)
            {
                public async Task<Order?> GetOrderAsync(Guid id) =>
                    await repository.GetByIdAsync(id);
            }

            public record Order(Guid Id, string Name);
            """;

        // Act
        var structure = _sut.AnalyzeDeep(code);

        // Assert
        structure.Classes.Should().Contain("OrderService");
        structure.Interfaces.Should().Contain("IOrderRepository");
        structure.Dependencies.Should().ContainKey("OrderService");
    }

    [Fact]
    public void AnalyzeDeep_DetectsPotentialCycles()
    {
        // Arrange — A depende de B, B depende de A
        var code = """
            namespace MyApp;
            public class ServiceA(ServiceB b) { }
            public class ServiceB(ServiceA a) { }
            """;

        // Act
        var structure = _sut.AnalyzeDeep(code);

        // Assert
        structure.PotentialCycles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_MultipleFiles_ValidatesAll()
    {
        // Arrange
        var files = new Dictionary<string, string>
        {
            ["Enums.cs"] = "namespace MyApp; public enum Status { Active, Inactive }",
            ["Entity.cs"] = "namespace MyApp; public record Customer(Guid Id, Status Status);"
        };

        // Act
        var result = await _sut.ValidateAsync(files);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Metrics.ClassCount.Should().BeGreaterThanOrEqualTo(0);
    }
}

public class ValidationPipelineTests
{
    [Fact]
    public async Task RunAsync_NoFiles_FailsCompilationGate()
    {
        // Arrange
        var bus = new FakeMessageBus();
        var validator = new RoslynCodeValidator();
        var llm = new FakeChatClient("{ \"overall_score\": 9, \"critical_issues\": [], \"approved\": true }");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ValidationPipeline>.Instance;

        var pipeline = new ValidationPipeline(bus, logger, validator, llm);

        var emptyResult = new MultiAgent.Core.Models.AgentResult(
            true, "TestAgent", "content", Files: null);

        // Act
        var result = await pipeline.RunAsync(emptyResult, CancellationToken.None);

        // Assert
        result.Passed.Should().BeFalse();
        result.FailedGate.Should().Be("Compilation");
    }
}

// ─── Fakes para testes ────────────────────────────────────────────────────────

internal sealed class FakeMessageBus : MultiAgent.Core.Bus.IAgentMessageBus
{
    public List<MultiAgent.Core.Bus.AgentMessage> Published = [];
    public event Func<MultiAgent.Core.Bus.AgentMessage, Task>? OnMessage;

    public Task PublishAsync(MultiAgent.Core.Bus.AgentMessage message, CancellationToken ct = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<MultiAgent.Core.Bus.AgentMessage> SubscribeAsync(
        string agentName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield break;
    }
}

internal sealed class FakeChatClient(string response) : Microsoft.Extensions.AI.IChatClient
{
    public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        Microsoft.Extensions.AI.ChatOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
            [new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant, response)]));

    public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        Microsoft.Extensions.AI.ChatOptions? options = null,
        CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Microsoft.Extensions.AI.ChatClientMetadata Metadata =>
        new("fake", null, "fake-model");

    public TService? GetService<TService>(object? key = null) where TService : class => null;
    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        throw new NotImplementedException();
    }
}
