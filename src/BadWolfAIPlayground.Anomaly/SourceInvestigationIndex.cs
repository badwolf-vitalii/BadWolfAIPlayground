using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// A source location is a *candidate*, not a proven runtime origin.
/// Even an exact logging literal may appear in multiple places.
/// </summary>
public enum SourceEvidenceKind
{
    ExactLoggingLiteral,
    FormattedLoggingTemplate,
    ExplicitStackFrame,
    OperationMethodName
}

public sealed record SourceInvestigationCandidate(
    string RelativePath, int LineNumber, string TypeName, string MethodName,
    SourceEvidenceKind Kind, string Excerpt, string Conditions, string DirectCalls)
{
    public string Location => $"{RelativePath}:{LineNumber}";
    public string Symbol => string.IsNullOrEmpty(TypeName) ? MethodName
        : TypeName + "." + MethodName;
    public string EvidenceLabel => Kind switch
    {
        SourceEvidenceKind.ExactLoggingLiteral => "Exact logging literal",
        SourceEvidenceKind.FormattedLoggingTemplate => "Formatted logging template",
        SourceEvidenceKind.ExplicitStackFrame => "Stack frame location (runtime evidence)",
        _ => "Method name only (static hint)"
    };
}

/// <summary>
/// Syntax-only, offline index of C# source files explicitly selected by the
/// user. Roslyn parses declarations, invocations and enclosing conditions.
/// No compilation, assembly loading, code execution, semantic inference,
/// network request or persistence is involved.
/// </summary>
public sealed class SourceInvestigationIndex
{
    public const int MaxFiles = 4_000;
    public const int MaxSingleFileBytes = 1_048_576;
    public const long MaxSourceBytes = 48L * 1024 * 1024;
    public const int MaxLoggingSites = 20_000;
    public const int MaxMethods = 40_000;
    public const int MaxCandidates = 5;

    private readonly string _root;
    private readonly IReadOnlyList<LoggingSite> _sites;
    private readonly IReadOnlyList<SourceInvestigationCandidate> _methods;
    private readonly Dictionary<string, FileRecord> _files;
    private readonly Dictionary<string, SourceInvestigationCandidate[]> _methodsByName;

    public int FilesScanned => _files.Count;
    public int LoggingSites => _sites.Count;
    public int MethodsIndexed => _methods.Count;

    private sealed record LoggingSite(
        SourceInvestigationCandidate Candidate, Regex Matcher,
        int LiteralLength, string FixedPrefix, string FixedSuffix);
    private sealed record FileRecord(string RelativePath, int LineCount, long Bytes);

    private static readonly HashSet<string> LoggingNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Trace", "Debug", "Info", "Warn", "Warning", "Error", "Fatal",
        "SimpleLogI", "SimpleLogD", "SimpleLogW", "SimpleLogE",
        "LogTrace", "LogDebug", "LogInformation", "LogWarning",
        "LogError", "LogCritical"
    };

    private static readonly Regex Placeholder = new(
        @"(?<!\{)\{[^{}]+\}(?!\})", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex StackFrame = new(
        @"\bin\s+(?<path>(?:[a-zA-Z]:\\|/)?[^\r\n:]+?\.cs)\s*:\s*line\s+(?<line>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private SourceInvestigationIndex(
        string root, IReadOnlyList<LoggingSite> sites,
        IReadOnlyList<SourceInvestigationCandidate> methods,
        Dictionary<string, FileRecord> files)
    {
        _root = root;
        _sites = sites;
        _methods = methods;
        _files = files;
        _methodsByName = methods.GroupBy(method => method.MethodName,
            StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.LineNumber).ToArray(), StringComparer.Ordinal);
    }

    public static SourceInvestigationIndex Build(
        string rootFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
        if (!Directory.Exists(rootFolder))
            throw new DirectoryNotFoundException("The selected C# source folder does not exist.");
        string root = Path.GetFullPath(rootFolder);
        var sites = new List<LoggingSite>();
        var methods = new List<SourceInvestigationCandidate>();
        var files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        long usedBytes = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };

        foreach (string path in Directory.EnumerateFiles(root, "*.cs", options)
                     .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, path);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
                continue;

            FileInfo info = new(path);
            if (info.Length > MaxSingleFileBytes)
                continue;
            if (files.Count >= MaxFiles || usedBytes + info.Length > MaxSourceBytes)
                throw new InvalidDataException(
                    "The C# source tree exceeds the bounded local indexing budget.");
            usedBytes += info.Length;

            // Parse syntax only; there is no compile or dependency loading.
            string text = File.ReadAllText(path);
            SyntaxNode syntax = CSharpSyntaxTree.ParseText(text)
                .GetRoot(cancellationToken);
            int lineCount = syntax.SyntaxTree.GetText(cancellationToken).Lines.Count;
            files.Add(relative, new FileRecord(relative, lineCount, info.Length));

            foreach (SyntaxNode node in syntax.DescendantNodes().Where(n =>
                         n is MethodDeclarationSyntax or ConstructorDeclarationSyntax
                         or LocalFunctionStatementSyntax))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (methods.Count >= MaxMethods)
                    throw new InvalidDataException("Too many indexed C# methods.");
                string method = GetMethodName(node);
                if (string.IsNullOrEmpty(method))
                    continue;
                methods.Add(CreateCandidate(node, node, relative,
                    SourceEvidenceKind.OperationMethodName, cancellationToken));
            }

            foreach (InvocationExpressionSyntax invocation in syntax.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = InvokedName(invocation.Expression);
                if (!LoggingNames.Contains(name))
                    continue;
                if (!TryGetTemplate(invocation, out string template))
                    continue;
                if (!TryCreateMatcher(template, out Regex? matcher, out int literalLength))
                    continue;
                if (sites.Count >= MaxLoggingSites)
                    throw new InvalidDataException("Too many C# logging sites.");

                SyntaxNode containing = ContainingCallable(invocation) ?? invocation;
                SourceEvidenceKind kind = Placeholder.IsMatch(template)
                    ? SourceEvidenceKind.FormattedLoggingTemplate
                    : SourceEvidenceKind.ExactLoggingLiteral;
                MatchCollection placeholders = Placeholder.Matches(template);
                string prefix = placeholders.Count == 0 ? template :
                    template[..placeholders[0].Index];
                Match? last = placeholders.Count == 0 ? null :
                    placeholders[placeholders.Count - 1];
                string suffix = last is null ? template :
                    template[(last.Index + last.Length)..];
                sites.Add(new LoggingSite(
                    CreateCandidate(invocation, containing, relative, kind,
                        cancellationToken), matcher!, literalLength, prefix, suffix));
            }
        }

        return new SourceInvestigationIndex(root, sites, methods, files);
    }

    public IReadOnlyList<SourceInvestigationCandidate> FindLogSites(
        string message, int limit = MaxCandidates, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        int take = Math.Clamp(limit, 1, MaxCandidates);
        var matched = new List<LoggingSite>();
        foreach (LoggingSite site in _sites)
        {
            token.ThrowIfCancellationRequested();
            // Reject most unmatched templates with cheap deterministic
            // string checks before evaluating bounded regular expressions.
            if ((site.FixedPrefix.Length > 0 &&
                 !message.StartsWith(site.FixedPrefix,
                     StringComparison.OrdinalIgnoreCase)) ||
                (site.FixedSuffix.Length > 0 &&
                 !message.EndsWith(site.FixedSuffix,
                     StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                if (site.Matcher.IsMatch(message))
                    matched.Add(site);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological log message cannot stall the investigation.
            }
        }
        return matched.OrderByDescending(item =>
                item.Candidate.Kind == SourceEvidenceKind.ExactLoggingLiteral)
            .ThenByDescending(item => item.LiteralLength)
            .ThenBy(item => item.Candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Candidate.LineNumber)
            .Select(item => item.Candidate).Take(take).ToArray();
    }

    public IReadOnlyList<SourceInvestigationCandidate> FindMethods(
        string operationName, int limit = MaxCandidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return _methodsByName.TryGetValue(operationName, out var found)
            ? found.Take(Math.Clamp(limit, 1, MaxCandidates)).ToArray()
            : [];
    }

    /// <summary>
    /// A frame is considered only if its .cs source exists in the explicitly
    /// indexed checkout and the recorded physical line is in range.
    /// Basename collisions remain separate, ambiguous candidates.
    /// </summary>
    public IReadOnlyList<SourceInvestigationCandidate> FindStackFrames(
        string continuation, int limit = MaxCandidates,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var results = new List<SourceInvestigationCandidate>();
        foreach (Match match in StackFrame.Matches(continuation).Cast<Match>())
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(match.Groups["line"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
                line < 1)
                continue;
            string claimedPath = match.Groups["path"].Value.Replace('\\', '/');
            string filename = claimedPath.Split('/').Last();
            foreach (FileRecord file in _files.Values
                         .Where(item => string.Equals(
                             Path.GetFileName(item.RelativePath), filename,
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (line > file.LineCount)
                    continue;
                // A filename/line reference is not sufficient to choose the
                // containing method without a verified source span.
                results.Add(new SourceInvestigationCandidate(
                    file.RelativePath, line, "", "<stack frame>",
                    SourceEvidenceKind.ExplicitStackFrame,
                    ReadLineExcerpt(file.RelativePath, line),
                    "Stack frame supplied a source filename and line; check the checkout revision.",
                    "Runtime method invocation graph not established."));
                if (results.Count >= Math.Clamp(limit, 1, MaxCandidates))
                    return results;
            }
        }
        return results;
    }

    /// <summary>
    /// Resolve only locations supplied by this index. This guards against
    /// arbitrary file opening or traversal via untrusted log contents.
    /// </summary>
    public (string FullPath, int Line) ResolveSourceLocation(
        SourceInvestigationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!_files.TryGetValue(candidate.RelativePath, out FileRecord? source) ||
            candidate.LineNumber < 1 || candidate.LineNumber > source.LineCount)
            throw new InvalidOperationException("That location was not indexed.");

        string path = Path.GetFullPath(Path.Combine(_root, candidate.RelativePath));
        string prefix = Path.TrimEndingDirectorySeparator(_root) +
            Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Refusing to open a source outside the selected C# checkout.");
        return (path, candidate.LineNumber);
    }

    private string ReadLineExcerpt(string relative, int line)
    {
        try
        {
            string path = Path.GetFullPath(Path.Combine(_root, relative));
            if (!File.Exists(path))
                return "";
            string excerpt = string.Join(Environment.NewLine,
                File.ReadLines(path).Skip(Math.Max(0, line - 2)).Take(3)).Trim();
            return excerpt.Length > 480 ? excerpt[..480] + "..." : excerpt;
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static SourceInvestigationCandidate CreateCandidate(
        SyntaxNode site, SyntaxNode callable, string relative,
        SourceEvidenceKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string type = site.Ancestors().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.ValueText ?? "";
        string method = GetMethodName(callable);
        int sourceLine = site.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var sourceLines = site.SyntaxTree.GetText(token).Lines;
        int first = Math.Max(0, sourceLine - 2);
        int last = Math.Min(sourceLines.Count - 1, sourceLine + 1);
        string excerpt = string.Join(Environment.NewLine,
            Enumerable.Range(first, last - first + 1)
                .Select(i => sourceLines[i].ToString().TrimEnd()));
        if (excerpt.Length > 700)
            excerpt = excerpt[..700] + "...";

        IEnumerable<SyntaxNode> guardNodes = ReferenceEquals(site, callable)
            ? callable.DescendantNodes().OfType<IfStatementSyntax>().Take(4)
            : site.Ancestors().TakeWhile(node => node != callable);
        var guards = guardNodes
            .Select(node => node switch
            {
                IfStatementSyntax condition => "if (" +
                    condition.Condition.ToString() + ")",
                CatchClauseSyntax catchSyntax => "catch (" +
                    catchSyntax.Declaration?.ToString() + ")",
                _ => ""
            })
            .Where(value => value.Length > 0).Take(3)
            .Select(value => value.Length > 140 ? value[..140] + "..." : value);
        string conditions = string.Join("; ", guards);
        if (conditions.Length == 0)
            conditions = "No enclosing if/catch guard established for this site.";

        string calls = string.Join(", ", callable.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => InvokedName(invocation.Expression))
            .Where(name => name.Length > 0 && !LoggingNames.Contains(name))
            .Distinct(StringComparer.Ordinal).Take(8));
        if (calls.Length == 0)
            calls = "No direct invocations established.";
        return new SourceInvestigationCandidate(
            relative, sourceLine, type, method, kind, excerpt, conditions, calls);
    }

    private static SyntaxNode? ContainingCallable(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(parent =>
            parent is BaseMethodDeclarationSyntax or
                LocalFunctionStatementSyntax or AccessorDeclarationSyntax);

    private static string GetMethodName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        _ => "<top-level>"
    };

    private static string InvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => ""
    };

    private static bool TryGetTemplate(
        InvocationExpressionSyntax invocation, out string text)
    {
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                text = literal.Token.ValueText;
                return text.Length >= 10;
            }
            if (argument.Expression is InterpolatedStringExpressionSyntax formatted)
            {
                var parts = new StringBuilder();
                foreach (InterpolatedStringContentSyntax content in formatted.Contents)
                {
                    if (content is InterpolatedStringTextSyntax literalPart)
                        parts.Append(literalPart.TextToken.ValueText);
                    else if (content is InterpolationSyntax expr)
                        parts.Append('{').Append(expr.Expression.ToString()).Append('}');
                }
                text = parts.ToString();
                return text.Length >= 10;
            }
        }
        text = "";
        return false;
    }

    private static bool TryCreateMatcher(
        string template, out Regex? matcher, out int literalLength)
    {
        matcher = null;
        literalLength = 0;
        var pattern = new StringBuilder("^");
        int start = 0;
        foreach (Match placeholder in Placeholder.Matches(template).Cast<Match>())
        {
            string fragment = template[start..placeholder.Index];
            literalLength += fragment.Trim().Length;
            pattern.Append(Regex.Escape(fragment)).Append(@".{0,240}?");
            start = placeholder.Index + placeholder.Length;
        }
        string end = template[start..];
        literalLength += end.Trim().Length;
        pattern.Append(Regex.Escape(end)).Append('$');
        if (literalLength < 10 || template.Length > 2_048)
            return false;
        try
        {
            matcher = new Regex(pattern.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(25));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
