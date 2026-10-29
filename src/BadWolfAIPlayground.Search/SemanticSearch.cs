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
        CancellationToken cancellationToken = default,
        SearchIndexCheckpointStore? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(provider);
        if (chunks.Count == 0 || chunks.Count > MaxChunks)
        {
            throw new ArgumentException($"Select between 1 and {MaxChunks} text chunks.", nameof(chunks));
        }

        SearchIndexCheckpointStore.Session? session = null;
        if (checkpoints is not null)
        {
            string fingerprint = await provider.GetModelFingerprintAsync(cancellationToken)
                .ConfigureAwait(false);
            session = await checkpoints.OpenAsync(chunks, fingerprint, cancellationToken)
                .ConfigureAwait(false);
        }

        var vectors = new List<float[]>(chunks.Count);
        int? expectedDimension = null;
        int reused = 0;
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
            float[][]? cached = session is null ? null :
                await session.TryReadBatchAsync(start, batchCount, cancellationToken)
                    .ConfigureAwait(false);
            if (cached is not null && expectedDimension is not null &&
                cached[0].Length != expectedDimension.Value)
            {
                cached = null;
            }

            float[][] normalized;
            if (cached is not null)
            {
                normalized = cached;
                reused += batchCount;
            }
            else
            {
                IReadOnlyList<float[]> output = await provider.EmbedAsync(texts, cancellationToken)
                    .ConfigureAwait(false);

                if (output.Count != batchCount)
                {
                    throw new InvalidDataException("Embedding service returned an incorrect number of vectors.");
                }

                normalized = new float[batchCount][];
                for (int index = 0; index < batchCount; index++)
                {
                    float[] embedding = output[index];
                    if (embedding is null || embedding.Length is <= 0 or > 4096 ||
                        (expectedDimension is not null && embedding.Length != expectedDimension.Value))
                    {
                        throw new InvalidDataException("Embedding dimensions are missing or inconsistent.");
                    }
                    normalized[index] = Normalize(embedding);
                    expectedDimension = embedding.Length;
                }

                // Persist the entire validated batch BEFORE reporting progress.
                // Incomplete writes never replace earlier completed checkpoints.
                if (session is not null)
                {
                    await session.SaveBatchAsync(start, normalized, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            expectedDimension ??= normalized[0].Length;
            vectors.AddRange(normalized);
            progress?.Report(new IndexProgress(vectors.Count, chunks.Count, reused));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SemanticIndex(chunks.ToArray(), vectors, provider.ModelName, reused);
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
