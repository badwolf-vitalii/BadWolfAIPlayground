using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BadWolfAIPlayground.App;
using BadWolfAIPlayground.App.ViewModels;

namespace BadWolfAIPlayground.App.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        App? application = null;
        MainWindow? window = null;

        try
        {
            application = new App();
            application.InitializeComponent();

            // Instantiate the real WPF window to check binding validation at startup.
            window = new MainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            if (window.DataContext is not MainViewModel viewModel ||
                viewModel.Predictions.Count != 4)
            {
                throw new InvalidOperationException("The four XOR prediction cards were not initialized.");
            }

            var bindings = FindDescendants<ProgressBar>(window)
                .Select(bar => BindingOperations.GetBinding(bar, RangeBase.ValueProperty))
                .ToArray();

            if (bindings.Length != 5)
            {
                throw new InvalidOperationException(
                    $"Expected five progress bars, found {bindings.Length}.");
            }

            if (bindings.Any(binding => binding?.Mode != BindingMode.OneWay))
            {
                throw new InvalidOperationException(
                    "All progress bars must use OneWay bindings to read-only properties.");
            }

            int completionBindings = bindings.Count(binding => binding?.Path?.Path == "Completion");
            int percentageBindings = bindings.Count(binding => binding?.Path?.Path == "Percentage");

            if (completionBindings != 1 || percentageBindings != 4)
            {
                throw new InvalidOperationException(
                    $"Unexpected progress bar bindings: Completion={completionBindings}, Percentage={percentageBindings}.");
            }

            Console.WriteLine("PASS WPF startup, XOR prediction cards, and read-only progress bindings");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL WPF startup smoke test: {exception}");
            return 1;
        }
        finally
        {
            window?.Close();
            application?.Shutdown();
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
