using System.Collections.ObjectModel;
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
    private readonly List<SearchDocument> _documents = [];
    private readonly RelayCommand _loadDemoCommand;
    private readonly RelayCommand _loadFilesCommand;
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
    private string _query = "Чому програма не може записати дані в базу?";
    private string _statusText = "Step 1: Load example documents or select your own text files.";
    private string _modelStatus = "Check the local AI connection, then build an index.";
    private string _documentsLabel = "0 documents selected";
    private string _indexLabel = "No AI index yet";
    private string _searchHint = "Try a Ukrainian query against English demo documents.";
    private double _indexProgress;

    public SmartSearchViewModel(OllamaEmbeddingProvider? embeddings = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _embeddings = embeddings ?? new OllamaEmbeddingProvider();
        _loadDemoCommand = new RelayCommand(LoadDemo, () => !IsBusy);
        _loadFilesCommand = new RelayCommand(async () => await LoadFilesAsync(), () => !IsBusy);
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

    private void LoadDemo() => ReplaceDocuments(DemoDocuments.Create());

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

        await RunOperationAsync(async token =>
        {
            IReadOnlyList<SearchDocument> documents = await LocalTextLoader.LoadAsync(dialog.FileNames, token);
            await DispatchAsync(() => ReplaceDocuments(documents));
        });
    }

    private void ReplaceDocuments(IReadOnlyList<SearchDocument> documents)
    {
        _documents.Clear();
        _documents.AddRange(documents);
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
        DocumentsLabel = $"{_documents.Count} documents loaded";
        IndexLabel = "Not indexed yet";
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
        IReadOnlyList<SearchChunk> chunks = TextChunker.Split(_documents);
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
        IndexLabel = $"Embedding {chunks.Count} text chunks...";
        await RunOperationAsync(async token =>
        {
            var progress = new Progress<IndexProgress>(step =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _indexGeneration || !IsBusy || _disposed)
                    {
                        return;
                    }
                    IndexProgress = step.Completed * 100.0 / step.Total;
                    IndexLabel = $"Indexing {step.Completed}/{step.Total} chunks";
                }));
            });
            SemanticIndex built = await SemanticSearch.BuildAsync(chunks, _embeddings, progress, token);
            await DispatchAsync(() =>
            {
                _index = built;
                OnPropertyChanged(nameof(HasIndex));
                IndexProgress = 100;
                IndexLabel = $"Ready: {built.Chunks.Count} chunks · {built.ModelName}";
                StatusText = "Step 3: Type a question and compare both search methods.";
                SearchHint = "Cosine score measures vector similarity; it is not a probability of correctness.";
                ModelStatus = $"Ready: {built.ModelName} on 127.0.0.1 (local Ollama).";
            });
        });
    }

    private async Task CompareAsync()
    {
        string query = Query.Trim();
        IReadOnlyList<SearchChunk> chunks = _index?.Chunks ?? TextChunker.Split(_documents);
        IReadOnlyList<SearchHit> keyword = KeywordSearch.Find(chunks, query);
        KeywordResults.Clear();
        foreach (SearchHit result in keyword)
        {
            KeywordResults.Add(new SearchResultRow(result, semantic: false));
        }

        SemanticResults.Clear();
        if (_index is null)
        {
            StatusText = $"Keyword search returned {keyword.Count} results. Build the AI index to compare semantic matches.";
            SearchHint = "Semantic search is not available until the local embedding index is built.";
            return;
        }

        await RunOperationAsync(async token =>
        {
            SemanticIndex activeIndex = _index!;
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

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await DispatchAsync(() =>
            {
                StatusText = "Operation cancelled.";
                if (!HasIndex)
                {
                    IndexLabel = "No completed AI index";
                }
            });
        }
        catch (Exception exception)
        {
            await DispatchAsync(() =>
            {
                StatusText = $"Operation failed: {exception.Message}";
                if (!HasIndex)
                {
                    IndexLabel = "No completed AI index";
                }
            });
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
