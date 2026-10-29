using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Search;

/// <summary>A verbatim excerpt the user can check against a selected source document.</summary>
public sealed record EvidencePassage(int Id, SearchChunk Chunk, double SearchScore);

/// <summary>A model-authored answer is not trusted until its quotes match source text.</summary>
public sealed record ProposedEvidence(int Id, string Quote);
public sealed record ProposedAnswer(bool Supported, string Answer, IReadOnlyList<ProposedEvidence> Evidence);

public sealed record VerifiedCitation(int Id, SearchChunk Chunk, string Quote);
public enum AnswerOutcome
{
    Verified,
    NoCandidates,
    ModelDeclined,
    UnverifiableCitation
}

public sealed record GroundedAnswer(
    string Text, bool Supported, IReadOnlyList<VerifiedCitation> Citations, int ExaminedPassages)
{
    // An unsupported answer must still reveal the passages the local model read.
    public AnswerOutcome Outcome { get; init; } = AnswerOutcome.ModelDeclined;
    public IReadOnlyList<EvidencePassage> Candidates { get; init; } = [];
    public IReadOnlyList<string> SearchQueries { get; init; } = [];
}

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
        CancellationToken cancellationToken = default,
        bool expandQueries = false)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // Fast mode uses one local generation call. Query expansion makes a
        // second, sequential LLM request and is opt-in for hard questions.
        IReadOnlyList<string> rewrites = expandQueries
            ? await generator.ExpandSearchAsync(question, cancellationToken)
                .ConfigureAwait(false)
            : [];
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
        IReadOnlyList<SearchHit> originalLexical = [];
        IReadOnlyList<SearchHit> originalSemantic = [];
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

            if (string.Equals(query, queries[0], StringComparison.Ordinal))
            {
                originalLexical = lexical;
                originalSemantic = semantic;
            }

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

        // Reserve room for exact source-term matches and semantic matches from
        // the original question. Pure fusion can bury a rare exact term under
        // several weakly related rewrites, especially in long documents.
        var selected = new List<SearchChunk>(MaxPassages);
        void AddDistinct(IEnumerable<SearchChunk> source, int max)
        {
            foreach (SearchChunk chunk in source.Take(max))
            {
                if (selected.Count >= MaxPassages)
                {
                    return;
                }
                if (!selected.Contains(chunk))
                {
                    selected.Add(chunk);
                }
            }
        }

        AddDistinct(originalLexical.Select(hit => hit.Chunk), 2);
        AddDistinct(originalSemantic.Select(hit => hit.Chunk), 2);
        AddDistinct(fused
            .OrderByDescending(pair => pair.Value.Rank)
            .ThenByDescending(pair => pair.Value.Similarity)
            .Select(pair => pair.Key), MaxPassages);

        EvidencePassage[] passages = selected
            .Select((chunk, position) =>
                new EvidencePassage(position + 1, chunk, fused[chunk].Similarity))
            .ToArray();

        if (passages.Length == 0)
        {
            return new GroundedAnswer(NotFound, false, [], 0)
            {
                Outcome = AnswerOutcome.NoCandidates,
                SearchQueries = queries
            };
        }

        ProposedAnswer proposed = await generator.GenerateAsync(
            question, passages, cancellationToken).ConfigureAwait(false);
        if (!proposed.Supported || string.IsNullOrWhiteSpace(proposed.Answer) ||
            proposed.Evidence is null)
        {
            return new GroundedAnswer(NotFound, false, [], passages.Length)
            {
                Outcome = AnswerOutcome.ModelDeclined,
                Candidates = passages,
                SearchQueries = queries
            };
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
            // Verify quotes against exactly the excerpt sent to Qwen,
            // not hidden text elsewhere in a larger indexed chunk.
            string source = NormalizeWhitespace(
                OllamaAnswerProvider.SelectExcerpt(passage.Chunk.Content, question));
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
              {
                  Outcome = AnswerOutcome.UnverifiableCitation,
                  Candidates = passages,
                  SearchQueries = queries
              }
            : new GroundedAnswer(proposed.Answer.Trim(), true, valid, passages.Length)
              {
                  Outcome = AnswerOutcome.Verified,
                  Candidates = passages,
                  SearchQueries = queries
              };
    }

    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();
}
