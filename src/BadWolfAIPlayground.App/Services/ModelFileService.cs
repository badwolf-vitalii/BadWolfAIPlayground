using System.IO;
using Microsoft.Win32;

namespace BadWolfAIPlayground.App.Services;

public sealed class ModelFileService : IModelFileService
{
    public bool TrySave(string content)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save trained network",
            Filter = "AI Playground model (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "xor-network.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        File.WriteAllText(dialog.FileName, content);
        return true;
    }

    public string? TryOpen()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open trained network",
            Filter = "AI Playground model (*.json)|*.json|JSON files (*.json)|*.json"
        };

        return dialog.ShowDialog() == true
            ? File.ReadAllText(dialog.FileName)
            : null;
    }
}
