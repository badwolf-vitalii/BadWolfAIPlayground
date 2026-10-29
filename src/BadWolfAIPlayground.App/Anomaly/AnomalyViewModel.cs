using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using BadWolfAIPlayground.Anomaly;
using BadWolfAIPlayground.App.ViewModels;
using BadWolfAIPlayground.Search;
using Microsoft.Win32;

namespace BadWolfAIPlayground.App.Anomaly;

/// <summary>Only explicitly selected local files or synthetic data are analyzed.</summary>
public sealed class AnomalyViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly RelayCommand _demo, _open, _run, _cancel, _chooseSource, _chooseFolder;
    private CancellationTokenSource? _active;
    private LogParseResult? _loaded;
    private AnomalyReport? _lastReport;
    private LogSourceContextIndex? _sourceIndex;
    private string _sourceContextStatus =
        "Optional: select a local Visual Studio solution to match log messages to C# logging statements.";
    private string _source = "No file selected";
    private string _filesSummary = "Choose a Logs folder to inspect all components.";
    private int _duplicateCopies;
    private string _status = "Load a generated example or select a local LOG/TXT file.";
    private string _summary = "No analysis yet.";
    private string _diagnostics = "No parser diagnostics.";
    private string _preview = "Nothing is uploaded or sent to Ollama.";
    private double _sensitivity = 3;
    private bool _busy;
    private int _events, _problems, _findings;
    private bool _disposed;

    public AnomalyViewModel()
    {
        _demo = new RelayCommand(async () => await UseDemoAsync(), () => !Busy);
        _open = new RelayCommand(async () => await ChooseFileAsync(), () => !Busy);
        _run = new RelayCommand(async () => await AnalyzeAsync(), () => !Busy && _loaded is not null);
        _cancel = new RelayCommand(() => _active?.Cancel(), () => Busy);
        _chooseSource = new RelayCommand(async () => await ChooseSourceAsync(), () => !Busy);
        _chooseFolder = new RelayCommand(async () => await ChooseFolderAsync(), () => !Busy);
    }

    public ObservableCollection<AnomalyBucketRow> Timeline { get; } = [];
    public ObservableCollection<AnomalyFindingRow> Findings { get; } = [];
    public ObservableCollection<AnomalySourceFileRow> SourceFiles { get; } = [];
    public ICommand UseDemoCommand => _demo;
    public ICommand ChooseFileCommand => _open;
    public ICommand ChooseFolderCommand => _chooseFolder;
    public ICommand AnalyzeCommand => _run;
    public ICommand CancelCommand => _cancel;
    public ICommand ChooseSourceCommand => _chooseSource;
    public string SourceContextStatus
    {
        get => _sourceContextStatus;
        private set => SetProperty(ref _sourceContextStatus, value);
    }
    public string SourceName { get => _source; private set => SetProperty(ref _source, value); }
    public string SourceFilesSummary
    {
        get => _filesSummary;
        private set => SetProperty(ref _filesSummary, value);
    }
    public int DuplicateCopies
    {
        get => _duplicateCopies;
        private set => SetProperty(ref _duplicateCopies, value);
    }
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public string Preview { get => _preview; private set => SetProperty(ref _preview, value); }
    public int EventCount { get => _events; private set => SetProperty(ref _events, value); }
    public int ProblemCount { get => _problems; private set => SetProperty(ref _problems, value); }
    public int FindingCount { get => _findings; private set => SetProperty(ref _findings, value); }

    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            if (SetProperty(ref _sensitivity, Math.Clamp(value, 1.5, 8)))
            {
                StatusText = "Sensitivity changed. Click Reanalyze to update the findings.";
            }
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                _demo.NotifyCanExecuteChanged();
                _open.NotifyCanExecuteChanged();
                _run.NotifyCanExecuteChanged();
                _cancel.NotifyCanExecuteChanged();
                _chooseSource.NotifyCanExecuteChanged();
                _chooseFolder.NotifyCanExecuteChanged();
            }
        }
    }

    public Task UseDemoAsync() => ImportTextAsync(
        "Generated demonstration — 36 minutes",
        DemoLogs.Create());

    public Task ImportTextAsync(string name, string text) => ExecuteAsync(async token =>
    {
        LogParseResult parsed = await Task.Run(() => LogParser.Parse(text, token, name), token);
        AnomalyReport analyzed = await Task.Run(() =>
            AnomalyDetector.Analyze(parsed, new AnomalyOptions(Sensitivity), token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = parsed;
            ClearFileSummary();
            SourceName = name;
            string preview = string.Join(Environment.NewLine,
                text.Split('\n').Take(5).Select(s => s.TrimEnd('\r')));
            Preview = preview[..Math.Min(650, preview.Length)];
            Apply(analyzed);
            _run.NotifyCanExecuteChanged();
        });
    });

    public Task ImportFileAsync(string path) => ExecuteAsync(async token =>
    {
        // Reuse the established bounded, binary-safe file loader.
        // No imported log is persisted by this lab.
        var imported = await LocalTextLoader.LoadAsync([path], token);
        string text = imported[0].Content;
        LogParseResult parsed = await Task.Run(() => LogParser.Parse(text, token, Path.GetFileName(path)), token);
        AnomalyReport result = await Task.Run(() =>
            AnomalyDetector.Analyze(parsed, new AnomalyOptions(Sensitivity), token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = parsed;
            ClearFileSummary();
            SourceName = Path.GetFileName(path);
            Preview = text[..Math.Min(text.Length, 650)];
            Apply(result);
            _run.NotifyCanExecuteChanged();
        });
    });

    /// <summary>
    /// Loads every supported NLog text file from one selected Logs folder,
    /// including nested archives. All-or-nothing, cancellable, offline.
    /// </summary>
    public Task ImportFolderAsync(string folder) => ExecuteAsync(async token =>
    {
        StatusText = "Scanning local Logs folder...";
        var progress = new Progress<LogFolderProgress>(item =>
        {
            if (Busy && _active?.IsCancellationRequested == false)
            {
                StatusText =
                    $"Reading {item.CurrentFile}/{item.TotalFiles}: {item.RelativePath}";
            }
        });
        LogFolderImportResult imported = await Task.Run(() =>
            LogFolderImporter.Import(folder, token, progress), token);
        AnomalyReport report = await Task.Run(() =>
            AnomalyDetector.Analyze(imported.Combined,
                new AnomalyOptions(Sensitivity), token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = imported.Combined;
            SourceName = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}" +
                $" — {imported.Files.Count:N0} log files";
            Preview =
                "All supported .log and .txt files in the chosen folder, including old/, " +
                "were analyzed locally. Every finding includes its original " +
                "relative filename and physical line number.";
            SourceFiles.Clear();
            foreach (LogFolderFile file in imported.Files
                .OrderByDescending(f => f.ProblemEvents)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                SourceFiles.Add(new AnomalySourceFileRow(
                    file.RelativePath, file.Component, file.DatedEvents,
                    file.ProblemEvents, file.SkippedLines, file.ContinuationLines));
            }
            DuplicateCopies = imported.DuplicateCopies;
            int components = imported.Files.Select(f => f.Component)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            SourceFilesSummary =
                $"{imported.Files.Count:N0} log files · {components:N0} components · " +
                $"{imported.TotalBytes / 1048576.0:0.##} MiB. " +
                $"Exact copies in multiple NLog targets: {imported.DuplicateCopies:N0}. " +
                "The aggregate counts distinct events; file rows include original copies.";
            Apply(report);
            _run.NotifyCanExecuteChanged();
        });
    });

    private async Task ChooseFolderAsync()
    {
        if (Busy || _disposed)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select the folder containing all application logs",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            await ImportFolderAsync(dialog.FolderName);
        }
    }

    private void ClearFileSummary()
    {
        SourceFiles.Clear();
        SourceFilesSummary = "Single log / built-in demo; no Logs folder selected.";
        DuplicateCopies = 0;
    }

    public Task AnalyzeAsync() => ExecuteAsync(async token =>
    {
        LogParseResult? parsed = _loaded;
        if (parsed is null)
        {
            return;
        }
        AnomalyReport analyzed = await Task.Run(() =>
            AnomalyDetector.Analyze(parsed, new AnomalyOptions(Sensitivity), token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() => Apply(analyzed));
    });

    public Task LoadSourceContextAsync(string folder) => ExecuteAsync(async token =>
    {
        LogSourceContextIndex index = await Task.Run(() =>
            LogSourceContextIndex.Build(folder, token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _sourceIndex = index;
            SourceContextStatus =
                $"Local C# context loaded: {index.TemplateCount:N0} logging templates " +
                $"from {index.FilesScanned:N0} source files. " +
                "Matches are heuristic; the source stays on this PC.";
            if (_lastReport is not null)
            {
                Apply(_lastReport);
            }
        });
    });

    private async Task ChooseSourceAsync()
    {
        if (Busy || _disposed)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Choose the Visual Studio solution in your local source checkout",
            Filter = "Visual Studio solution (*.sln)|*.sln",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            string root = Path.GetDirectoryName(dialog.FileName)!;
            await LoadSourceContextAsync(root);
        }
    }

    private async Task ChooseFileAsync()
    {
        if (Busy || _disposed)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Select a local application log",
            Filter = "Log files (*.log;*.txt)|*.log;*.txt",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            await ImportFileAsync(dialog.FileName);
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation)
    {
        if (Busy || _disposed)
        {
            return;
        }
        using var source = new CancellationTokenSource();
        _active = source;
        Busy = true;
        StatusText = "Analyzing local events...";
        try
        {
            await operation(source.Token);
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() =>
                StatusText = "Canceled. Previous valid results were preserved.");
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            InvalidDataException or System.Text.DecoderFallbackException)
        {
            await _dispatcher.InvokeAsync(() =>
                StatusText = $"Import/analysis failed: {exception.Message}");
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _active = null;
                Busy = false;
            });
        }
    }

    private void Apply(AnomalyReport report)
    {
        _lastReport = report;
        Findings.Clear();
        Timeline.Clear();
        int maximum = Math.Max(1, report.PeakProblemCount);
        foreach (LogTimeBucket bucket in report.Buckets)
        {
            Timeline.Add(new AnomalyBucketRow(
                bucket.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                bucket.ProblemEvents, bucket.TotalEvents, bucket.IsBurst,
                (int)Math.Round(8 + 72.0 * bucket.ProblemEvents / maximum)));
        }
        foreach (AnomalyFinding item in report.Findings)
        {
            LogSourceMatch? source = _sourceIndex?.FindBest(item.Evidence);
            string sourceHint = source is null
                ? _sourceIndex is null
                    ? "Source context not loaded (optional)."
                    : "No straightforward C# logging template match."
                : "Possible log origin: " + source.Description;

            // Some log helpers emit an explicit caller file and line in
            // the DEBUG entry immediately before an ERROR message.
            if (_loaded is not null)
            {
                LogEvent? problem = _loaded.Events.FirstOrDefault(e =>
                    e.LineNumber == item.SourceLine &&
                    string.Equals(e.SourceFile, item.SourceFile,
                        StringComparison.OrdinalIgnoreCase));
                if (problem is not null)
                {
                    LogEvent? caller = _loaded.Events
                        .Where(e =>
                                    string.Equals(e.SourceFile, item.SourceFile,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    e.LineNumber < item.SourceLine &&
                                    e.LineNumber >= item.SourceLine - 3 &&
                                    (problem.Timestamp - e.Timestamp).Duration() <=
                                        TimeSpan.FromSeconds(3))
                        .OrderByDescending(e => e.LineNumber)
                        .FirstOrDefault(e =>
                            LogSourceContextIndex.TryReadCallerLocation(e.Message) is not null);
                    string? callerLocation = caller is null ? null :
                        LogSourceContextIndex.TryReadCallerLocation(caller.Message);
                    if (callerLocation is not null)
                    {
                        sourceHint = $"Nearby logged caller: {callerLocation} " +
                            "(DEBUG metadata, verify correlation). " + sourceHint;
                    }
                }
            }

            string originalLocation = string.IsNullOrWhiteSpace(item.SourceFile)
                ? $"Log line {item.SourceLine}"
                : $"{item.SourceFile}:{item.SourceLine}";
            string otherTargets = item.AlsoInFiles.Count == 0 ? "" :
                $" (also in {string.Join(", ", item.AlsoInFiles)})";
            Findings.Add(new AnomalyFindingRow(
                item.Kind == AnomalyKind.ErrorBurst ? "BURST" : "RARE ERROR",
                item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                item.Title,
                item.Explanation,
                $"{originalLocation}{otherTargets}: {item.Evidence}",
                sourceHint));
        }

        EventCount = report.Source.Events.Count;
        ProblemCount = report.TotalProblems;
        FindingCount = report.Findings.Count;
        Summary = report.HasEnoughHistory
            ? $"Typical WARN/ERROR/FATAL per {report.BucketMinutes}-minute bucket: " +
              $"{report.TypicalProblemCount:0.##}. Statistical threshold: " +
              $"{report.BurstThreshold:0.##}. Findings: {report.Findings.Count}."
            : "Not enough time history to establish a reliable spike baseline. " +
              "No artificial burst alerts have been generated.";
        Diagnostics = $"Dated events: {report.Source.Events.Count:N0}. " +
            $"Skipped/unrecognized lines: {report.Source.SkippedLines:N0}. " +
            $"Stack-trace continuations: {report.Source.ContinuationLines:N0}.";
        StatusText = report.Source.Events.Count == 0
            ? "No recognized timestamped events. A sanitized sample is needed to support this format."
            : $"Completed locally. {report.Findings.Count} finding(s). No Ollama call or upload.";
    }

    public void Dispose()
    {
        _disposed = true;
        _active?.Cancel();
    }
}

public sealed record AnomalyBucketRow(
    string Time, int Problems, int Total, bool Burst, int Height)
{
    public string Color => Burst ? "#F0A080" : "#438BC1";
    public string Label => $"{Time}: {Problems} warning/error events of {Total}.";
}

public sealed record AnomalyFindingRow(
    string Kind, string Time, string Title, string Explanation,
    string Evidence, string SourceHint);

public sealed record AnomalySourceFileRow(
    string RelativePath, string Component, int DatedEvents,
    int ProblemEvents, int SkippedLines, int ContinuationLines);
