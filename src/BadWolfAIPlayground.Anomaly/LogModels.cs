namespace BadWolfAIPlayground.Anomaly;

/// <summary>Only explicitly parsed severities count toward the incident baseline.</summary>
public enum LogSeverity { Unknown, Trace, Debug, Info, Warning, Error, Fatal }
public enum AnomalyKind { ErrorBurst, RareErrorPattern }

public sealed record LogEvent(
    DateTimeOffset Timestamp, LogSeverity Severity, string Message, int LineNumber);

public sealed record LogParseResult(
    IReadOnlyList<LogEvent> Events, int TotalLines, int SkippedLines, int ContinuationLines);

public sealed record AnomalyOptions(double Sensitivity = 3.0, int PreferredBucketMinutes = 1);

public sealed record LogTimeBucket(
    DateTimeOffset Start, int TotalEvents, int ProblemEvents, bool IsBurst);

public sealed record AnomalyFinding(
    AnomalyKind Kind, DateTimeOffset Timestamp, string Title, string Explanation,
    string Evidence, int SourceLine, int Observed, double Typical, double Threshold);

public sealed record AnomalyReport(
    LogParseResult Source, IReadOnlyList<LogTimeBucket> Buckets,
    IReadOnlyList<AnomalyFinding> Findings, int BucketMinutes,
    double TypicalProblemCount, double Mad, double BurstThreshold, bool HasEnoughHistory,
    int TotalProblems)
{
    public int PeakProblemCount => Buckets.Count == 0 ? 0 : Buckets.Max(b => b.ProblemEvents);
}
