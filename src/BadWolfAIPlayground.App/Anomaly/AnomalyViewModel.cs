using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
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
    private const int MaxDisplayedExceptions = 40;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly RelayCommand _demo, _open, _run, _cancel, _chooseSource, _chooseFolder;
    private readonly RelayCommand _selectAll, _selectNone, _chooseEditor;
    private readonly ParameterRelayCommand<AnomalyFindingRow> _openFinding;
    private readonly ParameterRelayCommand<AnomalyExceptionRow> _openException;
    private readonly ParameterRelayCommand<AnomalyOperationEventRow> _openOperationEvent;
    private readonly ParameterRelayCommand<AnomalySourceFileRow> _openLogFile;
    private LogFolderImportResult? _folderImport;
    private string? _folderRoot;
    private string? _singleFilePath;
    private string? _notepadExecutable;
    private bool _suppressFileSelection;
    private string _editorStatus =
        "Notepad++ is auto-detected. For a portable copy, click Locate Notepad++.";
    private CancellationTokenSource? _active;
    private LogParseResult? _loaded;
    private AnomalyReport? _lastReport;
    private OperationTimelineReport? _lastOperations;
    private LogSourceContextIndex? _sourceIndex;
    private string _sourceContextStatus =
        "Optional: select a local Visual Studio solution to match log messages to C# logging statements.";
    private string _source = "No file selected";
    private string _filesSummary = "Choose a Logs folder to inspect all components.";
    private string _skippedSummary = "No files skipped.";
    private string _exceptionSummary = "Load a log or the demo to inspect exception details.";
    private string _operationSummary = "Select logs to correlate structured TOM_OP operations.";
    private int _operationGroupCount;
    private int _duplicateCopies;
    private string _status = "Load a generated example or select a local LOG/TXT file.";
    private string _summary = "No analysis yet.";
    private string _diagnostics = "No parser diagnostics.";
    private string _preview = "Nothing is uploaded or sent to Ollama.";
    private double _sensitivity = 3;
    private bool _busy;
    private int _events, _problems, _findings, _exceptions;
    private bool _disposed;

    public AnomalyViewModel()
    {
        _demo = new RelayCommand(async () => await UseDemoAsync(), () => !Busy);
        _open = new RelayCommand(async () => await ChooseFileAsync(), () => !Busy);
        _run = new RelayCommand(async () => await AnalyzeAsync(), () => !Busy && _loaded is not null);
        _cancel = new RelayCommand(() => _active?.Cancel(), () => Busy);
        _chooseSource = new RelayCommand(async () => await ChooseSourceAsync(), () => !Busy);
        _chooseFolder = new RelayCommand(async () => await ChooseFolderAsync(), () => !Busy);
        _selectAll = new RelayCommand(async () => await SelectAllFilesAsync(true),
            () => CanEditFiles);
        _selectNone = new RelayCommand(async () => await SelectAllFilesAsync(false),
            () => CanEditFiles);
        _chooseEditor = new RelayCommand(ChooseNotepadPlusPlus);
        _openFinding = new ParameterRelayCommand<AnomalyFindingRow>(
            item => OpenLogInNotepadPlusPlus(item.SourceFile, item.SourceLine),
            _ => _folderRoot is not null || _singleFilePath is not null);
        _openException = new ParameterRelayCommand<AnomalyExceptionRow>(
            item => OpenLogInNotepadPlusPlus(item.SourceFile, item.SourceLine),
            _ => _folderRoot is not null || _singleFilePath is not null);
        _openOperationEvent = new ParameterRelayCommand<AnomalyOperationEventRow>(
            item => OpenLogInNotepadPlusPlus(item.SourceFile, item.SourceLine),
            _ => _folderRoot is not null || _singleFilePath is not null);
        _openLogFile = new ParameterRelayCommand<AnomalySourceFileRow>(
            item => OpenLogInNotepadPlusPlus(item.RelativePath, 1),
            _ => _folderRoot is not null);
    }

    public ObservableCollection<AnomalyBucketRow> Timeline { get; } = [];
    public ObservableCollection<AnomalyFindingRow> Findings { get; } = [];
    public ObservableCollection<AnomalyExceptionRow> ExceptionEntries { get; } = [];
    public ObservableCollection<AnomalyOperationGroupRow> OperationGroups { get; } = [];
    public ObservableCollection<AnomalySourceFileRow> SourceFiles { get; } = [];
    public ObservableCollection<LogFolderSkippedFile> SkippedSourceFiles { get; } = [];
    public ICommand UseDemoCommand => _demo;
    public ICommand ChooseFileCommand => _open;
    public ICommand ChooseFolderCommand => _chooseFolder;
    public ICommand SelectAllFilesCommand => _selectAll;
    public ICommand SelectNoFilesCommand => _selectNone;
    public ICommand ChooseNotepadCommand => _chooseEditor;
    public ICommand OpenFindingCommand => _openFinding;
    public ICommand OpenExceptionCommand => _openException;
    public ICommand OpenOperationEventCommand => _openOperationEvent;
    public ICommand OpenLogFileCommand => _openLogFile;
    public bool CanEditFiles => !Busy && _folderImport is not null;
    public string EditorStatus
    {
        get => _editorStatus;
        private set => SetProperty(ref _editorStatus, value);
    }
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
    public string SkippedFilesSummary
    {
        get => _skippedSummary;
        private set => SetProperty(ref _skippedSummary, value);
    }
    public Visibility SkippedFilesVisibility =>
        SkippedSourceFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
    public int ExceptionCount { get => _exceptions; private set => SetProperty(ref _exceptions, value); }
    public string ExceptionSummary
    {
        get => _exceptionSummary;
        private set => SetProperty(ref _exceptionSummary, value);
    }
    public string OperationSummary
    {
        get => _operationSummary;
        private set => SetProperty(ref _operationSummary, value);
    }
    public int OperationGroupCount
    {
        get => _operationGroupCount;
        private set => SetProperty(ref _operationGroupCount, value);
    }

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
                _selectAll.NotifyCanExecuteChanged();
                _selectNone.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditFiles));
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
        OperationTimelineReport operations = await Task.Run(() =>
            OperationTimelineAnalyzer.Analyze(parsed, token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = parsed;
            ClearFileSummary();
            SourceName = name;
            string preview = string.Join(Environment.NewLine,
                text.Split('\n').Take(5).Select(s => s.TrimEnd('\r')));
            Preview = preview[..Math.Min(650, preview.Length)];
            Apply(analyzed, operations);
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
        OperationTimelineReport operations = await Task.Run(() =>
            OperationTimelineAnalyzer.Analyze(parsed, token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = parsed;
            ClearFileSummary();
            _singleFilePath = Path.GetFullPath(path);
            SourceName = Path.GetFileName(path);
            NotifyOpenCommands();

            Preview = text[..Math.Min(text.Length, 650)];
            Apply(result, operations);
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
        bool showProgress = true;
        var progress = new Progress<LogFolderProgress>(item =>
        {
            if (showProgress && Busy && _active?.IsCancellationRequested == false)
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
        OperationTimelineReport operations = await Task.Run(() =>
            OperationTimelineAnalyzer.Analyze(imported.Combined, token), token);
        token.ThrowIfCancellationRequested();
        // Some progress callbacks may still be queued on the dispatcher.
        // Do not let them overwrite the final "Completed locally" status.
        showProgress = false;
        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = imported.Combined;
            ClearFileRows();
            _folderImport = imported;
            _folderRoot = Path.GetFullPath(folder);
            _singleFilePath = null;
            NotifyOpenCommands();
            OnPropertyChanged(nameof(CanEditFiles));
            SourceName = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}" +
                $" — {imported.Files.Count:N0} analyzed files" +
                (imported.SkippedFiles.Count == 0
                    ? "" : $" ({imported.SkippedFiles.Count:N0} skipped)");
            Preview = imported.SkippedFiles.Count == 0
                ? "All eligible .log/.txt files, including old/, were analyzed locally. " +
                  "Findings retain the original relative filename and physical line."
                : "PARTIAL IMPORT: oversized or over-budget logs were NOT analyzed. " +
                  "Only imported files contribute to results. The SKIPPED FILES " +
                  "list identifies the missing data.";
            foreach (LogFolderFile file in imported.Files
                .OrderByDescending(f => f.ProblemEvents)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var row = new AnomalySourceFileRow(
                    file.RelativePath, file.Component, file.DatedEvents,
                    file.ProblemEvents, file.SkippedLines, file.ContinuationLines);
                row.IncludedChanged += OnFolderFileIncludedChanged;
                SourceFiles.Add(row);
            }
            DuplicateCopies = imported.DuplicateCopies;
            UpdateSkippedFiles(imported.SkippedFiles);
            int components = imported.Files.Select(f => f.Component)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            UpdateSelectedFilesSummary(imported.Files.Count);
            Apply(report, operations);
            if (imported.SkippedFiles.Count > 0)
            {
                StatusText =
                    $"Partial import: {imported.Files.Count:N0} analyzed; " +
                    $"{imported.SkippedFiles.Count:N0} skipped due to size limits. " +
                    "See SKIPPED FILES — those files are NOT in the results.";
            }
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
        ClearFileRows();
        UpdateSkippedFiles([]);
        _folderImport = null;
        _folderRoot = null;
        _singleFilePath = null;
        SourceFilesSummary = "Single log / built-in demo; no Logs folder selected.";
        DuplicateCopies = 0;
        OnPropertyChanged(nameof(CanEditFiles));
        NotifyOpenCommands();
    }

    private void UpdateSkippedFiles(IReadOnlyList<LogFolderSkippedFile> skipped)
    {
        SkippedSourceFiles.Clear();
        foreach (LogFolderSkippedFile entry in skipped)
            SkippedSourceFiles.Add(entry);
        SkippedFilesSummary = skipped.Count == 0
            ? "No files skipped."
            : $"{skipped.Count:N0} files were NOT analyzed. " +
              "These sources have no events in charts, findings or operations. " +
              "Use a smaller file if the content is needed.";
        OnPropertyChanged(nameof(SkippedFilesVisibility));
    }

    private void ClearFileRows()
    {
        foreach (AnomalySourceFileRow row in SourceFiles)
        {
            row.IncludedChanged -= OnFolderFileIncludedChanged;
        }
        SourceFiles.Clear();
    }

    private void NotifyOpenCommands()
    {
        _openFinding.NotifyCanExecuteChanged();
        _openException.NotifyCanExecuteChanged();
        _openOperationEvent.NotifyCanExecuteChanged();
        _openLogFile.NotifyCanExecuteChanged();
    }

    private async void OnFolderFileIncludedChanged(object? sender, EventArgs e)
    {
        if (_suppressFileSelection)
        {
            return;
        }
        try
        {
            await RefreshFolderFilterAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"Could not apply file filter: {exception.Message}";
        }
    }

    /// <summary>
    /// Reanalyze the original imported events based on the current enabled
    /// file set, rebuilding duplicate provenance. Never re-read work files.
    /// </summary>
    public Task RefreshFolderFilterAsync() => ExecuteAsync(async token =>
    {
        LogFolderImportResult? original = _folderImport;
        if (original is null)
        {
            return;
        }
        string[] enabled = SourceFiles.Where(x => x.Included)
            .Select(x => x.RelativePath).ToArray();
        LogFolderSelection filtered = await Task.Run(() =>
            LogFolderImporter.SelectFiles(original, enabled, token), token);
        AnomalyReport report = await Task.Run(() =>
            AnomalyDetector.Analyze(filtered.Combined,
                new AnomalyOptions(Sensitivity), token), token);
        OperationTimelineReport operations = await Task.Run(() =>
            OperationTimelineAnalyzer.Analyze(filtered.Combined, token), token);
        token.ThrowIfCancellationRequested();

        await _dispatcher.InvokeAsync(() =>
        {
            _loaded = filtered.Combined;
            DuplicateCopies = filtered.DuplicateCopies;
            UpdateSelectedFilesSummary(filtered.EnabledFileCount);
            Apply(report, operations);
            if (filtered.EnabledFileCount == 0)
            {
                StatusText =
                    "No logs enabled. Check one or more files to analyze their events.";
            }
        });
    });

    public async Task SetIncludedFilesAsync(IReadOnlyCollection<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (_folderImport is null || Busy)
        {
            return;
        }
        var known = new HashSet<string>(
            SourceFiles.Select(row => row.RelativePath), StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
        if (!selected.IsSubsetOf(known))
        {
            throw new ArgumentException(
                "Selection contains files that were not imported.", nameof(filePaths));
        }

        _suppressFileSelection = true;
        try
        {
            foreach (AnomalySourceFileRow row in SourceFiles)
            {
                row.Included = selected.Contains(row.RelativePath);
            }
        }
        finally
        {
            _suppressFileSelection = false;
        }
        await RefreshFolderFilterAsync();
    }

    private Task SelectAllFilesAsync(bool includeAll) => SetIncludedFilesAsync(
        includeAll ? SourceFiles.Select(f => f.RelativePath).ToArray() : []);

    private void UpdateSelectedFilesSummary(int enabledCount)
    {
        if (_folderImport is not LogFolderImportResult imported)
        {
            return;
        }
        int components = SourceFiles.Where(f => f.Included)
            .Select(f => f.Component)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SourceFilesSummary =
            $"Enabled {enabledCount:N0} / {imported.Files.Count:N0} files " +
            $"across {components:N0} components. " +
            $"Exact NLog copies among enabled files: {DuplicateCopies:N0}. " +
            (imported.SkippedFiles.Count == 0 ? "" :
                $"PARTIAL: {imported.SkippedFiles.Count:N0} files skipped entirely. ") +
            "Per-file counts below are original; the timeline uses only enabled files.";
    }

    public (string FullPath, int Line) ResolveLogLocation(string relativeFile, int line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeFile);
        if (line < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        if (_folderImport is not null && _folderRoot is not null)
        {
            bool registered = _folderImport.Files.Any(f =>
                string.Equals(f.RelativePath, relativeFile,
                    StringComparison.OrdinalIgnoreCase));
            if (!registered)
            {
                throw new InvalidOperationException(
                    "The requested file is not part of the selected Logs folder.");
            }

            string root = Path.GetFullPath(_folderRoot);
            string file = Path.GetFullPath(Path.Combine(root, relativeFile));
            string prefix = Path.TrimEndingDirectorySeparator(root) +
                Path.DirectorySeparatorChar;
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to open a log outside the selected folder.");
            }

            return (file, line);
        }

        if (_singleFilePath is not null &&
            string.Equals(Path.GetFileName(_singleFilePath), relativeFile,
                StringComparison.OrdinalIgnoreCase))
        {
            return (_singleFilePath, line);
        }

        throw new InvalidOperationException(
            "Load a local log file or Logs folder before opening in Notepad++.");
    }

    public void ConfigureNotepadPlusPlus(string executable)
    {
        NotepadPlusPlusLauncher.ValidateExecutable(executable);
        _notepadExecutable = Path.GetFullPath(executable);
        EditorStatus = "Notepad++ is configured for this session.";
    }

    private void ChooseNotepadPlusPlus()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate Notepad++ (notepad++.exe)",
            Filter = "Notepad++ executable (notepad++.exe)|notepad++.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ConfigureNotepadPlusPlus(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            EditorStatus = $"Could not use Notepad++: {exception.Message}";
        }
    }

    private void OpenLogInNotepadPlusPlus(string relativeFile, int line)
    {
        try
        {
            (string fullPath, int sourceLine) =
                ResolveLogLocation(relativeFile, line);
            string? executable = _notepadExecutable ??
                NotepadPlusPlusLauncher.DiscoverExecutable();
            if (executable is null)
            {
                StatusText = "Notepad++ not found. Click 'Locate Notepad++' " +
                    "and select your notepad++.exe.";
                return;
            }

            NotepadPlusPlusLauncher.Open(executable, fullPath, sourceLine);
            StatusText = $"Opened {relativeFile}:{sourceLine} in Notepad++.";
        }
        catch (Exception exception) when (exception is IOException or
            InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            StatusText = $"Could not open log in Notepad++: {exception.Message}";
        }
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
        OperationTimelineReport operations = await Task.Run(() =>
            OperationTimelineAnalyzer.Analyze(parsed, token), token);
        token.ThrowIfCancellationRequested();
        await _dispatcher.InvokeAsync(() => Apply(analyzed, operations));
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
            if (_lastReport is not null && _lastOperations is not null)
            {
                Apply(_lastReport, _lastOperations);
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

    private void Apply(AnomalyReport report, OperationTimelineReport operations)
    {
        _lastReport = report;
        _lastOperations = operations;
        Findings.Clear();
        ExceptionEntries.Clear();
        OperationGroups.Clear();
        Timeline.Clear();

        OperationGroupCount = operations.GroupCount;
        OperationSummary = operations.StructuredEvents == 0
            ? "No structured TOM_OP v=1 events in the enabled logs. " +
              "Statistical anomaly detection still works without these markers."
            : "Recognized " + operations.StructuredEvents.ToString("N0", CultureInfo.InvariantCulture) +
              " structured events in " + operations.GroupCount.ToString("N0", CultureInfo.InvariantCulture) +
              " correlated transaction/flow groups. Showing " +
              operations.Groups.Count.ToString("N0", CultureInfo.InvariantCulture) +
              " latest; " + operations.UnlinkedEvents.ToString("N0", CultureInfo.InvariantCulture) +
              " events have neither an explicit transaction ID nor a safe flow link.";

        foreach (OperationTimelineGroup group in operations.Groups)
        {
            string name = group.TransactionId.Length > 0
                ? "VTS transaction " + group.TransactionId
                : "Unlinked flow " + (group.FlowIds.FirstOrDefault() ?? "unknown");
            string period = group.StartedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                " - " + group.EndedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string flows = group.FlowIds.Count == 0 ? "No flow_id present" :
                group.FlowIds.Count + " flow_id(s): " + string.Join(", ", group.FlowIds);
            AnomalyOperationEventRow[] rows = group.Events.Select(entry =>
                new AnomalyOperationEventRow(
                    entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    entry.Description, entry.SourceFile, entry.SourceLine)).ToArray();
            OperationGroups.Add(new AnomalyOperationGroupRow(
                name + "  |  " + period + "  |  " + group.EventCount + " events",
                group.Outcome, group.Explanation,
                flows + (group.EventCount > rows.Length
                    ? " (showing the most recent " + rows.Length + " events)"
                    : ""),
                rows));
        }

        // Show all captured exception context, even when the detector found
        // no statistical anomaly. Display is bounded independently of import.
        List<LogEvent> withContext = report.Source.Events
            .Where(entry => entry.Continuation.Length > 0).ToList();
        ExceptionCount = withContext.Count;
        foreach (LogEvent entry in withContext
            .OrderByDescending(item => item.Timestamp)
            .Take(MaxDisplayedExceptions))
        {
            string continuation = entry.ContinuationTruncated
                ? entry.Continuation + "\n[Additional stack context omitted (safety limit).]"
                : entry.Continuation;
            ExceptionEntries.Add(new AnomalyExceptionRow(
                entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Severity.ToString().ToUpperInvariant(),
                entry.SourceFile, entry.LineNumber, entry.Message, continuation));
        }

        ExceptionSummary = ExceptionCount == 0
            ? "No multiline exception/stack details in the enabled logs. " +
              "A log with its entire exception on one dated line will not appear here."
            : $"Captured {ExceptionCount:N0} dated event(s) with exception/stack context. " +
              $"Showing the newest {ExceptionEntries.Count:N0} (max {MaxDisplayedExceptions}). " +
              "These are original log details, not necessarily statistical anomalies.";
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
                sourceHint)
            {
                SourceFile = item.SourceFile,
                SourceLine = item.SourceLine,
                Context = item.ContinuationTruncated
                    ? item.Continuation + "\n[Additional stack context omitted (safety limit).]"
                    : item.Continuation
            });
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
        ClearFileRows();
        _folderImport = null;
    }
}

public sealed record AnomalyBucketRow(
    string Time, int Problems, int Total, bool Burst, int Height)
{
    public string Color => Burst ? "#F0A080" : "#438BC1";
    public string Label => $"{Time}: {Problems} warning/error events of {Total}.";
}

/// <summary>Correlated operation group from explicit TOM_OP records.</summary>
public sealed record AnomalyOperationGroupRow(
    string Heading, string Outcome, string Explanation, string FlowSummary,
    IReadOnlyList<AnomalyOperationEventRow> Events);

/// <summary>A structured event retaining its original source line.</summary>
public sealed record AnomalyOperationEventRow(
    string Time, string Description, string SourceFile, int SourceLine)
{
    public string Display => Time + "  " + Description;
    public string Location => string.IsNullOrWhiteSpace(SourceFile)
        ? "Log line " + SourceLine : SourceFile + ":" + SourceLine;
}

/// <summary>One dated event with bounded original exception context.</summary>
public sealed record AnomalyExceptionRow(
    string Time, string Severity, string SourceFile,
    int SourceLine, string Message, string Context)
{
    public string Location => string.IsNullOrWhiteSpace(SourceFile)
        ? $"Log line {SourceLine}" : $"{SourceFile}:{SourceLine}";
    public string Heading => $"{Time}  |  {Severity}  |  {Location}";
}

public sealed record AnomalyFindingRow(
    string Kind, string Time, string Title, string Explanation,
    string Evidence, string SourceHint)
{
    public string SourceFile { get; init; } = "";
    public int SourceLine { get; init; } = 1;
    public string Context { get; init; } = "";
    public Visibility ContextVisibility => string.IsNullOrEmpty(Context)
        ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Per-file filter. The original file data stays in memory.</summary>
public sealed class AnomalySourceFileRow(
    string relativePath, string component, int datedEvents,
    int problemEvents, int skippedLines, int continuationLines) : ObservableObject
{
    private bool _included = true;
    public string RelativePath { get; } = relativePath;
    public string Component { get; } = component;
    public int DatedEvents { get; } = datedEvents;
    public int ProblemEvents { get; } = problemEvents;
    public int SkippedLines { get; } = skippedLines;
    public int ContinuationLines { get; } = continuationLines;

    public event EventHandler? IncludedChanged;

    public bool Included
    {
        get => _included;
        set
        {
            if (SetProperty(ref _included, value))
            {
                IncludedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
