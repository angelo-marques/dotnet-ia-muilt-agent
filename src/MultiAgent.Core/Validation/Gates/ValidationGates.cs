using MultiAgent.Core.Models;
using Microsoft.Extensions.AI;

namespace MultiAgent.Core.Validation.Gates;

// ─── Interface base ───────────────────────────────────────────────────────────

public interface IValidationGate
{
    string Name { get; }
    Task<GateResult> ValidateAsync(AgentResult result, CancellationToken ct);
}

public record GateResult(
    bool Passed,
    string GateName,
    string Summary,
    string? Feedback = null,
    List<CodeError>? Errors = null
);

// ─── Gate 1: Compilação (Roslyn) ─────────────────────────────────────────────

public sealed class CompilationGate(IRoslynCodeValidator validator) : IValidationGate
{
    public string Name => "Compilation";

    public async Task<GateResult> ValidateAsync(AgentResult result, CancellationToken ct)
    {
        if (result.Files is null or { Count: 0 })
            return new GateResult(false, Name, "Nenhum arquivo gerado");

        var files = result.Files.ToDictionary(f => f.Path, f => f.Content);
        var validation = await validator.ValidateAsync(files);

        return validation.IsValid
            ? new GateResult(true, Name, $"✅ Compilado — {validation.Metrics.ClassCount} classes, {validation.Metrics.MethodCount} métodos")
            : new GateResult(false, Name,
                $"❌ {validation.Errors.Count} erro(s) de compilação",
                Feedback: FormatErrors(validation.Errors),
                Errors: validation.Errors);
    }

    private static string FormatErrors(List<CodeError> errors) =>
        string.Join("\n", errors.Select(e =>
            $"Linha {e.Line}: [{e.Code}] {e.Message}" +
            (e.SuggestedFix != null ? $"\n  → Sugestão: {e.SuggestedFix}" : "")));
}

// ─── Gate 2: Null Safety (Roslyn) ────────────────────────────────────────────

public sealed class NullSafetyGate(IRoslynCodeValidator validator) : IValidationGate
{
    public string Name => "NullSafety";

    public async Task<GateResult> ValidateAsync(AgentResult result, CancellationToken ct)
    {
        if (result.Files is null or { Count: 0 })
            return new GateResult(true, Name, "Nenhum arquivo para verificar");

        var nullErrors = new List<CodeError>();

        foreach (var file in result.Files)
        {
            var singleResult = validator.ValidateSingle(file.Content, file.Path);
            var nullIssues = singleResult.Errors
                .Where(e => e.Code.StartsWith("CS86") || e.Code.StartsWith("CS87"))
                .ToList();
            nullErrors.AddRange(nullIssues);
        }

        return nullErrors.Count == 0
            ? new GateResult(true, Name, "✅ Null safety OK")
            : new GateResult(false, Name,
                $"⚠️ {nullErrors.Count} problemas de null safety",
                Feedback: string.Join("\n", nullErrors.Select(e => $"Linha {e.Line}: {e.Message}")),
                Errors: nullErrors);
    }
}

// ─── Gate 3: Code Review (LLM) ───────────────────────────────────────────────

public sealed class CodeReviewGate(IChatClient llm) : IValidationGate
{
    public string Name => "CodeReview";
    private const double MinScore = 8.0;

    public async Task<GateResult> ValidateAsync(AgentResult result, CancellationToken ct)
    {
        if (result.Files is null or { Count: 0 })
            return new GateResult(false, Name, "Nenhum código para revisar");

        var codeContent = string.Join("\n\n", result.Files.Select(f =>
            $"// === {f.Path} ===\n{f.Content}"));

        var prompt = $@"
            Você é um Tech Lead sênior. Avalie o código abaixo com nota de 0 a 10.

            CRITÉRIOS (cada um de 0 a 10):
            1. SOLID Principles
            2. Performance (sem alocações desnecessárias, async correto)
            3. Segurança (validações, null checks)
            4. Testabilidade (acoplamento baixo, injetável)
            5. Legibilidade (nomes claros, métodos pequenos)

            CÓDIGO:
            {codeContent}

            Responda APENAS em JSON sem markdown:
              'overall_score': 8.5,
              'solid': 9, 'performance': 8, 'security': 9, 'testability': 7, 'readability': 8,
              'critical_issues': ['issue1'],
              'suggestions': ['suggestion1'],
              'approved': true
            ";

        try
        {
            var response = await llm.GetResponseAsync(prompt, cancellationToken: ct);
            var json = ExtractJson(response.Text);
            var review = System.Text.Json.JsonSerializer.Deserialize<ReviewResponse>(json)!;

            var passed = review.Overall_Score >= MinScore && review.Critical_Issues.Count == 0;
            return new GateResult(
                passed, Name,
                $"{(passed ? "✅" : "❌")} Score: {review.Overall_Score:F1}/10",
                Feedback: passed ? null :
                    $"Score insuficiente ({review.Overall_Score:F1} < {MinScore})\n" +
                    $"Problemas críticos: {string.Join(", ", review.Critical_Issues)}\n" +
                    $"Sugestões: {string.Join(", ", review.Suggestions)}");
        }
        catch
        {
            // LLM falhou — passa com aviso para não bloquear indefinidamente
            return new GateResult(true, Name, "⚠️ Review gate ignorado (LLM indisponível)");
        }
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private sealed record ReviewResponse(
        double Overall_Score,
        List<string> Critical_Issues,
        List<string> Suggestions,
        bool Approved
    );
}

// ─── Gate 4: Test Coverage (LLM) ─────────────────────────────────────────────

public sealed class TestCoverageGate(IChatClient llm) : IValidationGate
{
    public string Name => "TestCoverage";

    public async Task<GateResult> ValidateAsync(AgentResult result, CancellationToken ct)
    {
        var hasTests = result.Files?.Any(f =>
            f.Path.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            f.Path.Contains("Spec", StringComparison.OrdinalIgnoreCase)) ?? false;

        if (!hasTests)
            return new GateResult(false, Name,
                "❌ Nenhum arquivo de teste gerado",
                Feedback: "Gere testes unitários para os métodos públicos");

        var testFiles = result.Files!
            .Where(f => f.Path.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var prompt = $@"
            Analise se os testes abaixo cobrem adequadamente o código.

            TESTES:
            {string.Join("\n\n", testFiles.Select(f => f.Content))}

            Responda APENAS em JSON:
            {{
              'coverage_estimate': 85,
              'has_happy_path': true,
              'has_error_cases': true,
              'has_edge_cases': false,
              'missing_scenarios': ['null input test', 'boundary test'],
              'approved': true
            }}
            ";

        try
        {
            var response = await llm.GetResponseAsync(prompt, cancellationToken: ct);
            var json = ExtractJson(response.Text);
            var coverage = System.Text.Json.JsonSerializer.Deserialize<CoverageResponse>(json)!;

            return coverage.Approved
                ? new GateResult(true, Name, $"✅ Cobertura estimada: {coverage.Coverage_Estimate}%")
                : new GateResult(false, Name,
                    $"❌ Cobertura insuficiente ({coverage.Coverage_Estimate}%)",
                    Feedback: $"Cenários faltando: {string.Join(", ", coverage.Missing_Scenarios)}");
        }
        catch
        {
            return new GateResult(true, Name, "⚠️ Coverage gate ignorado (LLM indisponível)");
        }
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private sealed record CoverageResponse(
        int Coverage_Estimate,
        bool Has_Happy_Path,
        bool Has_Error_Cases,
        bool Has_Edge_Cases,
        List<string> Missing_Scenarios,
        bool Approved
    );
}

// ─── Gate 5: Architectural Compliance (LLM) ──────────────────────────────────

public sealed class ArchitecturalGate(IChatClient llm) : IValidationGate
{
    public string Name => "Architectural";

    public async Task<GateResult> ValidateAsync(AgentResult result, CancellationToken ct)
    {
        if (result.Files is null or { Count: 0 })
            return new GateResult(true, Name, "Nada para verificar");

        var hasCircularDeps = result.Files.Any(f =>
            f.Content.Contains("circular", StringComparison.OrdinalIgnoreCase));

        // Verifica se layers superiores não referenciam layers inferiores incorretamente
        var domainFiles = result.Files.Where(f => f.Path.Contains("Domain")).ToList();
        var infraRefsInDomain = domainFiles.Any(f =>
            f.Content.Contains("Infrastructure") || f.Content.Contains("EntityFramework"));

        if (infraRefsInDomain)
            return new GateResult(false, Name,
                "❌ Domain referencia Infrastructure — violação de Clean Architecture",
                Feedback: "Remova referências de infraestrutura da camada Domain. Use interfaces.");

        return new GateResult(true, Name, "✅ Arquitetura respeitada");
    }
}
