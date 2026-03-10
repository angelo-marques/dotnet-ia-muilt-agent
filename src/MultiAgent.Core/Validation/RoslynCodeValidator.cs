using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MultiAgent.Core.Models;

namespace MultiAgent.Core.Validation;

public interface IRoslynCodeValidator
{
    Task<ValidationResult> ValidateAsync(Dictionary<string, string> files);
    ValidationResult ValidateSingle(string code, string fileName = "temp.cs");
    DeepStructure AnalyzeDeep(string code);
}

public sealed class RoslynCodeValidator : IRoslynCodeValidator
{
    private static readonly IReadOnlyList<MetadataReference> DefaultReferences = BuildDefaultReferences();

    public async Task<ValidationResult> ValidateAsync(Dictionary<string, string> files)
    {
        var syntaxTrees = files.Select(f =>
            CSharpSyntaxTree.ParseText(f.Value, path: f.Key)).ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "AgentValidation_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: syntaxTrees,
            references: DefaultReferences,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Debug));

        var diagnostics = compilation.GetDiagnostics();

        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => new CodeError(
                Code: d.Id,
                Message: d.GetMessage(),
                Line: d.Location.GetLineSpan().StartLinePosition.Line + 1,
                Column: d.Location.GetLineSpan().StartLinePosition.Character + 1,
                Severity: "error",
                SuggestedFix: BuildSuggestedFix(d)
            )).ToList();

        var warnings = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning)
            .Select(d => new CodeWarning(
                Code: d.Id,
                Message: d.GetMessage(),
                Line: d.Location.GetLineSpan().StartLinePosition.Line + 1,
                Column: d.Location.GetLineSpan().StartLinePosition.Character + 1
            )).ToList();

        var metrics = errors.Count == 0
            ? await ComputeMetricsAsync(syntaxTrees)
            : CodeMetrics.Empty;

        return errors.Count == 0
            ? ValidationResult.Success(metrics)
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateSingle(string code, string fileName = "temp.cs")
    {
        var files = new Dictionary<string, string> { [fileName] = code };
        return ValidateAsync(files).GetAwaiter().GetResult();
    }

    public DeepStructure AnalyzeDeep(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        var classes = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Select(c => c.Identifier.Text)
            .ToList();

        var interfaces = root.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .Select(i => i.Identifier.Text)
            .ToList();

        var enums = root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Select(e => e.Identifier.Text)
            .ToList();

        var namespaces = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .ToList();

        var dependencies = BuildDependencyMap(root);
        var cycles = DetectPotentialCycles(dependencies);

        return new DeepStructure(classes, interfaces, enums, dependencies, cycles, namespaces);
    }

    private Dictionary<string, List<string>> BuildDependencyMap(CompilationUnitSyntax root)
    {
        var deps = new Dictionary<string, List<string>>();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var name = classDecl.Identifier.Text;
            var classDeps = new HashSet<string>();

            // Dependências via construtor (injeção de dependência)
            foreach (var ctor in classDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            foreach (var param in ctor.ParameterList.Parameters)
            {
                var typeName = param.Type?.ToString().TrimStart('I') ?? "";
                if (!IsSystemType(typeName) && typeName != name)
                    classDeps.Add(typeName);
            }

            // Dependências via campos
            foreach (var field in classDecl.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                var typeName = field.Declaration.Type.ToString();
                if (!IsSystemType(typeName))
                    classDeps.Add(typeName);
            }

            // Dependências via herança
            if (classDecl.BaseList != null)
            foreach (var baseType in classDecl.BaseList.Types)
                classDeps.Add(baseType.Type.ToString());

            deps[name] = [.. classDeps];
        }

        return deps;
    }

    private List<List<string>> DetectPotentialCycles(Dictionary<string, List<string>> deps)
    {
        var cycles = new List<List<string>>();
        var visited = new HashSet<string>();
        var stack = new Stack<string>();

        foreach (var node in deps.Keys)
        {
            if (!visited.Contains(node))
                DfsCycleDetect(node, deps, visited, stack, cycles);
        }

        return cycles;
    }

    private void DfsCycleDetect(
        string node,
        Dictionary<string, List<string>> deps,
        HashSet<string> visited,
        Stack<string> stack,
        List<List<string>> cycles)
    {
        visited.Add(node);
        stack.Push(node);

        if (deps.TryGetValue(node, out var neighbors))
        foreach (var neighbor in neighbors)
        {
            if (stack.Contains(neighbor))
            {
                var cycle = stack.TakeWhile(n => n != neighbor).Prepend(neighbor).ToList();
                cycles.Add(cycle);
            }
            else if (!visited.Contains(neighbor))
            {
                DfsCycleDetect(neighbor, deps, visited, stack, cycles);
            }
        }

        stack.Pop();
    }

    private async Task<CodeMetrics> ComputeMetricsAsync(List<SyntaxTree> trees)
    {
        var classCount = 0;
        var methodCount = 0;
        var lineCount = 0;
        var complexity = 0;

        foreach (var tree in trees)
        {
            var root = await tree.GetRootAsync();
            classCount += root.DescendantNodes().OfType<ClassDeclarationSyntax>().Count();
            methodCount += root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count();
            lineCount += (await tree.GetTextAsync()).Lines.Count;

            // Complexidade ciclomática simplificada
            complexity += root.DescendantNodes()
                .Count(n => n is IfStatementSyntax or WhileStatementSyntax
                    or ForStatementSyntax or ForEachStatementSyntax
                    or CaseSwitchLabelSyntax or ConditionalExpressionSyntax);
        }

        return new CodeMetrics(classCount, methodCount, lineCount, complexity);
    }

    private static string? BuildSuggestedFix(Diagnostic diagnostic)
    {
        return diagnostic.Id switch
        {
            "CS0246" => $"Adicione o using correto ou instale o pacote NuGet para '{diagnostic.GetMessage()}'",
            "CS0103" => "Verifique o namespace ou adicione o using necessário",
            "CS1061" => "O membro não existe neste tipo — verifique o nome ou o tipo correto",
            "CS8600" or "CS8601" or "CS8602" => "Adicione verificação de null ou use o operador '?'",
            "CS0501" => "Implemente o corpo do método ou adicione 'abstract'",
            _ => null
        };
    }

    private static bool IsSystemType(string typeName) =>
        typeName.StartsWith("System.") ||
        typeName is "string" or "int" or "bool" or "long" or "double" or "float"
            or "decimal" or "byte" or "char" or "object" or "void" or "var"
            or "Task" or "ValueTask" or "IEnumerable" or "IList" or "List"
            or "Dictionary" or "HashSet" or "CancellationToken" or "DateTime"
            or "Guid" or "TimeSpan" or "Action" or "Func" or "ILogger";

    private static IReadOnlyList<MetadataReference> BuildDefaultReferences()
    {
        var refs = new List<MetadataReference>();
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;

        if (trustedAssemblies != null)
        {
            var important = new[]
            {
                "System.Runtime", "System.Collections", "System.Linq",
                "System.Threading", "System.Threading.Tasks",
                "Microsoft.CSharp", "netstandard", "System.Private.CoreLib"
            };

            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (important.Any(i => name.Contains(i, StringComparison.OrdinalIgnoreCase)))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Fallback com tipos básicos
        if (refs.Count == 0)
            refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        return refs;
    }
}
