using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private static readonly Regex LowerCode = new(
        "^[a-z][a-z0-9_]*(?:[.-][a-z0-9_]+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex LowerWordCode = new(
        "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex LowerLiteralCode = new(
        "^[a-z][a-z0-9_]*(?:[.-][a-z0-9_]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex UpperCode = new(
        "^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+$", RegexOptions.CultureInvariant);
    /// <summary>A text that starts like a code and carries the runtime type name of an object right after it
    /// (<c>"native-bot.native-error:" + error.GetType().Name</c>): wherever it ends up, the type name belongs in the detail beside a fixed code.</summary>
    private static readonly Regex ExceptionTypeInCode = new(
        "^[a-z][a-z0-9_]*(?:[.-][a-z0-9_]+)+[:.-]?\\{exceptionType\\}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "tests", "tools", "bin", "obj", "media", "Release"
    };
    private static readonly string[] NonCodeSuffixes =
    {
        ".asset", ".bank", ".bytes", ".cfg", ".cs", ".dll", ".fbx", ".forge", ".glb", ".gltf",
        ".html", ".json", ".log", ".mat", ".md", ".ogg", ".png", ".prefab", ".shader", ".txt",
        ".wav", ".xml", ".yaml", ".yml"
    };

    public static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("Usage: DiagnosticCodeScan <repository-root> [output-json-path]");
            return 2;
        }

        try
        {
            var repositoryRoot = Path.GetFullPath(args[0]);
            var modsRoot = Path.Combine(repositoryRoot, "Mods");
            if (!Directory.Exists(modsRoot))
                throw new DirectoryNotFoundException("Repository root has no Mods directory: " + repositoryRoot);

            var files = Directory.EnumerateFiles(modsRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !Excluded(repositoryRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = files.AsParallel().AsOrdered().Select(path => CSharpSyntaxTree.ParseText(
                File.ReadAllText(path), parseOptions, path, Encoding.UTF8)).ToArray();
            // The generated tables (block table and result-code vocabulary, wire tables, diagnostic code constants) are not
            // tracked and live outside Mods. They join the compilation so a constant of theirs binds to its value, and they
            // are not scanned themselves: such a constant counts as used only where scanned source names it.
            var generatedRoot = Path.Combine(repositoryRoot, "artifacts", "generated");
            var generated = (Directory.Exists(generatedRoot)
                    ? Directory.EnumerateFiles(generatedRoot, "*.g.cs", SearchOption.AllDirectories) : Array.Empty<string>())
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path, Encoding.UTF8)).ToHashSet();
            if (generated.Count == 0)
                throw new FileNotFoundException("No generated table under " + generatedRoot + "; run pnpm generate:ephemeral.");
            var parseError = trees.Concat(generated).SelectMany(tree => tree.GetDiagnostics())
                .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (parseError != null) throw new InvalidDataException(parseError.ToString());

            var compilation = CSharpCompilation.Create(
                "DiagnosticCodeScan.Input",
                trees.Concat(generated),
                PlatformReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var generatedConstants = GeneratedConstants(compilation, generated, repositoryRoot);
            // One file's hits do not depend on another's and the output below is fully sorted, so the files are bound in parallel.
            var hits = new HashSet<Hit>();
            foreach (var found in trees.AsParallel().Select(tree =>
            {
                var treeHits = new HashSet<Hit>();
                var relative = ForgeCore.PosixPath.ToPosix(Path.GetRelativePath(repositoryRoot, tree.FilePath));
                Scan(tree.GetRoot(), compilation.GetSemanticModel(tree, ignoreAccessibility: true), relative, treeHits, generatedConstants);
                return treeHits;
            }).ToArray())
                hits.UnionWith(found);

            var semanticHits = hits.Where(hit => hit.Kind != "literal-use")
                .GroupBy(hit => (hit.Code, hit.Path, hit.Line))
                .Select(group => group.OrderBy(hit => KindPriority(hit.Kind)).ThenBy(hit => hit.Kind, StringComparer.Ordinal).First())
                .Concat(hits.Where(hit => hit.Kind == "literal-use"));
            var output = semanticHits
                .OrderBy(hit => hit.Path, StringComparer.Ordinal)
                .ThenBy(hit => hit.Line)
                .ThenBy(hit => hit.Code, StringComparer.Ordinal)
                .ThenBy(hit => hit.Kind, StringComparer.Ordinal)
                .Select(hit => new { code = hit.Code, path = hit.Path, line = hit.Line, kind = hit.Kind })
                .ToArray();
            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            if (args.Length == 1) Console.WriteLine(json);
            else
            {
                var outputPath = WriteJson(args[1], json);
                Console.WriteLine($"DiagnosticCodeScan: wrote {output.Length} entries to {outputPath}");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("DiagnosticCodeScan: " + error.Message);
            return 1;
        }
    }

    private static string WriteJson(string path, string json)
    {
        var outputPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("Output path has no parent directory: " + path);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory,
            "." + Path.GetFileName(outputPath) + "." + Environment.ProcessId + ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return outputPath;
    }

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The .NET platform assembly list is unavailable.");
        return trusted.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
    }

    private static bool Excluded(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(ExcludedDirectories.Contains);
    }

    /// <summary>The string constants the generated tables declare, by name. A class one of them fills must not declare a
    /// string constant by hand as well: its value would come from no table.</summary>
    private static ILookup<string, IFieldSymbol> GeneratedConstants(
        CSharpCompilation compilation, HashSet<SyntaxTree> generated, string repositoryRoot)
    {
        var constants = new List<IFieldSymbol>();
        foreach (var tree in generated)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
                if (model.GetDeclaredSymbol(declaration) is IFieldSymbol { IsConst: true, ConstantValue: string } field) constants.Add(field);
        }
        foreach (var type in constants.Select(field => field.ContainingType).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
                if (field is { IsConst: true, ConstantValue: string } && field.DeclaringSyntaxReferences[0] is var declared
                    && !generated.Contains(declared.SyntaxTree))
                    throw new InvalidDataException(
                        $"{ForgeCore.PosixPath.ToPosix(Path.GetRelativePath(repositoryRoot, declared.SyntaxTree.FilePath))}:"
                        + $"{declared.GetSyntax().GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {type.Name}.{field.Name} is written by hand "
                        + "in a class whose constants are generated; register it in the table that generates the class.");
        return constants.ToLookup(field => field.Name);
    }

    private static void Scan(SyntaxNode root, SemanticModel model, string path, HashSet<Hit> hits, ILookup<string, IFieldSymbol> generatedConstants)
    {
        // A generated constant has no literal in scanned source: naming it is the use its literal would have been.
        foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!generatedConstants.Contains(name.Identifier.ValueText)
                || model.GetSymbolInfo(name).Symbol is not IFieldSymbol { ConstantValue: string value } field
                || !generatedConstants[field.Name].Contains(field, SymbolEqualityComparer.Default)
                || !LiteralCode(value)) continue;
            hits.Add(new Hit(value, path, name.GetLocation().GetLineSpan().StartLinePosition.Line + 1, "literal-use"));
        }

        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Initializer == null) continue;
            var name = variable.Identifier.ValueText;
            if (IsConst(variable) && IsCodeConstant(name, ContainingTypeName(variable)))
                AddExpression(variable.Initializer.Value, model, path, ConstantKind(name, ContainingTypeName(variable)), hits,
                    allowWord: UnambiguousCodeTarget(name));
            else if (CodeTarget(name))
                AddExpression(variable.Initializer.Value, model, path, TargetKind(name, "assignment"), hits,
                    allowWord: UnambiguousCodeTarget(name));
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
            if (parameter.Default != null && CodeTarget(parameter.Identifier.ValueText))
                AddExpression(parameter.Default.Value, model, path,
                    TargetKind(parameter.Identifier.ValueText, "default"), hits,
                    allowWord: UnambiguousCodeTarget(parameter.Identifier.ValueText));

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (TargetName(assignment.Left) is { } name && CodeTarget(name))
                AddExpression(assignment.Right, model, path, TargetKind(name, "assignment"), hits,
                    allowWord: UnambiguousCodeTarget(name));

        foreach (var member in root.DescendantNodes().OfType<AnonymousObjectMemberDeclaratorSyntax>())
            if (member.NameEquals?.Name.Identifier.ValueText is { } name && CodeTarget(name))
                AddExpression(member.Expression, model, path, TargetKind(name, "assignment"), hits,
                    allowWord: UnambiguousCodeTarget(name));

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = Method(model.GetSymbolInfo(invocation));
            if (method is { Name: "Of", ContainingType.Name: "DynamicDiagnosticCodes" })
            {
                AddExpansionCall(invocation, model, path, hits);
                continue;
            }
            AddArguments(invocation.ArgumentList.Arguments, method, model, path,
                CallKind(method, invocation.Expression.ToString(), objectCreation: false), hits);
            AddKeyValue(invocation.ArgumentList.Arguments, method, model, path, hits);
            if (method == null && UnboundCodedError(invocation) is { } code && !ForwardedCode(code, model))
            {
                // A code this scan can neither read nor match against a registered pattern is refused, never passed.
                if (ConstantString(code, model) == null && !DynamicCodeExpressions(code, model).Any())
                    throw new InvalidDataException($"{path}:{code.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: "
                        + $"cannot determine the code of {invocation.Expression}({code}, ...); pass a constant code or a registered pattern.");
                AddExpression(code, model, path, "error-code-argument", hits, allowWord: true);
            }
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.ArgumentList == null) continue;
            var method = Method(model.GetSymbolInfo(creation));
            var typeName = creation.Type.ToString();
            AddArguments(creation.ArgumentList.Arguments, method, model, path,
                CallKind(method, typeName, objectCreation: true), hits);
            if (ExceptionLike(typeName) && creation.ArgumentList.Arguments.Count > 0
                && !ArgumentHasCodeParameter(creation.ArgumentList.Arguments[0], method, 0))
                AddExpression(creation.ArgumentList.Arguments[0].Expression, model, path, "exception", hits,
                    allowWord: false);
        }

        foreach (var creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            var method = Method(model.GetSymbolInfo(creation));
            AddArguments(creation.ArgumentList.Arguments, method, model, path,
                CallKind(method, method?.ContainingType.Name ?? "new", objectCreation: true), hits);
            if (method != null && ExceptionLike(method.ContainingType.Name) && creation.ArgumentList.Arguments.Count > 0
                && !ArgumentHasCodeParameter(creation.ArgumentList.Arguments[0], method, 0))
                AddExpression(creation.ArgumentList.Arguments[0].Expression, model, path, "exception", hits,
                    allowWord: false);
        }

        foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (expression is not (InterpolatedStringExpressionSyntax or BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression })
                || expression.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }
                || expression.Parent is ParenthesizedExpressionSyntax { Parent: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } }
                || model.GetConstantValue(expression).HasValue) continue;
            var builder = new StringBuilder();
            AppendPattern(expression, model, builder);
            var pattern = builder.ToString();
            if (!ExceptionTypeInCode.IsMatch(pattern)) continue;
            var line = expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            hits.Add(new Hit(pattern, path, line, "exception-type-in-code"));
        }
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
            var value = literal.Token.ValueText;
            if (!LiteralCode(value)) continue;
            var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            hits.Add(new Hit(value, path, line, "literal-use"));
        }
    }

    /// <summary>`DynamicDiagnosticCodes.Of(pattern, holes...)`: the pattern is the registered key, and it must name as many
    /// holes as the call fills, so the runtime table and the central table cannot drift apart.</summary>
    private static void AddExpansionCall(InvocationExpressionSyntax invocation, SemanticModel model, string path, HashSet<Hit> hits)
    {
        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0 || ConstantString(arguments[0].Expression, model) is not { } pattern)
            throw new InvalidDataException($"{path}:{line}: DynamicDiagnosticCodes.Of needs a constant pattern as its first argument.");
        var holes = Regex.Matches(pattern, "\\{[^}]*\\}").Count;
        if (arguments.Count - 1 != holes)
            throw new InvalidDataException($"{path}:{line}: pattern {pattern} names {holes} holes but the call fills {arguments.Count - 1}.");
        hits.Add(new Hit(pattern, path, line, "dynamic-code:expansion-call"));
    }
    private static void AddArguments(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IMethodSymbol? method,
        SemanticModel model,
        string path,
        string callKind,
        HashSet<Hit> hits)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var name = argument.NameColon?.Name.Identifier.ValueText ?? ParameterName(method, index);
            if (name == null || !CodeTarget(name)) continue;
            var kind = callKind == "code" ? TargetKind(name, "argument") : callKind;
            AddExpression(argument.Expression, model, path, kind, hits,
                allowWord: UnambiguousCodeTarget(name));
        }
    }

    private static void AddKeyValue(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IMethodSymbol? method,
        SemanticModel model,
        string path,
        HashSet<Hit> hits)
    {
        // Schema builders declare a field named "code" with a type, rather than emit a code value.
        if (ParameterName(method, 1) is "type" or "schema") return;
        if (arguments.Count < 2 || ConstantString(arguments[0].Expression, model) is not { } key
            || !CodeTarget(key)) return;
        AddExpression(arguments[1].Expression, model, path, TargetKind(key, "value"), hits,
            allowWord: UnambiguousCodeTarget(key));
    }

    private static void AddExpression(
        ExpressionSyntax expression,
        SemanticModel model,
        string path,
        string kind,
        HashSet<Hit> hits,
        bool allowWord)
    {
        foreach (var candidate in StaticCodeExpressions(expression, model, allowWord))
        {
            var line = candidate.Expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            hits.Add(new Hit(candidate.Code, path, line, kind));
        }
        foreach (var candidate in DynamicCodeExpressions(expression, model))
        {
            var line = candidate.Expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            hits.Add(new Hit(candidate.Pattern, path, line, "dynamic-code:" + kind));
        }
    }

    /// <summary>A code built by concatenation or interpolation: its text with every non-constant part written as
    /// <c>{name}</c>. Only shapes that read as a code once the holes are filled are reported.</summary>
    private static IEnumerable<(ExpressionSyntax Expression, string Pattern)> DynamicCodeExpressions(
        ExpressionSyntax expression,
        SemanticModel model)
    {
        if (model.GetConstantValue(expression).HasValue) yield break;
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                foreach (var row in DynamicCodeExpressions(parenthesized.Expression, model)) yield return row;
                yield break;
            case ConditionalExpressionSyntax conditional:
                foreach (var row in DynamicCodeExpressions(conditional.WhenTrue, model)) yield return row;
                foreach (var row in DynamicCodeExpressions(conditional.WhenFalse, model)) yield return row;
                yield break;
            case SwitchExpressionSyntax @switch:
                foreach (var arm in @switch.Arms)
                    foreach (var row in DynamicCodeExpressions(arm.Expression, model)) yield return row;
                yield break;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var row in DynamicCodeExpressions(binary.Left, model)) yield return row;
                foreach (var row in DynamicCodeExpressions(binary.Right, model)) yield return row;
                yield break;
            case InterpolatedStringExpressionSyntax or BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }:
                var builder = new StringBuilder();
                AppendPattern(expression, model, builder);
                var pattern = builder.ToString();
                if (CodePattern(pattern)) yield return (expression, pattern);
                yield break;
        }
    }

    private static void AppendPattern(ExpressionSyntax expression, SemanticModel model, StringBuilder builder)
    {
        if (model.GetConstantValue(expression) is { HasValue: true, Value: string constant })
        {
            builder.Append(constant);
            return;
        }
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                AppendPattern(parenthesized.Expression, model, builder);
                return;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                AppendPattern(binary.Left, model, builder);
                AppendPattern(binary.Right, model, builder);
                return;
            case InterpolatedStringExpressionSyntax interpolated:
                foreach (var part in interpolated.Contents)
                {
                    if (part is InterpolatedStringTextSyntax text) builder.Append(text.TextToken.ValueText);
                    else if (part is InterpolationSyntax hole) AppendPattern(hole.Expression, model, builder);
                }
                return;
            default:
                builder.Append('{').Append(HoleName(expression)).Append('}');
                return;
        }
    }

    private static string HoleName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Name" or "FullName", Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetType" } } } => "exceptionType",
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Replace" or "ToLowerInvariant" or "ToLower" or "ToString" or "Trim" } member } => HoleName(member.Expression),
        _ => "value"
    };

    private static bool CodePattern(string pattern)
    {
        var filled = Regex.Replace(pattern, "\\{[^}]*\\}", "x");
        var literal = Regex.Replace(pattern, "\\{[^}]*\\}", "");
        return literal.Any(char.IsLetter) && LowerLiteralCode.IsMatch(filled) && !PathOrResource(filled);
    }

    private static IEnumerable<(ExpressionSyntax Expression, string Code)> StaticCodeExpressions(
        ExpressionSyntax expression,
        SemanticModel model,
        bool allowWord)
    {
        if (ConstantString(expression, model) is { } constant)
        {
            if (MachineCode(constant, allowWord)) yield return (expression, constant);
            yield break;
        }

        IEnumerable<(ExpressionSyntax Expression, string Code)> Children() => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => StaticCodeExpressions(parenthesized.Expression, model, allowWord),
            CastExpressionSyntax cast => StaticCodeExpressions(cast.Expression, model, allowWord),
            CheckedExpressionSyntax checkedExpression => StaticCodeExpressions(checkedExpression.Expression, model, allowWord),
            PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                => StaticCodeExpressions(postfix.Operand, model, allowWord),
            ConditionalExpressionSyntax conditional =>
                StaticCodeExpressions(conditional.WhenTrue, model, allowWord)
                    .Concat(StaticCodeExpressions(conditional.WhenFalse, model, allowWord)),
            SwitchExpressionSyntax @switch => @switch.Arms
                .SelectMany(arm => StaticCodeExpressions(arm.Expression, model, allowWord)),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression) =>
                StaticCodeExpressions(binary.Left, model, allowWord)
                    .Concat(StaticCodeExpressions(binary.Right, model, allowWord)),
            _ => Array.Empty<(ExpressionSyntax, string)>()
        };
        foreach (var candidate in Children()) yield return candidate;
    }

    private static string? ConstantString(ExpressionSyntax expression, SemanticModel model)
    {
        var constant = model.GetConstantValue(expression);
        return constant.HasValue ? constant.Value as string : null;
    }

    private static bool MachineCode(string code, bool allowWord)
    {
        if (!(LowerCode.IsMatch(code) || UpperCode.IsMatch(code) || allowWord && LowerWordCode.IsMatch(code))) return false;
        return !PathOrResource(code);
    }

    private static bool LiteralCode(string code) =>
        (LowerLiteralCode.IsMatch(code) || UpperCode.IsMatch(code)) && !PathOrResource(code);

    private static bool PathOrResource(string code)
    {
        if (code.StartsWith("forge.", StringComparison.Ordinal)
            || code.StartsWith("gtfo.", StringComparison.Ordinal)
            || code.StartsWith("dev.", StringComparison.Ordinal)
            || code.StartsWith("com.", StringComparison.Ordinal)
            || code.StartsWith("org.", StringComparison.Ordinal)
            || code.StartsWith("steam.", StringComparison.Ordinal)
            || code.StartsWith("unity.", StringComparison.Ordinal)
            || code.StartsWith("bepinex.", StringComparison.Ordinal)) return true;
        return NonCodeSuffixes.Any(suffix => code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConst(VariableDeclaratorSyntax variable) => variable.Parent?.Parent switch
    {
        FieldDeclarationSyntax field => field.Modifiers.Any(SyntaxKind.ConstKeyword),
        LocalDeclarationStatementSyntax local => local.Modifiers.Any(SyntaxKind.ConstKeyword),
        _ => false
    };

    private static bool IsCodeConstant(string name, string? typeName)
    {
        if (name.Contains("Prefix", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Suffix", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pattern", StringComparison.OrdinalIgnoreCase)) return false;
        return typeName != null && (typeName.EndsWith("Log", StringComparison.Ordinal)
                || typeName.Contains("Log", StringComparison.Ordinal) && typeName.EndsWith("Codes", StringComparison.Ordinal))
            || name.StartsWith("Code", StringComparison.Ordinal)
            || name.EndsWith("Code", StringComparison.Ordinal);
    }

    private static bool CodeTarget(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "code" or "reason" or "reasoncode" or "nativecode" or "errorcode" or "logcode"
            or "refusal" or "refusalcode" or "failurecode" or "resultcode"
            || name.EndsWith("Code", StringComparison.Ordinal)
            || name.EndsWith("_code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UnambiguousCodeTarget(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal);
        return normalized.Equals("code", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Code", StringComparison.Ordinal)
            || name.EndsWith("_code", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TargetName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        ElementAccessExpressionSyntax element when element.ArgumentList.Arguments.Count == 1
            && element.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        _ => null
    };

    private static string? ContainingTypeName(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

    private static string ConstantKind(string name, string? typeName)
    {
        if (typeName != null && typeName.Contains("Log", StringComparison.Ordinal)) return "log-constant";
        return TargetKind(name, "constant");
    }

    private static string TargetKind(string name, string source)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized.Contains("native", StringComparison.Ordinal)) return "native-code-" + source;
        if (normalized.Contains("reason", StringComparison.Ordinal) || normalized.Contains("refusal", StringComparison.Ordinal))
            return "reason-code-" + source;
        if (normalized.Contains("error", StringComparison.Ordinal) || normalized.Contains("failure", StringComparison.Ordinal))
            return "error-code-" + source;
        return "code-" + source;
    }

    private static IMethodSymbol? Method(SymbolInfo info)
    {
        if (info.Symbol is IMethodSymbol method) return method;
        var candidates = info.CandidateSymbols.OfType<IMethodSymbol>().Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string? ParameterName(IMethodSymbol? method, int argumentIndex)
    {
        if (method == null || method.Parameters.Length == 0) return null;
        if (argumentIndex < method.Parameters.Length) return method.Parameters[argumentIndex].Name;
        return method.Parameters[^1].IsParams ? method.Parameters[^1].Name : null;
    }

    /// <summary>The coded error form every package's log shares, `Error(code, detail)` / `ErrorOnce(code, detail)`, at a call
    /// the scan cannot bind (the packages compile together here, so a receiver such as `Plugin` is ambiguous): the first of
    /// two arguments is a code by its place, so an unregistered one cannot pass as a plain literal. Null for any other call.</summary>
    private static ExpressionSyntax? UnboundCodedError(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
        var arguments = invocation.ArgumentList.Arguments;
        return name is "Error" or "ErrorOnce" && arguments.Count == 2 && arguments[0].NameColon == null
            ? arguments[0].Expression : null;
    }

    /// <summary>A wrapper handing its own code-named parameter on (`Error(string code, string detail) => Log.Error(code,
    /// detail)`): the code is whatever each caller writes, and the callers' arguments are what this scan reads. Only an
    /// ordinary method's parameter is a wrapper's: a lambda's parameter (`codes.ForEach(code => Plugin.Error(code, d))`)
    /// is fed by the code that builds the list, which no caller argument names, so it is not passed.</summary>
    private static bool ForwardedCode(ExpressionSyntax code, SemanticModel model)
        => code is IdentifierNameSyntax && model.GetSymbolInfo(code).Symbol is IParameterSymbol parameter && CodeTarget(parameter.Name)
            && parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Ordinary };

    private static bool ArgumentHasCodeParameter(ArgumentSyntax argument, IMethodSymbol? method, int index) =>
        CodeTarget(argument.NameColon?.Name.Identifier.ValueText ?? ParameterName(method, index) ?? "");

    private static string CallKind(IMethodSymbol? method, string writtenName, bool objectCreation)
    {
        var typeName = method?.ContainingType.Name ?? writtenName;
        var methodName = method?.Name ?? writtenName;
        if (ExceptionLike(typeName)) return "exception";
        if (typeName.Contains("Log", StringComparison.OrdinalIgnoreCase)
            || methodName.StartsWith("Log", StringComparison.OrdinalIgnoreCase)
            || methodName is "Record" or "Emit") return "log-sink";
        if (typeName.Contains("Reason", StringComparison.OrdinalIgnoreCase)) return "reason-sink";
        if (objectCreation && (typeName.EndsWith("Result", StringComparison.Ordinal)
                || typeName.EndsWith("Outcome", StringComparison.Ordinal)
                || typeName.EndsWith("Failure", StringComparison.Ordinal)
                || typeName.EndsWith("Refusal", StringComparison.Ordinal))) return "result";
        if (methodName is "Reject" or "Refuse" or "Fail") return "result";
        return "code";
    }

    private static bool ExceptionLike(string name)
    {
        var simple = name.Split('.').Last();
        return simple.EndsWith("Exception", StringComparison.Ordinal)
            || simple.EndsWith("Refusal", StringComparison.Ordinal);
    }

    private static int KindPriority(string kind) => kind switch
    {
        "exception-type-in-code" => -1,
        "log-constant" => 0,
        "log-sink" or "reason-sink" or "exception" or "result" => 1,
        _ when kind.StartsWith("native-code-", StringComparison.Ordinal) => 2,
        _ when kind.StartsWith("reason-code-", StringComparison.Ordinal) => 3,
        _ when kind.StartsWith("error-code-", StringComparison.Ordinal) => 4,
        _ => 5
    };

    private sealed record Hit(string Code, string Path, int Line, string Kind);
}
