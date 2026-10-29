using System.Text.Json;

namespace BadWolfAIPlayground.Core;

public static class ModelPersistence
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    public static string Serialize(NeuralNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return JsonSerializer.Serialize(
            new ModelFile(CurrentFormatVersion, network.CaptureSnapshot()), Options);
    }

    public static NeuralNetwork Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        ModelFile? model = JsonSerializer.Deserialize<ModelFile>(json, Options);

        if (model is null || model.FormatVersion != CurrentFormatVersion ||
            model.Network is null)
        {
            throw new InvalidDataException("Unsupported or incomplete model file.");
        }

        return NeuralNetwork.FromSnapshot(model.Network);
    }

    public sealed record ModelFile(int FormatVersion, NetworkSnapshot Network);
}
