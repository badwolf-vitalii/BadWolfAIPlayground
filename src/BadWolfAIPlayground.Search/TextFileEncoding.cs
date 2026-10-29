namespace BadWolfAIPlayground.Search;

/// <summary>
/// Decoding choice for explicitly selected local text files. Auto intentionally rejects
/// ambiguous legacy encodings rather than silently corrupting the searchable text.
/// </summary>
public enum TextFileEncoding
{
    Auto,
    Windows1251,
    Windows1252,
    Utf16LittleEndian,
    Utf16BigEndian
}
