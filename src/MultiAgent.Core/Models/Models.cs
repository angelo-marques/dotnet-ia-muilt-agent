namespace MultiAgent.Core.Models;

// ─── Resultado de um agente ───────────────────────────────────────────────────

public record AgentResult(
    bool Success,
    string AgentName,
    string Content,
    double ReviewScore = 0,
    List<CodeError>? Errors = null,
    List<GeneratedFile>? Files = null,
    string? LastError = null
)
{
    public static AgentResult Ok(string agent, string content, List<GeneratedFile>? files = null) =>
        new(true, agent, content, 10, Files: files);

    public static AgentResult Fail(string agent, string error, List<CodeError>? errors = null) =>
        new(false, agent, string.Empty, 0, Errors: errors, LastError: error);
}

public record CodeError(
    string Code,
    string Message,
    int Line,
    int Column,
    string Severity,
    string? SuggestedFix = null
);

public record GeneratedFile(
    string Path,
    string Content,
    string Description,
    string TargetProject
);

// ─── WorkItem (unidade de geração de código) ──────────────────────────────────

public record WorkItem(
    string Id,
    string Name,
    string Type,
    int LayerNumber,
    string TargetProject,
    string TargetPath,
    List<string> Dependencies,
    List<string> ContractsNeeded,
    string FullSpecification,
    string ValidationCriteria,
    List<string> CanBreakIfMissing
);

// ─── Resultado da decomposição ────────────────────────────────────────────────

public record DecompositionResult(
    string SystemName,
    int TotalLayers,
    Dictionary<string, List<string>> DependencyGraph,
    List<DecompositionLayer> Layers,
    ExecutionPlan ExecutionPlan
);

public record DecompositionLayer(
    int LayerNumber,
    string Name,
    string Reason,
    List<WorkItem> WorkItems
);

public record ExecutionPlan(
    List<List<string>> ParallelGroups,
    string Reason
);

// ─── Resultado de validação ───────────────────────────────────────────────────

public record ValidationResult(
    bool IsValid,
    List<CodeError> Errors,
    List<CodeWarning> Warnings,
    CodeMetrics Metrics
)
{
    public static ValidationResult Success(CodeMetrics metrics) =>
        new(true, [], [], metrics);

    public static ValidationResult Failure(List<CodeError> errors) =>
        new(false, errors, [], CodeMetrics.Empty);
}

public record CodeWarning(
    string Code,
    string Message,
    int Line,
    int Column
);

public record CodeMetrics(
    int ClassCount,
    int MethodCount,
    int LineCount,
    int CyclomaticComplexity
)
{
    public static readonly CodeMetrics Empty = new(0, 0, 0, 0);
}

// ─── Estrutura profunda via Roslyn ────────────────────────────────────────────

public record DeepStructure(
    List<string> Classes,
    List<string> Interfaces,
    List<string> Enums,
    Dictionary<string, List<string>> Dependencies,
    List<List<string>> PotentialCycles,
    List<string> Namespaces
);

// ─── Contexto de geração de código ───────────────────────────────────────────

public record CodeGenerationContext(
    WorkItem WorkItem,
    List<GeneratedFile> AlreadyGeneratedContracts,
    string ArchitectureDecisions,
    string RagContext
);
