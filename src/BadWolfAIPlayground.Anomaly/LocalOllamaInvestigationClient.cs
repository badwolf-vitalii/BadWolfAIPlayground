using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace BadWolfAIPlayground.Anomaly;

public sealed record LocalInvestigationHypothesis(
    string Claim, IReadOnlyList<InvestigationPromptEvidence> References)
{
    public string ReferencesText => string.Join(", ", References.Select(e =>
        e.Id + " (" + e.Location + ")"));
}

public sealed record LocalInvestigationExplanation(
    string Summary, IReadOnlyList<LocalInvestigationHypothesis> Hypotheses,
    IReadOnlyList<string> NextChecks, IReadOnlyList<string> Unknowns)
{
    public string Notice =>
        "Model-generated hypotheses, NOT verified root cause. " +
        "Evidence IDs refer to original imported logs and local indexed C# excerpts.";
}

/// <summary>
/// Explicit user-initiated local-only chat call; never triggered by log import.
/// It uses the same Ollama loopback /api/chat protocol as Smart Search Lab.
/// A strict response parser rejects fabricated references or malformed JSON.
/// </summary>
public sealed class LocalOllamaInvestigationClient : IDisposable
{
    public const string LocalEndpoint = "http://127.0.0.1:11434/";
    public const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public LocalOllamaInvestigationClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            BaseAddress = new Uri(LocalEndpoint),
            Timeout = TimeSpan.FromMinutes(4)
        };
        ValidateEndpoint(_http.BaseAddress);
    }

    private static void ValidateEndpoint(Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.Host != "127.0.0.1" || endpoint.Port != 11434 ||
            endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query))
            throw new ArgumentException(
                "Investigation chat is restricted to http://127.0.0.1:11434/.");
    }

    public async Task<LocalInvestigationExplanation> ExplainAsync(
        InvestigationPrompt prompt, string model, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 120 || model.Any(c =>
                !(char.IsAsciiLetterOrDigit(c) || c is ':' or '/' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid local Ollama model name.", nameof(model));
        ValidateEndpoint(_http.BaseAddress);

        var payload = new
        {
            model,
            stream = false,
            format = "json",
            messages = new[]
            {
                new { role = "system", content = prompt.SystemMessage },
                new { role = "user", content = prompt.UserMessage }
            },
            options = new
            {
                temperature = 0.0,
                num_predict = 700,
                num_ctx = 8192
            }
        };
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                "api/chat", payload, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"Local model '{model}' was not found. Install it using Ollama.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Local Ollama investigation failed (HTTP {(int)response.StatusCode}).");
            string jsonText = await ReadBoundedAsync(response.Content, token)
                .ConfigureAwait(false);
            using JsonDocument envelope = JsonDocument.Parse(jsonText);
            if (!envelope.RootElement.TryGetProperty("message", out JsonElement message) ||
                !message.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Ollama returned no chat message.");
            using JsonDocument answer = JsonDocument.Parse(content.GetString() ?? "");
            return ParseExplanation(answer.RootElement, prompt.Evidence);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                "Cannot connect to local Ollama on http://127.0.0.1:11434/.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The local model returned invalid structured JSON; no diagnosis was published.", ex);
        }
    }

    public static LocalInvestigationExplanation ParseExplanation(
        JsonElement json, IReadOnlyList<InvestigationPromptEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ollama explanation must be a JSON object.");

        string summary = ReadText(json, "summary", 1000);
        var evidenceById = evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        JsonElement hypotheses = RequireArray(json, "hypotheses", 3);
        var claims = new List<LocalInvestigationHypothesis>();
        foreach (JsonElement item in hypotheses.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Malformed explanation hypothesis.");
            string claim = ReadText(item, "claim", 650);
            JsonElement ids = RequireArray(item, "evidence_ids", 4);
            if (ids.GetArrayLength() == 0)
                throw new InvalidDataException("Uncited hypothesis rejected.");
            var references = new List<InvestigationPromptEvidence>();
            foreach (JsonElement value in ids.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String ||
                    !evidenceById.TryGetValue(value.GetString() ?? "",
                        out InvestigationPromptEvidence? real))
                    throw new InvalidDataException(
                        "Model invented or omitted an evidence reference; no claims were published.");
                if (!references.Contains(real))
                    references.Add(real);
            }
            claims.Add(new LocalInvestigationHypothesis(claim, references));
        }

        var checks = ReadTexts(json, "next_checks", 5, 500);
        var unknowns = ReadTexts(json, "unknowns", 4, 500).ToList();
        bool hasCode = evidence.Any(item => item.Id.StartsWith('C'));
        // Cite an actual *supporting* entry, not just the primary L1 failure.
        // A separate LOG line or an indexed guard at a matched logging
        // template is a prerequisite, not proof that a proposed cause is true.
        HashSet<string> supportingIds = evidence.Where(item =>
            (item.Id.StartsWith('L') && item.Id != "L1") ||
            (item.Id.StartsWith('C') &&
             (item.Kind.Contains("logging literal", StringComparison.OrdinalIgnoreCase) ||
              item.Kind.Contains("logging template", StringComparison.OrdinalIgnoreCase)) &&
             item.Excerpt.Contains("Enclosing guards", StringComparison.Ordinal) &&
             !item.Excerpt.Contains("No enclosing if/catch guard",
                 StringComparison.Ordinal)))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        int withheld = claims.RemoveAll(claim =>
            !claim.References.Any(item => supportingIds.Contains(item.Id)));

        if (!hasCode)
            unknowns.Insert(0, "No indexed C# source candidate was supplied. " +
                "The application control flow was not verified.");

        if (supportingIds.Count == 0 || (withheld > 0 && claims.Count == 0))
        {
            // Avoid displaying an unsupported model narrative as an observed
            // fact when all proposed causes lacked corroborating citations.
            InvestigationPromptEvidence? primary =
                evidence.FirstOrDefault(item => item.Id == "L1");
            summary = primary is null
                ? "No primary LOG observation was supplied."
                : "The available LOG evidence records an event at " +
                    primary.Location + ". Its underlying cause is not established.";
        }
        if (supportingIds.Count == 0)
            unknowns.Insert(0, "Causal hypotheses were withheld: no separate " +
                "related LOG observation or indexed enclosing C# branch " +
                "supports a mechanism. Inspect the source branch and earlier " +
                "operation state.");
        else if (withheld > 0)
            unknowns.Insert(0, "Causal hypotheses citing only the primary " +
                "failure or non-guard source location were withheld. Inspect " +
                "the relevant L#/C# evidence before inferring a cause.");
        if (unknowns.Count > 4)
            unknowns.RemoveRange(4, unknowns.Count - 4);
        if (claims.Count == 0 && unknowns.Count == 0)
            throw new InvalidDataException(
                "The model provided neither supported hypotheses nor limitations.");
        return new LocalInvestigationExplanation(summary, claims, checks, unknowns);
    }

    private static JsonElement RequireArray(JsonElement root, string name, int maxCount)
    {
        if (!root.TryGetProperty(name, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() > maxCount)
            throw new InvalidDataException($"Invalid or oversized '{name}' in model response.");
        return values;
    }

    private static IReadOnlyList<string> ReadTexts(
        JsonElement root, string name, int maxCount, int maxLength)
    {
        JsonElement list = RequireArray(root, name, maxCount);
        return list.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : throw new InvalidDataException($"Invalid '{name}' element."))
            .Select(value => value.Trim())
            .Select(value => value.Length is > 0 && value.Length <= maxLength
                ? value
                : throw new InvalidDataException($"Oversized or empty '{name}' element."))
            .ToArray();
    }

    private static string ReadText(JsonElement item, string key, int maxLength)
    {
        if (!item.TryGetProperty(key, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > maxLength)
            throw new InvalidDataException($"Invalid or oversized '{key}' in model response.");
        return value.GetString()!.Trim();
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content, CancellationToken token)
    {
        await using Stream stream = await content.ReadAsStreamAsync(token)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes.AsMemory(), token)
                   .ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                throw new InvalidDataException("Local Ollama response exceeds 64 KiB.");
            buffer.Write(bytes, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
