using System.Text;

namespace BadWolfAIPlayground.Search;

/// <summary>Reads explicitly selected text files in bounded, cancellable blocks.</summary>
public static class LocalTextLoader
{
    public const int MaxDocuments = 40;
    public const long MaxFileBytes = 16L * 1024 * 1024;
    public const long MaxTotalBytes = 32L * 1024 * 1024;
    public const int MaxTotalCharacters = 24_000_000;
    private const int BufferCharacters = 32 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".csv", ".xml", ".cs", ".xaml", ".config", ".yaml", ".yml"
    };

    public static async Task<IReadOnlyList<SearchDocument>> LoadAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default,
        IProgress<FileImportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0 || filePaths.Count > MaxDocuments)
        {
            throw new ArgumentException($"Select between 1 and {MaxDocuments} files.", nameof(filePaths));
        }

        // Check all sizes before loading anything into memory; the old documents remain
        // untouched if a later file fails validation or the user cancels the import.
        var files = new List<FileInfo>(filePaths.Count);
        long totalBytes = 0;
        foreach (string path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AllowedExtensions.Contains(Path.GetExtension(path)))
            {
                throw new InvalidDataException($"Unsupported file type: {Path.GetFileName(path)}.");
            }

            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                throw new InvalidDataException($"{info.Name} exceeds the {MaxFileBytes / 1048576} MiB per-file limit.");
            }

            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaxTotalBytes)
            {
                throw new InvalidDataException($"Selected files exceed the {MaxTotalBytes / 1048576} MiB combined limit.");
            }
            files.Add(info);
        }

        var documents = new List<SearchDocument>(files.Count);
        long bytesCompleted = 0;
        long charactersCompleted = 0;
        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = files[fileIndex];
            progress?.Report(new FileImportProgress(bytesCompleted, totalBytes, fileIndex, files.Count, info.Name));

            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferCharacters, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, bufferSize: BufferCharacters, leaveOpen: true);
            var buffer = new char[BufferCharacters];
            var builder = new StringBuilder((int)Math.Min(stream.Length, 1_000_000));

            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    if (buffer.AsSpan(0, count).Contains('\0'))
                    {
                        throw new InvalidDataException($"{info.Name} contains binary data (NUL characters).");
                    }

                    charactersCompleted += count;
                    if (charactersCompleted > MaxTotalCharacters)
                    {
                        throw new InvalidDataException(
                            $"Selected documents exceed the {MaxTotalCharacters:N0}-character safety limit.");
                    }
                    if (stream.Position > MaxFileBytes)
                    {
                        throw new InvalidDataException($"{info.Name} grew beyond the per-file limit while reading.");
                    }

                    builder.Append(buffer, 0, count);
                    progress?.Report(new FileImportProgress(
                        Math.Min(totalBytes, bytesCompleted + stream.Position),
                        totalBytes, fileIndex, files.Count, info.Name));
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"{info.Name} is not UTF-8 text (or UTF-16 with a BOM). Convert it to UTF-8 and try again.",
                    exception);
            }

            // A file growing beyond the preflight size must not escape the overall safety cap.
            bytesCompleted = checked(bytesCompleted + stream.Position);
            if (bytesCompleted > MaxTotalBytes)
            {
                throw new InvalidDataException("Selected files grew beyond the combined size limit while reading.");
            }

            string content = builder.ToString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                documents.Add(new SearchDocument(info.Name, content, info.FullName));
            }
            progress?.Report(new FileImportProgress(
                Math.Min(bytesCompleted, totalBytes), totalBytes, fileIndex + 1, files.Count, info.Name));
        }

        if (documents.Count == 0)
        {
            throw new InvalidDataException("The selected files contain no searchable text.");
        }

        return documents;
    }
}
