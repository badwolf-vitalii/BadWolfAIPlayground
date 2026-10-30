using System.Windows.Input;

namespace BadWolfAIPlayground.App.ViewModels;

/// <summary>
/// A typed WPF command for actions on a concrete selected source row.
/// Keeps the log file or finding as an explicit command parameter.
/// </summary>
public sealed class ParameterRelayCommand<T>(
    Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
    where T : class
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T item && (canExecute?.Invoke(item) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T item && CanExecute(item))
        {
            execute(item);
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
