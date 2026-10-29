using BadWolfAIPlayground.Core;

var tests = new (string Name, Action Run)[]
{
    ("XOR convergence", VerifyXorConvergence),
    ("Deterministic initialization", VerifyDeterministicInitialization),
    ("JSON persistence", VerifyJsonRoundTrip),
    ("Snapshot isolation", VerifySnapshotIsolation),
    ("Arbitrary topology", VerifyArbitraryTopology),
    ("Invalid inputs and models", VerifyValidation)
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void VerifyXorConvergence()
{
    var network = new NeuralNetwork([2, 4, 1], seed: 42);
    var samples = XorDataset.CreateSamples();
    double initial = network.MeanSquaredError(samples);

    for (int epoch = 0; epoch < 25_000; epoch++)
    {
        foreach (var sample in samples)
        {
            network.TrainSample(sample.Inputs, sample.Targets, learningRate: 0.7);
        }
    }

    double final = network.MeanSquaredError(samples);
    Assert(final < initial / 5, $"Loss did not decrease enough: {initial} -> {final}.");
    Assert(final < 0.02, $"Final XOR loss is too high: {final}.");

    foreach (var sample in samples)
    {
        double output = network.Predict(sample.Inputs)[0];
        Assert(Math.Abs(output - sample.Targets[0]) < 0.2,
            $"Wrong XOR prediction for {string.Join(',', sample.Inputs)}: {output}.");
    }
}

static void VerifyDeterministicInitialization()
{
    var first = new NeuralNetwork([2, 5, 1], seed: 17);
    var second = new NeuralNetwork([2, 5, 1], seed: 17);
    Assert(first.Predict([0.25, 0.75])[0] == second.Predict([0.25, 0.75])[0],
        "Seeded networks must have the same initial parameters.");
}

static void VerifyJsonRoundTrip()
{
    var network = new NeuralNetwork([2, 4, 1], seed: 12);
    foreach (var sample in XorDataset.CreateSamples())
    {
        network.TrainSample(sample.Inputs, sample.Targets, 0.3);
    }

    string json = ModelPersistence.Serialize(network);
    NeuralNetwork restored = ModelPersistence.Deserialize(json);
    foreach (var sample in XorDataset.CreateSamples())
    {
        Assert(Math.Abs(network.Predict(sample.Inputs)[0] -
                        restored.Predict(sample.Inputs)[0]) < 1e-12,
            "JSON round-trip changed a prediction.");
    }

    restored.TrainSample([1, 0], [1], 0.3);
}

static void VerifySnapshotIsolation()
{
    var network = new NeuralNetwork([2, 3, 1]);
    double original = network.Predict([0, 1])[0];
    NetworkSnapshot snapshot = network.CaptureSnapshot();
    snapshot.Weights[0][0][0] = 999;
    snapshot.Biases[0][0] = 999;
    snapshot.LayerSizes[0] = 999;
    Assert(network.Predict([0, 1])[0] == original,
        "Changing a snapshot must not change the original network.");
}

static void VerifyArbitraryTopology()
{
    var network = new NeuralNetwork([3, 5, 4, 2]);
    double[] before = network.Predict([1, 0, 1]);
    Assert(before.Length == 2, "Output size does not match the topology.");
    double loss = network.TrainSample([1, 0, 1], [1, 0], 0.2);
    Assert(double.IsFinite(loss) && loss >= 0, "Training produced an invalid loss.");
}

static void VerifyValidation()
{
    AssertThrows<ArgumentException>(() => new NeuralNetwork([2]));
    AssertThrows<ArgumentException>(() => new NeuralNetwork([2, 0, 1]));
    var network = new NeuralNetwork([2, 4, 1]);
    AssertThrows<ArgumentException>(() => network.Predict([1]));
    AssertThrows<ArgumentException>(() => network.TrainSample([0, 1], [0, 1], 0.2));
    AssertThrows<ArgumentOutOfRangeException>(() =>
        network.TrainSample([0, 1], [1], -0.1));
    AssertThrows<ArgumentException>(() => NeuralNetwork.FromSnapshot(
        new NetworkSnapshot([2, 4, 1], [], [])));
    AssertThrows<Exception>(() => ModelPersistence.Deserialize("not json"));
    AssertThrows<InvalidDataException>(() => ModelPersistence.Deserialize(
        ModelPersistence.Serialize(network).Replace("\"FormatVersion\": 1", "\"FormatVersion\": 2")));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected exception: {typeof(T).Name}.");
}
