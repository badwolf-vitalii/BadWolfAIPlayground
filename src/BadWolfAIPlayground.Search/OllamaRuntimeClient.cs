using System.Net.Http;
using System.Text.Json;

namespace BadWolfAIPlayground.Search;

public sealed record LocalOllamaModel(string Name, long Bytes);

public sealed record OllamaExecutionInfo(string ModelName, bool Loaded, int? GpuPercent, int? ContextLength)
{
    public string Display => !Loaded
        ? "Model not loaded — processor use is unknown until a request runs."
        : GpuPercent is null
            ? "Model loaded, but Ollama did not report GPU memory allocation."
            : GpuPercent == 0
                ? $"100% CPU (Ollama /api/ps), context {ContextLength?.ToString() ?? "unknown"}"
                : GpuPercent == 100
                    ? $"100% GPU (Ollama /api/ps), context {ContextLength?.ToString() ?? "unknown"}"
                    : $"{GpuPercent}% GPU / {100 - GpuPercent}% CPU (estimated by Ollama model memory), context {ContextLength?.ToString() ?? "unknown"}";
}

/// <summary>
/// Talks exclusively to the already-running local Ollama process.
/// /api/tags lists installed models; /api/ps reports loaded model GPU memory.
/// No hardware is inferred from the Windows machine or from model size alone.
/// </summary>
public sealed class OllamaRuntimeClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public OllamaRuntimeClient(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<IReadOnlyList<LocalOllamaModel>> ListInstalledAsync(
        CancellationToken token = default)
    {
        using HttpResponseMessage response = await _client.GetAsync("api/tags", token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("models", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Ollama did not return a list of installed models.");
        }

        var result = new List<LocalOllamaModel>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out JsonElement name) ||
                name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
            {
                continue;
            }

            long bytes = item.TryGetProperty("size", out JsonElement size) &&
                         size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long value)
                ? Math.Max(0, value) : 0;
            result.Add(new LocalOllamaModel(name.GetString()!, bytes));
        }

        return result.OrderBy(model => model.Bytes).ThenBy(model => model.Name).ToArray();
    }

    public async Task<OllamaExecutionInfo> GetExecutionAsync(
        string model, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        using HttpResponseMessage response = await _client.GetAsync("api/ps", token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("models", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Ollama did not return a list of running models.");
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out JsonElement name) ||
                name.ValueKind != JsonValueKind.String ||
                !string.Equals(NormalizeModel(name.GetString()), NormalizeModel(model),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int? gpu = null;
            if (item.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long total) &&
                total > 0 && item.TryGetProperty("size_vram", out JsonElement vram) &&
                vram.TryGetInt64(out long onGpu))
            {
                gpu = (int)Math.Clamp(Math.Round(onGpu * 100.0 / total), 0, 100);
            }

            int? context = item.TryGetProperty("context_length", out JsonElement contextValue) &&
                           contextValue.ValueKind == JsonValueKind.Number &&
                           contextValue.TryGetInt32(out int contextLength)
                ? contextLength : null;
            return new OllamaExecutionInfo(model, true, gpu, context);
        }

        return new OllamaExecutionInfo(model, false, null, null);
    }

    private static string? NormalizeModel(string? name) =>
        name is not null && name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? name[..^7] : name;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
