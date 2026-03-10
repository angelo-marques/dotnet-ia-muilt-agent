using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using MultiAgent.Core.Agents;
using MultiAgent.Core.Bus;
using MultiAgent.Core.Orchestration;
using MultiAgent.Core.RAG;
using MultiAgent.Core.Validation;
using MultiAgent.Core.Validation.Gates;
using MultiAgent.Host.Hubs;

// ─── Serilog ──────────────────────────────────────────────────────────────────

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/agent-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// ─── Configuração ─────────────────────────────────────────────────────────────

var config = builder.Configuration;
var llmProvider = config["LLM:Provider"] ?? "ollama";
var ollamaEndpoint = config["LLM:OllamaEndpoint"] ?? "http://localhost:11434";
var ollamaModel = config["LLM:OllamaModel"] ?? "llama3.2";
var openAiKey = config["LLM:OpenAiKey"] ?? string.Empty;
var anythingLLMUrl = config["AnythingLLM:BaseUrl"] ?? "http://localhost:3001";
var anythingLLMKey = config["AnythingLLM:ApiKey"] ?? string.Empty;

// ─── LLM Provider ─────────────────────────────────────────────────────────────

IChatClient chatClient = llmProvider.ToLower() switch
{
    "ollama" => new OllamaChatClient(new Uri(ollamaEndpoint), ollamaModel),
    _ => throw new InvalidOperationException($"Provider '{llmProvider}' desconhecido")
};

builder.Services.AddSingleton<IChatClient>(chatClient);

// ─── SignalR ──────────────────────────────────────────────────────────────────

builder.Services.AddSignalR();

// ─── Core Services ────────────────────────────────────────────────────────────

builder.Services.AddSingleton<IAgentMessageBus, AgentMessageBus>();
builder.Services.AddSingleton<IRoslynCodeValidator, RoslynCodeValidator>();

// AnythingLLM (opcional — usa NullClient se indisponível)
builder.Services.AddHttpClient<AnythingLLMClient>(c =>
    c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton<IAnythingLLMClient>(sp =>
{
    if (string.IsNullOrEmpty(anythingLLMKey))
    {
        Log.Warning("AnythingLLM não configurado — agentes funcionarão sem RAG");
        return new NullAnythingLLMClient();
    }
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var logger = sp.GetRequiredService<ILogger<AnythingLLMClient>>();
    return new AnythingLLMClient(http, anythingLLMUrl, anythingLLMKey, logger);
});

// Agentes
builder.Services.AddSingleton<ArchitectAgent>();
builder.Services.AddSingleton<DecomposerAgent>();
builder.Services.AddSingleton<CodeGeneratorAgent>();
builder.Services.AddSingleton<RoslynValidatorAgent>();
builder.Services.AddSingleton<ReviewerAgent>();
builder.Services.AddSingleton<TestAgent>();

// Validação
builder.Services.AddSingleton<IValidationPipeline, ValidationPipeline>();

// Orquestração
builder.Services.AddSingleton<SmartRetryOrchestrator>();
builder.Services.AddSingleton<IMainOrchestrator, MainOrchestrator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─── SignalR → MessageBus bridge ─────────────────────────────────────────────

var messageBus = app.Services.GetRequiredService<IAgentMessageBus>();
var hubContext = app.Services.GetRequiredService<IHubContext<AgentHub>>();

messageBus.OnMessage += async msg =>
{
    await hubContext.Clients.All.SendAsync("AgentMessage", new
    {
        id = msg.Id,
        from = msg.FromAgent,
        to = msg.ToAgent,
        content = msg.Content.Length > 500 ? msg.Content[..500] + "..." : msg.Content,
        type = msg.Type.ToString(),
        timestamp = msg.Timestamp.ToString("HH:mm:ss.fff")
    });
};

// ─── Spectre.Console output no terminal ──────────────────────────────────────

messageBus.OnMessage += msg =>
{
    var icon = msg.Type switch
    {
        MessageType.Complete => "🎉",
        MessageType.Error => "❌",
        MessageType.Escalation => "⚠️",
        MessageType.Status => "📡",
        MessageType.Task => "📋",
        _ => "💬"
    };

    var color = msg.Type switch
    {
        MessageType.Complete => "green",
        MessageType.Error => "red",
        MessageType.Escalation => "yellow",
        _ => "white"
    };

    Spectre.Console.AnsiConsole.MarkupLine(
        $"[grey]{msg.Timestamp:HH:mm:ss}[/] {icon} " +
        $"[bold yellow]{msg.FromAgent}[/] → [bold cyan]{msg.ToAgent}[/]: " +
        $"[{color}]{Spectre.Console.Markup.Escape(msg.Content[..Math.Min(120, msg.Content.Length)])}[/{color}]");

    return Task.CompletedTask;
};

// ─── Inicia todos os agentes em background ────────────────────────────────────

var cts = new CancellationTokenSource();
var agents = new[]
{
    app.Services.GetRequiredService<ArchitectAgent>().RunAsync(cts.Token),
    app.Services.GetRequiredService<DecomposerAgent>().RunAsync(cts.Token),
    app.Services.GetRequiredService<CodeGeneratorAgent>().RunAsync(cts.Token),
    app.Services.GetRequiredService<RoslynValidatorAgent>().RunAsync(cts.Token),
    app.Services.GetRequiredService<ReviewerAgent>().RunAsync(cts.Token),
    app.Services.GetRequiredService<TestAgent>().RunAsync(cts.Token),
};

// Roda todos os agentes em paralelo em background
_ = Task.WhenAll(agents);

// ─── Minimal API Endpoints ────────────────────────────────────────────────────

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapHub<AgentHub>("/hubs/agents");

// POST /generate — dispara geração a partir de texto
app.MapPost("/api/generate", async (GenerateRequest request,
    IMainOrchestrator orchestrator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
        return Results.BadRequest("Input é obrigatório");

    var result = await orchestrator.GenerateSystemAsync(request.Input, ct);

    return Results.Ok(new
    {
        success = result.Success,
        systemName = result.SystemName,
        filesGenerated = result.GeneratedFiles.Count,
        totalAttempts = result.TotalAttempts,
        durationSeconds = result.Duration.TotalSeconds,
        files = result.GeneratedFiles.Select(f => new
        {
            path = f.Path,
            project = f.TargetProject,
            description = f.Description,
            lines = f.Content.Split('\n').Length
        })
    });
});

// POST /generate/file — gera a partir de upload de arquivo
app.MapPost("/api/generate/file", async (
    IFormFile file,
    IMainOrchestrator orchestrator,
    CancellationToken ct) =>
{
    var tempPath = Path.GetTempFileName();
    await using (var stream = File.Create(tempPath))
        await file.CopyToAsync(stream, ct);

    var result = await orchestrator.GenerateFromFileAsync(tempPath, ct);
    File.Delete(tempPath);

    return Results.Ok(new { result.Success, result.SystemName, FilesGenerated = result.GeneratedFiles.Count });
});

// GET /api/status — status dos agentes
app.MapGet("/api/status", () => Results.Ok(new
{
    agents = new[] { "ArchitectAgent", "DecomposerAgent", "CodeGeneratorAgent",
        "RoslynValidatorAgent", "ReviewerAgent", "TestAgent" },
    status = "running",
    timestamp = DateTime.UtcNow
}));

app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

app.Run();

record GenerateRequest(string Input, string? OutputDir = null);
