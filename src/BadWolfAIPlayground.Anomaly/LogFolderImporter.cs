using System.Text;
using System.Text.RegularExpressions;

namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// Import a complete selected Logs folder, including old/ and other
/// nested log directories, without saving paths or exposing its contents.
/// An active log is opened with FileShare.ReadWrite|Delete so applications
/// can keep writing while we read a bounded snapshot.
/// </summary>
public static partial class LogFolderImporter
{
    public const int MaxFiles = 512;
    public const long MaxFileBytes = 16L * 1024 * 1024;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    public const int MaxCombinedEvents = 500_000;
    public const int MaxRawEvents = 1_000_000;

    public static LogFolderImportResult Import(
        string folder, CancellationToken cancellationToken = default,
        IProgress<LogFolderProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                "The selected Logs folder does not exist.");
        }

        string root = Path.GetFullPath(folder);
        var candidates = new List<(string Path, string Relative, long Size)>();
        var skippedFiles = new List<LogFolderSkippedFile>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (string fullPath in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(fullPath);
            if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Do not read .zip, binaries, arbitrary work files.
            }

            if (candidates.Count == MaxFiles)
            {
                throw new InvalidDataException(
                    $"Logs folder contains more than {MaxFiles} supported files. " +
                    "Choose a smaller log collection.");
            }
            var info = new FileInfo(fullPath);
            candidates.Add((fullPath, Path.GetRelativePath(root, fullPath), info.Length));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                "No .log or .txt files found in this folder or its subfolders.");
        }

        // A high-volume profiler should not hide every other operation log.
        // Exclude only by a known resource limit and always report its path.
        var eligible = new List<(string Path, string Relative, long Size)>();
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Size > MaxFileBytes)
            {
                skippedFiles.Add(new LogFolderSkippedFile(
                    entry.Relative, entry.Size,
                    $"Exceeds the {MaxFileBytes / 1048576} MiB per-file safety limit."));
            }
            else
            {
                eligible.Add(entry);
            }
        }

        // Reserve the total in-memory budget for as many eligible files as
        // possible, in size+path order. Re-sort selected files by path for
        // deterministic original NLog provenance and file counts.
        long totalBytes = 0;
        var entries = new List<(string Path, string Relative, long Size)>();
        foreach (var entry in eligible.OrderBy(e => e.Size)
                     .ThenBy(e => e.Relative, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Size > MaxTotalBytes - totalBytes)
            {
                skippedFiles.Add(new LogFolderSkippedFile(
                    entry.Relative, entry.Size,
                    $"Excluded to stay within the {MaxTotalBytes / 1048576} MiB " +
                    "combined import budget."));
            }
            else
            {
                entries.Add(entry);
                totalBytes += entry.Size;
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                $"No .log/.txt files fit the {MaxFileBytes / 1048576} MiB per-file " +
                $"and {MaxTotalBytes / 1048576} MiB total limits. " +
                $"{skippedFiles.Count} files could not be analyzed.");
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(
            a.Relative, b.Relative));

        var files = new List<LogFolderFile>(entries.Count);
        // Retain parsed, original per-file events in memory. Filtering needs
        // these raw rows: merely hiding a deduplicated App/Sale event would
        // discard the remaining copy even when its log is still enabled.
        var originalEvents = new List<LogEvent>();
        for (int i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string full, string relative, _) = entries[i];
            progress?.Report(new LogFolderProgress(i + 1, entries.Count, relative));

            string content = ReadBoundedText(full, relative, cancellationToken);
            LogParseResult parsed = LogParser.Parse(content, cancellationToken, relative);
            int problems = parsed.Events.Count(e => e.Severity >= LogSeverity.Warning);
            files.Add(new LogFolderFile(relative, ComponentName(relative),
                parsed.Events.Count, problems, parsed.SkippedLines,
                parsed.ContinuationLines)
            {
                TotalLines = parsed.TotalLines
            });

            if (originalEvents.Count + parsed.Events.Count > MaxRawEvents)
            {
                throw new InvalidDataException(
                    $"More than {MaxRawEvents:N0} raw log events found. " +
                    "Choose a smaller log folder.");
            }
            originalEvents.AddRange(parsed.Events);
        }

        cancellationToken.ThrowIfCancellationRequested();
        LogFolderSelection selected = MergeSelected(
            files, originalEvents, new HashSet<string>(
                files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        return new LogFolderImportResult(
            selected.Combined, files, selected.DuplicateCopies, totalBytes)
        {
            RawEvents = originalEvents,
            SkippedFiles = skippedFiles
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    /// <summary>
    /// Recompute the timeline for a user-selected subset without re-reading
    /// changing work files. Cross-target duplicates are recomputed *after*
    /// filtering so turning off a component never loses its remaining App copy.
    /// </summary>
    public static LogFolderSelection SelectFiles(
        LogFolderImportResult original, IReadOnlyCollection<string> enabledFiles,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(enabledFiles);
        var allPaths = new HashSet<string>(
            original.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(enabledFiles, StringComparer.OrdinalIgnoreCase);
        if (!enabled.IsSubsetOf(allPaths))
        {
            throw new ArgumentException(
                "The requested selection contains a file outside the imported Logs folder.",
                nameof(enabledFiles));
        }

        return MergeSelected(original.Files, original.RawEvents, enabled, token);
    }

    private static LogFolderSelection MergeSelected(
        IReadOnlyList<LogFolderFile> files, IReadOnlyList<LogEvent> raw,
        HashSet<string> enabled, CancellationToken token)
    {
        var combined = new List<LogEvent>();
        var seen = new Dictionary<(DateTimeOffset, LogSeverity, string), List<int>>();
        int duplicateCopies = 0;

        foreach (LogEvent entry in raw)
        {
            token.ThrowIfCancellationRequested();
            if (!enabled.Contains(entry.SourceFile))
            {
                continue;
            }

            var key = (entry.Timestamp, entry.Severity, entry.Message);
            if (!seen.TryGetValue(key, out List<int>? candidates))
            {
                candidates = [];
                seen[key] = candidates;
            }

            int duplicateIndex = -1;
            foreach (int index in candidates)
            {
                LogEvent previous = combined[index];
                if (!string.Equals(previous.SourceFile, entry.SourceFile,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsKnownMirroredTargetPair(previous.SourceFile, entry.SourceFile) &&
                    !previous.AlsoInFiles.Contains(
                        entry.SourceFile, StringComparer.OrdinalIgnoreCase))
                {
                    duplicateIndex = index;
                    break;
                }
            }

            if (duplicateIndex >= 0)
            {
                LogEvent previous = combined[duplicateIndex];
                // Keep component-specific provenance for a mirrored App
                // target and retain the real aliases for later opening.
                combined[duplicateIndex] = IsGeneralAppLog(previous.SourceFile) &&
                                           !IsGeneralAppLog(entry.SourceFile)
                    ? entry with
                    {
                        AlsoInFiles = [..previous.AlsoInFiles, previous.SourceFile]
                    }
                    : previous with
                    {
                        AlsoInFiles = [..previous.AlsoInFiles, entry.SourceFile]
                    };
                duplicateCopies++;
            }
            else
            {
                if (combined.Count >= MaxCombinedEvents)
                {
                    throw new InvalidDataException(
                        $"More than {MaxCombinedEvents:N0} distinct dated events found. " +
                        "Choose a smaller folder or date range.");
                }
                candidates.Add(combined.Count);
                combined.Add(entry);
            }
        }

        token.ThrowIfCancellationRequested();
        LogFolderFile[] activeFiles = files
            .Where(file => enabled.Contains(file.RelativePath)).ToArray();
        var result = new LogParseResult(
            combined,
            checked((int)activeFiles.Sum(f => (long)f.TotalLines)),
            checked(activeFiles.Sum(f => f.SkippedLines)),
            checked(activeFiles.Sum(f => f.ContinuationLines)));

        return new LogFolderSelection(result, duplicateCopies,
            activeFiles.Length);
    }

    private static string ReadBoundedText(
        string path, string relativePath, CancellationToken token)
    {
        // BOM-aware UTF-8/UTF-16, with strict decoding for legacy/invalid data.
        // Reusing this mode avoids silently corrupting customer log messages.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 32 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 32 * 1024);
        var builder = new StringBuilder((int)Math.Min(stream.Length, 1_000_000));
        var chars = new char[32 * 1024];
        try
        {
            int length;
            while ((length = reader.Read(chars, 0, chars.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (builder.Length + length > MaxFileBytes ||
                    chars.AsSpan(0, length).Contains('\0'))
                {
                    throw new InvalidDataException(
                        $"'{relativePath}' exceeds safe text limits or appears binary.");
                }
                builder.Append(chars, 0, length);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"'{relativePath}' is not readable as UTF-8/UTF-16 text. " +
                "Use Select local LOG/TXT with an appropriate encoding " +
                "or convert a copy to UTF-8.", ex);
        }
        return builder.ToString();
    }

    private static string ComponentName(string relativePath)
    {
        string name = Path.GetFileNameWithoutExtension(relativePath);
        return DateSuffix().Replace(name, "");
    }

    private static bool IsGeneralAppLog(string path) =>
        ComponentName(path).Equals("App", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownMirroredTargetPair(string first, string second)
    {
        // Do not silently conflate two independent components that happen
        // to log the same text at the same time. Only known App/component
        // mirrors and copied versions of the same named file are deduplicated.
        bool firstIsGeneral = IsGeneralAppLog(first);
        bool secondIsGeneral = IsGeneralAppLog(second);
        return firstIsGeneral != secondIsGeneral ||
            string.Equals(Path.GetFileName(first), Path.GetFileName(second),
                StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[_-]\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();
}

public sealed record LogFolderFile(
    string RelativePath, string Component,
    int DatedEvents, int ProblemEvents, int SkippedLines, int ContinuationLines)
{
    public int TotalLines { get; init; }
}

public sealed record LogFolderProgress(
    int CurrentFile, int TotalFiles, string RelativePath);

/// <summary>Discovered but not read; contributes no timeline or findings.</summary>
public sealed record LogFolderSkippedFile(string RelativePath, long SizeBytes, string Reason)
{
    public string Display => RelativePath + " (" +
        (SizeBytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
        " MiB): " + Reason;
}

public sealed record LogFolderImportResult(
    LogParseResult Combined, IReadOnlyList<LogFolderFile> Files,
    int DuplicateCopies, long TotalBytes)
{
    // Original per-file entries; never persisted or sent to a service.
    public IReadOnlyList<LogEvent> RawEvents { get; init; } = [];
    public IReadOnlyList<LogFolderSkippedFile> SkippedFiles { get; init; } = [];
}

public sealed record LogFolderSelection(
    LogParseResult Combined, int DuplicateCopies, int EnabledFileCount);
