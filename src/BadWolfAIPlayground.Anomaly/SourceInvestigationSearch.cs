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

    /// <summary>
    /// Searches every retained event (INFO/DEBUG included) by message,
    /// continuation and original/alias filename. Results are grouped by an
    /// explicit operation_id when present, otherwise by source line.
    /// No timestamp-only joins, source execution, network or cloud access.
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
                    correlated[id] = group = new CorrelatedEvents();
                group.Add(entry);
            }
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
                    id, linked) with { ObservedAt = entry.Timestamp };
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

    private static bool ContainsQuery(LogEvent entry, string query) =>
        entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Continuation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.SourceFile.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.AlsoInFiles.Any(file =>
            file.Contains(query, StringComparison.OrdinalIgnoreCase));
}
