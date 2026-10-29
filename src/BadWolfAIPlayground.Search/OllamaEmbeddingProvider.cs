using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BadWolfAIPlayground.Search;

/// <summary>Uses the loopback-only Ollama API. Documents never leave the local computer.</summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public const string DefaultModel = "embeddinggemma";
    public const string LocalEndpoint = "http://127.0.0.1:11434/";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public string ModelName { get; }

    public OllamaEmbeddingProvider(string modelName = DefaultModel, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name is required.", nameof(modelName));
        }

        ModelName = modelName;
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            BaseAddress = new Uri(LocalEndpoint),
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    public async Task<string> CheckStatusAsync(CancellationToken token = default)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync("api/tags", token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));

            if (!json.RootElement.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Unexpected response from Ollama /api/tags.");
            }

            bool found = models.EnumerateArray().Any(item =>
                item.TryGetProperty("name", out JsonElement name) &&
                (string.Equals(name.GetString(), ModelName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name.GetString(), ModelName + ":latest", StringComparison.OrdinalIgnoreCase)));

            return found
                ? $"Ready: {ModelName} is installed locally."
                : $"Ollama is running. Install the model: ollama pull {ModelName}";
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "Cannot reach local Ollama. Install/start Ollama first (http://127.0.0.1:11434).", exception);
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Embedding inputs must be nonempty.", nameof(inputs));
        }

        try
        {
            using HttpResponseMessage response = await _client.PostAsJsonAsync(
                "api/embed", new { model = ModelName, input = inputs, truncate = true }, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (detail.Length > 250)
                {
                    detail = detail[..250];
                }

                throw new InvalidOperationException(
                    response.StatusCode == HttpStatusCode.NotFound
                        ? $"Embedding model '{ModelName}' is missing. Run: ollama pull {ModelName}"
                        : $"Local Ollama embedding request failed ({(int)response.StatusCode}): {detail}");
            }

            using JsonDocument json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("embeddings", out JsonElement embeddings) ||
                embeddings.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Ollama returned no embeddings array.");
            }

            var results = new List<float[]>();
            foreach (JsonElement embedding in embeddings.EnumerateArray())
            {
                if (embedding.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Ollama returned an invalid embedding vector.");
                }

                results.Add(embedding.EnumerateArray().Select(element => element.GetSingle()).ToArray());
            }

            if (results.Count != inputs.Count)
            {
                throw new InvalidDataException("Ollama returned fewer or more embeddings than requested.");
            }

            return results;
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "Cannot reach local Ollama. Install/start Ollama and run: ollama pull " + ModelName, exception);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
