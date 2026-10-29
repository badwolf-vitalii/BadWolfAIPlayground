using System.Globalization;
using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// Transparent unsupervised statistical detectors; no trained neural net,
/// remote service, or confident root-cause prediction is involved.
/// </summary>
public static partial class AnomalyDetector
{
    public const int MaxTimelineBuckets = 720;
    public const int MinimumBaselineBuckets = 8;
    private const int MaxFindings = 24;

    public static AnomalyReport Analyze(
        LogParseResult parsed, AnomalyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        options ??= new AnomalyOptions();
        if (!double.IsFinite(options.Sensitivity) ||
            options.Sensitivity is < 1.5 or > 8.0 ||
            options.PreferredBucketMinutes is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Sensitivity must be 1.5–8 and bucket size 1–120 minutes.");
        }

        LogEvent[] events = parsed.Events.OrderBy(e => e.Timestamp).ToArray();
        if (events.Length == 0)
        {
            return new AnomalyReport(parsed, [], [], options.PreferredBucketMinutes,
                0, 0, 0, false, 0);
        }

        long first = events[0].Timestamp.ToUnixTimeSeconds();
        long last = events[^1].Timestamp.ToUnixTimeSeconds();
        if (last - first > 366L * 24 * 3600)
        {
            throw new InvalidDataException(
                "This log covers over 366 days. Select a shorter time range for a meaningful baseline.");
        }

        // Widen buckets for long logs to keep UI and memory bounded.
        int minutes = Math.Max(options.PreferredBucketMinutes,
            (int)Math.Ceiling((last - first + 1.0) / (60 * (MaxTimelineBuckets - 1))));
        long seconds = (long)minutes * 60;
        long firstBucket = first / seconds * seconds;
        long lastBucket = last / seconds * seconds;
        int bucketCount = checked((int)((lastBucket - firstBucket) / seconds + 1));
        if (bucketCount > MaxTimelineBuckets + 1)
        {
            minutes++;
            seconds = (long)minutes * 60;
            firstBucket = first / seconds * seconds;
            lastBucket = last / seconds * seconds;
            bucketCount = checked((int)((lastBucket - firstBucket) / seconds + 1));
        }

        int[] all = new int[bucketCount];
        int[] problems = new int[bucketCount];
        var firstProblem = new LogEvent?[bucketCount];
        int totalProblems = 0;
        foreach (LogEvent entry in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = (int)((entry.Timestamp.ToUnixTimeSeconds() / seconds * seconds -
                firstBucket) / seconds);
            all[index]++;
            if (entry.Severity >= LogSeverity.Warning)
            {
                problems[index]++;
                totalProblems++;
                firstProblem[index] ??= entry;
            }
        }

        bool hasHistory = bucketCount >= MinimumBaselineBuckets;
        double typical = hasHistory ? Median(problems) : 0;
        double mad = hasHistory ? Median(problems.Select(v => Math.Abs(v - typical))) : 0;
        double variability = Math.Max(1.0, Math.Max(mad * 1.4826, Math.Sqrt(typical + 1)));
        double threshold = typical + Math.Max(3, options.Sensitivity * variability);

        var timeline = new List<LogTimeBucket>(bucketCount);
        var findings = new List<AnomalyFinding>();
        for (int i = 0; i < bucketCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(firstBucket + i * seconds)
                .ToLocalTime();
            bool burst = hasHistory && problems[i] >= Math.Max(5, Math.Ceiling(threshold));
            timeline.Add(new LogTimeBucket(start, all[i], problems[i], burst));
            if (burst && firstProblem[i] is LogEvent example)
            {
                findings.Add(new AnomalyFinding(
                    AnomalyKind.ErrorBurst, start,
                    "Unusual warning/error burst",
                    $"Observed {problems[i]} warning/error events in {minutes} minute(s), " +
                    $"versus a typical {typical:0.#}; robust threshold {threshold:0.#}. " +
                    "This detects a spike, not its root cause.",
                    example.Message, example.LineNumber, problems[i], typical, threshold)
                {
                    SourceFile = example.SourceFile,
                    AlsoInFiles = example.AlsoInFiles
                });
            }
        }

        // Novel error templates are only meaningful with enough comparable
        // failures; a single failure in a tiny file is NOT called anomalous.
        LogEvent[] errors = events.Where(e => e.Severity >= LogSeverity.Error).ToArray();
        if (errors.Length >= 25)
        {
            int maxRare = Math.Max(1, errors.Length / 100);
            foreach (IGrouping<string, LogEvent> group in errors.GroupBy(
                         e => $"{e.Severity}|{NormalizePattern(e.Message)}"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = group.Count();
                if (count > maxRare)
                {
                    continue;
                }

                LogEvent example = group.First();
                findings.Add(new AnomalyFinding(AnomalyKind.RareErrorPattern,
                    example.Timestamp, "Rare error message pattern",
                    $"This normalized error pattern occurred {count} time(s) among " +
                    $"{errors.Length} errors. IDs/numbers are normalized. " +
                    "Rare does not necessarily mean dangerous.",
                    example.Message, example.LineNumber, count,
                    errors.Length, maxRare)
                {
                    SourceFile = example.SourceFile,
                    AlsoInFiles = example.AlsoInFiles
                });
            }
        }

        // Keep results usable even for noisy logs; deterministic ordering.
        AnomalyFinding[] prioritized = findings
            .OrderBy(f => f.Kind == AnomalyKind.ErrorBurst ? 0 : 1)
            .ThenByDescending(f => f.Kind == AnomalyKind.ErrorBurst
                ? f.Observed / Math.Max(1, f.Threshold)
                : 1.0 / f.Observed)
            .ThenBy(f => f.Timestamp)
            .Take(MaxFindings)
            .ToArray();

        return new AnomalyReport(parsed, timeline, prioritized, minutes, typical,
            mad, threshold, hasHistory, totalProblems);
    }

    public static string NormalizePattern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string clean = value.ToLowerInvariant();
        clean = GuidPattern().Replace(clean, "<guid>");
        clean = HexPattern().Replace(clean, "<hex>");
        clean = NumberPattern().Replace(clean, "#");
        clean = Whitespace().Replace(clean, " ").Trim();
        return clean.Length > 180 ? clean[..180] : clean;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] data = values.OrderBy(v => v).ToArray();
        if (data.Length == 0)
        {
            return 0;
        }
        int middle = data.Length / 2;
        return data.Length % 2 != 0 ? data[middle] : (data[middle - 1] + data[middle]) / 2;
    }

    private static double Median(IEnumerable<int> values) => Median(values.Select(v => (double)v));

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b0x[0-9a-f]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
