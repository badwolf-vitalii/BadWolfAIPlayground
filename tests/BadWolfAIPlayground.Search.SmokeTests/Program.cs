using System.Net;
using System.Text;
using System.Text.Json;
using BadWolfAIPlayground.Search;

var tests = new (string Name, Func<Task> Check)[]
{
    ("Chunk boundaries and overlap", TestChunking),
    ("Keyword search", TestKeyword),
    ("Whole-word search avoids Ukrainian question filler", TestWholeWordKeyword),
    ("Multilingual semantic ranking", TestSemantic),
    ("Embedding validation", TestValidation),
    ("Cancellation", TestCancellation),
    ("Ollama API protocol", TestOllamaProtocol),
    ("Large text file imports and safety limits", TestFileLoader),
    ("Explicit legacy encodings and readable import errors", TestTextEncodings),
    ("Fast document Q&A skips slow query-expansion call", TestFastAnswers),
    ("Adaptive cited answers and fallback retrieval", TestAdaptiveAnswering),
    ("Incremental embedding batches and progress", TestLargeIndex),
    ("Oversized index protection", TestIndexLimit),
    ("Resumable, validated on-disk embedding checkpoints", TestResumableIndex),
    ("Ollama model digest validation", TestOllamaFingerprint),
    ("Grounded answers and verbatim citations", TestGroundedAnswers),
    ("Hybrid retrieval retains exact source anchors", TestSourceAnchorRetrieval),
    ("Local Ollama Q&A protocol", TestOllamaAnswerProtocol),
    ("Bounded Q&A context and actionable context errors", TestOllamaAnswerContext),
    ("Local model catalog, CPU/GPU and chat timing", TestRuntimeCatalogAndMetrics),
};
int passed = 0;
foreach ((string name, Func<Task> check) in tests)
{
    try
    {
        await check();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
Console.WriteLine($"{passed}/{tests.Length} search checks passed.");
return passed == tests.Length ? 0 : 1;

static Task TestChunking()
{
    var original = new SearchDocument("long.txt", new string('a', 2_300));
    IReadOnlyList<SearchChunk> chunks = TextChunker.Split([original]);
    Assert(chunks.Count == 2, "Expected 2 overlapping windows after increasing the chunk size.");
    Assert(chunks.All(x => x.Content.Length <= TextChunker.DefaultChunkSize),
        "Chunks must not exceed the configured limit.");
    Assert(chunks[1].Content.StartsWith(new string('a', TextChunker.DefaultOverlap), StringComparison.Ordinal),
        "Overlap is missing.");
    Assert(chunks[^1].Content.Length > 0, "The end of the document was lost.");
    Assert(TextChunker.Split([new SearchDocument("blank", "  ")]).Count == 0, "Blank files must be ignored.");
    return Task.CompletedTask;
}

static Task TestKeyword()
{
    var chunks = TextChunker.Split(DemoDocuments.Create());
    IReadOnlyList<SearchHit> hits = KeywordSearch.Find(chunks, "SQLite locked");
    Assert(hits.Count > 0 && hits[0].Chunk.DocumentName == "database_errors.txt", "Keyword ranking mismatch.");
    Assert(KeywordSearch.Find(chunks, "несумісний_запит").Count == 0, "Keyword search should not translate.");
    return Task.CompletedTask;
}

static Task TestWholeWordKeyword()
{
    SearchChunk[] text =
    [
        new("Genesis.txt", null, 1, "І побачила жінка, що дерево добре на їжу, і взяла з його плоду."),
        new("Other.txt", null, 2, "Де де де, серед дерева, де відбувається історія."),
        new("Other.txt", null, 3, "Залишається навколо дерева, серед людей і дещо інше.")
    ];

    IReadOnlyList<SearchHit> matches = KeywordSearch.Find(text, "Де жінка взяла яблуко?");
    Assert(matches.Count > 0 && matches[0].Chunk.DocumentName == "Genesis.txt" &&
           matches[0].Score == 2,
        "Content words (жінка, взяла) must outweigh repeated filler 'де'.");
    Assert(matches.All(x => x.Chunk.DocumentName != "Other.txt"),
        "Filler words should not dominate a meaningful multi-word question.");

    IReadOnlyList<SearchHit> shortQuery = KeywordSearch.Find(
        [new SearchChunk("false.txt", null, 1, "Дерево має плоди.")], "де");
    Assert(shortQuery.Count == 0,
        "Whole-word search must never count 'де' embedded within 'дерево'.");
    return Task.CompletedTask;
}

static async Task TestSemantic()
{
    IReadOnlyList<SearchChunk> chunks = TextChunker.Split(DemoDocuments.Create());
    var provider = new FakeEmbeddings();
    SemanticIndex index = await SemanticSearch.BuildAsync(chunks, provider);
    IReadOnlyList<SearchHit> hits = await SemanticSearch.FindAsync(index, provider, "Чому база даних заблокована?");
    Assert(hits.Count > 0 && hits[0].Chunk.DocumentName == "database_errors.txt", "Cross-language ranking should find SQLite.");
    Assert(hits[0].Score > 0.9, "Expected high cosine similarity in the deterministic test.");
    Assert(provider.Queries.Any(x => x.StartsWith("task: search result | query:", StringComparison.Ordinal)), "Query retrieval prefix is missing.");
    Assert(provider.Queries.Any(x => x.StartsWith("title: database_errors.txt | text:", StringComparison.Ordinal)), "Document retrieval prefix is missing.");
}

static async Task TestValidation()
{
    IReadOnlyList<SearchChunk> chunks = TextChunker.Split([new SearchDocument("x", "a database error")]);
    await AssertThrowsAsync<InvalidDataException>(() => SemanticSearch.BuildAsync(chunks, new FaultyEmbeddingProvider()));
    Assert(KeywordSearch.Find(chunks, "", 3).Count == 0, "Empty keyword queries should return zero results.");
}

static async Task TestCancellation()
{
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    IReadOnlyList<SearchChunk> chunks = TextChunker.Split([new SearchDocument("x", "a database error")]);
    await AssertThrowsAsync<OperationCanceledException>(() => SemanticSearch.BuildAsync(chunks, new FakeEmbeddings(), cancellationToken: cancel.Token));
}

static async Task TestOllamaProtocol()
{
    string? requestJson = null;
    var handler = new StubHandler(async (request, token) =>
    {
        requestJson = await request.Content!.ReadAsStringAsync(token);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"embeddings\":[[0.5,0.1],[0.1,0.8]]}", Encoding.UTF8, "application/json")
        };
    });
    using var http = new HttpClient(handler) { BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint) };
    using var provider = new OllamaEmbeddingProvider(client: http);
    IReadOnlyList<float[]> values = await provider.EmbedAsync(["hello", "world"]);
    Assert(values.Count == 2 && values[0].Length == 2, "Unexpected API embedding shape.");
    Assert(requestJson?.Contains("embeddinggemma", StringComparison.Ordinal) == true, "Model name missing from request.");
    Assert(requestJson?.Contains("hello", StringComparison.Ordinal) == true, "Embedding input missing.");
}

static async Task TestFileLoader()
{
    string folder = Path.Combine(Path.GetTempPath(), $"badwolf-search-{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    string path = Path.Combine(folder, "large.txt");
    string other = Path.Combine(folder, "other.txt");
    try
    {
        const string marker = "UNIQUE_DATABASE_ERROR_TAIL_MARKER";
        // The requested real-world use case: a roughly 3.5 MiB TXT file.
        string content = new string('x', 3_670_016) + "\n" + marker + "\n";
        await File.WriteAllTextAsync(path, content);
        var measurements = new List<FileImportProgress>();
        var progress = new InlineProgress<FileImportProgress>(x => measurements.Add(x));
        IReadOnlyList<SearchDocument> loaded = await LocalTextLoader.LoadAsync([path], progress: progress);
        Assert(loaded.Count == 1 && loaded[0].Content == content,
            "The multi-megabyte import changed the text or dropped the tail.");
        Assert(measurements.Count > 2 && measurements[^1].FilesCompleted == 1 &&
               measurements[^1].BytesRead == measurements[^1].TotalBytes,
            "Block-by-block import must report completion in bytes.");
        Assert(measurements.Zip(measurements.Skip(1)).All(pair => pair.First.BytesRead <= pair.Second.BytesRead),
            "Import progress must never go backwards.");

        IReadOnlyList<SearchChunk> chunks = TextChunker.Split(loaded);
        Assert(chunks.Count > 260 && chunks[^1].Content.Contains(marker, StringComparison.Ordinal),
            "Large file splitting must include all text, including the final chunk.");
        Assert(KeywordSearch.Find(chunks, marker).Count > 0,
            "Keyword search should find a marker near the end of a large imported file.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => LocalTextLoader.LoadAsync([path], cancelled.Token));
        await AssertThrowsAsync<OperationCanceledException>(() =>
            Task.Run(() => TextChunker.Split(loaded, cancellationToken: cancelled.Token)));

        // Sparse files make safety limit checks fast, without allocating huge strings.
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(LocalTextLoader.MaxFileBytes + 1);
        }
        await AssertThrowsAsync<InvalidDataException>(() => LocalTextLoader.LoadAsync([path]));

        await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
        await AssertThrowsAsync<InvalidDataException>(() => LocalTextLoader.LoadAsync([path]));

        // Cumulative limits must be checked before attempting to read a single file.
        string[] paths = [path, other, Path.Combine(folder, "third.txt")];
        foreach (string file in paths)
        {
            await using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
            stream.SetLength(LocalTextLoader.MaxFileBytes);
        }
        await AssertThrowsAsync<InvalidDataException>(() => LocalTextLoader.LoadAsync(paths));
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
}

static async Task TestTextEncodings()
{
    string folder = Path.Combine(Path.GetTempPath(), $"badwolf-text-encoding-{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    string file = Path.Combine(folder, "legacy.txt");
    try
    {
        const string cyrillic = "Помилка запису в базу даних. З'єднання закрито.";
        byte[] cp1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!.GetBytes(cyrillic);
        await File.WriteAllBytesAsync(file, cp1251);
        InvalidDataException wrongEncoding = await CaptureAsync<InvalidDataException>(
            () => LocalTextLoader.LoadAsync([file]));
        Assert(wrongEncoding.Message.Contains("legacy.txt", StringComparison.Ordinal) &&
               wrongEncoding.Message.Contains("Windows-1251", StringComparison.Ordinal),
            "Default decoding errors must name the file and explain how to retry in a different encoding.");

        IReadOnlyList<SearchDocument> legacy = await LocalTextLoader.LoadAsync(
            [file], fileEncoding: TextFileEncoding.Windows1251);
        Assert(legacy.Count == 1 && legacy[0].Content == cyrillic,
            "Explicit Windows-1251 must preserve Ukrainian text without replacing characters.");
        Assert(KeywordSearch.Find(TextChunker.Split(legacy), "Помилка").Count > 0,
            "Decoded Cyrillic must be searchable.");

        const string western = "Errore di comunicazione: città, perché, già.";
        byte[] cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(western);
        await File.WriteAllBytesAsync(file, cp1252);
        IReadOnlyList<SearchDocument> westernText = await LocalTextLoader.LoadAsync(
            [file], fileEncoding: TextFileEncoding.Windows1252);
        Assert(westernText[0].Content == western,
            "Explicit Windows-1252 must preserve accented Italian text.");

        const string unicode = "No BOM - документ UTF16 без заголовка";
        await File.WriteAllBytesAsync(file, Encoding.Unicode.GetBytes(unicode));
        InvalidDataException noBom = await CaptureAsync<InvalidDataException>(
            () => LocalTextLoader.LoadAsync([file]));
        Assert(noBom.Message.Contains("UTF-16", StringComparison.Ordinal),
            "BOM-less UTF-16 errors must recommend the correct encoding override.");
        IReadOnlyList<SearchDocument> utf16 = await LocalTextLoader.LoadAsync(
            [file], fileEncoding: TextFileEncoding.Utf16LittleEndian);
        Assert(utf16[0].Content == unicode, "BOM-less UTF-16LE decoding failed.");
        await File.WriteAllBytesAsync(file, Encoding.BigEndianUnicode.GetBytes(unicode));
        IReadOnlyList<SearchDocument> utf16be = await LocalTextLoader.LoadAsync(
            [file], fileEncoding: TextFileEncoding.Utf16BigEndian);
        Assert(utf16be[0].Content == unicode, "BOM-less UTF-16BE decoding failed.");

        await File.WriteAllTextAsync(file, "Valid UTF-8 text — тест.");
        IReadOnlyList<SearchDocument> normal = await LocalTextLoader.LoadAsync([file]);
        Assert(normal[0].Content == "Valid UTF-8 text — тест.",
            "Auto detection must continue to read ordinary UTF-8.");

        // Regression from an actual WPF screenshot: the user retained a
        // Windows-1251 selection when importing a UTF-8 Ukrainian book.
        const string fox = "Жив собі Лис Микита, і вся лісова громада знала його ім'я. ";
        await File.WriteAllTextAsync(file, string.Concat(Enumerable.Repeat(fox, 40)),
            new UTF8Encoding(false));
        InvalidDataException wrongLegacy = await CaptureAsync<InvalidDataException>(
            () => LocalTextLoader.LoadAsync([file], fileEncoding: TextFileEncoding.Windows1251));
        Assert(wrongLegacy.Message.Contains("valid UTF-8", StringComparison.OrdinalIgnoreCase) &&
               wrongLegacy.Message.Contains("Auto", StringComparison.Ordinal),
            "UTF-8 imported as Windows-1251 must be rejected before indexing mojibake.");
        IReadOnlyList<SearchDocument> fixedFox = await LocalTextLoader.LoadAsync([file]);
        Assert(fixedFox[0].Content.StartsWith("Жив собі Лис Микита", StringComparison.Ordinal),
            "Auto must correctly decode the original Ukrainian characters.");

        // Even when the broken text was already saved to disk as UTF-8,
        // visible mojibake must not be silently indexed.
        string corrupted = CodePagesEncodingProvider.Instance.GetEncoding(1251)!
            .GetString(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(fox, 40))));
        await File.WriteAllTextAsync(file, corrupted, new UTF8Encoding(false));
        InvalidDataException doubleCorrupted = await CaptureAsync<InvalidDataException>(
            () => LocalTextLoader.LoadAsync([file]));
        Assert(doubleCorrupted.Message.Contains("garbled", StringComparison.OrdinalIgnoreCase),
            "Repeated UTF-8/code-page corruption patterns must be rejected.");
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
}

static async Task<T> CaptureAsync<T>(Func<Task> operation) where T : Exception
{
    try
    {
        await operation();
    }
    catch (T error)
    {
        return error;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name} to capture.");
}

static async Task TestLargeIndex()
{
    var sample = new SearchChunk("log.txt", null, 1, "Database write was blocked.");
    SearchChunk[] chunks = Enumerable.Repeat(sample, 49).ToArray();
    var embeddings = new FakeEmbeddings();
    var updates = new List<IndexProgress>();
    SemanticIndex index = await SemanticSearch.BuildAsync(
        chunks, embeddings, new InlineProgress<IndexProgress>(step => updates.Add(step)));
    Assert(index.Chunks.Count == 49 && embeddings.Batches.Count == 7,
        "49 items must be embedded in seven bounded batches, not one giant HTTP request.");
    Assert(embeddings.Batches.Max() <= SemanticSearch.BatchSize &&
           embeddings.Batches[^1] == 1, "Unexpected embedding batch size.");
    Assert(updates.Count == 7 && updates[^1].Completed == 49 && updates[^1].Total == 49,
        "Index progress must include the final completed batch.");
}

static async Task TestIndexLimit()
{
    var sample = new SearchChunk("log.txt", null, 1, "test");
    SearchChunk[] tooMany = Enumerable.Repeat(sample, SemanticSearch.MaxChunks + 1).ToArray();
    await AssertThrowsAsync<ArgumentException>(() => SemanticSearch.BuildAsync(tooMany, new FakeEmbeddings()));
}

static void Assert(bool condition, string reason)
{
    if (!condition)
    {
        throw new InvalidOperationException(reason);
    }
}

static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task TestResumableIndex()
{
    string root = Path.Combine(Path.GetTempPath(), "badwolf-checkpoints-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new SearchIndexCheckpointStore(root);
        SearchChunk[] chunks = Enumerable.Range(0, 25)
            .Select(i => new SearchChunk("large.txt", @"C:\data\large.txt", i + 1,
                "Data fragment " + i)).ToArray();

        var interrupted = new CheckpointEmbeddings("model-digest-A", stopAfterBatches: 2);
        await AssertThrowsAsync<OperationCanceledException>(() =>
            SemanticSearch.BuildAsync(chunks, interrupted, checkpoints: store));
        Assert(interrupted.Calls == 3, "Test must interrupt after exactly two persisted batches.");

        var restarted = new CheckpointEmbeddings("model-digest-A");
        var updates = new List<IndexProgress>();
        SemanticIndex result = await SemanticSearch.BuildAsync(chunks, restarted,
            new InlineProgress<IndexProgress>(update => updates.Add(update)),
            checkpoints: store);
        Assert(result.Chunks.Count == 25 && result.ReusedChunks == 16 &&
               restarted.Calls == 2,
            "Restart must reuse the first two batches and only embed the remaining nine chunks.");
        Assert(updates.Count == 4 && updates[^1].Reused == 16,
            "Progress must include both restored and newly embedded chunks.");

        var completed = new CheckpointEmbeddings("model-digest-A");
        SemanticIndex restored = await SemanticSearch.BuildAsync(chunks, completed, checkpoints: store);
        Assert(completed.Calls == 0 && restored.ReusedChunks == chunks.Length,
            "A completed index must load from disk without embedding any documents.");

        var changedModel = new CheckpointEmbeddings("model-digest-B");
        await SemanticSearch.BuildAsync(chunks, changedModel, checkpoints: store);
        Assert(changedModel.Calls == 4,
            "A changed model digest must invalidate every cached vector.");

        SearchChunk[] changedDocument = chunks.ToArray();
        changedDocument[0] = changedDocument[0] with { Content = "modified content" };
        var modified = new CheckpointEmbeddings("model-digest-A");
        await SemanticSearch.BuildAsync(changedDocument, modified, checkpoints: store);
        Assert(modified.Calls == 4,
            "A changed document must invalidate the old cache key.");

        // Damaged or truncated batch files are treated as misses, never trusted.
        foreach (string path in Directory.GetFiles(root, "batch-000000.bin", SearchOption.AllDirectories))
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        }
        var afterCorruption = new CheckpointEmbeddings("model-digest-A");
        await SemanticSearch.BuildAsync(chunks, afterCorruption, checkpoints: store);
        Assert(afterCorruption.Calls == 1,
            "A corrupt batch must be re-embedded; valid batches must still be reused.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task TestOllamaFingerprint()
{
    var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"models\":[{\"name\":\"embeddinggemma:latest\",\"digest\":\"sha256-content-v1\"}]}",
            Encoding.UTF8, "application/json")
    }));
    using var client = new HttpClient(handler) { BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint) };
    using var ollama = new OllamaEmbeddingProvider(client: client);
    Assert(await ollama.GetModelFingerprintAsync() == "embeddinggemma@sha256-content-v1",
        "Cache keys must incorporate Ollama model content digests, not mutable tag names.");
}

static async Task TestGroundedAnswers()
{
    SearchChunk[] chunks =
    [
        new("Genesis.txt", null, 1,
            "І побачила жінка, що дерево добре на їжу, і взяла з його плоду, та й з'їла."),
        new("Other.txt", null, 1, "Сонце світить над полем. Птахи літають над рікою.")
    ];
    var vectors = new FakeEmbeddings();
    SemanticIndex index = await SemanticSearch.BuildAsync(chunks, vectors);

    var grounded = new FakeAnswerGenerator();
    GroundedAnswer answer = await GroundedQuestionAnswering.AskAsync(
        index, vectors, grounded, "Де Єва взяла яблуко?");
    Assert(answer.Supported && answer.Outcome == AnswerOutcome.Verified &&
           answer.Candidates.Count > 0 && answer.SearchQueries.Count > 0 &&
           answer.Citations.Count == 1 &&
           answer.Citations[0].Chunk.DocumentName == "Genesis.txt" &&
           answer.Citations[0].Quote.Contains("плоду", StringComparison.Ordinal),
        "The answer must cite an exact passage from the selected document.");
    Assert(grounded.ReceivedPassages.Any(x => x.Chunk.DocumentName == "Genesis.txt"),
        "Retrieval should find a relevant passage even if the wording differs.");

    GroundedAnswer fabricated = await GroundedQuestionAnswering.AskAsync(
        index, vectors, new FakeAnswerGenerator(inventQuote: true), "Де Єва взяла яблуко?");
    Assert(!fabricated.Supported &&
           fabricated.Outcome == AnswerOutcome.UnverifiableCitation &&
           fabricated.Candidates.Count > 0 && fabricated.Citations.Count == 0 &&
           fabricated.Text.Contains("Не вдалося", StringComparison.Ordinal),
        "A model answer with an invented quote must not be displayed as verified.");

    GroundedAnswer missing = await GroundedQuestionAnswering.AskAsync(
        index, vectors, new FakeAnswerGenerator(refuse: true), "Хто побудував піраміди?");
    Assert(!missing.Supported && missing.Outcome == AnswerOutcome.ModelDeclined &&
           missing.Candidates.Count > 0,
        "Model refusal must remain distinguishable from missing retrieval candidates.");
}

static async Task TestSourceAnchorRetrieval()
{
    // Dense search's top 24 is intentionally crowded by irrelevant chunks.
    // A rare word from the original question must still be sent to the
    // answer model even if semantic score favors boilerplate.
    var chunks = Enumerable.Range(1, 30)
        .Select(number => new SearchChunk("A-filler.txt", null, number,
            "Нічого суттєвого про запитання в цьому фрагменті немає."))
        .ToList();
    var relevant = new SearchChunk("Z-original.txt", null, 31,
        "І сказав змій до жінки: ваші очі розкриються, і станете ви, немов Боги, знаючи добро й зло. " +
        "І взяла вона з його плоду.");
    chunks.Add(relevant);
    var provider = new FakeEmbeddings();
    SemanticIndex index = await SemanticSearch.BuildAsync(chunks, provider);
    var generator = new FakeAnswerGenerator();

    GroundedAnswer answer = await GroundedQuestionAnswering.AskAsync(
        index, provider, generator, "Що запропонував Єві змій?");
    Assert(generator.ReceivedPassages.Any(p => p.Chunk == relevant) &&
           answer.Candidates.Any(p => p.Chunk == relevant),
        "An exact rare-word match from the original question must survive fusion.");
    Assert(answer.Candidates.Count <= GroundedQuestionAnswering.MaxPassages &&
           answer.SearchQueries.Count <= GroundedQuestionAnswering.MaxQueries,
        "Only a bounded, inspectable number of source passages may be shown.");

    foreach (EvidencePassage passage in answer.Candidates)
    {
        string sent = OllamaAnswerProvider.SelectExcerpt(passage.Chunk.Content, "Що запропонував Єві змій?");
        Assert(passage.Chunk.Content.Contains(sent, StringComparison.Ordinal) &&
               sent.Length <= OllamaAnswerProvider.MaxExcerptCharacters,
            "The inspector must show only text really present in the source and sent to the local model.");
    }
}

static async Task TestFastAnswers()
{
    SearchChunk[] chunks =
    [
        new("Fox.txt", null, 1,
            "У лісі жив Лис Микита. Всі звірі знали, що його звали Микита.")
    ];
    var embedding = new FakeEmbeddings();
    SemanticIndex index = await SemanticSearch.BuildAsync(chunks, embedding);

    var fast = new FakeAnswerGenerator(refuse: true);
    GroundedAnswer answer = await GroundedQuestionAnswering.AskAsync(
        index, embedding, fast, "Як звали лиса?");
    Assert(fast.ExpandCalls == 0 && fast.ReceivedPassages.Count > 0 &&
           answer.SearchQueries.Count == 1,
        "Fast document answers must use only one generation call (no query rewrite).");

    var deep = new FakeAnswerGenerator(refuse: true);
    GroundedAnswer expanded = await GroundedQuestionAnswering.AskAsync(
        index, embedding, deep, "Як звали лиса?", expandQueries: true);
    Assert(deep.ExpandCalls == 1 && expanded.SearchQueries.Count == 2,
        "The slower multi-query expansion must remain explicitly opt-in.");
}

static async Task TestAdaptiveAnswering()
{
    SearchChunk[] chunks = Enumerable.Range(1, 9)
        .Select(i => new SearchChunk("A-Fox.txt", null, i,
            (i == 1 ? "Лис Микита полюбляв хитрувати. " :
                      $"У лісі номер {i} відбувалася інша пригода. ") +
            new string('а', 1100)))
        .ToArray();
    var embeddings = new FakeEmbeddings();
    SemanticIndex index = await SemanticSearch.BuildAsync(chunks, embeddings);

    var immediate = new AdaptiveAnswerGenerator();
    GroundedAnswer fast = await GroundedQuestionAnswering.AskAsync(
        index, embeddings, immediate, "Як звали лиса?");
    Assert(fast.Supported && fast.AnswerAttempts == 1 &&
           fast.Candidates.Count == GroundedQuestionAnswering.InitialPassages &&
           fast.TotalPassagesSubmitted == GroundedQuestionAnswering.InitialPassages &&
           fast.AvailablePassages == GroundedQuestionAnswering.MaxPassages &&
           immediate.CallCount == 1 &&
           immediate.MaxPassageLengths.SequenceEqual(
               [GroundedQuestionAnswering.InitialExcerptCharacters]),
        "An immediate cited result must use one 3-passage prompt and never retry.");
    Assert(fast.ExcerptCharactersSubmitted < fast.FullContextCharacters &&
           fast.Citations.Count == 1 &&
           fast.Citations[0].Quote.Contains("Лис Микита", StringComparison.Ordinal),
        "Adaptive quick answer must preserve exact citations and accurately count reduced context.");

    var retry = new AdaptiveAnswerGenerator(refuseFirst: true);
    GroundedAnswer recovered = await GroundedQuestionAnswering.AskAsync(
        index, embeddings, retry, "Як звали лиса?");
    Assert(recovered.Supported && recovered.AnswerAttempts == 2 &&
           recovered.Candidates.Count == 7 && recovered.TotalPassagesSubmitted == 10 &&
           retry.CallCount == 2 && retry.MaxPassageLengths.SequenceEqual(
               [GroundedQuestionAnswering.InitialExcerptCharacters,
                GroundedQuestionAnswering.ExpandedExcerptCharacters]),
        "A declined first attempt must retry once with seven wider passages.");
    Assert(recovered.Citations[0].Id >= 1 && recovered.Citations[0].Id <= 7 &&
           recovered.ExcerptCharactersSubmitted > fast.ExcerptCharactersSubmitted,
        "Fallback must retain stable evidence IDs and report both submitted contexts.");

    var invented = new AdaptiveAnswerGenerator(inventQuote: true);
    GroundedAnswer noEvidence = await GroundedQuestionAnswering.AskAsync(
        index, embeddings, invented, "Як звали лиса?");
    Assert(!noEvidence.Supported && noEvidence.AnswerAttempts == 2 &&
           noEvidence.Outcome == AnswerOutcome.UnverifiableCitation &&
           noEvidence.Citations.Count == 0 && invented.CallCount == 2,
        "Never publish unsupported text; a fabricated quote must fail after one bounded retry.");

    using var cancel = new CancellationTokenSource();
    var stopAfterFirst = new AdaptiveAnswerGenerator(refuseFirst: true, cancelAfterFirst: cancel);
    await AssertThrowsAsync<OperationCanceledException>(() =>
        GroundedQuestionAnswering.AskAsync(
            index, embeddings, stopAfterFirst, "Як звали лиса?", cancel.Token));
    Assert(stopAfterFirst.CallCount == 1,
        "Cancellation must stop before launching an expensive fallback request.");

    OllamaChatMetrics first = new(4_000_000_000, 100_000_000, 900, 2_000_000_000,
        25, 1_000_000_000);
    OllamaChatMetrics second = new(6_000_000_000, 0, 1200, 3_000_000_000,
        35, 2_000_000_000);
    OllamaChatMetrics sum = OllamaChatMetrics.Sum([first, second])!;
    Assert(sum.PromptTokens == 2100 && sum.OutputTokens == 60 &&
           Math.Abs(sum.TokensPerSecond!.Value - 20) < 0.001,
        "Fallback metrics must include both real Ollama replies and use aggregate generation duration.");
    Assert(OllamaChatMetrics.Sum([]) is null &&
           OllamaChatMetrics.Sum([first, first with { PromptTokens = null }])!.PromptTokens is null,
        "Missing metrics must not silently become zero or invented token savings.");
}

static async Task TestRuntimeCatalogAndMetrics()
{
    var handler = new StubHandler((request, _) =>
    {
        string json = request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => """
                {"models":[
                  {"name":"embeddinggemma:latest","size":720000000},
                  {"name":"qwen2.5:0.5b-instruct","size":398000000},
                  {"name":"qwen3:4b-instruct","size":3900000000}
                ]}
                """,
            "/api/ps" => """
                {"models":[
                  {"name":"qwen3:4b-instruct","size":3900000000,
                   "size_vram":0,"context_length":8192},
                  {"name":"qwen2.5:0.5b-instruct","size":398000000,
                   "size_vram":398000000,"context_length":4096}
                ]}
                """,
            _ => throw new InvalidOperationException("Unexpected Ollama request")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    });
    using var http = new HttpClient(handler)
    {
        BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint)
    };
    using var runtime = new OllamaRuntimeClient(http);
    IReadOnlyList<LocalOllamaModel> models = await runtime.ListInstalledAsync();
    Assert(models.Count == 3 && models[0].Name == "qwen2.5:0.5b-instruct" &&
           models[0].Bytes == 398000000,
        "Model discovery must return actual locally installed names and sizes.");
    OllamaExecutionInfo cpu = await runtime.GetExecutionAsync("qwen3:4b-instruct");
    OllamaExecutionInfo gpu = await runtime.GetExecutionAsync("qwen2.5:0.5b-instruct");
    OllamaExecutionInfo unloaded = await runtime.GetExecutionAsync("qwen2.5:1.5b-instruct");
    Assert(cpu.Loaded && cpu.GpuPercent == 0 && cpu.Display.Contains("100% CPU") &&
           gpu.GpuPercent == 100 && gpu.Display.Contains("100% GPU") &&
           !unloaded.Loaded && unloaded.GpuPercent is null &&
           unloaded.Display.Contains("unknown", StringComparison.OrdinalIgnoreCase),
        "Processor display must come from /api/ps; an unloaded model must be unknown.");

    OllamaChatMetrics metrics = OllamaChatMetrics.FromResponse(
        JsonDocument.Parse("""{"total_duration":5000000000,"load_duration":1000000000,"prompt_eval_count":110,"prompt_eval_duration":2000000000,"eval_count":80,"eval_duration":2000000000}""").RootElement);
    Assert(metrics.PromptTokens == 110 && metrics.OutputTokens == 80 &&
           Math.Abs(metrics.TokensPerSecond!.Value - 40.0) < 0.0001,
        "Token speed must use output tokens divided by generation duration, not total request time.");
    OllamaChatMetrics unknown = OllamaChatMetrics.FromResponse(
        JsonDocument.Parse("{}").RootElement);
    Assert(unknown.TokensPerSecond is null,
        "Missing telemetry must be displayed as unknown, never as zero speed.");

    string? selectedModel = null;
    var chatHandler = new StubHandler(async (request, token) =>
    {
        string requestText = await request.Content!.ReadAsStringAsync(token);
        using JsonDocument requestJson = JsonDocument.Parse(requestText);
        selectedModel = requestJson.RootElement.GetProperty("model").GetString();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"message":{"role":"assistant","content":
                "{\"supported\":false,\"answer\":\"\",\"evidence\":[]}"},
                 "prompt_eval_count":40,"eval_count":20,"eval_duration":1000000000}
                """, Encoding.UTF8, "application/json")
        };
    });
    using var chatHttp = new HttpClient(chatHandler)
    {
        BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint)
    };
    using var answer = new OllamaAnswerProvider(client: chatHttp);
    answer.SelectModel("qwen2.5:0.5b-instruct");
    ProposedAnswer refusal = await answer.GenerateAsync("test",
    [
        new EvidencePassage(1, new SearchChunk("test.txt", null, 1, "Some sample evidence."), 0.5)
    ]);
    Assert(!refusal.Supported && selectedModel == "qwen2.5:0.5b-instruct" &&
           answer.LastResponseMetrics?.OutputTokens == 20 &&
           Math.Abs(answer.LastResponseMetrics.TokensPerSecond!.Value - 20) < 0.001,
        "Switching the answer model must change /api/chat model and capture exact generation metrics.");
}

static async Task TestOllamaAnswerProtocol()
{
    int requests = 0;
    var handler = new StubHandler(async (request, token) =>
    {
        Assert(request.RequestUri?.AbsolutePath == "/api/chat",
            "Answer generation must use the local Ollama chat endpoint.");
        string body = await request.Content!.ReadAsStringAsync(token);
        using JsonDocument requestJson = JsonDocument.Parse(body);
        Assert(requestJson.RootElement.GetProperty("model").GetString() == "qwen3:4b-instruct" &&
               requestJson.RootElement.GetProperty("stream").GetBoolean() == false &&
               requestJson.RootElement.GetProperty("format").GetString() == "json" &&
               requestJson.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32() ==
                   OllamaAnswerProvider.AnswerContextTokens,
            "The answer model, structured response, or explicit context window is missing.");

        string response = requests++ == 0
            ? "{\"queries\":[\"дерево плід Едем\"]}"
            : "{\"supported\":true,\"answer\":\"Взяла плід із дерева.\",\"evidence\":[{\"id\":1,\"quote\":\"взяла з його плоду\"}]}";
        string wrapper = JsonSerializer.Serialize(new { message = new { role = "assistant", content = response } });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wrapper, Encoding.UTF8, "application/json")
        };
    });

    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint)
    };
    using var localModel = new OllamaAnswerProvider(client: client);
    IReadOnlyList<string> expanded = await localModel.ExpandSearchAsync("Де Єва взяла яблуко?");
    Assert(expanded.Count == 1 && expanded[0].Contains("плід", StringComparison.Ordinal),
        "The model must suggest semantic search vocabulary.");

    ProposedAnswer generated = await localModel.GenerateAsync("Де?", [
        new EvidencePassage(1,
            new SearchChunk("Genesis.txt", null, 1, "взяла з його плоду"),
            0.5)]);
    Assert(generated.Supported && generated.Evidence.Count == 1 &&
           generated.Evidence[0].Quote == "взяла з його плоду" && requests == 2,
        "The structured answer and source quote must be parsed correctly.");
}

static async Task TestOllamaAnswerContext()
{
    int calls = 0;
    var handler = new StubHandler(async (request, token) =>
    {
        calls++;
        Assert(request.RequestUri?.AbsolutePath == "/api/chat",
            "Q&A must use local Ollama chat.");
        string body = await request.Content!.ReadAsStringAsync(token);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert(root.GetProperty("options").GetProperty("num_ctx").GetInt32() == 8192,
            "The local model must receive an explicit 8192-token context option.");
        string prompt = root.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
        Assert(prompt.Length < 8500 && prompt.Contains("[SOURCE 7]", StringComparison.Ordinal),
            "All seven source citations must remain present without overflowing the context.");
        Assert(prompt.Contains("взяла", StringComparison.Ordinal),
            "A question term located deep in the source must be preserved in the excerpt.");
        Assert(!prompt.Contains(new string('а', 1250), StringComparison.Ordinal),
            "Long source files must never be sent unbounded to the chat model.");

        string payload = JsonSerializer.Serialize(new
        {
            message = new
            {
                role = "assistant",
                content = "{\"supported\":false,\"answer\":\"\",\"evidence\":[]}"
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    });

    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint)
    };
    using var local = new OllamaAnswerProvider(client: client);
    const string source = "жінка взяла плід із дерева.";
    var excerpts = Enumerable.Range(1, 7)
        .Select(i => new EvidencePassage(i, new SearchChunk(
            "Genesis.txt", null, i, new string('а', 1300) + source + new string('б', 1200)), 0.4))
        .ToArray();

    ProposedAnswer output = await local.GenerateAsync("Де Єва взяла яблуко?", excerpts);
    Assert(!output.Supported && calls == 1,
        "A bounded context must allow the model to report insufficient evidence.");

    var contextErrorHandler = new StubHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"request (7068 tokens) exceeds the available context size (4096 tokens)\"}")
        }));
    using var errorClient = new HttpClient(contextErrorHandler)
    {
        BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint)
    };
    using var errorModel = new OllamaAnswerProvider(client: errorClient);
    InvalidOperationException problem = await CaptureAsync<InvalidOperationException>(
        () => errorModel.GenerateAsync("Де Єва взяла яблуко?", excerpts));
    Assert(problem.Message.Contains("context window", StringComparison.OrdinalIgnoreCase) &&
           !problem.Message.Contains("pull qwen", StringComparison.OrdinalIgnoreCase),
        "A context-size error must not claim the already-running model is missing.");
}

sealed class CheckpointEmbeddings(string fingerprint, int stopAfterBatches = int.MaxValue) : IEmbeddingProvider
{
    public string ModelName => "checkpoint-test";
    public int Calls { get; private set; }

    public Task<string> GetModelFingerprintAsync(CancellationToken token = default) =>
        Task.FromResult(fingerprint);

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Calls++;
        if (Calls > stopAfterBatches)
        {
            throw new OperationCanceledException();
        }

        return Task.FromResult<IReadOnlyList<float[]>>(
            inputs.Select(_ => new[] { 1f, 0f, 0f }).ToArray());
    }
}

sealed class AdaptiveAnswerGenerator(
    bool refuseFirst = false, bool inventQuote = false,
    CancellationTokenSource? cancelAfterFirst = null) : ILocalAnswerGenerator
{
    public string ModelName => "adaptive-test";
    public int CallCount { get; private set; }
    public List<int> MaxPassageLengths { get; } = [];

    public Task<IReadOnlyList<string>> ExpandSearchAsync(string question, CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<ProposedAnswer> GenerateAsync(
        string question, IReadOnlyList<EvidencePassage> passages, CancellationToken token = default)
    {
        CallCount++;
        MaxPassageLengths.Add(passages[0].ExcerptCharacters);
        if (CallCount == 1 && refuseFirst)
        {
            cancelAfterFirst?.Cancel();
            return Task.FromResult(new ProposedAnswer(false, "", []));
        }

        EvidencePassage? fox = passages.FirstOrDefault(p =>
            OllamaAnswerProvider.SelectExcerpt(
                p.Chunk.Content, question, p.ExcerptCharacters)
                .Contains("Лис Микита", StringComparison.Ordinal));
        if (fox is null)
        {
            return Task.FromResult(new ProposedAnswer(false, "", []));
        }

        return Task.FromResult(new ProposedAnswer(true, "Його звали Лис Микита.",
        [
            new ProposedEvidence(fox.Id,
                inventQuote ? "Вигадана цитата без слів із джерела" :
                              "Лис Микита полюбляв хитрувати.")
        ]));
    }
}

sealed class FakeAnswerGenerator(bool inventQuote = false, bool refuse = false) : ILocalAnswerGenerator
{
    public string ModelName => "fake-local-answer-model";
    public IReadOnlyList<EvidencePassage> ReceivedPassages { get; private set; } = [];
    public int ExpandCalls { get; private set; }

    public Task<IReadOnlyList<string>> ExpandSearchAsync(string question, CancellationToken token = default)
    {
        ExpandCalls++;
        return Task.FromResult<IReadOnlyList<string>>(["жінка взяла плід дерева"]);
    }

    public Task<ProposedAnswer> GenerateAsync(
        string question, IReadOnlyList<EvidencePassage> passages, CancellationToken token = default)
    {
        ReceivedPassages = passages;
        EvidencePassage? relevant = passages.FirstOrDefault(
            p => p.Chunk.Content.Contains("плоду", StringComparison.Ordinal));
        if (refuse || relevant is null)
        {
            return Task.FromResult(new ProposedAnswer(false, "", []));
        }

        return Task.FromResult(new ProposedAnswer(true, "Вона взяла плід із дерева.",
        [
            new ProposedEvidence(relevant.Id, inventQuote
                ? "вигадана цитата з відсутнього документа"
                : "взяла з його плоду")
        ]));
    }
}

sealed class FakeEmbeddings : IEmbeddingProvider
{
    public string ModelName => "test-vectors";
    public List<string> Queries { get; } = [];
    public List<int> Batches { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Batches.Add(inputs.Count);
        var output = new List<float[]>();
        foreach (string input in inputs)
        {
            Queries.Add(input);
            bool database = input.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("база даних", StringComparison.OrdinalIgnoreCase);
            output.Add(database ? [1, 0, 0] : [0, 1, 0]);
        }
        return Task.FromResult<IReadOnlyList<float[]>>(output);
    }
}

sealed class FaultyEmbeddingProvider : IEmbeddingProvider
{
    public string ModelName => "invalid";
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<float[]>>([new float[] { float.NaN, 0 }]);
}

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        action(request, cancellationToken);
}

sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
