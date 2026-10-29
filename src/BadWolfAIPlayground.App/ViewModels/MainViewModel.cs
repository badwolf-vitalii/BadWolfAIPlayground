using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using BadWolfAIPlayground.App.Services;
using BadWolfAIPlayground.Core;

namespace BadWolfAIPlayground.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IModelFileService _files;
    private readonly IReadOnlyList<TrainingSample> _samples = XorDataset.CreateSamples();
    private readonly List<double> _losses = [];
    private readonly RelayCommand _trainCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _resetCommand;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _loadCommand;
    private readonly RelayCommand _nextLessonCommand;
    private readonly RelayCommand _previousLessonCommand;
    private readonly RelayCommand _restartLessonCommand;

    private NeuralNetwork _network = new([2, 4, 1], seed: 42);
    private CancellationTokenSource? _cancellation;
    private int _modelSeed = 42;
    private double[] _beforeOutputs = [];
    private double _beforeLoss;
    private double _currentLoss;
    private int _correctPredictions;

    private string _hiddenNeuronsText = "4";
    private string _epochsText = "10000";
    private string _learningRateText = "0.7";
    private string _seedText = "42";
    private bool _watchTraining = true;
    private bool _isTraining;
    private bool _hasTrainingResults;
    private int _lessonStep = 1;
    private string _statusText = "Ready to explore";
    private string _epochText = "0 / 10,000";
    private string _lossText = "--";
    private string _accuracyText = "--";
    private string _beforeAccuracyText = "--";
    private string _beforeLossText = "--";
    private string _lossChangeText = "No change yet";
    private string _baselineContextText = "Fresh random weights";
    private double _completion;
    private NetworkSnapshot _currentSnapshot;
    private IReadOnlyList<double> _lossHistory = [];

    public MainViewModel(IModelFileService files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _currentSnapshot = _network.CaptureSnapshot();

        _trainCommand = new RelayCommand(async () => await TrainAsync(), () => !IsTraining);
        _stopCommand = new RelayCommand(Stop, () => IsTraining);
        _resetCommand = new RelayCommand(Reset, () => !IsTraining);
        _saveCommand = new RelayCommand(SaveModel, () => !IsTraining);
        _loadCommand = new RelayCommand(LoadModel, () => !IsTraining);
        _nextLessonCommand = new RelayCommand(NextLesson, CanAdvanceLesson);
        _previousLessonCommand = new RelayCommand(PreviousLesson,
            () => LessonStep > 1 && !IsTraining);
        _restartLessonCommand = new RelayCommand(() => LessonStep = 1, () => !IsTraining);

        SetBaseline(_currentSnapshot, "Fresh random weights");
    }

    public ICommand TrainCommand => _trainCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand ResetCommand => _resetCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand LoadCommand => _loadCommand;
    public ICommand NextLessonCommand => _nextLessonCommand;
    public ICommand PreviousLessonCommand => _previousLessonCommand;
    public ICommand RestartLessonCommand => _restartLessonCommand;

    public ObservableCollection<PredictionRow> Predictions { get; } = [];

    public string HiddenNeuronsText
    {
        get => _hiddenNeuronsText;
        set => SetProperty(ref _hiddenNeuronsText, value);
    }

    public string EpochsText
    {
        get => _epochsText;
        set => SetProperty(ref _epochsText, value);
    }

    public string LearningRateText
    {
        get => _learningRateText;
        set => SetProperty(ref _learningRateText, value);
    }

    public string SeedText
    {
        get => _seedText;
        set => SetProperty(ref _seedText, value);
    }

    public bool WatchTraining
    {
        get => _watchTraining;
        set => SetProperty(ref _watchTraining, value);
    }

    public bool IsTraining
    {
        get => _isTraining;
        private set
        {
            if (!SetProperty(ref _isTraining, value))
            {
                return;
            }

            _trainCommand.NotifyCanExecuteChanged();
            _stopCommand.NotifyCanExecuteChanged();
            _resetCommand.NotifyCanExecuteChanged();
            _saveCommand.NotifyCanExecuteChanged();
            _loadCommand.NotifyCanExecuteChanged();
            RefreshLessonCommands();
            OnPropertyChanged(nameof(LessonHint));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(LessonHint));
            }
        }
    }

    public string EpochText
    {
        get => _epochText;
        private set
        {
            if (SetProperty(ref _epochText, value))
            {
                OnPropertyChanged(nameof(LessonHint));
            }
        }
    }

    public string LossText
    {
        get => _lossText;
        private set => SetProperty(ref _lossText, value);
    }

    public string AccuracyText
    {
        get => _accuracyText;
        private set
        {
            if (SetProperty(ref _accuracyText, value))
            {
                OnPropertyChanged(nameof(LessonOutcome));
                OnPropertyChanged(nameof(LessonHint));
            }
        }
    }

    public string BeforeAccuracyText
    {
        get => _beforeAccuracyText;
        private set => SetProperty(ref _beforeAccuracyText, value);
    }

    public string BeforeLossText
    {
        get => _beforeLossText;
        private set => SetProperty(ref _beforeLossText, value);
    }

    public string LossChangeText
    {
        get => _lossChangeText;
        private set => SetProperty(ref _lossChangeText, value);
    }

    public string BaselineContextText
    {
        get => _baselineContextText;
        private set => SetProperty(ref _baselineContextText, value);
    }

    public double Completion
    {
        get => _completion;
        private set => SetProperty(ref _completion, value);
    }

    public NetworkSnapshot CurrentSnapshot
    {
        get => _currentSnapshot;
        private set => SetProperty(ref _currentSnapshot, value);
    }

    public IReadOnlyList<double> LossHistory
    {
        get => _lossHistory;
        private set => SetProperty(ref _lossHistory, value);
    }

    public bool HasTrainingResults
    {
        get => _hasTrainingResults;
        private set
        {
            if (!SetProperty(ref _hasTrainingResults, value))
            {
                return;
            }

            RefreshLessonCommands();
            OnPropertyChanged(nameof(LessonHint));
            OnPropertyChanged(nameof(LessonOutcome));
        }
    }

    public int LessonStep
    {
        get => _lessonStep;
        private set
        {
            if (value is < 1 or > 4 || !SetProperty(ref _lessonStep, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LessonStepLabel));
            OnPropertyChanged(nameof(LessonTitle));
            OnPropertyChanged(nameof(LessonDescription));
            OnPropertyChanged(nameof(LessonHint));
            OnPropertyChanged(nameof(LessonNextLabel));
            RefreshLessonCommands();
        }
    }

    public string LessonStepLabel => $"STEP {LessonStep} OF 4";

    public string LessonTitle => LessonStep switch
    {
        1 => "Understand the four examples",
        2 => "Look at the network before training",
        3 => "Train it and watch the mistakes shrink",
        _ => "Compare before and after"
    };

    public string LessonDescription => LessonStep switch
    {
        1 => "XOR gives 1 when the two inputs differ, otherwise 0. These four rows are everything the network will be shown.",
        2 => "The lines between neurons have random starting weights. The four predictions below are guesses, not learned answers.",
        3 => "An epoch is one pass through all four examples. Press Train now and watch the error (loss) and predictions change.",
        _ => "Compare today's predictions to the saved starting state. Lower loss means outputs closer to the four expected answers."
    };

    public string LessonHint => LessonStep switch
    {
        1 => "Try predicting each answer yourself, then press Next step.",
        2 => $"Starting accuracy: {BeforeAccuracyText}. This is the reference point for the comparison.",
        3 when IsTraining => $"Training: {EpochText} epochs. You can press Stop without losing the learned weights.",
        3 when HasTrainingResults => $"Finished this training run: {AccuracyText} correct. Press See results to understand the change.",
        3 => "Keep the suggested settings (4 neurons, 10,000 epochs, rate 0.7, seed 42) and press Train now.",
        _ => LessonOutcome
    };

    public string LessonOutcome => !HasTrainingResults
        ? "No training yet. Go to Step 3 and train the network to compare results."
        : _correctPredictions == _samples.Count
            ? "All four training examples are now classified correctly. That demonstrates learning XOR, not general intelligence."
            : $"{_correctPredictions} of {_samples.Count} training examples are correct. Try more epochs or a different seed and compare again.";

    public string LessonNextLabel => LessonStep == 3 ? "See results  →" : "Next step  →";

    private bool CanAdvanceLesson() => !IsTraining && LessonStep < 4 &&
        (LessonStep != 3 || HasTrainingResults);

    private void NextLesson()
    {
        if (CanAdvanceLesson())
        {
            LessonStep++;
        }
    }

    private void PreviousLesson()
    {
        if (!IsTraining && LessonStep > 1)
        {
            LessonStep--;
        }
    }

    private void RefreshLessonCommands()
    {
        _nextLessonCommand.NotifyCanExecuteChanged();
        _previousLessonCommand.NotifyCanExecuteChanged();
        _restartLessonCommand.NotifyCanExecuteChanged();
    }

    private void SetBaseline(NetworkSnapshot snapshot, string context)
    {
        NeuralNetwork baseline = NeuralNetwork.FromSnapshot(snapshot);
        _beforeOutputs = _samples.Select(sample => baseline.Predict(sample.Inputs)[0]).ToArray();
        _beforeLoss = baseline.MeanSquaredError(_samples);
        BeforeLossText = _beforeLoss.ToString("0.000000", CultureInfo.InvariantCulture);
        int correct = _samples.Where((sample, index) =>
            (_beforeOutputs[index] >= 0.5 ? 1 : 0) == (int)sample.Targets[0]).Count();
        BeforeAccuracyText = $"{correct} / {_samples.Count}";
        BaselineContextText = context;
        HasTrainingResults = false;
        _losses.Clear();
        LossHistory = [];
        UpdateCurrentState(snapshot, _beforeLoss);
        OnPropertyChanged(nameof(LessonHint));
    }

    private void UpdateCurrentState(NetworkSnapshot snapshot, double loss)
    {
        CurrentSnapshot = snapshot;
        _currentLoss = loss;
        LossText = loss.ToString("0.000000", CultureInfo.InvariantCulture);
        double decrease = _beforeLoss > 0 ? (_beforeLoss - loss) / _beforeLoss * 100 : 0;
        LossChangeText = Math.Abs(decrease) < 0.05
            ? "No change yet"
            : decrease > 0
                ? $"Error reduced by {decrease:0.0}%"
                : $"Error increased by {-decrease:0.0}%";
        UpdatePredictions(snapshot);
    }

    private async Task TrainAsync()
    {
        if (!TryReadSettings(out int hidden, out int epochs, out double rate, out int seed))
        {
            return;
        }

        if (_network.LayerSizes[1] != hidden || _modelSeed != seed)
        {
            _network = new NeuralNetwork([2, hidden, 1], seed);
            _modelSeed = seed;
            SetBaseline(_network.CaptureSnapshot(), "New architecture / random seed");
        }

        LessonStep = 3;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        IsTraining = true;
        StatusText = "Training in progress";
        Completion = 0;
        EpochText = $"0 / {epochs:N0}";
        NeuralNetwork trainingNetwork = _network;
        int reportInterval = Math.Max(1, epochs / 120);
        bool watchTraining = WatchTraining;
        int completedEpochs = 0;

        IProgress<TrainingUpdate> progress = new Progress<TrainingUpdate>(update =>
        {
            // Progress<T> posts asynchronously. Do not let stale queued updates replace
            // the final snapshot after a run has finished or the user has reset the model.
            if (!IsTraining)
            {
                return;
            }

            UpdateCurrentState(update.Snapshot, update.Loss);
            _losses.Add(update.Loss);
            LossHistory = _losses.ToArray();
            EpochText = $"{update.Epoch:N0} / {epochs:N0}";
            Completion = update.Epoch * 100.0 / epochs;
        });

        try
        {
            await Task.Run(() =>
            {
                for (int epoch = 1; epoch <= epochs; epoch++)
                {
                    token.ThrowIfCancellationRequested();

                    foreach (TrainingSample sample in _samples)
                    {
                        trainingNetwork.TrainSample(sample.Inputs, sample.Targets, rate);
                    }

                    completedEpochs = epoch;
                    if (epoch == 1 || epoch % reportInterval == 0 || epoch == epochs)
                    {
                        double loss = trainingNetwork.MeanSquaredError(_samples);
                        progress.Report(new TrainingUpdate(
                            epoch, loss, trainingNetwork.CaptureSnapshot()));
                        // Deliberately pace only the teaching visualization, on the worker.
                        // Turn off WatchTraining to train at full speed.
                        if (watchTraining)
                        {
                            Thread.Sleep(25);
                        }
                    }
                }
            }, token);

            StatusText = "Training completed";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Training stopped; learned weights are preserved";
        }
        catch (Exception exception)
        {
            StatusText = $"Training failed: {exception.Message}";
        }
        finally
        {
            // Apply the exact final state, even when progress notifications are still queued.
            NetworkSnapshot finalSnapshot = trainingNetwork.CaptureSnapshot();
            double finalLoss = trainingNetwork.MeanSquaredError(_samples);
            UpdateCurrentState(finalSnapshot, finalLoss);
            EpochText = $"{completedEpochs:N0} / {epochs:N0}";
            Completion = completedEpochs * 100.0 / epochs;

            if (completedEpochs > 0)
            {
                _losses.Add(finalLoss);
                LossHistory = _losses.ToArray();
                HasTrainingResults = true;
            }

            IsTraining = false;
            _cancellation?.Dispose();
            _cancellation = null;
            OnPropertyChanged(nameof(LessonOutcome));
            OnPropertyChanged(nameof(LessonHint));
        }
    }

    private bool TryReadSettings(out int hidden, out int epochs, out double rate, out int seed)
    {
        hidden = epochs = seed = 0;
        rate = 0;

        if (!int.TryParse(HiddenNeuronsText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out hidden) || hidden is < 2 or > 12)
        {
            StatusText = "Hidden neurons must be between 2 and 12.";
            return false;
        }

        if (!int.TryParse(EpochsText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out epochs) || epochs is < 1 or > 250_000)
        {
            StatusText = "Epochs must be between 1 and 250,000.";
            return false;
        }

        if (!double.TryParse(LearningRateText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out rate) || !double.IsFinite(rate) ||
            rate is < 0.001 or > 2.0)
        {
            StatusText = "Learning rate must be between 0.001 and 2 (use a dot).";
            return false;
        }

        if (!int.TryParse(SeedText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out seed))
        {
            StatusText = "Seed must be an integer.";
            return false;
        }

        return true;
    }

    private void Stop() => _cancellation?.Cancel();

    private void Reset()
    {
        if (!int.TryParse(HiddenNeuronsText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int hidden) || hidden is < 2 or > 12 ||
            !int.TryParse(SeedText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int seed))
        {
            StatusText = "Enter valid hidden neurons (2-12) and an integer seed.";
            return;
        }

        _network = new NeuralNetwork([2, hidden, 1], seed);
        _modelSeed = seed;
        SetBaseline(_network.CaptureSnapshot(), "Fresh random weights");
        EpochText = "0 / 0";
        Completion = 0;
        LessonStep = 1;
        StatusText = "Network reset with new random weights";
    }

    private void SaveModel()
    {
        try
        {
            StatusText = _files.TrySave(ModelPersistence.Serialize(_network))
                ? "Model saved as JSON"
                : "Save cancelled";
        }
        catch (Exception exception)
        {
            StatusText = $"Could not save model: {exception.Message}";
        }
    }

    private void LoadModel()
    {
        try
        {
            string? json = _files.TryOpen();
            if (json is null)
            {
                StatusText = "Open cancelled";
                return;
            }

            NeuralNetwork loaded = ModelPersistence.Deserialize(json);
            if (loaded.LayerSizes.Count != 3 || loaded.LayerSizes[0] != 2 ||
                loaded.LayerSizes[^1] != 1 || loaded.LayerSizes[1] is < 2 or > 12)
            {
                StatusText = "This lab accepts only 2 → hidden (2-12) → 1 models.";
                return;
            }

            _network = loaded;
            HiddenNeuronsText = loaded.LayerSizes[1].ToString(CultureInfo.InvariantCulture);
            int.TryParse(SeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _modelSeed);
            SetBaseline(_network.CaptureSnapshot(), "Loaded model (starting point for this session)");
            EpochText = "Loaded model";
            Completion = 0;
            LessonStep = 1;
            StatusText = "Model loaded successfully. Train to compare against the loaded state.";
        }
        catch (Exception exception)
        {
            StatusText = $"Could not open model: {exception.Message}";
        }
    }

    private void UpdatePredictions(NetworkSnapshot snapshot)
    {
        NeuralNetwork network = NeuralNetwork.FromSnapshot(snapshot);
        Predictions.Clear();
        _correctPredictions = 0;

        for (int index = 0; index < _samples.Count; index++)
        {
            TrainingSample sample = _samples[index];
            double output = network.Predict(sample.Inputs)[0];
            var row = new PredictionRow(
                $"{sample.Inputs[0]:0}  XOR  {sample.Inputs[1]:0}",
                (int)sample.Targets[0], _beforeOutputs[index], output);
            Predictions.Add(row);
            if (row.IsCorrect)
            {
                _correctPredictions++;
            }
        }

        AccuracyText = $"{_correctPredictions} / {_samples.Count}";
        OnPropertyChanged(nameof(LessonOutcome));
    }

    private sealed record TrainingUpdate(int Epoch, double Loss, NetworkSnapshot Snapshot);
}
