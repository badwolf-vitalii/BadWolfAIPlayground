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
        var entries = new List<(string Path, string Relative, long Size)>();
        long totalBytes = 0;
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

            var info = new FileInfo(fullPath);
            if (info.Length > MaxFileBytes)
            {
                throw new InvalidDataException(
                    $"'{info.Name}' exceeds the {MaxFileBytes / 1048576} MiB per-file " +
                    "limit. Narrow the Logs folder or use a smaller date range.");
            }

            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaxTotalBytes)
            {
                throw new InvalidDataException(
                    $"Logs folder exceeds {MaxTotalBytes / 1048576} MiB. " +
                    "Choose a smaller date range or move older archives temporarily.");
            }

            if (entries.Count == MaxFiles)
            {
                throw new InvalidDataException(
                    $"Logs folder contains more than {MaxFiles} supported files. " +
                    "Choose a smaller log collection.");
            }

            entries.Add((fullPath, Path.GetRelativePath(root, fullPath), info.Length));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                "No .log or .txt files found in this folder or its subfolders.");
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(
            a.Relative, b.Relative));

        var files = new List<LogFolderFile>(entries.Count);
        var combined = new List<LogEvent>();
        // Track candidates for exact cross-file duplicate copies, but never
        // remove two identical log records from the SAME file.
        var seen = new Dictionary<(DateTimeOffset, LogSeverity, string), List<int>>();
        int totalLines = 0, skipped = 0, continuations = 0, duplicateCopies = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string full, string relative, _) = entries[i];
            progress?.Report(new LogFolderProgress(i + 1, entries.Count, relative));

            string content = ReadBoundedText(full, relative, cancellationToken);
            LogParseResult result = LogParser.Parse(
                content, cancellationToken, relative);

            totalLines = checked(totalLines + result.TotalLines);
            skipped = checked(skipped + result.SkippedLines);
            continuations = checked(continuations + result.ContinuationLines);
            int problems = result.Events.Count(e => e.Severity >= LogSeverity.Warning);
            files.Add(new LogFolderFile(relative, ComponentName(relative),
                result.Events.Count, problems, result.SkippedLines,
                result.ContinuationLines));

            foreach (LogEvent entry in result.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (entry.Timestamp, entry.Severity, entry.Message);
                if (!seen.TryGetValue(key, out List<int>? candidates))
                {
                    candidates = [];
                    seen[key] = candidates;
                }

                int duplicateIndex = -1;
                foreach (int candidateIndex in candidates)
                {
                    LogEvent previous = combined[candidateIndex];
                    if (!string.Equals(previous.SourceFile, relative,
                            StringComparison.OrdinalIgnoreCase) &&
                        IsKnownMirroredTargetPair(previous.SourceFile, relative) &&
                        !previous.AlsoInFiles.Contains(
                            relative, StringComparer.OrdinalIgnoreCase))
                    {
                        duplicateIndex = candidateIndex;
                        break;
                    }
                }

                if (duplicateIndex >= 0)
                {
                    LogEvent previous = combined[duplicateIndex];
                    // When a generic App log mirrors another component's NLog
                    // target, retain the more specific component as the
                    // primary origin and keep the mirrored file as an alias.
                    bool previousIsGeneral = IsGeneralAppLog(previous.SourceFile);
                    bool incomingIsSpecific = !IsGeneralAppLog(relative);
                    combined[duplicateIndex] = previousIsGeneral && incomingIsSpecific
                        ? entry with
                        {
                            AlsoInFiles = [..previous.AlsoInFiles, previous.SourceFile]
                        }
                        : previous with
                        {
                            AlsoInFiles = [..previous.AlsoInFiles, relative]
                        };
                    duplicateCopies++;
                }
                else
                {
                    if (combined.Count >= MaxCombinedEvents)
                    {
                        throw new InvalidDataException(
                            $"More than {MaxCombinedEvents:N0} dated log events " +
                            "were found. Choose a smaller date range.");
                    }

                    candidates.Add(combined.Count);
                    combined.Add(entry);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LogFolderImportResult(
            new LogParseResult(combined, totalLines, skipped, continuations),
            files, duplicateCopies, totalBytes);
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
    int DatedEvents, int ProblemEvents, int SkippedLines, int ContinuationLines);

public sealed record LogFolderProgress(
    int CurrentFile, int TotalFiles, string RelativePath);

public sealed record LogFolderImportResult(
    LogParseResult Combined, IReadOnlyList<LogFolderFile> Files,
    int DuplicateCopies, long TotalBytes);
