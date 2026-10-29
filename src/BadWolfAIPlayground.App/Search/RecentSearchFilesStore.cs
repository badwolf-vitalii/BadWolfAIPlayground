using System.Text.Json;
using BadWolfAIPlayground.Search;

namespace BadWolfAIPlayground.App.Search;

/// <summary>
/// Remembers only user-selected local source paths and their decoding choice.
/// Source text is never copied into this file. Restoring is explicitly user-initiated.
/// </summary>
public sealed record RecentSearchSelection(string[] Paths, TextFileEncoding Encoding);

public sealed class RecentSearchFilesStore
{
    public string FilePath { get; }

    public RecentSearchFilesStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BadWolfAIPlayground", "last-search-files.json");
    }

    public RecentSearchSelection? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            RecentSearchSelection? saved = JsonSerializer.Deserialize<RecentSearchSelection>(
                File.ReadAllText(FilePath));
            if (saved?.Paths is not { Length: > 0 and <= LocalTextLoader.MaxDocuments } ||
                !Enum.IsDefined(saved.Encoding) ||
                saved.Paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            {
                return null;
            }

            return saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(IReadOnlyList<string> paths, TextFileEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || paths.Count > LocalTextLoader.MaxDocuments ||
            !Enum.IsDefined(encoding) || paths.Any(path => !Path.IsPathFullyQualified(path)))
        {
            throw new ArgumentException("Invalid list of local files.", nameof(paths));
        }

        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Recent search selection path must have a directory.");
        }

        Directory.CreateDirectory(directory);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new RecentSearchSelection(paths.ToArray(), encoding));
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
