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
    private NeuralNetwork _network = new([2, 4, 1], seed: 42);
    private CancellationTokenSource? _cancellation;
    private int _modelSeed = 42;

    private string _hiddenNeuronsText = "4";
    private string _epochsText = "10000";
    private string _learningRateText = "0.7";
    private string _seedText = "42";
    private bool _isTraining;
    private string _statusText = "Ready to explore";
    private string _epochText = "0 / 10,000";
    private string _lossText = "--";
    private string _accuracyText = "--";
    private double _completion;
    private NetworkSnapshot _currentSnapshot;
    private IReadOnlyList<double> _lossHistory = [];

    public MainViewModel(IModelFileService files)
    {
        _files = files;
        _currentSnapshot = _network.CaptureSnapshot();
        _trainCommand = new RelayCommand(async () => await TrainAsync(), () => !IsTraining);
        _stopCommand = new RelayCommand(Stop, () => IsTraining);
        _resetCommand = new RelayCommand(Reset, () => !IsTraining);
        _saveCommand = new RelayCommand(SaveModel, () => !IsTraining);
        _loadCommand = new RelayCommand(LoadModel, () => !IsTraining);
        UpdatePredictions(_currentSnapshot);
        LossText = _network.MeanSquaredError(_samples).ToString("0.000000", CultureInfo.InvariantCulture);
    }

    public ICommand TrainCommand => _trainCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand ResetCommand => _resetCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand LoadCommand => _loadCommand;

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

    public bool IsTraining
    {
        get => _isTraining;
        private set
        {
            if (SetProperty(ref _isTraining, value))
            {
                _trainCommand.NotifyCanExecuteChanged();
                _stopCommand.NotifyCanExecuteChanged();
                _resetCommand.NotifyCanExecuteChanged();
                _saveCommand.NotifyCanExecuteChanged();
                _loadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string EpochText
    {
        get => _epochText;
        private set => SetProperty(ref _epochText, value);
    }

    public string LossText
    {
        get => _lossText;
        private set => SetProperty(ref _lossText, value);
    }

    public string AccuracyText
    {
        get => _accuracyText;
        private set => SetProperty(ref _accuracyText, value);
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
            _losses.Clear();
        }

        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        IsTraining = true;
        StatusText = "Training in progress";
        Completion = 0;
        EpochText = $"0 / {epochs:N0}";
        NeuralNetwork trainingNetwork = _network;
        int reportInterval = Math.Max(1, epochs / 120);

        var progress = new Progress<TrainingUpdate>(update =>
        {
            CurrentSnapshot = update.Snapshot;
            _losses.Add(update.Loss);
            LossHistory = _losses.ToArray();
            LossText = update.Loss.ToString("0.000000", CultureInfo.InvariantCulture);
            EpochText = $"{update.Epoch:N0} / {epochs:N0}";
            Completion = update.Epoch * 100.0 / epochs;
            UpdatePredictions(update.Snapshot);
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

                    if (epoch == 1 || epoch % reportInterval == 0 || epoch == epochs)
                    {
                        double loss = trainingNetwork.MeanSquaredError(_samples);
                        progress.Report(new TrainingUpdate(
                            epoch, loss, trainingNetwork.CaptureSnapshot()));
                    }
                }
            }, token);

            StatusText = "Training completed";
            Completion = 100;
            EpochText = $"{epochs:N0} / {epochs:N0}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Training stopped";
        }
        catch (Exception exception)
        {
            StatusText = $"Training failed: {exception.Message}";
        }
        finally
        {
            CurrentSnapshot = trainingNetwork.CaptureSnapshot();
            LossText = trainingNetwork.MeanSquaredError(_samples)
                .ToString("0.000000", CultureInfo.InvariantCulture);
            UpdatePredictions(CurrentSnapshot);
            IsTraining = false;
            _cancellation?.Dispose();
            _cancellation = null;
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
        _losses.Clear();
        LossHistory = [];
        CurrentSnapshot = _network.CaptureSnapshot();
        UpdatePredictions(CurrentSnapshot);
        LossText = _network.MeanSquaredError(_samples).ToString("0.000000", CultureInfo.InvariantCulture);
        EpochText = "0 / 0";
        Completion = 0;
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
            _losses.Clear();
            LossHistory = [];
            CurrentSnapshot = _network.CaptureSnapshot();
            UpdatePredictions(CurrentSnapshot);
            LossText = _network.MeanSquaredError(_samples)
                .ToString("0.000000", CultureInfo.InvariantCulture);
            EpochText = "Loaded model";
            Completion = 0;
            StatusText = "Model loaded successfully";
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
        int correct = 0;

        foreach (TrainingSample sample in _samples)
        {
            double output = network.Predict(sample.Inputs)[0];
            var row = new PredictionRow(
                $"{sample.Inputs[0]:0}  XOR  {sample.Inputs[1]:0}",
                (int)sample.Targets[0], output);
            Predictions.Add(row);
            if (row.IsCorrect)
            {
                correct++;
            }
        }

        AccuracyText = $"{correct} / {_samples.Count}";
    }

    private sealed record TrainingUpdate(int Epoch, double Loss, NetworkSnapshot Snapshot);
}
