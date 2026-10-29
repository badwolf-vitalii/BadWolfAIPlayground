using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Search;

/// <summary>A simple lexical baseline: counts occurrences of query terms, without AI.</summary>
public static partial class KeywordSearch
{
    public static IReadOnlyList<SearchHit> Find(
        IReadOnlyList<SearchChunk> chunks, string query, int limit = 5)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(query);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        string[] terms = Words().Matches(query)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        return chunks.Select(chunk => new SearchHit(chunk, Score(chunk.Content, terms)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Chunk.DocumentName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static int Score(string text, IReadOnlyList<string> terms)
    {
        int matches = 0;
        foreach (string term in terms)
        {
            int offset = 0;
            while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                matches++;
                offset += term.Length;
            }
        }
        return matches;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
