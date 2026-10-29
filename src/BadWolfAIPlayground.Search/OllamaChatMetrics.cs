using System.Text.Json;

namespace BadWolfAIPlayground.Search;

/// <summary>Raw Ollama non-streaming /api/chat metrics. Durations are nanoseconds.</summary>
public sealed record OllamaChatMetrics(
    long? TotalDurationNs, long? LoadDurationNs,
    long? PromptTokens, long? PromptDurationNs,
    long? OutputTokens, long? OutputDurationNs)
{
    public double? TokensPerSecond => OutputTokens is > 0 && OutputDurationNs is > 0
        ? OutputTokens.Value * 1_000_000_000.0 / OutputDurationNs.Value
        : null;

    public double? LoadSeconds => LoadDurationNs is > 0
        ? LoadDurationNs.Value / 1_000_000_000.0
        : null;

    public static OllamaChatMetrics? Sum(IReadOnlyList<OllamaChatMetrics> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return null;
        }

        static long? Combine(IReadOnlyList<OllamaChatMetrics> entries,
            Func<OllamaChatMetrics, long?> field)
        {
            long result = 0;
            foreach (OllamaChatMetrics metric in entries)
            {
                long? value = field(metric);
                if (!value.HasValue)
                {
                    // Never silently treat missing stats as zero.
                    return null;
                }
                result = checked(result + value.Value);
            }
            return result;
        }

        return new OllamaChatMetrics(
            Combine(samples, m => m.TotalDurationNs),
            Combine(samples, m => m.LoadDurationNs),
            Combine(samples, m => m.PromptTokens),
            Combine(samples, m => m.PromptDurationNs),
            Combine(samples, m => m.OutputTokens),
            Combine(samples, m => m.OutputDurationNs));
    }

    public static OllamaChatMetrics FromResponse(JsonElement json)
    {
        static long? Get(JsonElement item, string field) =>
            item.TryGetProperty(field, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) &&
            number >= 0
                ? number : null;

        return new OllamaChatMetrics(
            Get(json, "total_duration"), Get(json, "load_duration"),
            Get(json, "prompt_eval_count"), Get(json, "prompt_eval_duration"),
            Get(json, "eval_count"), Get(json, "eval_duration"));
    }
}
