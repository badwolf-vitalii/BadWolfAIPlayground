namespace BadWolfAIPlayground.Search;

/// <summary>Overlapping text windows with a line-break preference; never silently skips the tail.</summary>
public static class TextChunker
{
    // A larger chunk reduces Ollama calls for log and document collections while
    // remaining small enough for the local multilingual embedding model.
    public const int DefaultChunkSize = 1_800;
    public const int DefaultOverlap = 180;

    public static IReadOnlyList<SearchChunk> Split(
        IEnumerable<SearchDocument> documents,
        int chunkSize = DefaultChunkSize,
        int overlap = DefaultOverlap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (chunkSize < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be at least 100 characters.");
        }

        if (overlap < 0 || overlap >= chunkSize / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be less than half a chunk.");
        }

        var chunks = new List<SearchChunk>();
        foreach (SearchDocument document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(document);
            if (string.IsNullOrWhiteSpace(document.Content))
            {
                continue;
            }

            string text = document.Content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            int start = 0;
            int part = 0;
            while (start < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = Math.Min(text.Length, start + chunkSize);
                if (end < text.Length)
                {
                    int boundary = text.LastIndexOf('\n', end - 1, end - start);
                    if (boundary >= start + chunkSize / 2)
                    {
                        end = boundary + 1;
                    }
                }

                string content = text[start..end].Trim();
                if (content.Length > 0)
                {
                    if (chunks.Count >= SemanticSearch.MaxChunks)
                    {
                        throw new InvalidDataException(
                            $"The documents need more than {SemanticSearch.MaxChunks:N0} chunks. Select fewer or smaller files.");
                    }
                    chunks.Add(new SearchChunk(document.Name, document.FullPath, ++part, content));
                }

                if (end == text.Length)
                {
                    break;
                }

                start = Math.Max(start + 1, end - overlap);
            }
        }

        return chunks;
    }
}
