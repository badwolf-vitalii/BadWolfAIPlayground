using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Search;

/// <summary>
/// Whole-word lexical baseline. Common question filler words are ignored when
/// more meaningful terms are available; occurrences and distinct-term coverage
/// are tracked separately to avoid ranking by substring noise.
/// </summary>
public static partial class KeywordSearch
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "де", "як", "що", "хто", "коли", "чи", "це", "той", "та", "і", "й", "або",
        "але", "до", "від", "над", "під", "в", "у", "з", "із", "на", "за", "по",
        "the", "a", "an", "of", "and", "or", "is", "in", "at", "for", "to",
        "where", "what", "who", "how", "did", "does", "from",
        "dove", "come", "cosa", "che", "il", "la", "di", "da", "del", "per", "con"
    };

    public static IReadOnlyList<SearchHit> Find(
        IReadOnlyList<SearchChunk> chunks, string query, int limit = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(query);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        string[] allTerms = Words().Matches(query)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allTerms.Length == 0)
        {
            return [];
        }

        string[] meaningful = allTerms.Where(term => !CommonWords.Contains(term)).ToArray();
        var wanted = new HashSet<string>(
            meaningful.Length == 0 ? allTerms : meaningful,
            StringComparer.OrdinalIgnoreCase);

        var matches = new List<(SearchHit Hit, int CoveredTerms)>();
        foreach (SearchChunk chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int occurrences = 0;
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match word in Words().Matches(chunk.Content))
            {
                if (wanted.Contains(word.Value))
                {
                    occurrences++;
                    covered.Add(word.Value);
                }
            }

            if (occurrences > 0)
            {
                matches.Add((new SearchHit(chunk, occurrences), covered.Count));
            }
        }

        return matches
            .OrderByDescending(item => item.CoveredTerms)
            .ThenByDescending(item => item.Hit.Score)
            .ThenBy(item => item.Hit.Chunk.DocumentName, StringComparer.Ordinal)
            .ThenBy(item => item.Hit.Chunk.Number)
            .Take(limit)
            .Select(item => item.Hit)
            .ToArray();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
