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
    // File bytes are streamed; only retained event counts are bounded.
    public const int MaxDiscoveredFiles = 50_000;
    public const int MaxCombinedEvents = 1_000_000;
    public const int MaxRawEvents = 1_000_000;

    public static LogFolderImportResult Import(
        string folder, CancellationToken cancellationToken = default,
        IProgress<LogFolderProgress>? progress = null,
        int? maxImportedEvents = null)
    {
        // A conservative raw-event cap also protects the distinct-event
        // cap even if a folder contains no mirrored NLog targets.
        int eventBudget = Math.Min(MaxRawEvents, MaxCombinedEvents);
        if (maxImportedEvents.HasValue)
        {
            if (maxImportedEvents.Value < 1 || maxImportedEvents.Value > eventBudget)
                throw new ArgumentOutOfRangeException(nameof(maxImportedEvents),
                    $"Event budget must be between 1 and {eventBudget:N0}.");
            eventBudget = maxImportedEvents.Value;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                "The selected Logs folder does not exist.");
        }

        string root = Path.GetFullPath(folder);
        var candidates = new List<(string Path, string Relative, long Size, DateTime LastWriteUtc)>();
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

            // Metadata scanning can cover far more than one safe analysis batch.
            // Preserve the finite enumeration guard against unbounded folders.
            if (candidates.Count == MaxDiscoveredFiles)
            {
                throw new InvalidDataException(
                    $"Logs folder contains more than {MaxDiscoveredFiles:N0} supported files. " +
                    "Select a narrower directory to avoid excessive metadata scanning.");
            }
            var info = new FileInfo(fullPath);
            candidates.Add((fullPath, Path.GetRelativePath(root, fullPath),
                info.Length, info.LastWriteTimeUtc));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                "No .log or .txt files found in this folder or its subfolders.");
        }

        // No arbitrary byte or 512-file cap: rank all discovered logs and
        // incrementally parse complete sources within the event memory budget.
        var entries = candidates.OrderBy(item => SelectionPriority(item.Relative))
            .ThenByDescending(item => item.LastWriteUtc)
            .ThenBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Parse in priority order: sorting by path here would exhaust the
        // event budget on old components before reaching Operations logs.
        // Re-sort only the accepted per-file batches for stable provenance.
        var accepted = new List<(LogFolderFile File, IReadOnlyList<LogEvent> Events)>();
        int admittedEvents = 0;
        long analyzedBytes = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string full, string relative, long size, _) = entries[i];
            if (admittedEvents >= eventBudget)
            {
                skippedFiles.Add(new LogFolderSkippedFile(relative, size,
                    $"Excluded because the {eventBudget:N0} retained-event budget " +
                    "was already reached; this file was not read."));
                continue;
            }

            progress?.Report(new LogFolderProgress(i + 1, entries.Length, relative));
            int remaining = eventBudget - admittedEvents;
            LogParseResult parsed;
            try
            {
                parsed = ParseFile(full, relative, remaining, cancellationToken);
            }
            catch (LogEventLimitExceededException)
            {
                skippedFiles.Add(new LogFolderSkippedFile(relative, size,
                    $"Excluded by the {eventBudget:N0} retained-event budget. " +
                    $"This file contains more than {remaining:N0} dated events. " +
                    "It was not partially imported; the complete count is unknown."));
                continue;
            }

            int problems = parsed.Events.Count(e => e.Severity >= LogSeverity.Warning);
            accepted.Add((new LogFolderFile(relative, ComponentName(relative),
                parsed.Events.Count, problems, parsed.SkippedLines,
                parsed.ContinuationLines)
            {
                TotalLines = parsed.TotalLines
            }, parsed.Events));
            admittedEvents += parsed.Events.Count;
            analyzedBytes += size;
        }

        if (accepted.Count == 0)
            throw new InvalidDataException(
                "No log source fits the retained-event budget. " +
                "Select a narrower date range or component.");

        // Preserve the existing original source order and per-file mirror
        // deduplication semantics after resource-aware admission.
        var files = new List<LogFolderFile>(accepted.Count);
        var originalEvents = new List<LogEvent>(admittedEvents);
        foreach (var batch in accepted.OrderBy(entry => entry.File.RelativePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(batch.File);
            originalEvents.AddRange(batch.Events);
        }

        cancellationToken.ThrowIfCancellationRequested();
        LogFolderSelection selected = MergeSelected(
            files, originalEvents, new HashSet<string>(
                files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        return new LogFolderImportResult(
            selected.Combined, files, selected.DuplicateCopies, analyzedBytes)
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

    /// <summary>
    /// Import a standalone log as a stream, not via the document-search
    /// file loader, which has a separate 16 MiB text-indexing limit.
    /// </summary>
    public static LogParseResult ImportSingleFile(
        string path, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseFile(path, Path.GetFileName(path),
            Math.Min(MaxCombinedEvents, MaxRawEvents), token);
    }

    private static LogParseResult ParseFile(
        string path, string relativePath, int maxEvents, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 32 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 32 * 1024);
        try
        {
            return LogParser.Parse(reader, token, relativePath, maxEvents);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"'{relativePath}' is not valid UTF-8 or BOM-marked UTF-16. " +
                "Convert a copy to UTF-8 without modifying the original.", ex);
        }
    }

    // This is a deterministic import preference, not a diagnosis.
    // Give TOM_OP operation logs priority even when they live in old/.
    // Otherwise prefer current/root logs to archived copies.
    private static int SelectionPriority(string relativePath)
    {
        string filename = Path.GetFileName(relativePath);
        if (filename.StartsWith("Operations", StringComparison.OrdinalIgnoreCase))
            return 0;

        string? parent = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(parent))
            return 1;

        return parent.Replace('\\', '/').Split('/').Any(directory =>
            directory.Equals("old", StringComparison.OrdinalIgnoreCase))
            ? 3 : 2;
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
