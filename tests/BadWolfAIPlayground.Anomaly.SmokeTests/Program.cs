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
    ("Optional offline C# log-template source context", SourceContext),
    ("Import entire Logs directory with component provenance", FolderImport),
    ("Folder importer fails safely for invalid and oversized sets", FolderGuards)
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
        f.Evidence.Contains("Disk controller")),
        "The rare disk failure needs an explainable finding.");
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


static void FolderImport()
{
    string root = Path.Combine(Path.GetTempPath(),
        "BadWolfAIPlayground-Logs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "old"));
        string start = "2026-10-29 09:00:00.0000";
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

        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(LogFolderImporter.MaxFileBytes + 1);
        }
        failed = false;
        try { LogFolderImporter.Import(root); }
        catch (InvalidDataException) { failed = true; }
        Ensure(failed, "Oversized file must reject the folder before partial analysis.");

        File.WriteAllBytes(file, [0xFF, 0xFE, 0xFA]); // invalid UTF-16 or UTF-8.
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

static void Ensure(bool expression, string message)
{
    if (!expression) throw new InvalidOperationException(message);
}
