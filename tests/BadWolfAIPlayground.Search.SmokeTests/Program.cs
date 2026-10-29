using System.Net;
using System.Text;
using BadWolfAIPlayground.Search;

var tests = new (string Name, Func<Task> Check)[]
{
    ("Chunk boundaries and overlap", TestChunking),
    ("Keyword search", TestKeyword),
    ("Multilingual semantic ranking", TestSemantic),
    ("Embedding validation", TestValidation),
    ("Cancellation", TestCancellation),
    ("Ollama API protocol", TestOllamaProtocol),
    ("Text file safety limits", TestFileLoader)
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
    Assert(chunks.Count == 3, "Expected 3 overlapping windows.");
    Assert(chunks.All(x => x.Content.Length <= 900), "Chunks must not exceed the configured limit.");
    Assert(chunks[1].Content.StartsWith(new string('a', 120), StringComparison.Ordinal), "Overlap is missing.");
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
    string path = Path.Combine(Path.GetTempPath(), $"badwolf-search-{Guid.NewGuid():N}.txt");
    try
    {
        await File.WriteAllTextAsync(path, "alpha\nbeta");
        var files = await LocalTextLoader.LoadAsync([path]);
        Assert(files.Count == 1 && files[0].Content == "alpha\nbeta", "Text import failed.");
        await File.WriteAllTextAsync(path, new string('x', LocalTextLoader.MaxFileBytes + 1));
        await AssertThrowsAsync<InvalidDataException>(() => LocalTextLoader.LoadAsync([path]));
    }
    finally
    {
        File.Delete(path);
    }
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

sealed class FakeEmbeddings : IEmbeddingProvider
{
    public string ModelName => "test-vectors";
    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
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
