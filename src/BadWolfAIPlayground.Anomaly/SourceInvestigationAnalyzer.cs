namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// An evidence-backed, local-only investigation. A source candidate is
/// always a code-navigation suggestion; it is not a diagnosed root cause.
/// </summary>
public sealed record SourceInvestigationCase(
    string Category, string Title, string Evidence, string LogFile, int LogLine,
    IReadOnlyList<SourceInvestigationCandidate> Candidates, string Assessment)
{
    public string LogLocation => string.IsNullOrWhiteSpace(LogFile)
        ? $"Log line {LogLine}" : $"{LogFile}:{LogLine}";
    public bool Ambiguous => Candidates.Count > 1;
}

public sealed record SourceInvestigationReport(
    IReadOnlyList<SourceInvestigationCase> Cases, int Linked, int Ambiguous, int Unmatched)
{
    public int Total => Cases.Count;
}

/// <summary>
/// Joins only explicitly observed log evidence to C# syntax or original
/// stack frame locations. Never guesses a transaction link, runtime
/// call graph or root cause from temporal proximity.
/// </summary>
public static class SourceInvestigationAnalyzer
{
    public const int MaxPatternCases = 12;
    public const int MaxOperationCases = 8;
    public const int MaxExceptionCases = 4;

    public static SourceInvestigationReport Analyze(
        AnomalyReport report, OperationTimelineReport operations,
        SourceInvestigationIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(index);

        var cases = new List<SourceInvestigationCase>();
        foreach (LogMessagePattern item in report.TopMessagePatterns.Take(MaxPatternCases))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SourceInvestigationCandidate> candidates =
                index.FindLogSites(item.SampleMessage, token: cancellationToken);
            cases.Add(Create("LOG PATTERN",
                $"{item.Severity}: {item.Occurrences:N0} occurrences",
                item.SampleMessage, item.SampleSourceFile, item.SampleLine,
                candidates, "A matching literal/template suggests a logging site, " +
                    "not the cause of an incident."));
        }

        foreach (OperationTimelineGroup group in operations.Groups.Take(MaxOperationCases))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var named = group.Events.Where(e => !string.IsNullOrWhiteSpace(e.Operation))
                .GroupBy(e => e.Operation, StringComparer.Ordinal)
                .Select(g => g.Last()).Take(4).ToArray();
            if (named.Length == 0)
                continue;

            // Exact declared method names are useful navigation hints, but
            // a TOM_OP operation label is not a runtime call stack.
            SourceInvestigationCandidate[] candidates = named
                .SelectMany(e => index.FindMethods(e.Operation))
                .DistinctBy(c => (c.RelativePath, c.LineNumber))
                .Take(SourceInvestigationIndex.MaxCandidates).ToArray();

            OperationTimelineEvent evidence = named[0];
            string id = string.IsNullOrEmpty(group.TransactionId)
                ? group.FlowIds.FirstOrDefault() ?? "unknown flow"
                : "VTS " + group.TransactionId;
            cases.Add(Create("TOM_OP METHOD", id + " — " +
                    string.Join(", ", named.Select(e => e.Operation)),
                group.Outcome, evidence.SourceFile, evidence.SourceLine, candidates,
                "An explicit operation name matches a declaration. Static " +
                "method-name equality cannot prove this code actually ran."));
        }

        // An exception containing a real source line is stronger evidence
        // than a mere text-template match, but only where the referenced
        // file is part of the user's selected checkout.
        foreach (AnomalyFinding finding in report.Findings
                     .Where(f => !string.IsNullOrEmpty(f.Continuation))
                     .Take(MaxExceptionCases))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceInvestigationCandidate[] candidates =
                index.FindStackFrames(finding.Continuation, token: cancellationToken)
                    .Concat(index.FindLogSites(finding.Evidence, token: cancellationToken))
                    .DistinctBy(c => (c.RelativePath, c.LineNumber))
                    .Take(SourceInvestigationIndex.MaxCandidates)
                    .ToArray();
            cases.Add(Create("STACK / FINDING", finding.Title,
                finding.Evidence, finding.SourceFile, finding.SourceLine,
                candidates, "An original stack-frame path and line are runtime " +
                    "evidence; a static message match alone is not."));
        }

        return new SourceInvestigationReport(cases,
            cases.Count(c => c.Candidates.Count > 0),
            cases.Count(c => c.Ambiguous),
            cases.Count(c => c.Candidates.Count == 0));
    }

    private static SourceInvestigationCase Create(
        string category, string title, string evidence, string logFile,
        int logLine, IReadOnlyList<SourceInvestigationCandidate> candidates,
        string caveat)
    {
        string status = candidates.Count switch
        {
            0 => "No matching source found in this selected checkout.",
            1 when candidates[0].Kind == SourceEvidenceKind.ExactLoggingLiteral =>
                "One exact logging literal was found; runtime origin is not proven.",
            1 when candidates[0].Kind == SourceEvidenceKind.ExplicitStackFrame =>
                "One source file/line corresponds to an explicit stack frame. " +
                "Verify the deployed build matches this checkout.",
            1 => "One possible source site was found; runtime origin is not proven.",
            _ => $"{candidates.Count} possible source sites. Ambiguous; " +
                 "do not select one as the cause without more evidence."
        };
        return new SourceInvestigationCase(category, title, evidence,
            logFile, logLine, candidates, status + " " + caveat);
    }
}
