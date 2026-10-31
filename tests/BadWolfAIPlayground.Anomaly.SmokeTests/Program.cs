using BadWolfAIPlayground.Anomaly;

var checks = new (string Name, Action Execute)[]
{
    ("Demo shows genuine spike and rare failure", DemoFindings),
    ("Normal time series does not invent bursts", NormalData),
    ("Date formats, severities and unparsed lines", ParserFormats),
    ("Message template normalization groups changing IDs", NormalizedErrors),
    ("Insufficient history stays explicit", ShortLog),
    ("Cancellation and invalid options", FailSafely),
    ("Sensitivity controls actual threshold", Sensitivity),
    ("Repeated burst windows collapse without hiding rare errors", GroupedBursts),
    ("Recurring message patterns count real events and preserve evidence", RecurringPatterns),
    ("Optional offline C# log-template source context", SourceContext),
    ("Roslyn source investigations distinguish syntax evidence and ambiguity", RoslynSourceInvestigation),
    ("Cross-log operation_id links DEBUG caller to original C# line", CrossLogCallerEvidence),
    ("Import entire Logs directory with component provenance", FolderImport),
    ("Large files stream safely without 16 MiB exclusion", FolderGuards),
    ("More than 512 log files remain individually importable", FolderFileCount),
    ("Raw event overflow retains complete high-priority operations", FolderEventBudget),
    ("Multi-year Logs archives use a bounded coarse timeline", MultiYearTimeline),
    ("File selection recomputes events and duplicate provenance", FolderSelection),
    ("Multiline exception context is bounded and does not invent events", MultilineContext),
    ("Findings retain original exception evidence", FindingExceptionContext),
    ("TOM_OP lifecycle correlation, cancellation and receipts", StructuredOperationTimeline),
    ("Standalone card reads do not require VTS", StandaloneCardRead)
};

int failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Execute();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {check.Name}: {exception}");
    }
}
Console.WriteLine($"{checks.Length - failed}/{checks.Length} anomaly checks passed.");
return failed == 0 ? 0 : 1;

static void DemoFindings()
{
    LogParseResult parsed = LogParser.Parse(DemoLogs.Create());
    AnomalyReport report = AnomalyDetector.Analyze(parsed);
    Ensure(parsed.Events.Count >= 250 && parsed.SkippedLines == 0,
        "Demo must be a substantial correctly formatted log.");
    Ensure(report.HasEnoughHistory && report.Buckets.Count == 36 &&
           report.BucketMinutes == 1 && report.TotalProblems >= 40,
        "Demo baseline is missing.");
    Ensure(report.Findings.Any(f =>
        f.Kind == AnomalyKind.ErrorBurst &&
        f.Observed >= 12 && f.Evidence.Contains("Retry timeout")),
        "Demonstration spike should be detected.");
    Ensure(report.Findings.Any(f =>
        f.Kind == AnomalyKind.RareErrorPattern &&
        f.Evidence.Contains("Disk controller") &&
        f.Continuation.Contains("System.IO.IOException", StringComparison.Ordinal)),
        "The rare disk failure needs an explainable finding with real stack context.");
    Ensure(parsed.Events.Count(e => e.Continuation.Length > 0) == 1 &&
           parsed.ContinuationLines == 3 &&
           parsed.Events.Single(e => e.Continuation.Length > 0)
               .Continuation.Contains("Storage.cs:line 82", StringComparison.Ordinal),
        "The built-in demo must visibly exercise multiline exception capture.");
    Ensure(report.Buckets.Count(b => b.IsBurst) == 1,
        "Only the deliberate high-rate minute should be a burst.");
}
static void NormalData()
{
    string raw = string.Join("\n", Enumerable.Range(0, 40).Select(i =>
        $"2026-10-29 09:{i:00}:00 [ERROR] Retry timeout on connection {i}"));
    AnomalyReport report = AnomalyDetector.Analyze(LogParser.Parse(raw));
    Ensure(report.HasEnoughHistory && report.Findings.Count == 0 &&
           report.BurstThreshold > report.TypicalProblemCount,
        "A steady one-error-per-minute baseline must be healthy.");
}
static void ParserFormats()
{
    const string raw =
        "2026-10-29 09:00:00.123 [INFO] Started\n" +
        "2026-10-29T09:00:01Z ERROR Cannot connect\n" +
        "[2026-10-29 09:00:02] [WARNING] Slow response\n" +
        "2026-10-29 09:00:03,321 [FATAL] Crash code 42\n" +
        "   at Example.Run()\n" +
        "undated and unexplained line\n" +
        "2026-10-29 09:00:04 No explicit level\n";
    LogParseResult parsed = LogParser.Parse(raw);
    Ensure(parsed.Events.Count == 5 && parsed.TotalLines == 7 &&
           parsed.SkippedLines == 1 && parsed.ContinuationLines == 1,
        "Parser incorrectly invented or lost a timestamp.");
    Ensure(parsed.Events[1].Severity == LogSeverity.Error &&
           parsed.Events[2].Severity == LogSeverity.Warning &&
           parsed.Events[3].Severity == LogSeverity.Fatal &&
           parsed.Events[4].Severity == LogSeverity.Unknown,
        "Severity parsing should be honest about unknown levels.");
}
static void NormalizedErrors()
{
    string one = AnomalyDetector.NormalizePattern(
        "Connection 9024 failed for id 70 at 0xDEADBEEF");
    string two = AnomalyDetector.NormalizePattern(
        "Connection 6543 failed for id 39 at 0xCAFEBABE");
    Ensure(one == two, "Variable IDs and hex addresses should not create artificial new patterns.");
}
static void ShortLog()
{
    LogParseResult parsed = LogParser.Parse(
        "2026-10-29 09:00:00 [ERROR] Only one failure");
    AnomalyReport report = AnomalyDetector.Analyze(parsed);
    Ensure(!report.HasEnoughHistory && report.Findings.Count == 0,
        "A one-line log cannot establish an outlier baseline.");
    AnomalyReport empty = AnomalyDetector.Analyze(LogParser.Parse("undated text"));
    Ensure(empty.Buckets.Count == 0 && empty.Findings.Count == 0,
        "Invalid input must not invent incidents.");
}
static void FailSafely()
{
    var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool thrown = false;
    try { LogParser.Parse(DemoLogs.Create(), cancelled.Token); }
    catch (OperationCanceledException) { thrown = true; }
    Ensure(thrown, "Parser must honor cancellation.");
    thrown = false;
    try { AnomalyDetector.Analyze(LogParser.Parse(DemoLogs.Create()), null, cancelled.Token); }
    catch (OperationCanceledException) { thrown = true; }
    Ensure(thrown, "Detector must honor cancellation.");
    thrown = false;
    try { AnomalyDetector.Analyze(LogParser.Parse(DemoLogs.Create()),
        new AnomalyOptions(0.1)); }
    catch (ArgumentOutOfRangeException) { thrown = true; }
    Ensure(thrown, "Bad sensitivity should fail clearly.");
}
static void Sensitivity()
{
    var parsed = LogParser.Parse(DemoLogs.Create());
    AnomalyReport low = AnomalyDetector.Analyze(parsed, new AnomalyOptions(2));
    AnomalyReport high = AnomalyDetector.Analyze(parsed, new AnomalyOptions(5));
    Ensure(high.BurstThreshold > low.BurstThreshold &&
           low.Findings.Any(f => f.Kind == AnomalyKind.ErrorBurst),
        "Sensitivity must affect the actual threshold.");
}


static void GroupedBursts()
{
    DateTimeOffset start = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    var entries = new List<LogEvent>();
    for (int minute = 0; minute < 80; minute++)
    {
        // A stable, non-error baseline with several high-error windows
        // separated in time. Different dates and repeated device messages
        // should not create one investigation card per bucket.
        entries.Add(new LogEvent(start.AddMinutes(minute), LogSeverity.Info,
            "Health ping", minute + 1) { SourceFile = "App.log" });
        if (minute is 20 or 30 or 40 or 50 or 60)
        {
            for (int j = 0; j < 18; j++)
            {
                entries.Add(new LogEvent(start.AddMinutes(minute).AddMilliseconds(j),
                    LogSeverity.Error, "Purge() FAILED", 1000 + minute * 20 + j)
                {
                    SourceFile = "CardPrinter.log"
                });
            }
        }
    }

    // One distinct, unusual error should survive even when a device floods
    // several independent anomaly windows.
    for (int i = 0; i < 25; i++)
        entries.Add(new LogEvent(start.AddMinutes(70).AddSeconds(i),
            LogSeverity.Error, "Reader busy " + i, 9000 + i)
        { SourceFile = "Reader.log" });
    entries.Add(new LogEvent(start.AddMinutes(71), LogSeverity.Error,
        "Unique disk controller failure", 9999)
    { SourceFile = "App.log" });

    var parsed = new LogParseResult(entries, entries.Count, 0, 0);
    AnomalyReport report = AnomalyDetector.Analyze(parsed);
    AnomalyFinding[] purge = report.Findings.Where(f =>
        f.Kind == AnomalyKind.ErrorBurst &&
        f.Evidence == "Purge() FAILED").ToArray();
    Ensure(report.BurstWindowCount >= 5 && purge.Length == 1 &&
           purge[0].AffectedWindows == 5 &&
           purge[0].FirstWindow < purge[0].LastWindow &&
           purge[0].Explanation.Contains("NOT necessarily occurrences",
               StringComparison.Ordinal) &&
           report.Findings.Any(f => f.Kind == AnomalyKind.RareErrorPattern &&
               f.Evidence.Contains("Unique disk controller failure",
                   StringComparison.Ordinal)),
        "Repeated bursts must be grouped without counting source messages " +
        "as individual incidents or suppressing separate rare errors.");
}

static void RecurringPatterns()
{
    DateTimeOffset start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    var entries = new List<LogEvent>();
    for (int i = 0; i < 19; i++)
        entries.Add(new LogEvent(start.AddSeconds(i), LogSeverity.Error,
            "Printer 100" + i + " Purge() FAILED", 10 + i)
        { SourceFile = i % 2 == 0 ? "App.log" : "CardPrinter.log" });
    entries.Add(new LogEvent(start.AddMinutes(1), LogSeverity.Error,
        "PrintQueueException: Access is denied", 91)
    { SourceFile = "CardPrinter.log" });
    entries.Add(new LogEvent(start.AddMinutes(2), LogSeverity.Info,
        "Printer 1001 Purge() FAILED", 92)
    { SourceFile = "App.log" });

    AnomalyReport report = AnomalyDetector.Analyze(
        new LogParseResult(entries, entries.Count, 0, 0));
    LogMessagePattern purge = report.TopMessagePatterns.Single(p =>
        p.SampleMessage.Contains("Purge() FAILED", StringComparison.Ordinal));
    Ensure(purge.Occurrences == 19 &&
           purge.Severity == LogSeverity.Error &&
           purge.SourceFileCount == 2 &&
           purge.FirstSeen == start &&
           purge.LastSeen == start.AddSeconds(18) &&
           purge.SampleLine > 0 &&
           (purge.SampleSourceFile == "App.log" ||
            purge.SampleSourceFile == "CardPrinter.log") &&
           report.TotalMessagePatterns == 2 &&
           report.TopMessagePatterns.Any(p =>
               p.SampleMessage.Contains("Access is denied", StringComparison.Ordinal)),
        "The frequency chart must count WARN/ERROR/FATAL entries, " +
        "not benign INFO rows, and preserve original trace locations.");
}

static void SourceContext()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-CSharpContext-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "Worker.cs"),
            """
            class Worker
            {
                void Start(int connectionId)
                {
                    logger.Info("Service initialization started");
                    Helper.SimpleLogE(logger, $"Retry timeout on connection {connectionId}");
                    logger.Warn($"Card {connectionId} could not be read");
                    logger.Debug("OK");
                }
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, "obj", "Generated.cs"),
            "logger.Error(\"Skip generated artifacts\");");

        LogSourceContextIndex context = LogSourceContextIndex.Build(root);
        Ensure(context.FilesScanned == 1 && context.TemplateCount >= 3,
            "Scan C# logging statements and ignore generated obj files.");
        LogSourceMatch? exact = context.FindBest("Service initialization started");
        LogSourceMatch? variable = context.FindBest("Retry timeout on connection 9024");
        LogSourceMatch? missing = context.FindBest("Other unrelated message");
        Ensure(exact is { Exact: true } && exact.RelativePath == "Worker.cs" &&
               exact.LineNumber > 0 && variable is { Exact: false } &&
               variable.CodeTemplate.Contains("{connectionId}", StringComparison.Ordinal) &&
               missing is null, "Map exact/interpolated messages back to source without guessing.");
        Ensure(LogSourceContextIndex.TryReadCallerLocation(
            @"C:\dev\project\Source.cs: line 123") == "Source.cs:123",
            "Use explicit DEBUG file:line caller metadata where present.");
        Ensure(LogSourceContextIndex.TryReadCallerLocation("Unrelated error") is null,
            "Never fabricate a source location from an unrelated message.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}



static void RoslynSourceInvestigation()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-RoslynEvidence-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string first = Path.Combine(root, "PrinterWorker.cs");
        File.WriteAllText(first,
            """
            namespace Synthetic;
            class PrinterWorker
            {
                void RepairQueue(int cardId) { }
                void Print(int cardId)
                {
                    if (cardId > 0)
                    {
                        logger.Error($"Retry timeout on connection {cardId}");
                        RepairQueue(cardId);
                    }
                    logger.Info("Service initialization started");
                }
                void CardInfoReadAndChecks() { }
            }
            """);
        File.WriteAllText(Path.Combine(root, "SecondWorker.cs"),
            """
            class SecondWorker
            {
                void Run() { logger.Info("Service initialization started"); }
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, "obj", "Ignored.cs"),
            "class Generated { void Run() { logger.Error(\"Ignored generated file\"); } }");

        SourceInvestigationIndex index = SourceInvestigationIndex.Build(root);
        Ensure(index.FilesScanned == 2 && index.MethodsIndexed >= 4 &&
               index.LoggingSites == 3,
            "Only the explicitly chosen source files should be indexed with syntax.");
        var matched = index.FindLogSites("Retry timeout on connection 9024");
        Ensure(matched.Count == 1 &&
               matched[0].Kind == SourceEvidenceKind.FormattedLoggingTemplate &&
               matched[0].MethodName == "Print" &&
               matched[0].TypeName == "PrinterWorker" &&
               matched[0].Conditions.Contains("cardId > 0", StringComparison.Ordinal) &&
               matched[0].DirectCalls.Contains("RepairQueue", StringComparison.Ordinal) &&
               matched[0].Excerpt.Contains("Retry timeout", StringComparison.Ordinal),
            "Roslyn must return the containing C# method, conditional evidence " +
            "and possible direct calls without inferring runtime cause.");

        var ambiguous = index.FindLogSites("Service initialization started");
        Ensure(ambiguous.Count == 2 &&
               ambiguous.All(c => c.Kind == SourceEvidenceKind.ExactLoggingLiteral),
            "Two matching logging literals must be kept as distinct candidates.");
        Ensure(index.FindLogSites("Unrelated message").Count == 0,
            "No match must not be reported as a source link.");

        var named = index.FindMethods("CardInfoReadAndChecks");
        Ensure(named.Count == 1 &&
               named[0].Kind == SourceEvidenceKind.OperationMethodName &&
               named[0].MethodName == "CardInfoReadAndChecks",
            "A known structured operation should match the exact C# declaration.");
        Ensure(index.FindMethods("UnknownMethodName").Count == 0,
            "Missing operation names are not inferred from timing.");

        var stack = index.FindStackFrames(
            @"at Synthetic.PrinterWorker.Print() in C:\checkout\PrinterWorker.cs:line 9");
        Ensure(stack.Count == 1 &&
               stack[0].Kind == SourceEvidenceKind.ExplicitStackFrame &&
               stack[0].RelativePath == "PrinterWorker.cs" &&
               stack[0].LineNumber == 9,
            "Actual file/line metadata must be checked against scanned C# files.");

        (string resolved, int line) = index.ResolveSourceLocation(matched[0]);
        Ensure(resolved == Path.GetFullPath(first) && line == matched[0].LineNumber,
            "Matching source should navigate to the original indexed physical line.");
        bool rejected = false;
        try
        {
            index.ResolveSourceLocation(matched[0] with
            {
                RelativePath = Path.Combine("..", "Outside.cs")
            });
        }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, "A forged source path must not open outside the selected checkout.");

        const string log =
            "2026-10-30 18:00:00 ERROR Retry timeout on connection 9024\n" +
            "2026-10-30 18:00:01 ERROR Service initialization started\n" +
            "2026-10-30 18:00:02 INFO TOM_OP v=1 event=end " +
            "operation=CardInfoReadAndChecks transaction_id=- " +
            "flow_id=standalone-card duration_ms=4057 status=completed result=unknown\n";
        LogParseResult parsed = LogParser.Parse(log, sourceFile: "Operations.log");
        SourceInvestigationReport report = SourceInvestigationAnalyzer.Analyze(
            AnomalyDetector.Analyze(parsed),
            OperationTimelineAnalyzer.Analyze(parsed), index);
        Ensure(report.Cases.Any(c =>
                   c.Category == "LOG PATTERN" &&
                   c.Evidence.Contains("Retry timeout", StringComparison.Ordinal) &&
                   c.Candidates.Any(x => x.MethodName == "Print")) &&
               report.Cases.Any(c =>
                   c.Category == "LOG PATTERN" && c.Ambiguous &&
                   c.Assessment.Contains("Ambiguous", StringComparison.Ordinal)) &&
               report.Cases.Any(c =>
                   c.Category == "TOM_OP METHOD" &&
                   c.Candidates.Any(x => x.MethodName == "CardInfoReadAndChecks")) &&
               report.Cases.All(c => c.LogLine > 0 && c.LogFile == "Operations.log"),
            "Investigations must retain original log source lines and " +
            "distinguish template matches from method-name hints.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        rejected = false;
        try { SourceInvestigationIndex.Build(root, canceled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Ensure(rejected, "Cancellation must prevent indexing private source files.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}


static void CrossLogCallerEvidence()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-CrossLog-" + Guid.NewGuid().ToString("N"));
    string codeRoot = Path.Combine(root, "checkout");
    string sourceDir = Path.Combine(codeRoot, "src", "AFCS.TOM.UCUILib", "Views");
    string logs = Path.Combine(root, "Logs");
    Directory.CreateDirectory(sourceDir);
    Directory.CreateDirectory(logs);
    const string operationId = "41d14983730d4ac6b1c491fc232b77de";
    try
    {
        string sourceFile = Path.Combine(sourceDir, "UCCardInfoView.xaml.cs");
        string[] lines = Enumerable.Repeat("// unrelated line", 1844).ToArray();
        lines[0] = "namespace Synthetic { class UCCardInfoView {";
        lines[1] = "void ReadCard() {";
        lines[1838] = "try { } catch (Exception ex) {";
        lines[1839] = "BLogic.Helper.LogException(Logger, ex);";
        lines[1840] = "}";
        lines[1842] = "}";
        lines[1843] = "} }";
        File.WriteAllLines(sourceFile, lines);
        SourceInvestigationIndex index = SourceInvestigationIndex.Build(codeRoot);

        string caller = @"C:\Src\Azure\AFCS.TOM-ATM\src\AFCS.TOM.UCUILib\Views\UCCardInfoView\.xaml.cs: line 1840";
        var resolvedCaller = index.FindLoggedCallerLocations(caller);
        Ensure(resolvedCaller.Count == 1 &&
               resolvedCaller[0].Kind == SourceEvidenceKind.LoggedCallerLocation &&
               resolvedCaller[0].LineNumber == 1840 &&
               resolvedCaller[0].MethodName == "ReadCard" &&
               resolvedCaller[0].Excerpt.Contains("LogException", StringComparison.Ordinal),
            "A split .xaml.cs path must resolve ONLY when the selected checkout " +
            "confirms its exact relative path and physical line.");
        Ensure(index.FindLoggedCallerLocations(
                @"C:\Somewhere\OtherProject\Views\UCCardInfoView.xaml.cs: line 1840").Count == 0 &&
               index.FindLoggedCallerLocations(
                @"C:\Src\Azure\AFCS.TOM-ATM\src\AFCS.TOM.UCUILib\Views\UCCardInfoView.xaml.cs: line 99999").Count == 0,
            "Similar filename alone or an out-of-range source line is not proof.");
        (string actualPath, int actualLine) = index.ResolveSourceLocation(
            resolvedCaller[0]);
        Ensure(actualPath == Path.GetFullPath(sourceFile) && actualLine == 1840,
            "Source navigation must resolve only through indexed checkout files.");

        string appName = "App_2026-10-30.log";
        string webName = "WebClient_2026-10-30.log";
        string appError =
            "Unable to read data from the transport connection: The I/O " +
            "operation has been aborted because of either a thread exit " +
            "or an application request. | operation_id=" + operationId +
            " | exception_logger=AppLogger | exception_type=System.IO.IOException";
        File.WriteAllText(Path.Combine(logs, appName),
            "2026-10-30 16:26:53.3860 ERROR " + appError + "\n" +
            "   at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException()\n");
        File.WriteAllText(Path.Combine(logs, webName),
            "2026-10-30 16:26:53.3860 DEBUG " + caller +
            " | operation_id=" + operationId + "\n" +
            "2026-10-30 16:26:53.3860 ERROR " + appError + "\n" +
            // This has an identical timestamp but a different operation ID:
            "2026-10-30 16:26:53.3860 DEBUG " + caller +
            " | operation_id=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");

        LogFolderImportResult import = LogFolderImporter.Import(logs);
        SourceInvestigationReport Investigate(LogParseResult parsed)
        {
            LogEvent original = parsed.Events.First(e =>
                e.Severity == LogSeverity.Error &&
                e.Message.Contains("Unable to read data",
                    StringComparison.Ordinal));
            AnomalyReport normal = AnomalyDetector.Analyze(parsed);
            var finding = new AnomalyFinding(
                AnomalyKind.RareErrorPattern, original.Timestamp,
                "Rare error message pattern", "Original exception from App",
                original.Message, original.LineNumber, 1, 0, 1)
            {
                SourceFile = original.SourceFile,
                Continuation = original.Continuation
            };
            return SourceInvestigationAnalyzer.Analyze(
                normal with { Findings = [finding] },
                OperationTimelineAnalyzer.Analyze(parsed), index);
        }
        SourceInvestigationReport report = Investigate(import.Combined);
        SourceInvestigationCase matched = report.Cases.Single(c =>
            c.Category == "STACK / FINDING");
        Ensure(import.DuplicateCopies == 1 &&
               matched.OperationId == operationId &&
               matched.RelatedEventCount == 2 &&
               matched.RelatedEvents.Any(e => e.SourceFile == webName &&
                   e.Severity == LogSeverity.Debug && e.SourceLine == 1) &&
               matched.Candidates.Any(c =>
                   c.Kind == SourceEvidenceKind.LoggedCallerLocation &&
                   c.RelativePath.EndsWith("UCCardInfoView.xaml.cs",
                       StringComparison.OrdinalIgnoreCase) &&
                   c.LineNumber == 1840) &&
               matched.Assessment.Contains(
                   "Identifier equality proves correlation, not causation",
                   StringComparison.Ordinal) &&
               !matched.RelatedEvents.Any(e =>
                   e.Message.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                       StringComparison.Ordinal)),
            "App error and WebClient DEBUG must link by matching operation_id, " +
            "without borrowing same-timestamp events from unrelated operations.");

        // Search must inspect all retained log lines even if the statistical
        // detector's top-pattern and rare-finding slots omit this error.
        AnomalyReport hiddenError = AnomalyDetector.Analyze(import.Combined) with
        {
            Findings = [],
            TopMessagePatterns = []
        };
        Ensure(SourceInvestigationAnalyzer.Analyze(hiddenError,
                   OperationTimelineAnalyzer.Analyze(import.Combined), index)
                   .Cases.Count == 0,
            "The unranked example must not appear in the default top findings.");
        SourceInvestigationSearchResult exact =
            SourceInvestigationAnalyzer.Search(import.Combined, index, operationId);
        Ensure(exact.MatchingEventCount == 2 && exact.DisplayedGroups == 1 &&
               exact.Cases.Single().Category == "LOG SEARCH" &&
               exact.Cases.Single().Evidence.Contains("Unable to read data",
                   StringComparison.Ordinal) &&
               exact.Cases.Single().RelatedEventCount == 2 &&
               exact.Cases.Single().Candidates.Any(candidate =>
                   candidate.Kind == SourceEvidenceKind.LoggedCallerLocation &&
                   candidate.LineNumber == 1840) &&
               exact.Cases.Single().ObservedAt.HasValue,
            "Searching an old operation_id must find all DEBUG/ERROR entries " +
            "outside top anomalies, group one operation and expose C# caller evidence.");

        SourceInvestigationSearchResult byException =
            SourceInvestigationAnalyzer.Search(import.Combined, index, "IOException");
        Ensure(byException.MatchingEventCount == 1 &&
               byException.Cases.Single().RelatedEvents.Any(entry =>
                   entry.SourceFile == webName && entry.Severity == LogSeverity.Debug),
            "Searching exception text must bring in linked DEBUG metadata even " +
            "when that metadata does not include the exception's words.");

        SourceInvestigationSearchResult byMirrorAlias =
            SourceInvestigationAnalyzer.Search(import.Combined, index, appName);
        Ensure(byMirrorAlias.MatchingEventCount == 1 &&
               byMirrorAlias.Cases.Single().OperationId == operationId,
            "Filename queries must include the original App target if its " +
            "mirrored error was deduplicated in favor of WebClient.");

        SourceInvestigationSearchResult byOtherId =
            SourceInvestigationAnalyzer.Search(import.Combined, index,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Ensure(byOtherId.MatchingEventCount == 1 &&
               byOtherId.Cases.Single().OperationId != operationId &&
               byOtherId.Cases.Single().RelatedEventCount == 1 &&
               byOtherId.Cases.Single().RelatedEvents.All(entry =>
                   !entry.Message.Contains(operationId, StringComparison.Ordinal)),
            "Two events at exactly the same timestamp must never be joined " +
            "across different operation IDs.");

        SourceInvestigationSearchResult withoutSource =
            SourceInvestigationAnalyzer.Search(import.Combined, null, "IOException");
        Ensure(withoutSource.MatchingEventCount == 1 &&
               withoutSource.Cases.Single().Candidates.Count == 0 &&
               withoutSource.Cases.Single().Assessment.Contains(
                   "Choose a local C# solution", StringComparison.Ordinal),
            "Full-log search must still work without a selected source checkout.");

        bool shortQueryRejected = false;
        try { SourceInvestigationAnalyzer.Search(import.Combined, index, "IO"); }
        catch (ArgumentException) { shortQueryRejected = true; }
        Ensure(shortQueryRejected, "A very short query must not produce huge result lists.");

        using var canceledSearch = new CancellationTokenSource();
        canceledSearch.Cancel();
        bool canceledProperly = false;
        try
        {
            SourceInvestigationAnalyzer.Search(
                import.Combined, index, operationId, canceledSearch.Token);
        }
        catch (OperationCanceledException) { canceledProperly = true; }
        Ensure(canceledProperly, "Full-log search must honor cancellation.");

        // File filtering must recompute evidence from the currently enabled
        // report, not retain sources omitted from the user's analysis.
        LogFolderSelection appOnly = LogFolderImporter.SelectFiles(import, [appName]);
        SourceInvestigationCase isolated = Investigate(appOnly.Combined).Cases
            .Single(c => c.Category == "STACK / FINDING");
        Ensure(isolated.RelatedEventCount == 1 &&
               isolated.Candidates.All(c =>
                   c.Kind != SourceEvidenceKind.LoggedCallerLocation),
            "Disabling WebClient must remove its caller evidence and source link.");
        SourceInvestigationSearchResult filteredSearch =
            SourceInvestigationAnalyzer.Search(appOnly.Combined, index, operationId);
        Ensure(filteredSearch.MatchingEventCount == 1 &&
               filteredSearch.Cases.Single().RelatedEventCount == 1 &&
               filteredSearch.Cases.Single().Candidates.All(candidate =>
                   candidate.Kind != SourceEvidenceKind.LoggedCallerLocation),
            "Search must recompute from included files, never from the original folder.");

    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void FolderImport()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-Logs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "old"));
        var app = new System.Text.StringBuilder();
        var sale = new System.Text.StringBuilder();
        var pos = new System.Text.StringBuilder();
        for (int minute = 0; minute < 36; minute++)
        {
            string prefix = $"2026-10-29 09:{minute:00}:";
            app.AppendLine(prefix + "00.0000 INFO Service heartbeat");
            sale.AppendLine(prefix + "02.0000 ERROR Transaction timeout");
            if (minute == 24)
            {
                for (int k = 0; k < 12; k++)
                {
                    string burst = prefix + $"{20 + k:00}.0000 ERROR Sale request failed";
                    sale.AppendLine(burst);
                    app.AppendLine(burst); // NLog mirrors this exact event.
                }
            }

            pos.AppendLine(prefix + "03.0000 INFO POS reader active");
        }
        string appFile = Path.Combine(root, "App_2026-10-29.log");
        string saleFile = Path.Combine(root, "SaleController_2026-10-29.log");
        File.WriteAllText(appFile, app.ToString());
        File.WriteAllText(saleFile, sale.ToString());
        File.WriteAllText(Path.Combine(root, "old", "POSReader_2026-10-29.LOG"),
            pos.ToString());
        File.WriteAllText(Path.Combine(root, "ignore.json"),
            "not a dated log");
        File.WriteAllText(Path.Combine(root, "notes.md"),
            "ignored");

        LogFolderImportResult folder = LogFolderImporter.Import(root);
        Ensure(folder.Files.Count == 3 &&
               folder.Files.Any(f => f.RelativePath.StartsWith(
                   "old" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
               folder.Files.Any(f => f.Component == "SaleController") &&
               folder.Files.All(f => !Path.IsPathRooted(f.RelativePath)),
            "Recursive folder import must retain relative file origins and components.");

        Ensure(folder.DuplicateCopies == 12 &&
               folder.Combined.Events.Count == 120 &&
               folder.Combined.TotalLines == 132,
            "Mirrored NLog target events must not double-count in the combined timeline.");
        LogEvent? mirrored = folder.Combined.Events.FirstOrDefault(e =>
            e.Message == "Sale request failed");
        Ensure(mirrored is not null && mirrored.SourceFile.StartsWith(
                   "SaleController_", StringComparison.Ordinal) &&
               mirrored.AlsoInFiles.Any(f => f.StartsWith("App_", StringComparison.Ordinal)),
            "Prefer component-specific provenance and keep the mirrored App filename.");

        AnomalyReport report = AnomalyDetector.Analyze(folder.Combined);
        Ensure(report.Findings.Any(f =>
                   f.Kind == AnomalyKind.ErrorBurst &&
                   f.SourceFile.StartsWith("SaleController_", StringComparison.Ordinal) &&
                   f.SourceLine > 0) &&
               report.Buckets.Count(b => b.IsBurst) == 1,
            "Merged time series must detect a real cross-component burst " +
            "with a source filename and physical line number.");

        // Undated devices use another layout: preserve their dated events
        // but NEVER invent an ERROR severity when none was recorded.
        File.WriteAllText(Path.Combine(root, "old", "CardPrinterServer.log"),
            "2026-10-29 09:00:00.0000 Device failure without severity");
        LogFolderImportResult withUnknown = LogFolderImporter.Import(root);
        Ensure(withUnknown.Combined.Events.Any(e =>
                   e.SourceFile.EndsWith("CardPrinterServer.log",
                       StringComparison.OrdinalIgnoreCase) &&
                   e.Severity == LogSeverity.Unknown),
            "A device log without explicit severity must remain Unknown.");

        // Two independent component logs may legitimately record the same
        // message at the same instant. Never conflate them unless one is
        // the mirrored App target or a copy of the same named file.
        string independent = "2026-10-29 09:05:00.0000 ERROR Device unavailable";
        File.WriteAllText(Path.Combine(root, "QRReader_2026-10-29.log"),
            independent + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "CLReader_2026-10-29.log"),
            independent + Environment.NewLine);
        LogFolderImportResult independentSources = LogFolderImporter.Import(root);
        Ensure(independentSources.DuplicateCopies == 12 &&
               independentSources.Combined.Events.Count(e =>
                   e.Message == "Device unavailable") == 2,
            "Independent same-time/same-text device events must not be " +
            "mistaken for NLog copies and dropped.");

        // A copied archived file with the same name represents an older
        // backup of the SAME NLog target, not a second active component.
        File.WriteAllText(Path.Combine(root, "old", "App_2026-10-29.log"),
            app.ToString());
        LogFolderImportResult archivedCopy = LogFolderImporter.Import(root);
        Ensure(archivedCopy.DuplicateCopies == 60 &&
               archivedCopy.Combined.Events.Count(e =>
                   e.Message == "Sale request failed") == 12,
            "A copied App archive must not produce another incident burst, " +
            "and the original per-file statistics should remain intact.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void FolderGuards()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-FolderGuards-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        bool failed = false;
        try { LogFolderImporter.Import(root); }
        catch (InvalidDataException) { failed = true; }
        Ensure(failed, "Empty Logs folder needs a clear error, not a successful empty result.");

        string file = Path.Combine(root, "App_2026-10-29.log");
        File.WriteAllText(file, "2026-10-29 09:00:00.0000 ERROR Example");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        failed = false;
        try { LogFolderImporter.Import(root, canceled.Token); }
        catch (OperationCanceledException) { failed = true; }
        Ensure(failed, "Cancelled folder scan must stop immediately.");

        const int largeLines = 9_000;
        string payload = new('X', 2_050);
        using (var writer = new StreamWriter(file, append: false))
        {
            for (int i = 0; i < largeLines; i++)
                writer.WriteLine("2026-10-29 09:00:00 INFO profiler entry " +
                    i + " " + payload);
        }
        Ensure(new FileInfo(file).Length > 16L * 1024 * 1024,
            "Fixture must exceed the obsolete 16 MiB input limit.");

        string operations = Path.Combine(root, "Operations_2026-10-29.log");
        File.WriteAllText(operations,
            "2026-10-29 09:00:00 INFO TOM_OP v=1 event=start " +
            "operation=TrySellContract transaction_id=77 session_id=s1 flow_id=f1" +
            Environment.NewLine);
        LogFolderImportResult full = LogFolderImporter.Import(root);
        Ensure(full.Files.Count == 2 &&
               full.Combined.Events.Count == largeLines + 1 &&
               full.RawEvents.Count == largeLines + 1 &&
               full.SkippedFiles.Count == 0 &&
               full.Files.Any(f => f.RelativePath == "App_2026-10-29.log" &&
                   f.DatedEvents == largeLines) &&
               full.RawEvents.Any(e => e.SourceFile == "App_2026-10-29.log" &&
                   e.LineNumber == largeLines) &&
               full.TotalBytes > 16L * 1024 * 1024 &&
               OperationTimelineAnalyzer.Analyze(full.Combined).GroupCount == 1,
            "Large profiler must be parsed without a byte-based exclusion, " +
            "and the operation log must retain its source line.");
        LogParseResult standalone = LogFolderImporter.ImportSingleFile(file);
        Ensure(standalone.Events.Count == largeLines &&
               standalone.Events[^1].LineNumber == largeLines,
            "A large standalone LOG/TXT file must use incremental parsing.");
        File.Delete(operations);

        File.WriteAllBytes(file, [0xC3, 0x28, 0xFF]); // invalid UTF-8 sequence.
        failed = false;
        try { LogFolderImporter.Import(root); }
        catch (InvalidDataException) { failed = true; }
        Ensure(failed, "Invalid encoding must not be silently replaced with mojibake.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}



static void FolderFileCount()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-LargeArchive-" + Guid.NewGuid().ToString("N"));
    string old = Path.Combine(root, "old");
    Directory.CreateDirectory(old);
    try
    {
        const int archiveCount = 528;
        for (int i = 0; i < archiveCount; i++)
            File.WriteAllText(Path.Combine(old, $"Reader_{i:0000}.log"),
                $"2026-10-29 09:00:00 INFO archive reader {i}" + Environment.NewLine);

        string operations = Path.Combine(root, "Operations_2026-10-30(5).log");
        File.WriteAllText(operations,
            "2026-10-30 18:21:31 INFO TOM_OP v=1 event=step " +
            "step=card_issuing_cancelled session_id=s1 transaction_id=2854 " +
            "flow_id=f1 reason=user category=issuing" + Environment.NewLine);
        File.SetLastWriteTimeUtc(operations,
            new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(root, "App_2026-10-30.log"),
            "2026-10-30 18:21:32 INFO App running" + Environment.NewLine);

        LogFolderImportResult result = LogFolderImporter.Import(root);
        int discovered = archiveCount + 2;
        Ensure(result.Files.Count == discovered &&
               result.SkippedFiles.Count == 0 &&
               result.Combined.Events.Count == discovered &&
               result.Files.Any(f => f.RelativePath == "Operations_2026-10-30(5).log") &&
               result.Files.Any(f => f.RelativePath == "App_2026-10-30.log") &&
               OperationTimelineAnalyzer.Analyze(result.Combined).Groups.Any(g =>
                   g.TransactionId == "2854" &&
                   g.Outcome == "Cancelled by operator"),
            "Many small archived files should all be parsed; current operation " +
            "evidence must not be lost.");

        LogFolderSelection onlyArchived = LogFolderImporter.SelectFiles(
            result, [Path.Combine("old", "Reader_0000.log")]);
        Ensure(onlyArchived.Combined.Events.Count == 1,
            "Archival files must remain independently selectable.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}


static void FolderEventBudget()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-EventBudget-" + Guid.NewGuid().ToString("N"));
    string old = Path.Combine(root, "old");
    Directory.CreateDirectory(old);
    try
    {
        // Include one file that would exceed the remaining budget. Its
        // rows must never be partially included, but later smaller files
        // may still fit the remaining budget.
        File.WriteAllText(Path.Combine(old, "A_2026-10-28.log"),
            "2026-10-28 15:00:00 INFO first old event\n" +
            "2026-10-28 15:00:01 ERROR second old event\n");
        File.WriteAllText(Path.Combine(old, "B_2026-10-29.log"),
            "2026-10-29 15:00:00 INFO first event in B\n" +
            "2026-10-29 15:00:01 ERROR second event in B\n");
        File.WriteAllText(Path.Combine(old, "C_2026-10-30.log"),
            "2026-10-30 15:00:00 WARN C event\n");
        File.WriteAllText(Path.Combine(root, "Operations_2026-10-30(5).log"),
            "2026-10-30 18:21:31 INFO TOM_OP v=1 event=step " +
            "step=card_issuing_cancelled session_id=s1 transaction_id=2854 " +
            "flow_id=card-flow reason=user category=issuing\n");
        File.WriteAllText(Path.Combine(root, "App_2026-10-30.log"),
            "2026-10-30 15:00:00 INFO current app\n");
        // Force deterministically ranked archives (B newer than A and C).
        File.SetLastWriteTimeUtc(Path.Combine(old, "B_2026-10-29.log"),
            new DateTime(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(old, "A_2026-10-28.log"),
            new DateTime(2026, 10, 28, 14, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(old, "C_2026-10-30.log"),
            new DateTime(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc));

        LogFolderImportResult partial = LogFolderImporter.Import(
            root, maxImportedEvents: 4);
        Ensure(partial.Combined.Events.Count == 4 &&
               partial.RawEvents.Count == 4 &&
               partial.Files.Count == 3 &&
               partial.SkippedFiles.Count == 2 &&
               partial.SkippedFiles.All(f => f.Reason.Contains(
                   "retained-event budget", StringComparison.Ordinal)) &&
               partial.Files.Any(f => f.RelativePath == "Operations_2026-10-30(5).log") &&
               partial.Files.Any(f => f.RelativePath == "App_2026-10-30.log") &&
               partial.Files.Any(f => f.RelativePath.EndsWith("B_2026-10-29.log",
                   StringComparison.OrdinalIgnoreCase)) &&
               !partial.Files.Any(f => f.RelativePath.EndsWith("A_2026-10-28.log",
                   StringComparison.OrdinalIgnoreCase)) &&
               !partial.Files.Any(f => f.RelativePath.EndsWith("C_2026-10-30.log",
                   StringComparison.OrdinalIgnoreCase)) &&
               partial.RawEvents.Any(e => e.SourceFile == "Operations_2026-10-30(5).log" &&
                   e.LineNumber == 1) &&
               partial.TotalBytes == partial.Files.Sum(f =>
                   new FileInfo(Path.Combine(root, f.RelativePath)).Length) &&
               OperationTimelineAnalyzer.Analyze(partial.Combined).Groups.Any(g =>
                   g.TransactionId == "2854" && g.Outcome == "Cancelled by operator"),
            "Event-budget admission must preserve whole files, VTS provenance, " +
            "accurate retained bytes and explicit omissions.");
        bool rejected = false;
        try { LogFolderImporter.SelectFiles(partial, [
            partial.SkippedFiles[0].RelativePath]); }
        catch (ArgumentException) { rejected = true; }
        Ensure(rejected, "Event-budget-excluded files must never be selectable.");
        rejected = false;
        try { LogFolderImporter.Import(root, maxImportedEvents:
            LogFolderImporter.MaxCombinedEvents + 1); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Ensure(rejected, "Caller must not bypass the hard event safety budget.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void MultiYearTimeline()
{
    const string trace =
        "2023-09-01 09:00:00 ERROR old archive failure\n" +
        "2026-10-30 09:00:00 INFO TOM_OP v=1 event=step step=card_preview_ready " +
        "session_id=s1 transaction_id=2854 flow_id=f1\n";
    LogParseResult parsed = LogParser.Parse(trace);
    AnomalyReport report = AnomalyDetector.Analyze(parsed);
    Ensure(report.Buckets.Count > 0 &&
           report.Buckets.Count <= AnomalyDetector.MaxTimelineBuckets &&
           report.BucketMinutes > 120 &&
           report.Source.Events.Count == 2 &&
           report.TotalProblems == 1,
        "Multi-year archives must be analyzed using coarse bounded buckets " +
        "without discarding original events or creating fake findings.");
    Ensure(OperationTimelineAnalyzer.Analyze(parsed).Groups.Any(
            group => group.TransactionId == "2854"),
        "A long statistical window must not discard actual transaction evidence.");
}

static void FolderSelection()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-Filter-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var app = new System.Text.StringBuilder();
        var sales = new System.Text.StringBuilder();
        var reader = new System.Text.StringBuilder();
        for (int minute = 0; minute < 36; minute++)
        {
            string t = $"2026-10-29 09:{minute:00}:";
            app.AppendLine(t + "00.0000 INFO App alive");
            sales.AppendLine(t + "01.0000 ERROR Sale connection timeout");
            reader.AppendLine(t + "02.0000 INFO Device ready");
            if (minute == 24)
            {
                for (int i = 0; i < 12; i++)
                {
                    string mirrored = t + $"{20 + i:00}.0000 ERROR Sale request failed";
                    app.AppendLine(mirrored);
                    sales.AppendLine(mirrored);
                }
            }
        }

        string appPath = "App_2026-10-29.log";
        string salesPath = "SaleController_2026-10-29.log";
        string readerPath = "POSReader_2026-10-29.log";
        File.WriteAllText(Path.Combine(root, appPath), app.ToString());
        File.WriteAllText(Path.Combine(root, salesPath), sales.ToString());
        File.WriteAllText(Path.Combine(root, readerPath), reader.ToString());

        LogFolderImportResult original = LogFolderImporter.Import(root);
        Ensure(original.RawEvents.Count == 132 && original.Combined.Events.Count == 120,
            "A complete import must retain raw NLog copies for reversible filtering.");

        LogFolderSelection onlySales = LogFolderImporter.SelectFiles(original, [salesPath]);
        Ensure(onlySales.Combined.Events.Count == 48 &&
               onlySales.DuplicateCopies == 0 &&
               onlySales.Combined.Events.All(e => e.SourceFile == salesPath),
            "Filtering App out must retain all genuine sales records, including mirrors.");

        LogFolderSelection onlyApp = LogFolderImporter.SelectFiles(original, [appPath]);
        Ensure(onlyApp.Combined.Events.Count == 48 &&
               onlyApp.DuplicateCopies == 0 &&
               onlyApp.Combined.Events.Any(e =>
                   e.Message == "Sale request failed" && e.SourceFile == appPath),
            "Filtering Sales out must recover the App-side copy of mirrored entries.");

        LogFolderSelection unrelated = LogFolderImporter.SelectFiles(
            original, [readerPath]);
        Ensure(unrelated.Combined.Events.Count == 36 &&
               unrelated.Combined.Events.All(e => e.Severity == LogSeverity.Info) &&
               AnomalyDetector.Analyze(unrelated.Combined).Findings.Count == 0,
            "Disabled components must disappear from the actual anomaly result.");

        LogFolderSelection empty = LogFolderImporter.SelectFiles(original, []);
        Ensure(empty.Combined.Events.Count == 0 && empty.Combined.TotalLines == 0 &&
               AnomalyDetector.Analyze(empty.Combined).Findings.Count == 0,
            "Disabling all logs must leave an explicitly empty result.");

        LogFolderSelection restored = LogFolderImporter.SelectFiles(
            original, [appPath, readerPath, salesPath]);
        Ensure(restored.Combined.Events.Count == 120 &&
               restored.DuplicateCopies == 12 &&
               restored.Combined.TotalLines == original.Combined.TotalLines,
            "Re-enabling all files must restore the original deduplicated view.");

        bool badSelection = false;
        try { LogFolderImporter.SelectFiles(original, ["../outside.log"]); }
        catch (ArgumentException) { badSelection = true; }
        Ensure(badSelection, "Unknown or unsafe relative paths must not be accepted.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool canceledThrown = false;
        try { LogFolderImporter.SelectFiles(original, [salesPath], canceled.Token); }
        catch (OperationCanceledException) { canceledThrown = true; }
        Ensure(canceledThrown, "Re-filtering must support cancellation.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}


static void MultilineContext()
{
    const string raw =
        "2026-10-29 09:00:00 ERROR Operation failed\n" +
        "System.InvalidOperationException: Invalid state\n" +
        "   at Example.Reader.Process() in C:\\Example\\Reader.cs:line 47\n" +
        " ---> System.ArgumentException: Invalid argument\n" +
        "--- End of inner exception stack trace ---\n" +
        "unrelated undated text\n" +
        "   at Example.Unrelated.ShouldNotAttach()\n" +
        "2026-10-29 09:00:01 Device returned ERROR status without a level\n" +
        "2026-10-29 09:00:02 INFO Recovery completed\n" +
        "   at Example.Service.Recover()\n";
    LogParseResult parsed = LogParser.Parse(raw, sourceFile: "Reader.log");
    Ensure(parsed.Events.Count == 3 && parsed.ContinuationLines == 5 &&
           parsed.SkippedLines == 2 && parsed.TotalLines == 10,
        "Only contiguous exception/stack lines should be attached to dated events.");
    Ensure(parsed.Events[0].Continuation.Contains("System.InvalidOperationException") &&
           parsed.Events[0].Continuation.Contains("Reader.cs:line 47") &&
           !parsed.Events[0].Continuation.Contains("ShouldNotAttach") &&
           parsed.Events[0].SourceFile == "Reader.log" &&
           parsed.Events[0].LineNumber == 1 &&
           parsed.Events[1].Severity == LogSeverity.Unknown &&
           parsed.Events[1].Continuation.Length == 0 &&
           parsed.Events[2].Continuation.Contains("Recover()"),
        "Context must retain exact text and provenance without inventing severity.");

    string oversized = "2026-10-29 09:00:00 ERROR Too many frames\n" +
        string.Join("\n", Enumerable.Range(0, LogParser.MaxContinuationLinesPerEvent + 10)
            .Select(i => "   at Example.Frame" + i + "(" + new string('X', 180) + ")"));
    LogParseResult bounded = LogParser.Parse(oversized);
    Ensure(bounded.Events.Count == 1 &&
           bounded.ContinuationLines == LogParser.MaxContinuationLinesPerEvent + 10 &&
           bounded.Events[0].ContinuationTruncated &&
           bounded.Events[0].Continuation.Length <= LogParser.MaxContinuationCharactersPerEvent &&
           bounded.Events[0].Continuation.Split('\n').Length <=
               LogParser.MaxContinuationLinesPerEvent,
        "Always count recognized frames, but bound retained context per event.");
}

static void FindingExceptionContext()
{
    var builder = new System.Text.StringBuilder();
    for (int minute = 0; minute < 36; minute++)
    {
        builder.AppendLine($"2026-10-29 09:{minute:00}:00 ERROR Retry timeout for job {minute}");
        if (minute == 20)
        {
            builder.AppendLine("2026-10-29 09:20:05 ERROR Worker stopped unexpectedly");
            builder.AppendLine("System.TimeoutException: A synthetic timeout");
            builder.AppendLine("   at Example.Worker.Run() in C:\\Sample\\Worker.cs:line 25");
        }
    }

    LogParseResult parsed = LogParser.Parse(builder.ToString(), sourceFile: "Worker.log");
    AnomalyReport report = AnomalyDetector.Analyze(parsed);
    AnomalyFinding? rare = report.Findings.FirstOrDefault(f =>
        f.Kind == AnomalyKind.RareErrorPattern &&
        f.Evidence == "Worker stopped unexpectedly");
    Ensure(rare is not null && rare.Continuation.Contains("TimeoutException") &&
           rare.Continuation.Contains("Worker.cs:line 25") &&
           !rare.ContinuationTruncated &&
           rare.SourceFile == "Worker.log" && rare.SourceLine > 0 &&
           report.Source.Events.Count == 37 &&
           report.TotalProblems == 37,
        "Real exception details should reach a rare finding without adding dated events.");
}


static void StructuredOperationTimeline()
{
    const string source = "Operations_2026-10-30.log";
    const string raw =
        "2026-10-30 16:00:00 INFO TOM_OP v=1 event=step step=card_preview_ready session_id=s1 process_id=10 transaction_id=- flow_id=issue\n" +
        "2026-10-30 16:00:10 INFO TOM_OP v=1 event=step step=card_issuing_cancelled session_id=s1 transaction_id=- flow_id=issue category=issuing reason=user\n" +
        "2026-10-30 16:00:10 INFO TOM_OP v=1 event=step step=card_preview_decision session_id=s1 transaction_id=- flow_id=issue category=cancel\n" +
        "2026-10-30 16:00:11 INFO TOM_OP v=1 event=end operation=TryOpenTransaction session_id=s1 transaction_id=2849 flow_id=issue result=true duration_ms=55\n" +
        "2026-10-30 16:00:12 INFO TOM_OP v=1 event=step step=card_commit_status session_id=s1 transaction_id=2849 flow_id=issue category=GenericError success=false\n" +
        "2026-10-30 16:00:12 INFO TOM_OP v=1 event=start operation=CardEmissionCommit session_id=s1 transaction_id=2849 flow_id=issue operation_id=op-commit\n" +
        "2026-10-30 16:00:12 INFO TOM_OP v=1 event=end operation=CardEmissionCommit operation_id=op-commit transaction_id=2849 flow_id=issue result=true duration_ms=140\n" +
        "2026-10-30 16:00:13 INFO TOM_OP v=1 event=end operation=TryCloseEmptyTransaction session_id=s1 transaction_id=2849 result=true duration_ms=113\n" +
        "2026-10-30 16:01:00 INFO TOM_OP v=1 event=step step=quick_reload_requested session_id=s1 transaction_id=- flow_id=sale\n" +
        "2026-10-30 16:01:01 INFO TOM_OP v=1 event=end operation=TryOpenTransaction session_id=s1 transaction_id=2847 flow_id=sale result=true\n" +
        "2026-10-30 16:01:02 INFO TOM_OP v=1 event=step step=undo_mode session_id=s1 transaction_id=2847 flow_id=undo category=Contract\n" +
        "2026-10-30 16:01:03 INFO TOM_OP v=1 event=end operation=PrintReceipt session_id=s1 transaction_id=2847 flow_id=undo result=false duration_ms=1350\n" +
        "2026-10-30 16:01:04 INFO TOM_OP v=1 event=end operation=VtsTransactionClose session_id=s1 transaction_id=2847 flow_id=sale result=true duration_ms=115\n" +
        "2026-10-30 16:01:05 INFO TOM_OP v=1 event=step step=undo_receipt_print_result session_id=s1 transaction_id=- flow_id=undo success=false category=requested_2\n" +
        "2026-10-30 16:02:00 INFO TOM_OP v=1 event=end operation=CardEmissionCommit session_id=s2 transaction_id=2849 result=true\n" +
        "2026-10-30 16:02:02 INFO TOM_OP v=1 event=step step=quick_reload_requested session_id=s1 transaction_id=- flow_id=orphan\n" +
        "2026-10-30 16:02:03 INFO TOM_OP v=1 event=dialog action=closed session_id=s1 transaction_id=- dialog=unkeyed response=Cancel\n";
    LogParseResult parsed = LogParser.Parse(raw, sourceFile: source);
    OperationTimelineReport report = OperationTimelineAnalyzer.Analyze(parsed);
    Ensure(report.StructuredEvents == 17 && report.GroupCount == 4 &&
           report.UnlinkedEvents == 1,
        "Structured events must use explicit IDs; unrelated dialog cannot be inferred.");
    OperationTimelineGroup issuing = report.Groups.Single(group =>
        group.SessionId == "s1" && group.TransactionId == "2849");
    Ensure(issuing.Outcome == "Cancelled by operator" &&
           issuing.Explanation.Contains("GenericError was committed", StringComparison.Ordinal) &&
           issuing.Explanation.Contains("Preview-to-decision: 10.0 s", StringComparison.Ordinal) &&
           issuing.Events.Count == 8 &&
           issuing.Events[0].SourceFile == source &&
           issuing.Events[0].SourceLine == 1,
        "User cancellation + VTS GenericError must not be reported as a technical failure.");
    OperationTimelineGroup sale = report.Groups.Single(group =>
        group.TransactionId == "2847");
    Ensure(sale.Outcome == "VTS closed; printing incomplete" &&
           sale.FlowIds.Contains("sale") && sale.FlowIds.Contains("undo") &&
           sale.Events.Count == 6 &&
           sale.Events.Any(entry => entry.Step == "quick_reload_requested") &&
           sale.Events.Any(entry => entry.Step == "undo_receipt_print_result"),
        "Different flows inside one real transaction must retain earlier and later steps.");
    Ensure(report.Groups.Single(group => group.SessionId == "s2").EventCount == 1 &&
           report.Groups.Single(group => group.TransactionId == "").FlowIds.Single() == "orphan",
        "The same transaction number in a different session must not be merged.");
    Ensure(issuing.Events.Any(e => e.Operation == "CardEmissionCommit" &&
           e.EventKind == "end" && e.SessionId == "s1" &&
           e.Description.Contains("result=true", StringComparison.Ordinal)),
        "Commit API acknowledgement must remain visible separately from card status.");

    OperationTimelineReport unknown = OperationTimelineAnalyzer.Analyze(LogParser.Parse(
        "2026-10-30 16:00:00 INFO TOM_OP v=2 event=start operation=Unknown " +
        "session_id=x transaction_id=123"));
    Ensure(unknown.StructuredEvents == 0 && unknown.GroupCount == 0,
        "Unknown schema versions must be ignored, not interpreted as v1.");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool stopped = false;
    try { OperationTimelineAnalyzer.Analyze(parsed, cancelled.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Ensure(stopped, "Lifecycle analysis must support cancellation.");
}


static void StandaloneCardRead()
{
    const string path = "Operations_2026-10-30.log";
    const string flow = "6a7b1ae187644c80b9160c9003eb22cc";
    const string line =
        "2026-10-30 18:17:01.7750 INFO TOM_OP v=1 event=end " +
        "operation=CardInfoReadAndChecks " +
        "operation_id=e52290a2566c4d4a881a114bca3ef3dc " +
        "status=completed result=unknown duration_ms=4057 " +
        "transaction_id=- flow_id=" + flow;

    OperationTimelineReport report = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line, sourceFile: path));
    Ensure(report.StructuredEvents == 1 && report.UnlinkedEvents == 0 &&
           report.GroupCount == 1 && report.Groups.Count == 1,
        "Independent read must be visible without a VTS transaction.");
    OperationTimelineGroup cardRead = report.Groups.Single();
    Ensure(cardRead.Kind == OperationTimelineGroupKind.StandaloneCardRead &&
           cardRead.TransactionId.Length == 0 &&
           cardRead.FlowIds.Single() == flow &&
           cardRead.Outcome == "Card reading/checks completed" &&
           cardRead.Explanation.Contains("not return a value", StringComparison.Ordinal) &&
           cardRead.Explanation.Contains("no VTS transaction is required",
               StringComparison.Ordinal) &&
           !cardRead.Events[0].Description.Contains("result=unknown",
               StringComparison.Ordinal) &&
           cardRead.Events[0].DurationMs == 4057 &&
           cardRead.Events[0].SourceFile == path &&
           cardRead.Events[0].SourceLine == 1,
        "status=completed must not be interpreted as proven read success or missing VTS.");

    OperationTimelineReport pending = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line.Replace("event=end", "event=start",
            StringComparison.Ordinal), sourceFile: path));
    Ensure(pending.Groups.Single().Kind == OperationTimelineGroupKind.StandaloneCardRead &&
           pending.Groups.Single().Outcome == "Card reading/checks observed",
        "Without end event, completion must not be invented.");

    OperationTimelineReport succeeded = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line.Replace("result=unknown", "result=true",
            StringComparison.Ordinal), sourceFile: path));
    Ensure(succeeded.Groups.Single().Outcome == "Card reading/checks reported success",
        "Only explicit success may establish a positive read result.");

    OperationTimelineReport failed = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line.Replace("result=unknown", "result=false",
            StringComparison.Ordinal), sourceFile: path));
    Ensure(failed.Groups.Single().Outcome == "Card reading/checks reported failure",
        "Explicit read failure is not VTS link failure.");

    OperationTimelineReport transaction = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line.Replace("transaction_id=-", "transaction_id=2854",
            StringComparison.Ordinal), sourceFile: path));
    Ensure(transaction.Groups.Single().Kind == OperationTimelineGroupKind.VtsTransaction,
        "Explicit VTS ID takes precedence.");

    OperationTimelineReport unknown = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line.Replace("CardInfoReadAndChecks", "UnknownOperation",
            StringComparison.Ordinal), sourceFile: path));
    Ensure(unknown.Groups.Single().Kind == OperationTimelineGroupKind.UnlinkedFlow &&
           unknown.Groups.Single().Outcome == "Flow observed; VTS link unknown",
        "Unknown flow operations must not be silently presumed standalone.");

    OperationTimelineReport mixed = OperationTimelineAnalyzer.Analyze(
        LogParser.Parse(line + "\n" +
            "2026-10-30 18:17:02 INFO TOM_OP v=1 event=start " +
            "operation=PrintReceipt transaction_id=- flow_id=" + flow,
            sourceFile: path));
    Ensure(mixed.Groups.Single().Kind == OperationTimelineGroupKind.UnlinkedFlow,
        "Mixed named operations must not be labeled a standalone card read.");
}

static void Ensure(bool expression, string message)
{
    if (!expression) throw new InvalidOperationException(message);
}
