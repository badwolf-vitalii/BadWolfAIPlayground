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
    private readonly RelayCommand _demo, _open, _run, _cancel, _chooseSource;
    private CancellationTokenSource? _active;
    private LogParseResult? _loaded;
    private AnomalyReport? _lastReport;
    private LogSourceContextIndex? _sourceIndex;
    private string _sourceContextStatus =
        "Optional: select a local Visual Studio solution to match log messages to C# logging statements.";
    private string _source = "No file selected";
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
    }

    public ObservableCollection<AnomalyBucketRow> Timeline { get; } = [];
    public ObservableCollection<AnomalyFindingRow> Findings { get; } = [];
    public ICommand UseDemoCommand => _demo;
    public ICommand ChooseFileCommand => _open;
    public ICommand AnalyzeCommand => _run;
    public ICommand CancelCommand => _cancel;
    public ICommand ChooseSourceCommand => _chooseSource;
    public string SourceContextStatus
    {
        get => _sourceContextStatus;
        private set => SetProperty(ref _sourceContextStatus, value);
    }
    public string SourceName { get => _source; private set => SetProperty(ref _source, value); }
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
            }
        }
    }

    public Task UseDemoAsync() => ImportTextAsync(
        "Generated demonstration — 36 minutes",
        DemoLogs.Create());

    public Task ImportTextAsync(string name, string text) => ExecuteAsync(async token =>
    {
        LogParseResult parsed = await Task.Run(() => LogParser.Parse(text, token), token);
        AnomalyReport analyzed = await Task.Run(() =>
            AnomalyDetector.Analyze(parsed, new AnomalyOptions(Sensitivity), token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = parsed;
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
        LogParseResult parsed = await Task.Run(() => LogParser.Parse(text, token), token);
        AnomalyReport result = await Task.Run(() =>
            AnomalyDetector.Analyze(parsed, new AnomalyOptions(Sensitivity), token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = parsed;
            SourceName = Path.GetFileName(path);
            Preview = text[..Math.Min(text.Length, 650)];
            Apply(result);
            _run.NotifyCanExecuteChanged();
        });
    });

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
                    e.LineNumber == item.SourceLine);
                if (problem is not null)
                {
                    LogEvent? caller = _loaded.Events
                        .Where(e => e.LineNumber < item.SourceLine &&
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

            Findings.Add(new AnomalyFindingRow(
                item.Kind == AnomalyKind.ErrorBurst ? "BURST" : "RARE ERROR",
                item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                item.Title,
                item.Explanation,
                $"Log line {item.SourceLine}: {item.Evidence}",
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
