using System.Globalization;
using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// Conservative parser of common timestamped .NET / ISO log messages.
/// Undated lines are reported, not assigned a guessed timestamp.
/// </summary>
public static partial class LogParser
{
    public const int MaxEvents = 200_000;

    public static LogParseResult Parse(
        string content, CancellationToken cancellationToken = default, string sourceFile = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        var parsed = new List<LogEvent>();
        int total = 0, skipped = 0, continuations = 0;
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            total++;
            if ((total & 511) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Match match = TimestampPattern().Match(line);
            if (!match.Success ||
                !DateTimeOffset.TryParse(
                    match.Groups["time"].Value.Replace(',', '.'),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
                    out DateTimeOffset timestamp))
            {
                // A stack trace is continuation text, not a new dated event.
                // Count it but do not invent time or severity for it.
                if (parsed.Count > 0 && (line.StartsWith("   ", StringComparison.Ordinal) ||
                    line.StartsWith("\t", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("at ", StringComparison.OrdinalIgnoreCase)))
                {
                    continuations++;
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    skipped++;
                }
                continue;
            }

            string remainder = match.Groups["rest"].Value.Trim();
            Match level = SeverityPattern().Match(remainder[..Math.Min(remainder.Length, 60)]);
            LogSeverity severity = level.Success ? ParseSeverity(level.Groups["level"].Value)
                : LogSeverity.Unknown;
            string message = level.Success
                ? remainder[(level.Index + level.Length)..].TrimStart(' ', '\t', ']', ':', '-', '|')
                : remainder;
            if (message.Length == 0)
            {
                message = "(empty message)";
            }
            if (message.Length > 2_000)
            {
                message = message[..2_000];
            }
            if (parsed.Count >= MaxEvents)
            {
                throw new InvalidDataException(
                    $"Log exceeds {MaxEvents:N0} parsed events. Select a smaller time range.");
            }
            parsed.Add(new LogEvent(timestamp, severity, message, total)
            {
                SourceFile = sourceFile
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new LogParseResult(parsed, total, skipped, continuations);
    }

    private static LogSeverity ParseSeverity(string value) => value.ToUpperInvariant() switch
    {
        "TRACE" => LogSeverity.Trace,
        "DEBUG" => LogSeverity.Debug,
        "INFO" => LogSeverity.Info,
        "WARN" or "WARNING" => LogSeverity.Warning,
        "ERR" or "ERROR" => LogSeverity.Error,
        "CRITICAL" or "FATAL" => LogSeverity.Fatal,
        _ => LogSeverity.Unknown
    };

    [GeneratedRegex(
        @"^\s*\[?(?<time>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?)\]?\s*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(
        @"(?:^|[\s\[\]\(])(?<level>TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|ERR|FATAL|CRITICAL)(?=$|[\s\]\):|,\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeverityPattern();
}
