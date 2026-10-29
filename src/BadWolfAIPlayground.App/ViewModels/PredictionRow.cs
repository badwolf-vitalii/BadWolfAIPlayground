using System.Globalization;

namespace BadWolfAIPlayground.App.ViewModels;

public sealed class PredictionRow(string input, int expected, double output)
{
    public string Input { get; } = input;

    public string Expected { get; } = expected.ToString(CultureInfo.InvariantCulture);

    public double Percentage { get; } = output * 100;

    public string Output { get; } = output.ToString("0.0000", CultureInfo.InvariantCulture);

    public string Result { get; } = (output >= 0.5 ? 1 : 0).ToString(CultureInfo.InvariantCulture);

    public bool IsCorrect { get; } = (output >= 0.5 ? 1 : 0) == expected;
}
