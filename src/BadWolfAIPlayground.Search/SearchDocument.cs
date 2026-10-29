namespace BadWolfAIPlayground.Search;

/// <summary>A plain-text document. Only explicitly selected local files are indexed.</summary>
public sealed record SearchDocument(string Name, string Content, string? FullPath = null);

/// <summary>A bounded, potentially overlapping piece of a text document.</summary>
public sealed record SearchChunk(string DocumentName, string? FullPath, int Number, string Content);

public sealed record SearchHit(SearchChunk Chunk, double Score);

public sealed record IndexProgress(int Completed, int Total);

/// <summary>Progress of a cancellable local file import, measured in bytes.</summary>
public sealed record FileImportProgress(long BytesRead, long TotalBytes, int FilesCompleted, int TotalFiles, string CurrentFile);

/// <summary>An immutable semantic index. Its scores are cosine similarities, NOT probabilities.</summary>
public sealed class SemanticIndex
{
    public IReadOnlyList<SearchChunk> Chunks { get; }
    internal IReadOnlyList<float[]> Vectors { get; }
    public string ModelName { get; }

    internal SemanticIndex(IReadOnlyList<SearchChunk> chunks, IReadOnlyList<float[]> vectors, string modelName)
    {
        Chunks = chunks;
        Vectors = vectors;
        ModelName = modelName;
    }
}

public interface IEmbeddingProvider
{
    string ModelName { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token = default);
}
