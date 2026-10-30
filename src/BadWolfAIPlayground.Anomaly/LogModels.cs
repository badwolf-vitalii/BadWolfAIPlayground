namespace BadWolfAIPlayground.Anomaly;

/// <summary>Only explicitly parsed severities count toward the incident baseline.</summary>
public enum LogSeverity { Unknown, Trace, Debug, Info, Warning, Error, Fatal }
public enum AnomalyKind { ErrorBurst, RareErrorPattern }

public sealed record LogEvent(
    DateTimeOffset Timestamp, LogSeverity Severity, string Message, int LineNumber)
{
    // Relative to the explicitly selected Logs folder, never an absolute path.
    public string SourceFile { get; init; } = "";
    // Exact events copied to multiple NLog targets are counted only once.
    public IReadOnlyList<string> AlsoInFiles { get; init; } = [];
    // Bounded undated exception/stack text from the original log event.
    public string Continuation { get; init; } = "";
    public bool ContinuationTruncated { get; init; }
}

public sealed record LogParseResult(
    IReadOnlyList<LogEvent> Events, int TotalLines, int SkippedLines, int ContinuationLines);

public sealed record AnomalyOptions(double Sensitivity = 3.0, int PreferredBucketMinutes = 1);

public sealed record LogTimeBucket(
    DateTimeOffset Start, int TotalEvents, int ProblemEvents, bool IsBurst);

public sealed record AnomalyFinding(
    AnomalyKind Kind, DateTimeOffset Timestamp, string Title, string Explanation,
    string Evidence, int SourceLine, int Observed, double Typical, double Threshold)
{
    public string SourceFile { get; init; } = "";
    public IReadOnlyList<string> AlsoInFiles { get; init; } = [];
    public string Continuation { get; init; } = "";
    public bool ContinuationTruncated { get; init; }

    // Number and bounds of statistically flagged buckets grouped by
    // normalized representative evidence; not repeated-message counts.
    public int AffectedWindows { get; init; } = 1;
    public DateTimeOffset FirstWindow { get; init; }
    public DateTimeOffset LastWindow { get; init; }
}

/// <summary>
/// Approximate recurring message pattern in already deduplicated WARN/ERROR/FATAL
/// entries. The sample is traceable evidence, not an inferred root cause.
/// </summary>
public sealed record LogMessagePattern(
    LogSeverity Severity, string SampleMessage, int Occurrences,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int SourceFileCount, string SampleSourceFile, int SampleLine);

public sealed record AnomalyReport(
    LogParseResult Source, IReadOnlyList<LogTimeBucket> Buckets,
    IReadOnlyList<AnomalyFinding> Findings, int BucketMinutes,
    double TypicalProblemCount, double Mad, double BurstThreshold, bool HasEnoughHistory,
    int TotalProblems)
{
    public int PeakProblemCount => Buckets.Count == 0 ? 0 : Buckets.Max(b => b.ProblemEvents);
    public int BurstWindowCount { get; init; }
    public int TotalMessagePatterns { get; init; }
    public IReadOnlyList<LogMessagePattern> TopMessagePatterns { get; init; } = [];
}
