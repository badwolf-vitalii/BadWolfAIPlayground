namespace BadWolfAIPlayground.Anomaly;

/// <summary>An original log line sharing an explicit operation_id.</summary>
public sealed record SourceInvestigationLogEvent(
    DateTimeOffset Timestamp, LogSeverity Severity, string Message,
    string SourceFile, int SourceLine)
{
    public string Location => SourceFile + ":" + SourceLine;
}

/// <summary>
/// Source candidates are navigation suggestions, not established root causes.
/// Only included, deduplicated source events can contribute related log lines.
/// </summary>
public sealed record SourceInvestigationCase(
    string Category, string Title, string Evidence, string LogFile, int LogLine,
    IReadOnlyList<SourceInvestigationCandidate> Candidates, string Assessment)
{
    public string LogLocation => string.IsNullOrWhiteSpace(LogFile)
        ? $"Log line {LogLine}" : $"{LogFile}:{LogLine}";
    public bool Ambiguous => Candidates.Count > 1;
    public string OperationId { get; init; } = "";
    public int RelatedEventCount { get; init; }
    public IReadOnlyList<SourceInvestigationLogEvent> RelatedEvents { get; init; } = [];
    // Same-file nearby entries are local context only, never operation_id
    // correlation or evidence that a method/operation actually executed.
    public IReadOnlyList<SourceInvestigationLogEvent> NearbyEvents { get; init; } = [];
    // Null is unknown (e.g. user-built test case), false means no checkout
    // was loaded, true means a source index was available for this case.
    public bool? SourceCheckoutIndexed { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record SourceInvestigationReport(
    IReadOnlyList<SourceInvestigationCase> Cases, int Linked, int Ambiguous, int Unmatched)
{
    public int Total => Cases.Count;
}

/// <summary>
/// Joins only explicit operation_id evidence from the enabled, deduplicated
/// log report and syntax/source locations in an explicitly selected checkout.
/// No timestamp-proximity grouping and no inferred runtime call graph.
/// </summary>
public static partial class SourceInvestigationAnalyzer
{
    public const int MaxPatternCases = 12;
    public const int MaxOperationCases = 8;
    public const int MaxExceptionCases = 4;
    public const int MaxRelatedEventsDisplayed = 12;
    private const int MaxSampleEventsPerId = 8;
    private const int MaxCallerEventsPerId = 6;
    private const int MaxRecentEventsPerId = 8;
    private const int MaxRecentProblemsPerId = 4;
    private const int MaxFocusSideEvents = 3;

    private sealed class CorrelatedEvents
    {
        public int Count { get; private set; }
        public HashSet<string> Sources { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<LogEvent> Samples { get; } = [];
        public List<LogEvent> CallerLocations { get; } = [];

        // Bound storage even for a very long operation. The first events
        // alone are a poor representation of a 72-entry operation that
        // finally fails at Purge().
        private readonly List<LogEvent> _recent = [];
        private readonly List<LogEvent> _recentProblems = [];
        private readonly List<LogEvent> _recentCallerLocations = [];
        private readonly Queue<LogEvent> _precedingFocus = new();
        private readonly List<LogEvent> _focusWindow = [];
        private readonly LogEvent? _focus;
        private int _remainingAfterFocus;

        public CorrelatedEvents(LogEvent? focus = null) => _focus = focus;

        public void Add(LogEvent entry)
        {
            Count++;
            Sources.Add(entry.SourceFile);
            if (Samples.Count < MaxSampleEventsPerId)
                Samples.Add(entry);

            Remember(_recent, entry, MaxRecentEventsPerId);
            if (entry.Severity >= LogSeverity.Warning)
                Remember(_recentProblems, entry, MaxRecentProblemsPerId);

            if (entry.Message.Contains(".cs: line", StringComparison.OrdinalIgnoreCase))
            {
                if (CallerLocations.Count < MaxCallerEventsPerId)
                    CallerLocations.Add(entry);
                Remember(_recentCallerLocations, entry, MaxCallerEventsPerId);
            }

            // The representative search hit is already known before the
            // correlation pass. Retain a bounded actual neighborhood of
            // SAME-operation entries, not a timestamp-only cross-file join.
            if (_focus is not null)
            {
                if (ReferenceEquals(entry, _focus))
                {
                    _focusWindow.AddRange(_precedingFocus);
                    _focusWindow.Add(entry);
                    _remainingAfterFocus = MaxFocusSideEvents;
                }
                else if (_remainingAfterFocus > 0)
                {
                    _focusWindow.Add(entry);
                    _remainingAfterFocus--;
                }
                _precedingFocus.Enqueue(entry);
                if (_precedingFocus.Count > MaxFocusSideEvents)
                    _precedingFocus.Dequeue();
            }
        }

        private static void Remember(List<LogEvent> list, LogEvent entry, int max)
        {
            if (list.Count == max)
                list.RemoveAt(0);
            list.Add(entry);
        }

        public IEnumerable<LogEvent> CallerHints =>
            _focusWindow.Where(entry =>
                entry.Message.Contains(".cs: line", StringComparison.OrdinalIgnoreCase))
            .Concat(_recentCallerLocations)
            .Concat(CallerLocations)
            .DistinctBy(entry =>
                (entry.SourceFile.ToUpperInvariant(), entry.LineNumber))
            .Take(MaxCallerEventsPerId);

        public IReadOnlyList<SourceInvestigationLogEvent> Visible()
        {
            // Build a deliberately diversified and bounded candidate pool.
            // Focus on the selected failure, but retain start/end, severe
            // events and real logger-reported source locations when present.
            return _focusWindow
                .Concat(_recentCallerLocations.TakeLast(1))
                .Concat(_recentProblems.TakeLast(2))
                .Concat(Samples.Take(1))
                .Concat(_recent.TakeLast(2))
                .Concat(CallerLocations.Take(1))
                .Concat(Samples)
                .Concat(_recent)
                .DistinctBy(item =>
                    (item.SourceFile.ToUpperInvariant(), item.LineNumber))
                .Take(MaxRelatedEventsDisplayed)
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.LineNumber)
                .Select(item => new SourceInvestigationLogEvent(
                    item.Timestamp, item.Severity,
                    item.Message.Length > 450
                        ? item.Message[..450] + "..." : item.Message,
                    item.SourceFile, item.LineNumber))
                .ToArray();
        }
    }

    public static SourceInvestigationReport Analyze(
        AnomalyReport report, OperationTimelineReport operations,
        SourceInvestigationIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(index);

        var cases = new List<SourceInvestigationCase>();
        LogMessagePattern[] patterns =
            report.TopMessagePatterns.Take(MaxPatternCases).ToArray();
        // Some App/WebClient NLog targets flatten exception_stack into the
        // dated message itself; deduplication can also retain the mirrored
        // target without multiline Continuation. An explicit operation_id
        // is sufficient to pursue cross-log evidence in either form.
        AnomalyFinding[] findings = report.Findings
            .Where(item => !string.IsNullOrEmpty(item.Continuation) ||
                           ReadOperationId(item.Evidence).Length > 0)
            .Take(MaxExceptionCases).ToArray();

        // Restrict correlation to the small set of IDs already displayed as
        // evidence. Memory use is bounded even when a folder has 1M events.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LogMessagePattern item in patterns)
        {
            string id = ReadOperationId(item.SampleMessage);
            if (id.Length > 0) wanted.Add(id);
        }
        foreach (AnomalyFinding item in findings)
        {
            string id = ReadOperationId(item.Evidence);
            if (id.Length > 0) wanted.Add(id);
        }
        foreach (OperationTimelineGroup group in operations.Groups.Take(MaxOperationCases))
            foreach (OperationTimelineEvent item in group.Events)
                if (item.OperationId.Length >= 16 && item.OperationId != "-")
                    wanted.Add(item.OperationId);

        var correlated = new Dictionary<string, CorrelatedEvents>(
            StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < report.Source.Events.Count; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            LogEvent item = report.Source.Events[i];
            string id = ReadOperationId(item.Message);
            if (id.Length == 0 || !wanted.Contains(id))
                continue;
            if (!correlated.TryGetValue(id, out CorrelatedEvents? bucket))
                correlated[id] = bucket = new CorrelatedEvents();
            bucket.Add(item);
        }

        foreach (LogMessagePattern item in patterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = ReadOperationId(item.SampleMessage);
            correlated.TryGetValue(id, out CorrelatedEvents? linked);
            SourceInvestigationCandidate[] candidates = CombineCandidates(
                index.FindLogSites(item.SampleMessage, token: cancellationToken)
                    .Concat(index.FindNamedFailureMethods(item.SampleMessage,
                        token: cancellationToken)),
                linked, index, cancellationToken);
            cases.Add(Create("LOG PATTERN",
                $"{item.Severity}: {item.Occurrences:N0} occurrences",
                item.SampleMessage, item.SampleSourceFile, item.SampleLine,
                candidates, "A matching literal/template is a possible " +
                    "logging statement, not the incident's cause.", id, linked) with { ObservedAt = item.FirstSeen });
        }

        foreach (OperationTimelineGroup group in operations.Groups.Take(MaxOperationCases))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationTimelineEvent[] named = group.Events
                .Where(e => !string.IsNullOrWhiteSpace(e.Operation))
                .GroupBy(e => e.Operation, StringComparer.Ordinal)
                .Select(g => g.Last()).Take(4).ToArray();
            if (named.Length == 0)
                continue;

            OperationTimelineEvent original = named[0];
            string operationId = original.OperationId.Length >= 16
                ? original.OperationId : "";
            correlated.TryGetValue(operationId, out CorrelatedEvents? linked);
            SourceInvestigationCandidate[] candidates = CombineCandidates(
                named.SelectMany(e => index.FindMethods(e.Operation)),
                linked, index, cancellationToken);
            string id = string.IsNullOrEmpty(group.TransactionId)
                ? group.FlowIds.FirstOrDefault() ?? "unknown flow"
                : "VTS " + group.TransactionId;
            cases.Add(Create("TOM_OP METHOD", id + " — " +
                    string.Join(", ", named.Select(e => e.Operation)),
                group.Outcome, original.SourceFile, original.SourceLine, candidates,
                "Exact method-name equality does not prove a runtime call.",
                operationId, linked) with { ObservedAt = original.Timestamp });
        }

        // Exceptions without C# frames can still have a separate DEBUG record
        // describing the logger caller. These records are linked ONLY by ID.
        foreach (AnomalyFinding finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = ReadOperationId(finding.Evidence);
            correlated.TryGetValue(id, out CorrelatedEvents? linked);
            SourceInvestigationCandidate[] candidates = CombineCandidates(
                index.FindStackFrames(finding.Continuation,
                    token: cancellationToken)
                    .Concat(index.FindLogSites(finding.Evidence,
                        token: cancellationToken))
                    .Concat(index.FindNamedFailureMethods(finding.Evidence,
                        token: cancellationToken)),
                linked, index, cancellationToken);
            cases.Add(Create("STACK / FINDING", finding.Title,
                finding.Evidence, finding.SourceFile, finding.SourceLine,
                candidates, "A stack frame may locate a throwing function, " +
                    "while a DEBUG caller location identifies where an " +
                    "exception was logged; neither alone proves cause.", id, linked) with { ObservedAt = finding.Timestamp });
        }

        return new SourceInvestigationReport(cases,
            cases.Count(c => c.Candidates.Count > 0),
            cases.Count(c => c.Ambiguous),
            cases.Count(c => c.Candidates.Count == 0));
    }

    private static string ReadOperationId(string message)
    {
        const string tag = "operation_id=";
        int from = 0;
        while (from < message.Length)
        {
            int at = message.IndexOf(tag, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            from = at + tag.Length;
            if (at > 0 && message[at - 1] != '|' &&
                !char.IsWhiteSpace(message[at - 1]))
                continue;
            int end = from;
            while (end < message.Length &&
                (char.IsAsciiLetterOrDigit(message[end]) ||
                 message[end] is '-' or '_'))
                end++;
            int length = end - from;
            if (length is >= 16 and <= 64)
                return message.Substring(from, length);
        }
        return "";
    }

    private static SourceInvestigationCandidate[] CombineCandidates(
        IEnumerable<SourceInvestigationCandidate> direct,
        CorrelatedEvents? linked, SourceInvestigationIndex index,
        CancellationToken token)
    {
        IEnumerable<SourceInvestigationCandidate> callers = linked is null
            ? []
            : linked.CallerHints.SelectMany(entry =>
                index.FindLoggedCallerLocations(entry.Message, token: token));
        return callers.Concat(direct)
            .OrderBy(candidate => candidate.Kind switch
            {
                SourceEvidenceKind.LoggedCallerLocation => 0,
                SourceEvidenceKind.ExplicitStackFrame => 1,
                SourceEvidenceKind.ExactLoggingLiteral => 2,
                SourceEvidenceKind.FormattedLoggingTemplate => 3,
                _ => 4
            })
            .ThenBy(candidate => candidate.RelativePath,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.LineNumber)
            .DistinctBy(candidate =>
                (candidate.RelativePath.ToUpperInvariant(), candidate.LineNumber))
            .Take(SourceInvestigationIndex.MaxCandidates)
            .ToArray();
    }

    private static SourceInvestigationCase Create(
        string category, string title, string evidence, string logFile,
        int logLine, IReadOnlyList<SourceInvestigationCandidate> candidates,
        string caveat, string operationId = "", CorrelatedEvents? linked = null)
    {
        string status = candidates.Count switch
        {
            0 => "No matching C# source found in the selected checkout.",
            1 when candidates[0].Kind == SourceEvidenceKind.LoggedCallerLocation =>
                "A logger-reported C# line matches the selected checkout. " +
                "This is a logging/catch location, not a verified root cause.",
            1 when candidates[0].Kind == SourceEvidenceKind.ExactLoggingLiteral =>
                "One exact logging literal found; runtime origin not proven.",
            1 when candidates[0].Kind == SourceEvidenceKind.ExplicitStackFrame =>
                "One explicit stack-frame line found. Verify the deployed revision.",
            1 => "One possible source site found; runtime origin not proven.",
            _ => $"{candidates.Count} possible source sites. Ambiguous; " +
                 "do not select one as the cause without more evidence."
        };
        string correlation = linked is null ? "" :
            $" {linked.Count:N0} included log entries share explicit " +
            $"operation_id={operationId} across {linked.Sources.Count:N0} " +
            "source file(s). Identifier equality proves correlation, not " +
            "causation; the logger caller is not necessarily where the error began.";
        return new SourceInvestigationCase(category, title, evidence,
            logFile, logLine, candidates, status + " " + caveat + correlation)
        {
            OperationId = linked is null ? "" : operationId,
            RelatedEventCount = linked?.Count ?? 0,
            RelatedEvents = linked?.Visible() ?? [],
            SourceCheckoutIndexed = true
        };
    }
}
