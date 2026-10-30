using System.Text;
using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Anomaly;

public sealed record LogSourceMatch(
    string RelativePath, int LineNumber, string CodeTemplate, bool Exact)
{
    public string Description =>
        $"{RelativePath}:{LineNumber} — {(Exact ? "exact literal" : "template")} match; " +
        "inspect the source before concluding where an incident originated.";
}

/// <summary>
/// Builds a temporary in-memory map of straightforward C# logging statements
/// from a local checkout. Does not send, embed or persist source files.
/// This is an intentionally conservative text matcher, not Roslyn analysis.
/// </summary>
public sealed class LogSourceContextIndex
{
    private const int MaxFiles = 4_000;
    private const long MaxSourceBytes = 48L * 1024 * 1024;
    private const int MaxSingleFileBytes = 1_024 * 1_024;
    private const int MaxTemplates = 20_000;
    private readonly IReadOnlyList<Entry> _entries;
    public int TemplateCount => _entries.Count;
    public int FilesScanned { get; }

    private LogSourceContextIndex(IReadOnlyList<Entry> entries, int filesScanned)
    {
        _entries = entries;
        FilesScanned = filesScanned;
    }

    private sealed record Entry(LogSourceMatch Source, Regex Pattern, int LiteralLength);

    // Examples: logger?.Info("A connected"); logger.Error($"Card {id} not found")
    private static readonly Regex Direct = new(
        @"\b(?<method>Trace|Debug|Info|Warn|Warning|Error|Fatal)\s*\(\s*(?<interpolated>\$)?""(?<text>(?:\\.|[^""\\])*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    // Examples: Helper.SimpleLogE(logger, "Disconnected") and
    // Helper.SimpleLogI(logger, $"Connection {id} opened").
    private static readonly Regex Helper = new(
        @"\b(?<method>SimpleLog[IDWE]|LogDebug|LogError|LogInfo)\s*(?:<[^>]+>)?\s*\(\s*[^,\r\n]{1,100},\s*(?<interpolated>\$)?""(?<text>(?:\\.|[^""\\])*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex Placeholder = new(
        @"(?<!\{)\{[^{}]+\}(?!\})",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex RuntimeCaller = new(
        @"(?<path>(?:[a-zA-Z]:\\|/)[^\r\n]*?\.cs)\s*:\s*line\s*(?<line>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static LogSourceContextIndex Build(
        string rootFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
        if (!Directory.Exists(rootFolder))
        {
            throw new DirectoryNotFoundException(
                $"Source folder does not exist: {rootFolder}");
        }

        string root = Path.GetFullPath(rootFolder);
        var entries = new List<Entry>();
        int files = 0;
        long bytes = 0;

        foreach (string file in Directory.EnumerateFiles(
            root, "*.cs", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".git" or ".vs" or "node_modules"))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxSingleFileBytes)
            {
                continue;
            }
            if (++files > MaxFiles || (bytes += info.Length) > MaxSourceBytes)
            {
                throw new InvalidDataException(
                    "Source tree exceeds the safe scanning limit. Choose a smaller solution folder.");
            }

            int lineNumber = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNumber++;
                if ((lineNumber & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in Direct.Matches(line).Cast<Match>()
                    .Concat(Helper.Matches(line).Cast<Match>()))
                {
                    string template = match.Groups["text"].Value
                        .Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal);
                    if (template.Length < 12 || template.Length > 1_024)
                    {
                        continue;
                    }

                    MatchCollection placeholders = Placeholder.Matches(template);
                    var pattern = new StringBuilder("^");
                    int pos = 0;
                    int fixedLength = 0;
                    foreach (Match placeholder in placeholders)
                    {
                        string literal = template[pos..placeholder.Index];
                        fixedLength += literal.Trim().Length;
                        pattern.Append(Regex.Escape(literal)).Append(@".{0,180}?");
                        pos = placeholder.Index + placeholder.Length;
                    }
                    string last = template[pos..];
                    fixedLength += last.Trim().Length;
                    if (fixedLength < 10)
                    {
                        continue;
                    }
                    pattern.Append(Regex.Escape(last)).Append('$');

                    try
                    {
                        var matcher = new Regex(pattern.ToString(),
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(25));
                        var candidate = new LogSourceMatch(
                            relative, lineNumber, template, placeholders.Count == 0);
                        entries.Add(new Entry(candidate, matcher, fixedLength));
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (entries.Count >= MaxTemplates)
                    {
                        throw new InvalidDataException(
                            "Too many log templates. Select a smaller source folder.");
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LogSourceContextIndex(entries, files);
    }

    public LogSourceMatch? FindBest(string message, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Entry? best = null;
        foreach (Entry entry in _entries)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!entry.Pattern.IsMatch(message))
                {
                    continue;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (best is null || entry.LiteralLength > best.LiteralLength)
            {
                best = entry;
            }
        }
        return best?.Source;
    }

    /// <summary>
    /// Recognizes caller-location text generated by logging helpers (e.g.
    /// "C:\checkout\Service.cs: line 42"). It does not guarantee that the
    /// following error belongs to that caller: consumers must correlate time.
    /// </summary>
    public static string? TryReadCallerLocation(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Match match = RuntimeCaller.Match(message);
        if (!match.Success || !int.TryParse(match.Groups["line"].Value, out int line) ||
            line <= 0)
        {
            return null;
        }

        return $"{Path.GetFileName(match.Groups["path"].Value)}:{line}";
    }
}
