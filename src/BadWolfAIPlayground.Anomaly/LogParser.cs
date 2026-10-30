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
    public const int MaxContinuationLinesPerEvent = 32;
    public const int MaxContinuationCharactersPerEvent = 4_096;
    public const int MaxLineCharacters = 4 * 1024 * 1024;

    public static LogParseResult Parse(
        string content, CancellationToken cancellationToken = default, string sourceFile = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = new StringReader(content);
        return Parse(reader, cancellationToken, sourceFile, MaxEvents);
    }

    /// <summary>Incremental parser: the entire log text is never buffered.</summary>
    public static LogParseResult Parse(
        TextReader reader, CancellationToken cancellationToken = default,
        string sourceFile = "", int maxEvents = MaxEvents)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        var parsed = new List<LogEvent>();
        int total = 0, skipped = 0, continuations = 0;
        int capturedContinuationLines = 0;
        bool canAttachContinuation = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            total++;
            if ((total & 511) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > MaxLineCharacters || line.Contains('\0'))
                throw new InvalidDataException(
                    $"'{sourceFile}' contains an oversized individual line or " +
                    $"binary data at physical line {total:N0}.");

            Match match = TimestampPattern().Match(line);
            if (!match.Success ||
                !DateTimeOffset.TryParse(
                    match.Groups["time"].Value.Replace(',', '.'),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
                    out DateTimeOffset timestamp))
            {
                // Only contiguous exception/stack lines belong to the previous
                // dated entry. Do not invent a new timestamp or severity.
                if (canAttachContinuation && IsContinuationLine(line))
                {
                    continuations++;
                    LogEvent previous = parsed[^1];
                    if (capturedContinuationLines < MaxContinuationLinesPerEvent)
                    {
                        string fragment = line.TrimEnd();
                        int separator = previous.Continuation.Length == 0 ? 0 : 1;
                        int room = MaxContinuationCharactersPerEvent -
                            previous.Continuation.Length - separator;
                        if (room > 0)
                        {
                            string captured = fragment[..Math.Min(room, fragment.Length)];
                            parsed[^1] = previous with
                            {
                                Continuation = separator == 0
                                    ? captured : previous.Continuation + "\n" + captured,
                                ContinuationTruncated = previous.ContinuationTruncated ||
                                    captured.Length != fragment.Length
                            };
                            capturedContinuationLines++;
                        }
                        else
                        {
                            parsed[^1] = previous with { ContinuationTruncated = true };
                        }
                    }
                    else
                    {
                        parsed[^1] = previous with { ContinuationTruncated = true };
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        skipped++;
                    }
                    // Blank or unrelated lines terminate this context.
                    canAttachContinuation = false;
                }
                continue;
            }

            string remainder = match.Groups["rest"].Value.Trim();
            // Only a leading severity token is reliable. The word ERROR
            // inside a level-less device message is not severity metadata.
            Match level = SeverityPattern().Match(remainder);
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
            if (parsed.Count >= maxEvents)
                throw new LogEventLimitExceededException(maxEvents);
            parsed.Add(new LogEvent(timestamp, severity, message, total)
            {
                SourceFile = sourceFile
            });
            capturedContinuationLines = 0;
            canAttachContinuation = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new LogParseResult(parsed, total, skipped, continuations);
    }

    private static bool IsContinuationLine(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return char.IsWhiteSpace(line[0]) ||
            trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("---> ", StringComparison.Ordinal) ||
            trimmed.StartsWith("--- End of ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("InnerException:", StringComparison.OrdinalIgnoreCase) ||
            ExceptionHeaderPattern().IsMatch(trimmed);
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
        @"^\[?(?<level>TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|ERR|FATAL|CRITICAL)\]?(?=$|[\s:|,\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeverityPattern();

    [GeneratedRegex(
        @"^(?:--->\s*)?(?:[A-Za-z_][\w+]*\.)*[A-Za-z_][\w+]*(?:Exception|Error)(?:\s*:\s*.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionHeaderPattern();
}

/// <summary>Retained-event capacity reached without truncating a source file.</summary>
public sealed class LogEventLimitExceededException(int acceptedCapacity)
    : IOException($"Log contains more than {acceptedCapacity:N0} dated events.")
{
    public int AcceptedCapacity { get; } = acceptedCapacity;
}
