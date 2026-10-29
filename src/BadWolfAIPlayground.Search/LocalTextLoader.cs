using System.Text;

namespace BadWolfAIPlayground.Search;

/// <summary>Import bounded UTF-8 text files only, with no network or directory traversal.</summary>
public static class LocalTextLoader
{
    public const int MaxDocuments = 40;
    public const int MaxFileBytes = 1_048_576;
    public const int MaxTotalCharacters = 200_000;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".csv", ".xml", ".cs", ".xaml", ".config", ".yaml", ".yml"
    };

    public static async Task<IReadOnlyList<SearchDocument>> LoadAsync(
        IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0 || filePaths.Count > MaxDocuments)
        {
            throw new ArgumentException($"Select between 1 and {MaxDocuments} files.", nameof(filePaths));
        }

        var documents = new List<SearchDocument>(filePaths.Count);
        int totalCharacters = 0;
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
                throw new InvalidDataException($"{info.Name} exceeds the 1 MiB per-file limit.");
            }

            string content = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken)
                .ConfigureAwait(false);
            if (content.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException($"{info.Name} contains binary data.");
            }

            totalCharacters = checked(totalCharacters + content.Length);
            if (totalCharacters > MaxTotalCharacters)
            {
                throw new InvalidDataException("The selected documents exceed the 200,000-character experiment limit.");
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                documents.Add(new SearchDocument(info.Name, content, info.FullName));
            }
        }

        if (documents.Count == 0)
        {
            throw new InvalidDataException("The selected files contain no searchable text.");
        }

        return documents;
    }
}
