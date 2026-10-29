namespace BadWolfAIPlayground.Core;

public static class XorDataset
{
    public static IReadOnlyList<TrainingSample> CreateSamples() =>
    [
        new([0, 0], [0]),
        new([0, 1], [1]),
        new([1, 0], [1]),
        new([1, 1], [0])
    ];
}
