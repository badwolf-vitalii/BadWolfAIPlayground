namespace BadWolfAIPlayground.Search;

/// <summary>Index text with a local embedding provider, then rank using cosine similarity.</summary>
public static class SemanticSearch
{
    public const int MaxChunks = 25_000;
    public const int BatchSize = 8;

    public static async Task<SemanticIndex> BuildAsync(
        IReadOnlyList<SearchChunk> chunks,
        IEmbeddingProvider provider,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(provider);
        if (chunks.Count == 0 || chunks.Count > MaxChunks)
        {
            throw new ArgumentException($"Select between 1 and {MaxChunks} text chunks.", nameof(chunks));
        }

        var vectors = new List<float[]>(chunks.Count);
        int? expectedDimension = null;
        for (int start = 0; start < chunks.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchCount = Math.Min(BatchSize, chunks.Count - start);
            var texts = new string[batchCount];
            for (int offset = 0; offset < batchCount; offset++)
            {
                SearchChunk chunk = chunks[start + offset];
                texts[offset] = $"title: {chunk.DocumentName} | text: {chunk.Content}";
            }
            IReadOnlyList<float[]> output = await provider.EmbedAsync(texts, cancellationToken)
                .ConfigureAwait(false);

            if (output.Count != batchCount)
            {
                throw new InvalidDataException("Embedding service returned an incorrect number of vectors.");
            }

            foreach (float[] embedding in output)
            {
                if (embedding is null || embedding.Length == 0 ||
                    (expectedDimension is not null && embedding.Length != expectedDimension.Value))
                {
                    throw new InvalidDataException("Embedding dimensions are missing or inconsistent.");
                }

                expectedDimension = embedding.Length;
                vectors.Add(Normalize(embedding));
            }

            progress?.Report(new IndexProgress(vectors.Count, chunks.Count));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SemanticIndex(chunks.ToArray(), vectors, provider.ModelName);
    }

    public static async Task<IReadOnlyList<SearchHit>> FindAsync(
        SemanticIndex index,
        IEmbeddingProvider provider,
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(query);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (!string.Equals(index.ModelName, provider.ModelName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The embedding model differs from the indexed model. Rebuild the index.");
        }

        IReadOnlyList<float[]> output = await provider.EmbedAsync(
            [$"task: search result | query: {query}"], cancellationToken).ConfigureAwait(false);
        if (output.Count != 1 || output[0] is null || output[0].Length != index.Vectors[0].Length)
        {
            throw new InvalidDataException("Query embedding dimensions do not match the index.");
        }

        float[] queryVector = Normalize(output[0]);
        return index.Chunks.Select((chunk, i) => new SearchHit(chunk, Dot(queryVector, index.Vectors[i])))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Chunk.DocumentName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static float[] Normalize(float[] embedding)
    {
        double sumSquares = 0;
        foreach (float value in embedding)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("An embedding contains NaN or infinity.");
            }
            sumSquares += (double)value * value;
        }

        if (sumSquares is <= 0 or double.PositiveInfinity)
        {
            throw new InvalidDataException("An embedding cannot be zero or infinite.");
        }

        double magnitude = Math.Sqrt(sumSquares);
        return embedding.Select(value => (float)(value / magnitude)).ToArray();
    }

    private static double Dot(float[] left, float[] right)
    {
        double total = 0;
        for (int i = 0; i < left.Length; i++)
        {
            total += (double)left[i] * right[i];
        }
        return Math.Clamp(total, -1, 1);
    }
}
