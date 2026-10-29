using System.Net;
using System.Text;
using System.Text.Json;
using BadWolfAIPlayground.Search;

var tests = new (string Name, Func<Task> Check)[]
{
    ("Chunk boundaries and overlap", TestChunking),
    ("Keyword search", TestKeyword),
    ("Multilingual semantic ranking", TestSemantic),
    ("Embedding validation", TestValidation),
    ("Cancellation", TestCancellation),
    ("Ollama API protocol", TestOllamaProtocol),
    ("Large text file imports and safety limits", TestFileLoader),
    ("Explicit legacy encodings and readable import errors", TestTextEncodings),
    ("Incremental embedding batches and progress", TestLargeIndex),
    ("Oversized index protection", TestIndexLimit),
    ("Resumable, validated on-disk embedding checkpoints", TestResumableIndex),
    ("Ollama model digest validation", TestOllamaFingerprint),
    ("Grounded answers and verbatim citations", TestGroundedAnswers),
    ("Local Ollama Q&A protocol", TestOllamaAnswerProtocol),
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
    Assert(answer.Supported && answer.Citations.Count == 1 &&
           answer.Citations[0].Chunk.DocumentName == "Genesis.txt" &&
           answer.Citations[0].Quote.Contains("плоду", StringComparison.Ordinal),
        "The answer must cite an exact passage from the selected document.");
    Assert(grounded.ReceivedPassages.Any(x => x.Chunk.DocumentName == "Genesis.txt"),
        "Retrieval should find a relevant passage even if the wording differs.");

    GroundedAnswer fabricated = await GroundedQuestionAnswering.AskAsync(
        index, vectors, new FakeAnswerGenerator(inventQuote: true), "Де Єва взяла яблуко?");
    Assert(!fabricated.Supported && fabricated.Citations.Count == 0 &&
           fabricated.Text.Contains("Не вдалося", StringComparison.Ordinal),
        "A model answer with an invented quote must not be displayed as verified.");

    GroundedAnswer missing = await GroundedQuestionAnswering.AskAsync(
        index, vectors, new FakeAnswerGenerator(refuse: true), "Хто побудував піраміди?");
    Assert(!missing.Supported,
        "Questions unsupported by the selected text must produce a clear no-answer state.");
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
               requestJson.RootElement.GetProperty("format").GetString() == "json",
            "The answer model and structured local response settings are missing.");

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

sealed class FakeAnswerGenerator(bool inventQuote = false, bool refuse = false) : ILocalAnswerGenerator
{
    public string ModelName => "fake-local-answer-model";
    public IReadOnlyList<EvidencePassage> ReceivedPassages { get; private set; } = [];

    public Task<IReadOnlyList<string>> ExpandSearchAsync(string question, CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<string>>(["жінка взяла плід дерева"]);

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
