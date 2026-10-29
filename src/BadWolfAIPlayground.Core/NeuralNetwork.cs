namespace BadWolfAIPlayground.Core;

/// <summary>
/// A fully connected feed-forward network trained with stochastic gradient
/// descent and backpropagation. Every layer uses sigmoid activation.
/// No external machine-learning packages are required.
/// </summary>
public sealed class NeuralNetwork
{
    private readonly int[] _layerSizes;
    private readonly double[][][] _weights;
    private readonly double[][] _biases;

    public IReadOnlyList<int> LayerSizes => Array.AsReadOnly(_layerSizes);

    public NeuralNetwork(IReadOnlyList<int> layerSizes, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(layerSizes);

        if (layerSizes.Count < 2 || layerSizes.Any(size => size <= 0))
        {
            throw new ArgumentException(
                "A network requires at least two layers with positive sizes.",
                nameof(layerSizes));
        }

        _layerSizes = layerSizes.ToArray();
        _weights = new double[_layerSizes.Length - 1][][];
        _biases = new double[_layerSizes.Length - 1][];
        var random = new Random(seed);

        for (int layer = 0; layer < _weights.Length; layer++)
        {
            int inputs = _layerSizes[layer];
            int outputs = _layerSizes[layer + 1];
            double limit = Math.Sqrt(6.0 / (inputs + outputs));

            _weights[layer] = new double[outputs][];
            _biases[layer] = new double[outputs];

            for (int neuron = 0; neuron < outputs; neuron++)
            {
                _weights[layer][neuron] = new double[inputs];

                for (int input = 0; input < inputs; input++)
                {
                    _weights[layer][neuron][input] =
                        (random.NextDouble() * 2.0 - 1.0) * limit;
                }
            }
        }
    }

    public double[] Predict(IReadOnlyList<double> inputs)
    {
        ValidateVector(inputs, _layerSizes[0], nameof(inputs));
        return Forward(inputs)[^1];
    }

    /// <summary>Trains on one sample and returns its pre-update mean squared error.</summary>
    public double TrainSample(
        IReadOnlyList<double> inputs,
        IReadOnlyList<double> targets,
        double learningRate)
    {
        ValidateVector(inputs, _layerSizes[0], nameof(inputs));
        ValidateVector(targets, _layerSizes[^1], nameof(targets));

        if (!double.IsFinite(learningRate) || learningRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(learningRate));
        }

        double[][] activations = Forward(inputs);
        double[][] deltas = new double[_weights.Length][];
        int outputLayer = _weights.Length - 1;
        double loss = 0;
        double[] actual = activations[^1];
        deltas[outputLayer] = new double[actual.Length];

        for (int neuron = 0; neuron < actual.Length; neuron++)
        {
            double error = actual[neuron] - targets[neuron];
            loss += error * error;
            deltas[outputLayer][neuron] =
                error * SigmoidDerivativeFromOutput(actual[neuron]);
        }

        // Calculate every gradient before changing any weights.
        for (int layer = outputLayer - 1; layer >= 0; layer--)
        {
            deltas[layer] = new double[_layerSizes[layer + 1]];

            for (int neuron = 0; neuron < deltas[layer].Length; neuron++)
            {
                double downstreamError = 0;

                for (int next = 0; next < deltas[layer + 1].Length; next++)
                {
                    downstreamError +=
                        _weights[layer + 1][next][neuron] * deltas[layer + 1][next];
                }

                double activation = activations[layer + 1][neuron];
                deltas[layer][neuron] =
                    downstreamError * SigmoidDerivativeFromOutput(activation);
            }
        }

        for (int layer = 0; layer < _weights.Length; layer++)
        {
            for (int neuron = 0; neuron < _weights[layer].Length; neuron++)
            {
                double gradient = deltas[layer][neuron];

                for (int input = 0; input < _weights[layer][neuron].Length; input++)
                {
                    _weights[layer][neuron][input] -=
                        learningRate * gradient * activations[layer][input];
                }

                _biases[layer][neuron] -= learningRate * gradient;
            }
        }

        return loss / actual.Length;
    }

    public double MeanSquaredError(IEnumerable<TrainingSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double squaredError = 0;
        int outputCount = 0;

        foreach (TrainingSample sample in samples)
        {
            double[] actual = Predict(sample.Inputs);
            ValidateVector(sample.Targets, actual.Length, nameof(samples));

            for (int index = 0; index < actual.Length; index++)
            {
                double error = actual[index] - sample.Targets[index];
                squaredError += error * error;
                outputCount++;
            }
        }

        if (outputCount == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        return squaredError / outputCount;
    }

    public NetworkSnapshot CaptureSnapshot() => new(
        (int[])_layerSizes.Clone(),
        _weights.Select(layer =>
            layer.Select(neuron => (double[])neuron.Clone()).ToArray()).ToArray(),
        _biases.Select(layer => (double[])layer.Clone()).ToArray());

    public static NeuralNetwork FromSnapshot(NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.LayerSizes is null || snapshot.Weights is null ||
            snapshot.Biases is null)
        {
            throw new ArgumentException("Model contains missing arrays.", nameof(snapshot));
        }

        var network = new NeuralNetwork(snapshot.LayerSizes);

        if (snapshot.Weights.Length != network._weights.Length ||
            snapshot.Biases.Length != network._biases.Length)
        {
            throw new ArgumentException("Model layer count is invalid.", nameof(snapshot));
        }

        for (int layer = 0; layer < network._weights.Length; layer++)
        {
            if (snapshot.Weights[layer] is null || snapshot.Biases[layer] is null ||
                snapshot.Weights[layer].Length != network._weights[layer].Length ||
                snapshot.Biases[layer].Length != network._biases[layer].Length)
            {
                throw new ArgumentException("Model layer shape is invalid.", nameof(snapshot));
            }

            for (int neuron = 0; neuron < network._weights[layer].Length; neuron++)
            {
                double[]? sourceWeights = snapshot.Weights[layer][neuron];
                if (sourceWeights is null ||
                    sourceWeights.Length != network._weights[layer][neuron].Length ||
                    sourceWeights.Any(weight => !double.IsFinite(weight)) ||
                    !double.IsFinite(snapshot.Biases[layer][neuron]))
                {
                    throw new ArgumentException("Model contains invalid parameters.", nameof(snapshot));
                }

                Array.Copy(sourceWeights, network._weights[layer][neuron], sourceWeights.Length);
                network._biases[layer][neuron] = snapshot.Biases[layer][neuron];
            }
        }

        return network;
    }

    private double[][] Forward(IReadOnlyList<double> inputs)
    {
        var activations = new double[_layerSizes.Length][];
        activations[0] = inputs.ToArray();

        for (int layer = 0; layer < _weights.Length; layer++)
        {
            activations[layer + 1] = new double[_layerSizes[layer + 1]];

            for (int neuron = 0; neuron < _weights[layer].Length; neuron++)
            {
                double sum = _biases[layer][neuron];

                for (int input = 0; input < _weights[layer][neuron].Length; input++)
                {
                    sum += _weights[layer][neuron][input] * activations[layer][input];
                }

                activations[layer + 1][neuron] = Sigmoid(sum);
            }
        }

        return activations;
    }

    private static void ValidateVector(IReadOnlyList<double> vector, int size, string name)
    {
        ArgumentNullException.ThrowIfNull(vector);

        if (vector.Count != size || vector.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Vector has an invalid shape or value.", name);
        }
    }

    private static double Sigmoid(double value) =>
        value >= 0
            ? 1.0 / (1.0 + Math.Exp(-value))
            : Math.Exp(value) / (1.0 + Math.Exp(value));

    private static double SigmoidDerivativeFromOutput(double output) => output * (1 - output);
}
