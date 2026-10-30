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
    private const int MaxBurstsPerReport = 12;
    public const int MaxMessagePatterns = 12;

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
        // A large imported old/ archive can span multiple years. Refuse
        // to discard operation evidence just because the statistical
        // timeline needs coarse time buckets. UI discloses the resolution.
        // Widen buckets for long logs to keep UI and memory bounded.
        int minutes = Math.Max(options.PreferredBucketMinutes,
            (int)Math.Ceiling((last - first + 1.0) / (60 * (MaxTimelineBuckets - 1))));
        long seconds = (long)minutes * 60;
        long firstBucket = first / seconds * seconds;
        long lastBucket = last / seconds * seconds;
        int bucketCount = checked((int)((lastBucket - firstBucket) / seconds + 1));
        while (bucketCount > MaxTimelineBuckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                LogEvent? previous = firstProblem[index];
                // Prefer a severe example with original exception context,
                // not an earlier, less informative warning in the same burst.
                if (previous is null || entry.Severity > previous.Severity ||
                    (entry.Severity == previous.Severity &&
                     previous.Continuation.Length == 0 && entry.Continuation.Length > 0))
                {
                    firstProblem[index] = entry;
                }
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
                    AlsoInFiles = example.AlsoInFiles,
                    Continuation = example.Continuation,
                    ContinuationTruncated = example.ContinuationTruncated
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

                LogEvent example = group.FirstOrDefault(e =>
                    e.Continuation.Length != 0) ?? group.First();
                findings.Add(new AnomalyFinding(AnomalyKind.RareErrorPattern,
                    example.Timestamp, "Rare error message pattern",
                    $"This normalized error pattern occurred {count} time(s) among " +
                    $"{errors.Length} errors. IDs/numbers are normalized. " +
                    "Rare does not necessarily mean dangerous.",
                    example.Message, example.LineNumber, count,
                    errors.Length, maxRare)
                {
                    SourceFile = example.SourceFile,
                    AlsoInFiles = example.AlsoInFiles,
                    Continuation = example.Continuation,
                    ContinuationTruncated = example.ContinuationTruncated
                });
            }
        }

        // Each busy printer bucket previously occupied another of the 24
        // finding slots. Collapse bursts with the same *representative*
        // normalized message without pretending this proves the root cause
        // or counts exact occurrences of that message.
        AnomalyFinding[] burstGroups = findings
            .Where(item => item.Kind == AnomalyKind.ErrorBurst)
            .GroupBy(item => NormalizePattern(item.Evidence), StringComparer.Ordinal)
            .Select(group =>
            {
                AnomalyFinding[] windows = group.ToArray();
                AnomalyFinding peak = windows.OrderByDescending(item => item.Observed)
                    .ThenBy(item => item.Timestamp).First();
                if (windows.Length == 1)
                    return peak;

                DateTimeOffset firstWindow = windows.Min(item => item.Timestamp);
                DateTimeOffset lastWindow = windows.Max(item => item.Timestamp);
                long problemEventsInWindows = windows.Sum(item => (long)item.Observed);
                return peak with
                {
                    Title = $"Repeated warning/error bursts ({windows.Length} windows)",
                    Explanation = $"{windows.Length} flagged time windows had a " +
                        "similar normalized representative log message, between " +
                        $"{firstWindow:yyyy-MM-dd HH:mm} and " +
                        $"{lastWindow:yyyy-MM-dd HH:mm}. Peak: {peak.Observed:N0} " +
                        $"WARN/ERROR/FATAL events in a {minutes}-minute bucket. " +
                        $"{problemEventsInWindows:N0} total problem events occurred " +
                        "across these flagged buckets, NOT necessarily occurrences " +
                        "of the sample message. This is grouping, not a diagnosis.",
                    AffectedWindows = windows.Length,
                    FirstWindow = firstWindow,
                    LastWindow = lastWindow
                };
            })
            .OrderByDescending(item => item.AffectedWindows)
            .ThenByDescending(item => item.Observed)
            .ThenBy(item => item.Timestamp)
            .ToArray();

        AnomalyFinding[] rare = findings
            .Where(item => item.Kind == AnomalyKind.RareErrorPattern)
            .OrderBy(item => item.Observed)
            .ThenByDescending(item => item.Timestamp)
            .ToArray();

        // Reserve room for rare, distinct errors even when one type of
        // device log floods the statistical burst detector.
        AnomalyFinding[] prioritized = burstGroups.Take(MaxBurstsPerReport)
            .Concat(rare.Take(MaxFindings - MaxBurstsPerReport))
            .Concat(burstGroups.Skip(MaxBurstsPerReport))
            .Concat(rare.Skip(MaxFindings - MaxBurstsPerReport))
            .Take(MaxFindings)
            .ToArray();

        var patterns = new Dictionary<(LogSeverity Severity, string Template),
            MessagePatternAccumulator>();
        for (int i = 0; i < events.Length; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            LogEvent entry = events[i];
            if (entry.Severity < LogSeverity.Warning)
                continue;

            string normalized = NormalizePattern(entry.Message);
            if (normalized.Length == 0)
                continue;
            var key = (entry.Severity, normalized);
            if (!patterns.TryGetValue(key, out MessagePatternAccumulator? item))
                patterns[key] = item = new MessagePatternAccumulator();
            item.Add(entry);
        }

        LogMessagePattern[] topPatterns = patterns.Values
            .OrderByDescending(item => item.Occurrences)
            .ThenByDescending(item => item.Severity)
            .ThenBy(item => item.FirstSeen)
            .Take(MaxMessagePatterns)
            .Select(item => item.ToPattern())
            .ToArray();

        return new AnomalyReport(parsed, timeline, prioritized, minutes, typical,
            mad, threshold, hasHistory, totalProblems)
        {
            BurstWindowCount = timeline.Count(bucket => bucket.IsBurst),
            TotalMessagePatterns = patterns.Count,
            TopMessagePatterns = topPatterns
        };
    }

    private sealed class MessagePatternAccumulator
    {
        private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
        private LogEvent? _sample;
        public LogSeverity Severity { get; private set; }
        public int Occurrences { get; private set; }
        public DateTimeOffset FirstSeen { get; private set; }
        public DateTimeOffset LastSeen { get; private set; }

        public void Add(LogEvent entry)
        {
            if (Occurrences == 0)
            {
                Severity = entry.Severity;
                FirstSeen = entry.Timestamp;
            }
            LastSeen = entry.Timestamp;
            Occurrences++;
            // Show a recent, traceable example. Prefer stack evidence if
            // present, but never discard its actual source-file location.
            if (_sample is null || entry.Continuation.Length > 0 ||
                _sample.Continuation.Length == 0)
                _sample = entry;
            if (!string.IsNullOrWhiteSpace(entry.SourceFile))
                _sources.Add(entry.SourceFile);
        }

        public LogMessagePattern ToPattern()
        {
            LogEvent sample = _sample ??
                throw new InvalidOperationException("Empty message pattern.");
            return new LogMessagePattern(
                Severity, sample.Message, Occurrences, FirstSeen, LastSeen,
                _sources.Count, sample.SourceFile, sample.LineNumber);
        }
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
