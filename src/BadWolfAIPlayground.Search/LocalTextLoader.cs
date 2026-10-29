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
        IProgress<FileImportProgress>? progress = null,
        TextFileEncoding fileEncoding = TextFileEncoding.Auto)
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
            // Legacy ANSI decoding never fails on most UTF-8 bytes and can silently
            // turn a correct Ukrainian book into a stream of "РџРµС..." garbage.
            // Refuse a demonstrably valid UTF-8 file when a legacy codepage was chosen.
            if (fileEncoding is TextFileEncoding.Windows1251 or TextFileEncoding.Windows1252)
            {
                await RejectUtf8AsLegacyAsync(info, fileEncoding, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report(new FileImportProgress(bytesCompleted, totalBytes, fileIndex, files.Count, info.Name));

            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferCharacters, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, ResolveEncoding(fileEncoding),
                detectEncodingFromByteOrderMarks: fileEncoding is TextFileEncoding.Auto or
                    TextFileEncoding.Utf16LittleEndian or TextFileEncoding.Utf16BigEndian,
                bufferSize: BufferCharacters, leaveOpen: true);
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
                        throw new InvalidDataException(
                            $"{info.Name} contains NUL characters. If it is UTF-16 text without a BOM, " +
                            "select UTF-16 LE or UTF-16 BE; otherwise the file may contain binary data.");
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
                string reason = fileEncoding == TextFileEncoding.Auto
                    ? $"{info.Name} contains bytes that are not valid UTF-8. " +
                      "If this is a legacy TXT, select Windows-1251 (Cyrillic) or Windows-1252 (Western), then retry."
                    : $"{info.Name} could not be decoded as {EncodingName(fileEncoding)}. " +
                      "Choose the encoding used when the file was saved.";
                throw new InvalidDataException(reason, exception);
            }

            // A file growing beyond the preflight size must not escape the overall safety cap.
            bytesCompleted = checked(bytesCompleted + stream.Position);
            if (bytesCompleted > MaxTotalBytes)
            {
                throw new InvalidDataException("Selected files grew beyond the combined size limit while reading.");
            }

            string content = builder.ToString();
            if (LooksLikeMojibake(content))
            {
                throw new InvalidDataException(
                    $"{info.Name} contains strongly garbled text (e.g. repeated Р/С or Ð/Ñ characters). " +
                    "The file may have been decoded or saved using the wrong character encoding. " +
                    "Try File encoding: Auto (UTF-8), then reselect the original file. " +
                    "Never build an AI index from unreadable text.");
            }
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

    private static async Task RejectUtf8AsLegacyAsync(
        FileInfo file, TextFileEncoding forcedEncoding, CancellationToken token)
    {
        const int SampleBytes = 16 * 1024;
        byte[] sample = new byte[(int)Math.Min(file.Length, SampleBytes)];
        if (sample.Length == 0)
        {
            return;
        }

        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read = 0;
        while (read < sample.Length)
        {
            int received = await stream.ReadAsync(sample.AsMemory(read), token)
                .ConfigureAwait(false);
            if (received == 0)
            {
                break;
            }
            read += received;
        }

        // ASCII is valid in both encodings, so don't warn unless the sample
        // contains substantial non-ASCII letters decoded as strict UTF-8.
        if (!sample.AsSpan(0, read).ContainsAnyExceptInRange((byte)0, (byte)127))
        {
            return;
        }

        var strictUtf8 = new UTF8Encoding(false, true);
        for (int trim = 0; trim <= 3 && trim < read; trim++)
        {
            try
            {
                // A 16-KiB sample may end in the middle of a UTF-8 sequence.
                // Discard only that partial tail, never invalid bytes inside.
                string decoded = strictUtf8.GetString(sample, 0, read - trim);
                int nonAsciiLetters = decoded.Count(character =>
                    character > 127 && char.IsLetter(character));
                if (nonAsciiLetters >= 3 || decoded.StartsWith("\uFEFF", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{file.Name} appears to be valid UTF-8, but {EncodingName(forcedEncoding)} " +
                        "was selected. Change File encoding to Auto (UTF-8 / Unicode BOM) " +
                        "and select the file again. The current encoding would corrupt the AI index.");
                }
                return;
            }
            catch (DecoderFallbackException)
            {
                // This may be genuine Windows-1251/1252 text: accept the explicit
                // selection and let the normal decoder read it.
            }
        }
    }

    private static bool LooksLikeMojibake(string content)
    {
        int characters = Math.Min(content.Length, 12_000);
        if (characters < 100)
        {
            return false;
        }

        int letters = 0;
        int indicative = 0;
        for (int index = 0; index < characters; index++)
        {
            char letter = content[index];
            if (!char.IsLetter(letter))
            {
                continue;
            }

            letters++;
            if (letter is 'Р' or 'С' or 'Ð' or 'Ñ')
            {
                indicative++;
            }
        }

        // UTF-8 misread as a single-byte codepage generates one Р/С (or Ð/Ñ)
        // prefix for almost every Cyrillic character. Normal Ukrainian prose
        // contains far fewer of these uppercase letters.
        return letters >= 80 && indicative >= 30 && indicative * 100 > letters * 27;
    }

    private static Encoding ResolveEncoding(TextFileEncoding fileEncoding)
    {
        // Code pages are never guessed: picking an incorrect legacy code page can silently
        // produce readable but wrong text and therefore misleading semantic-search results.
        return fileEncoding switch
        {
            TextFileEncoding.Auto => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            TextFileEncoding.Utf16LittleEndian => new UnicodeEncoding(
                bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
            TextFileEncoding.Utf16BigEndian => new UnicodeEncoding(
                bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
            TextFileEncoding.Windows1251 => CodePagesEncodingProvider.Instance.GetEncoding(
                1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!,
            TextFileEncoding.Windows1252 => CodePagesEncodingProvider.Instance.GetEncoding(
                1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!,
            _ => throw new ArgumentOutOfRangeException(nameof(fileEncoding), fileEncoding, null)
        };
    }

    private static string EncodingName(TextFileEncoding fileEncoding) => fileEncoding switch
    {
        TextFileEncoding.Windows1251 => "Windows-1251",
        TextFileEncoding.Windows1252 => "Windows-1252",
        TextFileEncoding.Utf16LittleEndian => "UTF-16 LE",
        TextFileEncoding.Utf16BigEndian => "UTF-16 BE",
        _ => "UTF-8 / Unicode BOM"
    };
}
