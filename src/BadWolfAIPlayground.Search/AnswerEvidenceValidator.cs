using System.Text;
using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Search;

/// <summary>
/// Cheap, conservative lexical corroboration AFTER a quote has been verified
/// as an exact substring of the excerpt actually sent to the local model.
/// This is not an entailment model: it rejects obvious irrelevant citations,
/// especially answers naming an entity or a number absent from the quotation.
/// </summary>
public static partial class AnswerEvidenceValidator
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "і", "й", "та", "або", "але", "а", "в", "у", "на", "за", "із", "з", "до", "від",
        "що", "це", "той", "ця", "те", "тих", "цей", "він", "вона", "вони", "воно",
        "його", "її", "їх", "свій", "свого", "була", "було", "був", "були", "є",
        "як", "де", "коли", "хто", "який", "яка", "яке", "які", "чи", "так", "ні",
        "the", "and", "or", "of", "a", "an", "to", "by", "in", "on", "at", "is", "was",
        "were", "it", "its", "he", "she", "they", "his", "her", "their", "with",
        "il", "lo", "la", "i", "gli", "le", "e", "di", "da", "del", "della",
        "un", "una", "uno", "era", "sono", "che", "chi", "come", "dove"
    };

    // Narrow nominal inflections: allow Микита/Микиту/Микитою without
    // matching a distinct word like "Микитан". Never use substring contains.
    private static readonly string[] Suffixes =
    [
        "", "а", "у", "и", "і", "я", "ю", "е", "ою", "ею",
        "ом", "ем", "ові", "еві", "ами", "ями", "ого", "ому"
    ];

    public sealed record Assessment(
        IReadOnlyList<VerifiedCitation> Citations,
        IReadOnlyList<string> MissingAnswerTerms);

    public static Assessment Check(
        string question, string answer, IReadOnlyList<VerifiedCitation> exactQuotes)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(exactQuotes);
        if (exactQuotes.Count == 0)
        {
            return new Assessment([], []);
        }

        var questionTerms = Split(question)
            .Select(word => word.Normalized)
            .ToArray();
        var answerTerms = Split(answer)
            .Where(word => word.Normalized.Length >= 3 && !StopWords.Contains(word.Normalized))
            .Where(word => !questionTerms.Any(term => Equivalent(term, word.Normalized)))
            .DistinctBy(word => word.Normalized)
            .ToArray();

        // If all answer words merely repeat the question, there is no
        // independently checkable fact. Do not mark a random quote verified.
        if (answerTerms.Length == 0)
        {
            return new Assessment([], []);
        }

        var quoteWords = exactQuotes
            .Select(citation => Split(citation.Quote)
                .Select(word => word.Normalized).ToArray())
            .ToArray();
        var matched = answerTerms
            .Where(term => quoteWords.Any(words => words.Any(word =>
                Equivalent(word, term.Normalized))))
            .ToArray();

        // Numbers and distinctive proper names are never optional:
        // "Микита" must appear in a cited passage when that is the answer.
        var required = answerTerms.Where(word => word.IsNumber ||
            (word.IsCapitalized && (word.Position > 0 || answerTerms.Length == 1)))
            .ToArray();

        int requiredCoverage = Math.Max(1, (answerTerms.Length + 1) / 2);
        bool supported = matched.Length >= requiredCoverage &&
            required.All(term => matched.Any(hit => Equivalent(hit.Normalized, term.Normalized)));

        if (!supported)
        {
            string[] missing = answerTerms
                .Where(term => matched.All(hit => !Equivalent(hit.Normalized, term.Normalized)))
                .Select(term => term.Original)
                .ToArray();
            return new Assessment([], missing);
        }

        // Discard filler citations even if some other quotation supports
        // the response. Every displayed quote must itself mention at least
        // one answer-specific term.
        var relevant = exactQuotes
            .Where((citation, i) => answerTerms.Any(term =>
                quoteWords[i].Any(word => Equivalent(word, term.Normalized))))
            .ToArray();

        return new Assessment(relevant, []);
    }

    /// <summary>
    /// Finds an answer-bearing sentence in original indexed text when the
    /// model copied a genuine but unrelated quote. This is lexical source
    /// matching, NOT an entailment check or a second LLM inference.
    /// </summary>
    public static VerifiedCitation? LocateSupportingQuote(
        string question, string answer,
        IReadOnlyList<EvidencePassage> submitted,
        IReadOnlyList<SearchChunk> indexedChunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(indexedChunks);

        Word[] answerTerms = GetDistinctiveTerms(question, answer);
        Word[] questionTerms = Split(question)
            .Where(word => word.Normalized.Length >= 3 &&
                           !StopWords.Contains(word.Normalized))
            .ToArray();
        if (answerTerms.Length == 0 || questionTerms.Length == 0)
        {
            return null;
        }

        var candidates = new List<(SearchChunk Chunk, int Id, int Priority)>();
        var seen = new HashSet<SearchChunk>();
        foreach (EvidencePassage passage in submitted)
        {
            if (seen.Add(passage.Chunk))
            {
                candidates.Add((passage.Chunk, passage.Id, 2));
            }
        }

        // Read local imported text: no new embeddings, no network call,
        // and no request to Qwen. Scan at most 24 additional chunks.
        string lookup = string.Join(" ", answerTerms.Select(term => term.Original));
        IReadOnlyList<SearchHit> matches = KeywordSearch.Find(
            indexedChunks, lookup, limit: 24, cancellationToken: cancellationToken);
        foreach (SearchHit hit in matches)
        {
            if (seen.Add(hit.Chunk))
            {
                candidates.Add((hit.Chunk, 0, 0));
            }
        }

        VerifiedCitation? best = null;
        int bestScore = -1;
        foreach ((SearchChunk chunk, int id, int priority) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in Sentences().Matches(chunk.Content))
            {
                string sentence = match.Value.Trim();
                if (sentence.Length < 8)
                {
                    continue;
                }

                if (sentence.Length > 400)
                {
                    Match? anchor = Words().Matches(sentence).FirstOrDefault(word =>
                        answerTerms.Any(term =>
                            Equivalent(word.Value.ToLowerInvariant(), term.Normalized)));
                    if (anchor is null)
                    {
                        continue;
                    }

                    int start = Math.Clamp(anchor.Index - 90, 0, sentence.Length - 400);
                    sentence = sentence.Substring(start, 400);
                }

                var citation = new VerifiedCitation(id, chunk, sentence)
                {
                    LocatedInSource = true
                };
                if (Check(question, answer, [citation]).Citations.Count == 0)
                {
                    continue;
                }

                string[] quoteWords = Split(sentence)
                    .Select(word => word.Normalized).ToArray();
                int questionOverlap = questionTerms.Count(term => quoteWords.Any(word =>
                    RelatedToQuestion(word, term.Normalized)));
                if (questionOverlap == 0)
                {
                    // A random appearance of the name elsewhere in the book
                    // is not enough to substantiate a question about the fox.
                    continue;
                }

                int answerOverlap = answerTerms.Count(term => quoteWords.Any(word =>
                    Equivalent(word, term.Normalized)));
                int score = questionOverlap * 10 + answerOverlap * 5 + priority;
                if (score > bestScore)
                {
                    best = citation;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    private static Word[] GetDistinctiveTerms(string question, string answer)
    {
        string[] questionTerms = Split(question).Select(word => word.Normalized).ToArray();
        return Split(answer)
            .Where(word => word.Normalized.Length >= 3 &&
                           !StopWords.Contains(word.Normalized))
            .Where(word => !questionTerms.Any(query =>
                Equivalent(query, word.Normalized)))
            .DistinctBy(word => word.Normalized).ToArray();
    }

    // Prefix matching is allowed ONLY to locate the question's subject.
    // It never verifies named entities or numbers in the answer.
    private static bool RelatedToQuestion(string sourceTerm, string questionTerm)
    {
        if (Equivalent(sourceTerm, questionTerm))
        {
            return true;
        }

        int min = Math.Min(sourceTerm.Length, questionTerm.Length);
        int length = min >= 5 ? 4 : 3;
        return min >= 3 && sourceTerm.AsSpan(0, length)
            .SequenceEqual(questionTerm.AsSpan(0, length));
    }

    private static IReadOnlyList<Word> Split(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormC);
        return Words().Matches(normalized)
            .Select((match, position) => new Word(
                match.Value,
                match.Value.ToLowerInvariant(),
                position,
                char.IsUpper(match.Value[0]),
                match.Value.All(char.IsDigit)))
            .ToArray();
    }

    private static bool Equivalent(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (a.Length < 5 || b.Length < 5)
        {
            return false;
        }

        foreach (string aSuffix in Suffixes)
        {
            if (!a.EndsWith(aSuffix, StringComparison.Ordinal))
            {
                continue;
            }
            string aRoot = a[..(a.Length - aSuffix.Length)];
            if (aRoot.Length < 4)
            {
                continue;
            }

            foreach (string bSuffix in Suffixes)
            {
                if (b.EndsWith(bSuffix, StringComparison.Ordinal) &&
                    string.Equals(aRoot, b[..(b.Length - bSuffix.Length)],
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record Word(
        string Original, string Normalized, int Position,
        bool IsCapitalized, bool IsNumber);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    [GeneratedRegex(@"[^\r\n.!?;]+[.!?;]?", RegexOptions.CultureInvariant)]
    private static partial Regex Sentences();
}
