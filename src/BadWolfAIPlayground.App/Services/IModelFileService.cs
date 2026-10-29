namespace BadWolfAIPlayground.App.Services;

public interface IModelFileService
{
    bool TrySave(string content);

    string? TryOpen();
}
