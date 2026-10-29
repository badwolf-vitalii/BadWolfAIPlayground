using System.Globalization;
using System.Text.RegularExpressions;
using BadWolfAIPlayground.Search;

namespace BadWolfAIPlayground.App.Search;

public sealed class SearchResultRow
{
    private const int ExcerptLength = 350;

    public string Title { get; }
    public string Snippet { get; }
    public string ScoreText { get; }
    public string SourcePath { get; }

    public SearchResultRow(SearchHit hit, bool semantic, string? query = null)
    {
        Title = $"{hit.Chunk.DocumentName} · chunk {hit.Chunk.Number}";
        SourcePath = hit.Chunk.FullPath ?? "Built-in demonstration document";
        string text = hit.Chunk.Content.Replace('\n', ' ').Trim();
        Snippet = BuildExcerpt(text, semantic ? null : query);
        ScoreText = semantic
            ? $"cosine {hit.Score.ToString("0.000", CultureInfo.InvariantCulture)}"
            : $"{hit.Score.ToString("0", CultureInfo.InvariantCulture)} word matches";
    }

    private static string BuildExcerpt(string text, string? query)
    {
        if (text.Length <= ExcerptLength)
        {
            return text;
        }

        int position = 0;
        if (!string.IsNullOrWhiteSpace(query))
        {
            int exact = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (exact >= 0)
            {
                position = exact;
            }
            else
            {
                // A word-match may occur far into a large log chunk. Don't only
                // show its first 350 characters and hide the actual matched text.
                int firstMatch = int.MaxValue;
                foreach (Match match in Regex.Matches(query, @"[\p{L}\p{N}]+"))
                {
                    int found = text.IndexOf(match.Value, StringComparison.OrdinalIgnoreCase);
                    if (found >= 0 && found < firstMatch)
                    {
                        firstMatch = found;
                    }
                }
                if (firstMatch != int.MaxValue)
                {
                    position = firstMatch;
                }
            }
        }

        int start = Math.Clamp(position - 80, 0, text.Length - ExcerptLength);
        string snippet = text.Substring(start, ExcerptLength).Trim();
        return (start > 0 ? "…" : "") + snippet + (start + ExcerptLength < text.Length ? "…" : "");
    }
}
