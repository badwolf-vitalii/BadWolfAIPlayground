using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Search;

/// <summary>A verbatim excerpt the user can check against a selected source document.</summary>
public sealed record EvidencePassage(int Id, SearchChunk Chunk, double SearchScore)
{
    // The verifier and the evidence inspector must use the exact same excerpt
    // length as the local chat request, including the compact first pass.
    public int ExcerptCharacters { get; init; } = OllamaAnswerProvider.MaxExcerptCharacters;
}

/// <summary>A model-authored answer is not trusted until its quotes match source text.</summary>
public sealed record ProposedEvidence(int Id, string Quote);
public sealed record ProposedAnswer(bool Supported, string Answer, IReadOnlyList<ProposedEvidence> Evidence);

public sealed record VerifiedCitation(int Id, SearchChunk Chunk, string Quote);
public enum AnswerOutcome
{
    Verified,
    NoCandidates,
    ModelDeclined,
    UnverifiableCitation,
    UnsubstantiatedAnswer
}

public sealed record GroundedAnswer(
    string Text, bool Supported, IReadOnlyList<VerifiedCitation> Citations, int ExaminedPassages)
{
    // An unsupported answer must still reveal the passages the local model read.
    public AnswerOutcome Outcome { get; init; } = AnswerOutcome.ModelDeclined;
    public IReadOnlyList<EvidencePassage> Candidates { get; init; } = [];
    public IReadOnlyList<string> SearchQueries { get; init; } = [];
    public TimeSpan RetrievalDuration { get; init; }
    public TimeSpan GenerationDuration { get; init; }
    public int AnswerAttempts { get; init; }
    public int AvailablePassages { get; init; }
    public int TotalPassagesSubmitted { get; init; }
    public int ExcerptCharactersSubmitted { get; init; }
    public int FullContextCharacters { get; init; }
    public IReadOnlyList<string> UnsupportedAnswerTerms { get; init; } = [];
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
    public const int InitialPassages = 3;
    public const int InitialExcerptCharacters = 480;
    public const int ExpandedExcerptCharacters = 700;
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
        var timer = Stopwatch.StartNew();

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
        void AddDistinct(IEnumerable<SearchChunk> source, int maxNewItems)
        {
            int added = 0;
            foreach (SearchChunk chunk in source)
            {
                if (selected.Count >= MaxPassages || added >= maxNewItems)
                {
                    return;
                }
                if (!selected.Contains(chunk))
                {
                    selected.Add(chunk);
                    added++;
                }
            }
        }

        // The first three contain a lexical anchor, an original-query dense
        // candidate and the best remaining fused candidate when available.
        // This avoids blindly sending seven long pieces to a CPU-only model.
        AddDistinct(originalLexical.Select(hit => hit.Chunk), 1);
        AddDistinct(originalSemantic.Select(hit => hit.Chunk), 1);
        IEnumerable<SearchChunk> blended = fused
            .OrderByDescending(pair => pair.Value.Rank)
            .ThenByDescending(pair => pair.Value.Similarity)
            .Select(pair => pair.Key)
            .ToArray();
        AddDistinct(blended, 1);
        AddDistinct(originalLexical.Select(hit => hit.Chunk), 1);
        AddDistinct(originalSemantic.Select(hit => hit.Chunk), 1);
        AddDistinct(blended, MaxPassages);

        EvidencePassage[] passages = selected
            .Select((chunk, position) =>
                new EvidencePassage(position + 1, chunk, fused[chunk].Similarity))
            .ToArray();

        TimeSpan retrievalDuration = timer.Elapsed;
        if (passages.Length == 0)
        {
            return new GroundedAnswer(NotFound, false, [], 0)
            {
                Outcome = AnswerOutcome.NoCandidates,
                SearchQueries = queries,
                RetrievalDuration = retrievalDuration
            };
        }

        // Compute an exact character baseline (NOT a token or elapsed-time
        // estimate) for what our former seven-passage request would contain.
        int fullContextCharacters = passages.Sum(passage =>
            OllamaAnswerProvider.SelectExcerpt(passage.Chunk.Content, question).Length);

        EvidencePassage[] submitted = passages.Take(InitialPassages)
            .Select(passage => passage with { ExcerptCharacters = InitialExcerptCharacters })
            .ToArray();
        int submittedCharacters = 0;
        int submittedPassages = 0;
        int attempts = 0;
        ProposedAnswer proposed;
        VerifiedCitation[] valid;
        AnswerEvidenceValidator.Assessment corroboration =
            new([], []);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            submittedPassages += submitted.Length;
            submittedCharacters += submitted.Sum(passage =>
                OllamaAnswerProvider.SelectExcerpt(
                    passage.Chunk.Content, question, passage.ExcerptCharacters).Length);

            proposed = await generator.GenerateAsync(question, submitted, cancellationToken)
                .ConfigureAwait(false);
            VerifiedCitation[] exactQuotes = ValidateEvidence(
                proposed, submitted, question, cancellationToken);
            corroboration = AnswerEvidenceValidator.Check(
                question, proposed.Answer, exactQuotes);
            valid = corroboration.Citations.ToArray();

            // Retry only when the larger request can supply more passages
            // OR additional actual source characters. With two long chunks,
            // expanding 480 -> 700 chars may still uncover the missing fact;
            // with two short chunks a second call would be wasted CPU time.
            bool hasMoreEvidence = submitted.Length < passages.Length ||
                submitted.Any(passage =>
                    OllamaAnswerProvider.SelectExcerpt(
                        passage.Chunk.Content, question, ExpandedExcerptCharacters).Length >
                    OllamaAnswerProvider.SelectExcerpt(
                        passage.Chunk.Content, question, passage.ExcerptCharacters).Length);
            if (valid.Length > 0 || attempts == 2 || !hasMoreEvidence)
            {
                break;
            }

            submitted = passages
                .Select(passage => passage with { ExcerptCharacters = ExpandedExcerptCharacters })
                .ToArray();
        }

        TimeSpan generationDuration = timer.Elapsed - retrievalDuration;
        AnswerOutcome outcome = valid.Length > 0
            ? AnswerOutcome.Verified
            : !proposed.Supported || string.IsNullOrWhiteSpace(proposed.Answer)
                ? AnswerOutcome.ModelDeclined
                : corroboration.MissingAnswerTerms.Count > 0
                    ? AnswerOutcome.UnsubstantiatedAnswer
                    : AnswerOutcome.UnverifiableCitation;

        return new GroundedAnswer(
            outcome == AnswerOutcome.Verified ? proposed.Answer.Trim() : NotFound,
            outcome == AnswerOutcome.Verified, valid, submitted.Length)
        {
            Outcome = outcome,
            Candidates = submitted,
            SearchQueries = queries,
            RetrievalDuration = retrievalDuration,
            GenerationDuration = generationDuration,
            AnswerAttempts = attempts,
            AvailablePassages = passages.Length,
            TotalPassagesSubmitted = submittedPassages,
            ExcerptCharactersSubmitted = submittedCharacters,
            FullContextCharacters = fullContextCharacters,
            UnsupportedAnswerTerms = corroboration.MissingAnswerTerms
        };
    }

    private static VerifiedCitation[] ValidateEvidence(
        ProposedAnswer proposed,
        IReadOnlyList<EvidencePassage> submitted,
        string question,
        CancellationToken cancellationToken)
    {
        if (!proposed.Supported || string.IsNullOrWhiteSpace(proposed.Answer) ||
            proposed.Evidence is null)
        {
            return [];
        }

        var verified = new List<VerifiedCitation>();
        foreach (ProposedEvidence proof in proposed.Evidence.Take(MaxPassages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvidencePassage? passage = submitted.FirstOrDefault(item => item.Id == proof.Id);
            string quote = proof.Quote?.Trim() ?? "";
            if (passage is null || quote.Length is < 8 or > 400)
            {
                continue;
            }

            // Check only content that was really supplied on this attempt.
            // A fabricated quote is never validated against hidden chunk text.
            string originalExcerpt = OllamaAnswerProvider.SelectExcerpt(
                passage.Chunk.Content, question, passage.ExcerptCharacters);
            if (!NormalizeWhitespace(originalExcerpt).Contains(
                NormalizeWhitespace(quote), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (verified.All(citation => citation.Id != proof.Id))
            {
                verified.Add(new VerifiedCitation(proof.Id, passage.Chunk, quote));
            }
        }

        return verified.ToArray();
    }

    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();
}
