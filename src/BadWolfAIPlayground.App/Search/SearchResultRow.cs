using System.Globalization;
using BadWolfAIPlayground.Search;

namespace BadWolfAIPlayground.App.Search;

public sealed class SearchResultRow
{
    public string Title { get; }
    public string Snippet { get; }
    public string ScoreText { get; }
    public string SourcePath { get; }

    public SearchResultRow(SearchHit hit, bool semantic)
    {
        Title = $"{hit.Chunk.DocumentName} · chunk {hit.Chunk.Number}";
        SourcePath = hit.Chunk.FullPath ?? "Built-in demonstration document";
        string text = hit.Chunk.Content.Replace('\n', ' ').Trim();
        Snippet = text.Length > 350 ? text[..350].TrimEnd() + "…" : text;
        ScoreText = semantic
            ? $"cosine {hit.Score.ToString("0.000", CultureInfo.InvariantCulture)}"
            : $"{hit.Score.ToString("0", CultureInfo.InvariantCulture)} word matches";
    }
}
