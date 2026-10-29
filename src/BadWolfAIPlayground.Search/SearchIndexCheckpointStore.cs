using System.Security.Cryptography;
using System.Text;

namespace BadWolfAIPlayground.Search;

/// <summary>
/// Stores only embedding vectors, never original document text, under LocalAppData.
/// Each completed batch is checksummed and atomically replaced so an interrupted
/// indexing process can continue without trusting partially written files.
/// </summary>
public sealed class SearchIndexCheckpointStore
{
    private const int FormatVersion = 1;
    private const int HeaderMagic = 0x42574149; // BWAI
    private const int MaxDimensions = 4096;
    private const int Sha256Bytes = 32;

    public string RootDirectory { get; }

    public SearchIndexCheckpointStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BadWolfAIPlayground", "SearchIndexes");
    }

    internal async Task<Session> OpenAsync(
        IReadOnlyList<SearchChunk> chunks, string modelFingerprint, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFingerprint);

        // Hash the exact retrieval inputs and model digest; source modification,
        // chunk boundaries or an updated local model invalidate prior vectors.
        string key = await Task.Run(() => ComputeKey(chunks, modelFingerprint, token), token)
            .ConfigureAwait(false);
        string path = Path.Combine(RootDirectory, key);
        Directory.CreateDirectory(path);
        return new Session(path);
    }

    private static string ComputeKey(
        IReadOnlyList<SearchChunk> chunks, string modelFingerprint, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "BadWolfAIPlayground/embedding-checkpoints/v1");
        Append(hash, modelFingerprint);
        Append(hash, chunks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (SearchChunk chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            Append(hash, chunk.DocumentName);
            Append(hash, chunk.FullPath ?? "");
            Append(hash, chunk.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, chunk.Content);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(data.Length));
        hash.AppendData(data);
    }

    internal sealed class Session(string directory)
    {
        public async Task<float[][]?> TryReadBatchAsync(
            int start, int expectedCount, CancellationToken token)
        {
            string path = GetBatchPath(start);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                // Corrupted or malicious files must never cause unbounded disk reads.
                if (new FileInfo(path).Length > 20L + 8 * MaxDimensions * sizeof(float) + Sha256Bytes)
                {
                    return null;
                }
                bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException)
            {
                // Another instance may be replacing a batch. Recompute safely.
                return null;
            }

            // Validate both the layout and checksum before accepting disk data.
            if (bytes.Length < 20 + Sha256Bytes ||
                !SHA256.HashData(bytes.AsSpan(0, bytes.Length - Sha256Bytes))
                    .AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - Sha256Bytes)))
            {
                return null;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);
            int magic = reader.ReadInt32();
            int version = reader.ReadInt32();
            int fileStart = reader.ReadInt32();
            int count = reader.ReadInt32();
            int dimensions = reader.ReadInt32();
            if (magic != HeaderMagic || version != FormatVersion || fileStart != start ||
                count != expectedCount || dimensions is <= 0 or > MaxDimensions ||
                bytes.Length != 20L + (long)count * dimensions * sizeof(float) + Sha256Bytes)
            {
                return null;
            }

            var result = new float[count][];
            for (int row = 0; row < count; row++)
            {
                result[row] = new float[dimensions];
                double norm = 0;
                for (int index = 0; index < dimensions; index++)
                {
                    float value = reader.ReadSingle();
                    if (!float.IsFinite(value))
                    {
                        return null;
                    }
                    result[row][index] = value;
                    norm += (double)value * value;
                }

                // Embeddings saved by the engine are already unit normalized.
                if (!double.IsFinite(norm) || Math.Abs(norm - 1.0) > 0.01)
                {
                    return null;
                }
            }

            return result;
        }

        public async Task SaveBatchAsync(
            int start, IReadOnlyList<float[]> vectors, CancellationToken token)
        {
            if (vectors.Count == 0 || vectors[0] is null ||
                vectors[0].Length is <= 0 or > MaxDimensions ||
                vectors.Any(vector => vector is null || vector.Length != vectors[0].Length))
            {
                throw new InvalidDataException("Invalid embedding dimensions in a checkpoint batch.");
            }

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(HeaderMagic);
                writer.Write(FormatVersion);
                writer.Write(start);
                writer.Write(vectors.Count);
                writer.Write(vectors[0].Length);
                foreach (float[] vector in vectors)
                {
                    double norm = 0;
                    foreach (float number in vector)
                    {
                        if (!float.IsFinite(number))
                        {
                            throw new InvalidDataException("Cannot checkpoint nonfinite embeddings.");
                        }
                        norm += (double)number * number;
                        writer.Write(number);
                    }
                    if (!double.IsFinite(norm) || Math.Abs(norm - 1) > 0.01)
                    {
                        throw new InvalidDataException("Cannot checkpoint unnormalized embeddings.");
                    }
                }
            }

            byte[] data = stream.ToArray();
            byte[] hash = SHA256.HashData(data);
            var payload = new byte[data.Length + hash.Length];
            Buffer.BlockCopy(data, 0, payload, 0, data.Length);
            Buffer.BlockCopy(hash, 0, payload, data.Length, hash.Length);

            string target = GetBatchPath(start);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, payload, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private string GetBatchPath(int start) =>
            Path.Combine(directory, $"batch-{start:D6}.bin");
    }
}
