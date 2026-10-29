using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Search;

/// <summary>A verbatim excerpt the user can check against a selected source document.</summary>
public sealed record EvidencePassage(int Id, SearchChunk Chunk, double SearchScore);

/// <summary>A model-authored answer is not trusted until its quotes match source text.</summary>
public sealed record ProposedEvidence(int Id, string Quote);
public sealed record ProposedAnswer(bool Supported, string Answer, IReadOnlyList<ProposedEvidence> Evidence);

public sealed record VerifiedCitation(int Id, SearchChunk Chunk, string Quote);
public sealed record GroundedAnswer(
    string Text, bool Supported, IReadOnlyList<VerifiedCitation> Citations, int ExaminedPassages);

/// <summary>Only generates text; never reads files or makes remote calls on its own.</summary>
public interface ILocalAnswerGenerator
{
    string ModelName { get; }
    Task<IReadOnlyList<string>> ExpandSearchAsync(string question, CancellationToken token = default);
    Task<ProposedAnswer> GenerateAsync(
        string question, IReadOnlyList<EvidencePassage> passages, CancellationToken token = default);
}

/// <summary>
/// Evidence-oriented retrieval-augmented answers. Semantic and keyword rankings
/// are combined, with optional multilingual search rewrites. Exact source quotes
/// are checked after generation, and an unsupported result is never presented as fact.
/// </summary>
public static class GroundedQuestionAnswering
{
    public const int MaxPassages = 7;
    public const int MaxQueries = 3;
    private const string NotFound =
        "Не вдалося підтвердити відповідь у завантаженому тексті. " +
        "Перевір, чи потрібний розділ є у вибраному файлі, або уточни запит.";

    public static async Task<GroundedAnswer> AskAsync(
        SemanticIndex index,
        IEmbeddingProvider embeddings,
        ILocalAnswerGenerator generator,
        string question,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        IReadOnlyList<string> rewrites = await generator.ExpandSearchAsync(question, cancellationToken)
            .ConfigureAwait(false);
        string[] queries = new[] { question }
            .Concat(rewrites ?? [])
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Where(query => query.Length <= 500)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToArray();

        // Reciprocal-rank fusion prevents raw keyword occurrence counts
        // from overwhelming dense semantic similarity, and allows synonyms
        // to surface passages that do not contain the question's exact words.
        var fused = new Dictionary<SearchChunk, (double Rank, double Similarity)>();
        foreach (string query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SearchHit> semantic = await SemanticSearch.FindAsync(
                index, embeddings, query, limit: 24, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<SearchHit> lexical = await Task.Run(() =>
                KeywordSearch.Find(index.Chunks, query, limit: 24,
                    cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            for (int i = 0; i < semantic.Count; i++)
            {
                SearchHit hit = semantic[i];
                var old = fused.GetValueOrDefault(hit.Chunk);
                fused[hit.Chunk] = (old.Rank + 1.4 / (15 + i), Math.Max(old.Similarity, hit.Score));
            }

            for (int i = 0; i < lexical.Count; i++)
            {
                SearchHit hit = lexical[i];
                var old = fused.GetValueOrDefault(hit.Chunk);
                fused[hit.Chunk] = (old.Rank + 0.8 / (15 + i), old.Similarity);
            }
        }

        EvidencePassage[] passages = fused
            .OrderByDescending(pair => pair.Value.Rank)
            .ThenByDescending(pair => pair.Value.Similarity)
            .Take(MaxPassages)
            .Select((pair, index) => new EvidencePassage(index + 1, pair.Key, pair.Value.Similarity))
            .ToArray();

        if (passages.Length == 0)
        {
            return new GroundedAnswer(NotFound, false, [], 0);
        }

        ProposedAnswer proposed = await generator.GenerateAsync(
            question, passages, cancellationToken).ConfigureAwait(false);
        if (!proposed.Supported || string.IsNullOrWhiteSpace(proposed.Answer) ||
            proposed.Evidence is null)
        {
            return new GroundedAnswer(NotFound, false, [], passages.Length);
        }

        var valid = new List<VerifiedCitation>();
        foreach (ProposedEvidence proof in proposed.Evidence.Take(MaxPassages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvidencePassage? passage = passages.FirstOrDefault(item => item.Id == proof.Id);
            string quote = proof.Quote?.Trim() ?? "";
            if (passage is null || quote.Length is < 8 or > 400)
            {
                continue;
            }

            // Permit newline/spacing normalization, not paraphrases. The full
            // source text is never replaced by a model-generated quote.
            string source = NormalizeWhitespace(passage.Chunk.Content);
            if (!source.Contains(NormalizeWhitespace(quote), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (valid.All(citation => citation.Id != proof.Id))
            {
                valid.Add(new VerifiedCitation(proof.Id, passage.Chunk, quote));
            }
        }

        // A plausible answer with no checkable source is not an answer.
        return valid.Count == 0
            ? new GroundedAnswer(NotFound, false, [], passages.Length)
            : new GroundedAnswer(proposed.Answer.Trim(), true, valid, passages.Length);
    }

    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();
}
