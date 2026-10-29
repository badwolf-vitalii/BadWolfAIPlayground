using System.Globalization;

namespace BadWolfAIPlayground.App.ViewModels;

public sealed class PredictionRow(string input, int expected, double beforeOutput, double output)
{
    public string Input { get; } = input;

    public string Expected { get; } = expected.ToString(CultureInfo.InvariantCulture);

    // The baseline never changes while the same model is being trained.
    public string BeforeOutput { get; } = beforeOutput.ToString("0.0000", CultureInfo.InvariantCulture);

    public double Percentage { get; } = output * 100;

    public string Output { get; } = output.ToString("0.0000", CultureInfo.InvariantCulture);

    public string Result { get; } = (output >= 0.5 ? 1 : 0).ToString(CultureInfo.InvariantCulture);

    public bool IsCorrect { get; } = (output >= 0.5 ? 1 : 0) == expected;

    public string ResultDescription { get; } = (output >= 0.5 ? 1 : 0) == expected
        ? "Correct"
        : "Not yet";

    public string ChangeDescription { get; } = Math.Abs(output - beforeOutput) < 0.00005
        ? "Not trained yet"
        : Math.Abs(expected - output) < Math.Abs(expected - beforeOutput)
            ? "Closer to target"
            : "Farther from target";
}
