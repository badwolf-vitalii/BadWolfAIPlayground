using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BadWolfAIPlayground.App.ViewModels;
using BadWolfAIPlayground.Search;
using Microsoft.Win32;

namespace BadWolfAIPlayground.App.Search;

/// <summary>Coordinates the tutorial and WPF state; indexing and ranking live in the reusable search library.</summary>
public sealed class SmartSearchViewModel : ObservableObject, IDisposable
{
    private readonly OllamaEmbeddingProvider _embeddings;
    private readonly Dispatcher _dispatcher;
    private readonly SearchIndexCheckpointStore _checkpoints;
    private readonly RecentSearchFilesStore _recentFiles;
    private RecentSearchSelection? _lastSelection;
    private readonly List<SearchDocument> _documents = [];
    // Reused by keyword and AI search. Never re-split multi-megabyte files on the UI thread.
    private IReadOnlyList<SearchChunk> _chunks = [];
    private readonly RelayCommand _loadDemoCommand;
    private readonly RelayCommand _loadFilesCommand;
    private readonly RelayCommand _restoreFilesCommand;
    private readonly RelayCommand _clearCommand;
    private readonly RelayCommand _checkModelCommand;
    private readonly RelayCommand _buildIndexCommand;
    private readonly RelayCommand _compareCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _copyCommand;
    private SemanticIndex? _index;
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;
    private bool _disposed;
    private int _indexGeneration;
    private int _importGeneration;
    private string _query = "Чому програма не може записати дані в базу?";
    private string _statusText = "Step 1: Load example documents or select your own text files.";
    private string _modelStatus = "Check the local AI connection, then build an index.";
    private string _documentsLabel = "0 documents selected";
    private string _indexLabel = "No AI index yet";
    private string _searchHint = "Try a Ukrainian query against English demo documents.";
    private double _indexProgress;
    private double _importProgress;
    private string _importLabel = "Select text files (up to 16 MiB each).";
    private bool _hasImportError;
    private FileEncodingChoice? _selectedFileEncoding;

    public IReadOnlyList<FileEncodingChoice> FileEncodingChoices { get; } =
    [
        new("Auto (UTF-8 / Unicode BOM)", TextFileEncoding.Auto),
        new("Windows-1251 (Cyrillic)", TextFileEncoding.Windows1251),
        new("Windows-1252 (Western)", TextFileEncoding.Windows1252),
        new("UTF-16 LE (no BOM)", TextFileEncoding.Utf16LittleEndian),
        new("UTF-16 BE (no BOM)", TextFileEncoding.Utf16BigEndian)
    ];

    public SmartSearchViewModel(OllamaEmbeddingProvider? embeddings = null,
        SearchIndexCheckpointStore? checkpoints = null, RecentSearchFilesStore? recentFiles = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _embeddings = embeddings ?? new OllamaEmbeddingProvider();
        _checkpoints = checkpoints ?? new SearchIndexCheckpointStore();
        _recentFiles = recentFiles ?? new RecentSearchFilesStore();
        _lastSelection = _recentFiles.Load();
        _loadDemoCommand = new RelayCommand(LoadDemo, () => !IsBusy);
        _loadFilesCommand = new RelayCommand(async () => await LoadFilesAsync(), () => !IsBusy);
        _restoreFilesCommand = new RelayCommand(async () => await RestorePreviousFilesAsync(),
            () => !IsBusy && HasPreviousFiles);
        _clearCommand = new RelayCommand(Clear, () => !IsBusy && HasDocuments);
        _checkModelCommand = new RelayCommand(async () => await CheckModelAsync(), () => !IsBusy);
        _buildIndexCommand = new RelayCommand(async () => await BuildIndexAsync(), () => !IsBusy && HasDocuments);
        _compareCommand = new RelayCommand(async () => await CompareAsync(),
            () => !IsBusy && HasDocuments && !string.IsNullOrWhiteSpace(Query));
        _cancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
        _copyCommand = new RelayCommand(() => Clipboard.SetText("ollama pull embeddinggemma"));
    }

    public ICommand LoadDemoCommand => _loadDemoCommand;
    public ICommand LoadFilesCommand => _loadFilesCommand;
    public ICommand RestoreFilesCommand => _restoreFilesCommand;
    public ICommand ClearCommand => _clearCommand;
    public ICommand CheckModelCommand => _checkModelCommand;
    public ICommand BuildIndexCommand => _buildIndexCommand;
    public ICommand CompareCommand => _compareCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand CopyInstallCommand => _copyCommand;

    public ObservableCollection<string> DocumentNames { get; } = [];
    public ObservableCollection<SearchResultRow> KeywordResults { get; } = [];
    public ObservableCollection<SearchResultRow> SemanticResults { get; } = [];

    public string ModelName => _embeddings.ModelName;
    public bool HasDocuments => _documents.Count > 0;
    public bool HasIndex => _index is not null;
    public bool HasPreviousFiles => _lastSelection is not null;
    public string PreviousFilesLabel => _lastSelection is null
        ? "After your first import, you can restore the same files after restarting."
        : $"Last selection: {_lastSelection.Paths.Length} file(s). Restore them, then click Build AI index to resume saved batches.";

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                _compareCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                _loadDemoCommand.NotifyCanExecuteChanged();
                _loadFilesCommand.NotifyCanExecuteChanged();
                _restoreFilesCommand.NotifyCanExecuteChanged();
                _clearCommand.NotifyCanExecuteChanged();
                _checkModelCommand.NotifyCanExecuteChanged();
                _buildIndexCommand.NotifyCanExecuteChanged();
                _compareCommand.NotifyCanExecuteChanged();
                _cancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ModelStatus
    {
        get => _modelStatus;
        private set => SetProperty(ref _modelStatus, value);
    }

    public string DocumentsLabel
    {
        get => _documentsLabel;
        private set => SetProperty(ref _documentsLabel, value);
    }

    public string IndexLabel
    {
        get => _indexLabel;
        private set => SetProperty(ref _indexLabel, value);
    }

    public string SearchHint
    {
        get => _searchHint;
        private set => SetProperty(ref _searchHint, value);
    }

    public double IndexProgress
    {
        get => _indexProgress;
        private set => SetProperty(ref _indexProgress, value);
    }

    public double ImportProgress
    {
        get => _importProgress;
        private set => SetProperty(ref _importProgress, value);
    }

    public string ImportLabel
    {
        get => _importLabel;
        private set => SetProperty(ref _importLabel, value);
    }

    public bool HasImportError
    {
        get => _hasImportError;
        private set => SetProperty(ref _hasImportError, value);
    }

    public FileEncodingChoice SelectedFileEncoding
    {
        get => _selectedFileEncoding ?? FileEncodingChoices[0];
        set => SetProperty(ref _selectedFileEncoding, value);
    }

    private void LoadDemo()
    {
        ReplaceDocuments(DemoDocuments.Create());
        ImportProgress = 0;
        HasImportError = false;
        ImportLabel = "Loaded the 4 built-in demo notes.";
    }

    private async Task LoadFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select plain-text documents for local search",
            Multiselect = true,
            Filter = "Text and code (*.txt;*.md;*.log;*.json;*.csv;*.xml;*.cs;*.xaml;*.config;*.yaml;*.yml)|*.txt;*.md;*.log;*.json;*.csv;*.xml;*.cs;*.xaml;*.config;*.yaml;*.yml|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportFilesAsync(dialog.FileNames);
    }

    /// <summary>Restore the last explicitly selected local paths after restarting the app.</summary>
    public async Task RestorePreviousFilesAsync()
    {
        RecentSearchSelection? previous = _lastSelection;
        if (previous is null || IsBusy || _disposed)
        {
            return;
        }

        SelectedFileEncoding = FileEncodingChoices.First(
            choice => choice.Encoding == previous.Encoding);
        await ImportFilesAsync(previous.Paths);
    }

    /// <summary>Atomic, cancellable import. The current documents/index survive a failed load.</summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsBusy || _disposed)
        {
            return;
        }

        int generation = ++_importGeneration;
        bool completed = false;
        TextFileEncoding encoding = SelectedFileEncoding.Encoding;
        ImportProgress = 0;
        HasImportError = false;
        ImportLabel = "Reading selected files...";
        OperationOutcome outcome = await RunOperationAsync(async token =>
        {
            var progress = new Progress<FileImportProgress>(state =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed || generation != _importGeneration || !IsBusy)
                    {
                        return;
                    }

                    ImportProgress = state.TotalBytes == 0 ? 100 :
                        state.BytesRead * 100.0 / state.TotalBytes;
                    ImportLabel = $"Reading {state.CurrentFile} · {state.FilesCompleted}/{state.TotalFiles} files " +
                                  $"· {state.BytesRead / 1048576.0:0.0}/{state.TotalBytes / 1048576.0:0.0} MiB";
                }));
            });

            IReadOnlyList<SearchDocument> documents = await LocalTextLoader.LoadAsync(
                paths, token, progress, fileEncoding: encoding);
            await DispatchAsync(() => ImportLabel = "Preparing searchable text chunks...");
            IReadOnlyList<SearchChunk> chunks = await Task.Run(
                () => TextChunker.Split(documents, cancellationToken: token), token);
            token.ThrowIfCancellationRequested();
            await DispatchAsync(() =>
            {
                ReplaceDocuments(documents, chunks);
                completed = true;
            });
        });

        // Progress<T> callbacks are posted asynchronously; ignore any remaining old updates.
        // Even an STA smoke-test caller may have no SynchronizationContext:
        // all WPF-bound properties must be finalized explicitly on the Dispatcher.
        await DispatchAsync(() =>
        {
            _importGeneration++;
            ImportProgress = completed ? 100 : 0;
            HasImportError = !completed && !outcome.Cancelled;
            ImportLabel = completed
                ? $"Imported {_documents.Count} file(s) · {_chunks.Count:N0} searchable chunks."
                : outcome.Cancelled
                    ? "Import cancelled. Previously loaded documents are unchanged."
                    : $"Import failed: {outcome.ErrorMessage ?? "Unknown error"}. " +
                      "Previously loaded documents are unchanged.";

            if (completed)
            {
                try
                {
                    _recentFiles.Save(paths, encoding);
                    _lastSelection = new RecentSearchSelection(paths.ToArray(), encoding);
                    OnPropertyChanged(nameof(HasPreviousFiles));
                    OnPropertyChanged(nameof(PreviousFilesLabel));
                    _restoreFilesCommand.NotifyCanExecuteChanged();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    StatusText = "Files imported successfully, but the last selection " +
                                 "could not be remembered: " + error.Message;
                }
            }
        });
    }

    private void ReplaceDocuments(
        IReadOnlyList<SearchDocument> documents, IReadOnlyList<SearchChunk>? preparedChunks = null)
    {
        IReadOnlyList<SearchChunk> chunks = preparedChunks ?? TextChunker.Split(documents);
        _documents.Clear();
        _documents.AddRange(documents);
        _chunks = chunks;
        DocumentNames.Clear();
        foreach (SearchDocument document in documents)
        {
            DocumentNames.Add(document.Name);
        }

        _indexGeneration++;
        _index = null;
        KeywordResults.Clear();
        SemanticResults.Clear();
        OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(HasIndex));
        DocumentsLabel = $"{_documents.Count} document(s) · {_chunks.Count:N0} text chunks";
        IndexLabel = _chunks.Count == 0 ? "No text to index" :
            $"Ready: {_chunks.Count:N0} chunks (~{(_chunks.Count + SemanticSearch.BatchSize - 1) / SemanticSearch.BatchSize:N0} batches). " +
            "Build AI index will reuse matching batches saved on disk.";
        IndexProgress = 0;
        StatusText = "Step 2: Build AI index, or compare ordinary word matching first.";
        SearchHint = "The keyword side works immediately. The semantic side requires a local model and an index.";
        _clearCommand.NotifyCanExecuteChanged();
        _buildIndexCommand.NotifyCanExecuteChanged();
        _compareCommand.NotifyCanExecuteChanged();
    }

    private void Clear()
    {
        ReplaceDocuments([]);
        ImportProgress = 0;
        HasImportError = false;
        ImportLabel = "Select text files (up to 16 MiB each).";
        StatusText = "Documents cleared. Load examples or select files to begin again.";
    }

    private async Task CheckModelAsync()
    {
        await RunOperationAsync(async token =>
        {
            string status = await _embeddings.CheckStatusAsync(token);
            await DispatchAsync(() =>
            {
                ModelStatus = status;
                StatusText = status;
            });
        });
    }

    private async Task BuildIndexAsync()
    {
        IReadOnlyList<SearchChunk> chunks = _chunks;
        if (chunks.Count == 0)
        {
            StatusText = "There is no nonempty text to index.";
            return;
        }

        if (chunks.Count > SemanticSearch.MaxChunks)
        {
            StatusText = $"Too much text ({chunks.Count} chunks; maximum {SemanticSearch.MaxChunks}). Select fewer files.";
            return;
        }

        int generation = ++_indexGeneration;
        _index = null;
        OnPropertyChanged(nameof(HasIndex));
        SemanticResults.Clear();
        IndexProgress = 0;
        IndexLabel = $"Checking saved batches for {chunks.Count:N0} chunks...";
        OperationOutcome outcome = await RunOperationAsync(async token =>
        {
            var progress = new Progress<IndexProgress>(step =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _indexGeneration || !IsBusy || _disposed || _index is not null)
                    {
                        return;
                    }
                    IndexProgress = step.Completed * 100.0 / step.Total;
                    IndexLabel = $"Indexing {step.Completed:N0}/{step.Total:N0} chunks " +
                                 $"({IndexProgress:0.0}%) · {step.Reused:N0} restored from disk. " +
                                 "Completed batches remain saved if you cancel or restart.";
                }));
            });
            SemanticIndex built = await SemanticSearch.BuildAsync(
                chunks, _embeddings, progress, token, _checkpoints);
            await DispatchAsync(() =>
            {
                _index = built;
                OnPropertyChanged(nameof(HasIndex));
                IndexProgress = 100;
                IndexLabel = $"Ready: {built.Chunks.Count:N0} chunks · {built.ModelName} · " +
                             $"{built.ReusedChunks:N0} restored from disk.";
                StatusText = "Step 3: Type a question and compare both search methods.";
                SearchHint = "Cosine score measures vector similarity; it is not a probability of correctness.";
                ModelStatus = $"Ready: {built.ModelName} on 127.0.0.1 (local Ollama).";
            });
        });

        if (!outcome.Succeeded)
        {
            await DispatchAsync(() => IndexLabel = outcome.Cancelled
                ? "Indexing paused. Completed batches are saved locally. Click Build AI index to resume."
                : "Index incomplete. Completed batches are preserved; fix the error and click Build AI index.");
        }
    }

    private async Task CompareAsync()
    {
        string query = Query.Trim();
        IReadOnlyList<SearchChunk> chunks = _chunks;
        SemanticIndex? activeIndex = _index;
        await RunOperationAsync(async token =>
        {
            IReadOnlyList<SearchHit> keyword = await Task.Run(
                () => KeywordSearch.Find(chunks, query, cancellationToken: token), token);
            await DispatchAsync(() =>
            {
                KeywordResults.Clear();
                foreach (SearchHit result in keyword)
                {
                    KeywordResults.Add(new SearchResultRow(result, semantic: false, query: query));
                }
                SemanticResults.Clear();
            });

            if (activeIndex is null)
            {
                await DispatchAsync(() =>
                {
                    StatusText = $"Keyword search returned {keyword.Count} results. Build the AI index to compare semantic matches.";
                    SearchHint = "Semantic search needs a local model and an index.";
                });
                return;
            }

            IReadOnlyList<SearchHit> semantic = await SemanticSearch.FindAsync(
                activeIndex, _embeddings, query, cancellationToken: token);
            await DispatchAsync(() =>
            {
                foreach (SearchHit result in semantic)
                {
                    SemanticResults.Add(new SearchResultRow(result, semantic: true));
                }

                StatusText = $"Comparison complete: {keyword.Count} lexical and {semantic.Count} semantic matches.";
                SearchHint = "The AI ranks by cosine similarity. Verify the text: even a high score can be irrelevant.";
            });
        });
    }

    private async Task<OperationOutcome> RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return new OperationOutcome(false, false, "Another operation is already running");
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        try
        {
            await operation(cancellation.Token);
            return new OperationOutcome(true, false, null);
        }
        catch (OperationCanceledException)
        {
            await DispatchAsync(() =>
            {
                StatusText = "Operation cancelled.";
                if (!HasIndex)
                {
                    IndexProgress = 0;
                    IndexLabel = "No completed AI index (cancelled; source documents are retained).";
                }
            });
            return new OperationOutcome(false, true, null);
        }
        catch (Exception exception)
        {
            await DispatchAsync(() =>
            {
                StatusText = $"Operation failed: {exception.Message}";
                if (!HasIndex)
                {
                    IndexProgress = 0;
                    IndexLabel = "No completed AI index (source documents are retained).";
                }
            });
            return new OperationOutcome(false, false, exception.Message);
        }
        finally
        {
            await DispatchAsync(() =>
            {
                _cancellation = null;
                IsBusy = false;
            });
        }
    }

    private Task DispatchAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private sealed record OperationOutcome(bool Succeeded, bool Cancelled, string? ErrorMessage);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _indexGeneration++;
        _cancellation?.Cancel();
        _embeddings.Dispose();
    }
}

/// <summary>A visible, explicit file-decoding choice. Ambiguous legacy text is never guessed.</summary>
public sealed record FileEncodingChoice(string Label, TextFileEncoding Encoding);
