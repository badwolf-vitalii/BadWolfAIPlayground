using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Input;
using BadWolfAIPlayground.Anomaly;
using BadWolfAIPlayground.App.ViewModels;
using BadWolfAIPlayground.Search;

namespace BadWolfAIPlayground.App.Anomaly;

/// <summary>
/// Opt-in local AI explanations for already inspected evidence. The currently
/// prepared prompt is visible before transmission and invalidated whenever
/// any source evidence changes. Nothing runs automatically upon import.
/// </summary>
public sealed partial class AnomalyViewModel
{
    private OllamaRuntimeClient _localOllamaRuntime = null!;
    private LocalOllamaInvestigationClient _localInvestigationClient = null!;
    private bool _ownsLocalRuntime;
    private bool _ownsInvestigationClient;
    private RelayCommand _refreshLocalModels = null!;
    private RelayCommand _explainLocalEvidence = null!;
    private ParameterRelayCommand<AnomalyInvestigationRow> _prepareLocalEvidence = null!;

    private InvestigationPrompt? _preparedPrompt;
    private string _selectedLocalModel = "";
    private string _investigationQuestion =
        "What happened in this operation? Separate observed facts, possible " +
        "causes and the next concrete diagnostic checks.";
    private string _localAiStatus =
        "Optional: refresh locally installed Ollama chat models. " +
        "Select a case and click Prepare evidence before sending anything.";
    private string _localPromptPreview =
        "No evidence prepared. Neither logs nor code are sent automatically.";
    private string _localAnswerSummary =
        "No AI explanation generated. Deterministic log/source evidence remains available above.";
    private bool _localAiExpanded;
    private string _preparedEvidenceTitle = "No investigation selected.";

    public ObservableCollection<string> LocalOllamaModels { get; } = [];
    public ObservableCollection<LocalAiHypothesisRow> LocalAiHypotheses { get; } = [];
    public ObservableCollection<string> LocalAiNextChecks { get; } = [];
    public ObservableCollection<string> LocalAiUnknowns { get; } = [];

    public ICommand RefreshLocalModelsCommand => _refreshLocalModels;
    public ICommand PrepareLocalEvidenceCommand => _prepareLocalEvidence;
    public ICommand ExplainLocalEvidenceCommand => _explainLocalEvidence;

    public string SelectedLocalModel
    {
        get => _selectedLocalModel;
        set
        {
            if (Busy || !SetProperty(ref _selectedLocalModel, value ?? ""))
                return;
            ClearLocalAnswer();
            _explainLocalEvidence.NotifyCanExecuteChanged();
        }
    }

    public string InvestigationQuestion
    {
        get => _investigationQuestion;
        set
        {
            if (Busy || !SetProperty(ref _investigationQuestion, value ?? ""))
                return;
            // The prompt preview must always equal the bytes of the
            // eventual user message, even after the question is edited.
            _preparedPrompt = null;
            LocalPromptPreview = "Question edited. Click Prepare evidence again.";
            ClearLocalAnswer();
            _explainLocalEvidence.NotifyCanExecuteChanged();
        }
    }

    public bool LocalAiExpanded
    {
        get => _localAiExpanded;
        set => SetProperty(ref _localAiExpanded, value);
    }
    public string LocalAiStatus
    {
        get => _localAiStatus;
        private set => SetProperty(ref _localAiStatus, value);
    }
    public string LocalPromptPreview
    {
        get => _localPromptPreview;
        private set => SetProperty(ref _localPromptPreview, value);
    }
    public string PreparedEvidenceTitle
    {
        get => _preparedEvidenceTitle;
        private set => SetProperty(ref _preparedEvidenceTitle, value);
    }
    public string LocalAnswerSummary
    {
        get => _localAnswerSummary;
        private set => SetProperty(ref _localAnswerSummary, value);
    }

    private void InitializeLocalInvestigation(
        OllamaRuntimeClient? runtime, LocalOllamaInvestigationClient? client)
    {
        _ownsLocalRuntime = runtime is null;
        _ownsInvestigationClient = client is null;
        _localOllamaRuntime = runtime ?? new OllamaRuntimeClient();
        _localInvestigationClient = client ?? new LocalOllamaInvestigationClient();
        _refreshLocalModels = new RelayCommand(
            async () => await RefreshLocalModelsAsync(), () => !Busy);
        _prepareLocalEvidence = new ParameterRelayCommand<AnomalyInvestigationRow>(
            PrepareLocalEvidence, row => !Busy && row.SourceCase is not null);
        _explainLocalEvidence = new RelayCommand(
            async () => await ExplainLocalEvidenceAsync(),
            () => !Busy && _preparedPrompt is not null &&
                  !string.IsNullOrEmpty(SelectedLocalModel));
    }

    private void NotifyLocalInvestigationCommands()
    {
        _refreshLocalModels.NotifyCanExecuteChanged();
        _prepareLocalEvidence.NotifyCanExecuteChanged();
        _explainLocalEvidence.NotifyCanExecuteChanged();
    }

    public async Task RefreshLocalModelsAsync()
    {
        if (Busy || _disposed)
            return;
        using var source = new CancellationTokenSource();
        _active = source;
        Busy = true;
        LocalAiStatus = "Reading installed models from loopback Ollama...";
        try
        {
            IReadOnlyList<LocalOllamaModel> models =
                await _localOllamaRuntime.ListInstalledAsync(source.Token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                LocalOllamaModels.Clear();
                foreach (LocalOllamaModel model in models)
                    LocalOllamaModels.Add(model.Name);
                if (!LocalOllamaModels.Contains(SelectedLocalModel))
                    SelectedLocalModel = LocalOllamaModels.FirstOrDefault() ?? "";
                LocalAiStatus = LocalOllamaModels.Count == 0
                    ? "Ollama is running but no models were found. Install a local chat model."
                    : $"Found {LocalOllamaModels.Count} locally installed model(s). " +
                      "Choose a chat/instruction model (not an embedding-only model).";
                _explainLocalEvidence.NotifyCanExecuteChanged();
            });
        }
        catch (OperationCanceledException)
        {
            LocalAiStatus = "Local Ollama model lookup canceled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or
            InvalidOperationException or InvalidDataException)
        {
            LocalAiStatus = "Local Ollama models unavailable: " + ex.Message;
        }
        finally
        {
            _active = null;
            Busy = false;
        }
    }

    public void PrepareLocalEvidence(AnomalyInvestigationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Busy || row.SourceCase is not SourceInvestigationCase selected)
            return;
        try
        {
            InvestigationPrompt prompt =
                InvestigationPromptBuilder.Build(selected, InvestigationQuestion);
            _preparedPrompt = prompt;
            PreparedEvidenceTitle = row.Heading;
            LocalPromptPreview = prompt.Display;
            LocalAiStatus = $"{prompt.Evidence.Count} bounded, numbered evidence " +
                "excerpt(s) prepared locally. Inspect them below. " +
                "Nothing is sent until you click Explain with Ollama.";
            ClearLocalAnswer();
            LocalAiExpanded = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            _preparedPrompt = null;
            LocalPromptPreview = "Could not prepare evidence.";
            LocalAiStatus = ex.Message;
        }
        _explainLocalEvidence.NotifyCanExecuteChanged();
    }

    public async Task ExplainLocalEvidenceAsync()
    {
        InvestigationPrompt? prompt = _preparedPrompt;
        string model = SelectedLocalModel;
        if (Busy || _disposed || prompt is null || string.IsNullOrWhiteSpace(model))
            return;

        using var source = new CancellationTokenSource();
        _active = source;
        Busy = true;
        ClearLocalAnswer();
        LocalAiStatus = $"Requesting an evidence-grounded explanation from local {model}...";
        try
        {
            LocalInvestigationExplanation answer =
                await _localInvestigationClient.ExplainAsync(prompt, model, source.Token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(prompt, _preparedPrompt))
                    return;
                LocalAnswerSummary = answer.Summary;
                foreach (LocalInvestigationHypothesis item in answer.Hypotheses)
                    LocalAiHypotheses.Add(new LocalAiHypothesisRow(
                        item.Claim, item.ReferencesText));
                foreach (string check in answer.NextChecks)
                    LocalAiNextChecks.Add(check);
                foreach (string unknown in answer.Unknowns)
                    LocalAiUnknowns.Add(unknown);
                LocalAiStatus = $"{answer.Notice} Local model: {model}. " +
                    $"{prompt.Evidence.Count} excerpts submitted; " +
                    "no cloud request or persisted conversation.";
            });
        }
        catch (OperationCanceledException)
        {
            LocalAiStatus = "Local explanation canceled; no result published.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            InvalidDataException or HttpRequestException or ArgumentException)
        {
            LocalAiStatus = "Local AI explanation unavailable: " + ex.Message;
            ClearLocalAnswer();
        }
        finally
        {
            _active = null;
            Busy = false;
        }
    }

    private void ClearLocalAnswer()
    {
        LocalAiHypotheses.Clear();
        LocalAiNextChecks.Clear();
        LocalAiUnknowns.Clear();
        LocalAnswerSummary =
            "No AI explanation generated. Inspect the selected evidence before requesting one.";
    }

    private void ResetLocalInvestigation()
    {
        _preparedPrompt = null;
        LocalPromptPreview =
            "Evidence changed. Select an investigation and click Prepare evidence.";
        PreparedEvidenceTitle = "No investigation selected.";
        ClearLocalAnswer();
        LocalAiStatus = "Source evidence updated. No Ollama request was made.";
        _explainLocalEvidence.NotifyCanExecuteChanged();
    }

    private void DisposeLocalInvestigation()
    {
        if (_ownsInvestigationClient)
            _localInvestigationClient.Dispose();
        if (_ownsLocalRuntime)
            _localOllamaRuntime.Dispose();
    }
}

public sealed record LocalAiHypothesisRow(string Claim, string References);
