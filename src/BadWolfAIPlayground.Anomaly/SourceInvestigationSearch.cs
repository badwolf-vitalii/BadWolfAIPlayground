namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// A search of ALL retained, currently enabled log events, not only the
/// detector's statistically ranked error patterns or rare findings.
/// MatchingEventCount is exact for the retained log data; cards are bounded.
/// </summary>
public sealed record SourceInvestigationSearchResult(
    IReadOnlyList<SourceInvestigationCase> Cases, int MatchingEventCount)
{
    public int DisplayedGroups => Cases.Count;
}

public static partial class SourceInvestigationAnalyzer
{
    public const int MaxSearchGroups = 40;
    public const int MaxSearchQueryLength = 256;
    public const int MaxNearbyContextEvents = 6;
    public const int MaxNearbyPhysicalLineDistance = 60;
    public const int MaxNearbySeconds = 120;

    /// <summary>
    /// Searches every retained event (INFO/DEBUG included) by message,
    /// continuation and original/alias filename. Results are grouped by an
    /// explicit operation_id when present, otherwise by source line.
    /// Cross-file/operation correlation still requires an explicit ID.
    /// Separately labeled nearby lines from the SAME physical log file are
    /// contextual observations only, not correlated operations or causation.
    /// No source execution, network request or cloud access occurs.
    /// </summary>
    public static SourceInvestigationSearchResult Search(
        LogParseResult source, SourceInvestigationIndex? index,
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        query = query.Trim();
        if (query.Length is < 3 or > MaxSearchQueryLength)
            throw new ArgumentException(
                $"Enter between 3 and {MaxSearchQueryLength} characters.",
                nameof(query));

        var chosen = new Dictionary<string, LogEvent>(
            StringComparer.OrdinalIgnoreCase);
        int matchedCount = 0;
        // Newer events are generally at the end of a file. Still sort the
        // bounded retained results by actual timestamp before displaying.
        for (int i = source.Events.Count - 1; i >= 0; i--)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            LogEvent entry = source.Events[i];
            if (!ContainsQuery(entry, query))
                continue;

            matchedCount++;
            string id = ReadOperationId(entry.Message);
            string key = id.Length == 0
                ? "line:" + entry.SourceFile + ":" + entry.LineNumber
                : "operation:" + id;
            if (chosen.TryGetValue(key, out LogEvent? current))
            {
                // Prefer the error over the related DEBUG caller as the
                // representative case, but show BOTH in linked evidence.
                if (entry.Severity > current.Severity ||
                    (entry.Severity == current.Severity &&
                     entry.Continuation.Length > current.Continuation.Length))
                    chosen[key] = entry;
            }
            else if (chosen.Count < MaxSearchGroups)
            {
                chosen.Add(key, entry);
            }
        }

        if (chosen.Count == 0)
            return new SourceInvestigationSearchResult([], matchedCount);

        // A second pass collects explicit related operation records from
        // the enabled, mirror-deduplicated report (even if the linked DEBUG
        // line does not itself match the user's search text).
        string[] ids = chosen.Values.Select(entry => ReadOperationId(entry.Message))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var selectedById = chosen.Values
            .Where(entry => ReadOperationId(entry.Message).Length > 0)
            .ToDictionary(entry => ReadOperationId(entry.Message),
                StringComparer.OrdinalIgnoreCase);
        var correlated = new Dictionary<string, CorrelatedEvents>(
            StringComparer.OrdinalIgnoreCase);
        if (idSet.Count > 0)
        {
            for (int i = 0; i < source.Events.Count; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                LogEvent entry = source.Events[i];
                // Skip almost all rows without IDs before allocating strings.
                if (!entry.Message.Contains("operation_id=",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                string id = ReadOperationId(entry.Message);
                if (id.Length == 0 || !idSet.Contains(id))
                    continue;
                if (!correlated.TryGetValue(id, out CorrelatedEvents? group))
                {
                    selectedById.TryGetValue(id, out LogEvent? focus);
                    correlated[id] = group = new CorrelatedEvents(focus);
                }
                group.Add(entry);
            }
        }

        // Collect a small amount of same-file context even for older LOG
        // formats without operation_id. We inspect ONLY the already imported,
        // currently enabled retained events; no disk scan or cross-file
        // timestamp association is performed. Build a per-file lookup once,
        // avoiding rescanning a million events for each search result.
        var contextFiles = chosen.Values
            .Where(entry => ReadOperationId(entry.Message).Length == 0 &&
                            !string.IsNullOrWhiteSpace(entry.SourceFile))
            .Select(entry => entry.SourceFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nearbyByFile = new Dictionary<string, List<LogEvent>>(
            StringComparer.OrdinalIgnoreCase);
        if (contextFiles.Count > 0)
        {
            for (int i = 0; i < source.Events.Count; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                LogEvent entry = source.Events[i];
                if (!contextFiles.Contains(entry.SourceFile))
                    continue;
                if (!nearbyByFile.TryGetValue(entry.SourceFile, out List<LogEvent>? events))
                    nearbyByFile[entry.SourceFile] = events = [];
                events.Add(entry);
            }
            foreach (List<LogEvent> events in nearbyByFile.Values)
                events.Sort((left, right) => left.LineNumber.CompareTo(right.LineNumber));
        }

        SourceInvestigationCase[] cases = chosen.Values
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Severity)
            .Select(entry =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = ReadOperationId(entry.Message);
                correlated.TryGetValue(id, out CorrelatedEvents? linked);
                SourceInvestigationCandidate[] candidates = index is null
                    ? []
                    : CombineCandidates(
                        index.FindLoggedCallerLocations(entry.Message,
                            token: cancellationToken)
                        .Concat(index.FindStackFrames(entry.Continuation,
                            token: cancellationToken))
                        .Concat(index.FindLogSites(entry.Message,
                            token: cancellationToken)),
                        linked, index, cancellationToken);
                SourceInvestigationCase result = Create(
                    "LOG SEARCH",
                    entry.Severity.ToString().ToUpperInvariant() + " log match",
                    entry.Message, entry.SourceFile, entry.LineNumber,
                    candidates,
                    "Search results are original log observations, not " +
                    "anomaly classifications or confirmed diagnoses.",
                    id, linked) with
                {
                    ObservedAt = entry.Timestamp,
                    NearbyEvents = id.Length == 0
                        ? CollectNearby(entry, nearbyByFile)
                        : []
                };
                return index is null
                    ? result with
                    {
                        Assessment = "Matching local log evidence found. " +
                            "Choose a local C# solution to inspect source " +
                            "candidates; no source checkout is loaded."
                    }
                    : result;
            }).ToArray();

        return new SourceInvestigationSearchResult(cases, matchedCount);
    }


    private static IReadOnlyList<SourceInvestigationLogEvent> CollectNearby(
        LogEvent primary, Dictionary<string, List<LogEvent>> byFile)
    {
        if (string.IsNullOrWhiteSpace(primary.SourceFile) ||
            !byFile.TryGetValue(primary.SourceFile, out List<LogEvent>? events))
            return [];

        int position = events.BinarySearch(primary,
            Comparer<LogEvent>.Create((a, b) =>
                a.LineNumber.CompareTo(b.LineNumber)));
        if (position < 0)
            return [];

        var before = new List<LogEvent>();
        var after = new List<LogEvent>();
        int perSide = MaxNearbyContextEvents / 2;

        for (int i = position - 1; i >= 0 && before.Count < perSide; i--)
        {
            LogEvent candidate = events[i];
            if (primary.LineNumber - candidate.LineNumber >
                MaxNearbyPhysicalLineDistance)
                break;
            if (EligibleNearby(primary, candidate))
                before.Add(candidate);
        }
        for (int i = position + 1; i < events.Count && after.Count < perSide; i++)
        {
            LogEvent candidate = events[i];
            if (candidate.LineNumber - primary.LineNumber >
                MaxNearbyPhysicalLineDistance)
                break;
            if (EligibleNearby(primary, candidate))
                after.Add(candidate);
        }

        // Preserve physical file order; this is local context, not a call trace.
        return before.AsEnumerable().Reverse().Concat(after)
            .DistinctBy(entry => (entry.Timestamp.UtcTicks, entry.Message),
                new TimestampAndMessageComparer())
            .Select(entry => new SourceInvestigationLogEvent(
                entry.Timestamp, entry.Severity,
                entry.Message.Length > 450
                    ? entry.Message[..450] + "..." : entry.Message,
                entry.SourceFile, entry.LineNumber))
            .ToArray();
    }

    private static bool EligibleNearby(LogEvent primary, LogEvent candidate)
    {
        // Ignore different explicit operations; never borrow those as context
        // for an untagged event just because the timestamps are close.
        if (ReadOperationId(candidate.Message).Length > 0)
            return false;
        if (candidate.Timestamp == primary.Timestamp &&
            string.Equals(candidate.Message, primary.Message,
                StringComparison.OrdinalIgnoreCase))
            return false;
        return Math.Abs((candidate.Timestamp - primary.Timestamp).TotalSeconds)
               <= MaxNearbySeconds;
    }

    private sealed class TimestampAndMessageComparer :
        IEqualityComparer<(long Timestamp, string Message)>
    {
        public bool Equals((long Timestamp, string Message) a,
                           (long Timestamp, string Message) b) =>
            a.Timestamp == b.Timestamp &&
            StringComparer.OrdinalIgnoreCase.Equals(a.Message, b.Message);

        public int GetHashCode((long Timestamp, string Message) value) =>
            HashCode.Combine(value.Timestamp,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Message));
    }

    private static bool ContainsQuery(LogEvent entry, string query) =>
        entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Continuation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.SourceFile.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.AlsoInFiles.Any(file =>
            file.Contains(query, StringComparison.OrdinalIgnoreCase));
}
