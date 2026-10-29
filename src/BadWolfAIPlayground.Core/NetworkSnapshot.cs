namespace BadWolfAIPlayground.Core;

/// <summary>A detached copy of the network's topology and parameters.</summary>
public sealed record NetworkSnapshot(
    int[] LayerSizes,
    double[][][] Weights,
    double[][] Biases);
