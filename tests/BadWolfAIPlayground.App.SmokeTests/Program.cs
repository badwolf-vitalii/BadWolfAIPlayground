using System.Diagnostics;
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

            // Show the actual window so templates and runtime WPF bindings are evaluated.
            window = new MainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            if (window.DataContext is not MainViewModel viewModel)
            {
                throw new InvalidOperationException("MainWindow has no MainViewModel.");
            }

            Assert(viewModel.Predictions.Count == 4, "The four XOR predictions were not initialized.");

            var bindings = FindDescendants<ProgressBar>(window)
                .Select(bar => BindingOperations.GetBinding(bar, RangeBase.ValueProperty))
                .ToArray();

            Assert(bindings.Length == 5, $"Expected five progress bars, found {bindings.Length}.");
            Assert(bindings.All(binding => binding?.Mode == BindingMode.OneWay),
                "Progress bars must use OneWay bindings to read-only properties.");
            Assert(bindings.Count(binding => binding?.Path?.Path == "Completion") == 1,
                "Expected one completion binding.");
            Assert(bindings.Count(binding => binding?.Path?.Path == "Percentage") == 4,
                "Expected four prediction percentage bindings.");

            Console.WriteLine("PASS WPF startup and read-only progress bindings");

            // The guided tour must start with a snapshot of the pre-training model.
            Assert(viewModel.LessonStep == 1, "The guided experiment must start at Step 1.");
            Assert(!viewModel.HasTrainingResults, "A fresh network must not report training results.");
            Assert(viewModel.Predictions.All(row => row.BeforeOutput == row.Output),
                "The starting predictions must match the initial baseline.");
            Assert(viewModel.BeforeAccuracyText == viewModel.AccuracyText,
                "Initial accuracy should match the baseline.");

            viewModel.NextLessonCommand.Execute(null);
            Assert(viewModel.LessonStep == 2, "Next must advance to the before-training explanation.");
            viewModel.NextLessonCommand.Execute(null);
            Assert(viewModel.LessonStep == 3, "Next must reach the training step.");
            Assert(!viewModel.NextLessonCommand.CanExecute(null),
                "The results step must stay locked until training has occurred.");

            // Train a small run and pump the WPF dispatcher while awaiting the async command.
            viewModel.EpochsText = "500";
            viewModel.WatchTraining = false;
            viewModel.TrainCommand.Execute(null);
            var clock = Stopwatch.StartNew();
            while (viewModel.IsTraining && clock.Elapsed < TimeSpan.FromSeconds(25))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(2);
            }

            Assert(!viewModel.IsTraining, "Training did not finish within 25 seconds.");
            Assert(viewModel.HasTrainingResults, "The completed training run should unlock results.");
            Assert(viewModel.LossHistory.Count > 0, "Training should produce loss data.");
            Assert(viewModel.NextLessonCommand.CanExecute(null),
                "The results step should unlock after training.");
            viewModel.NextLessonCommand.Execute(null);
            Assert(viewModel.LessonStep == 4, "The results step must be reachable after training.");
            Assert(viewModel.Predictions.Any(row => row.BeforeOutput != row.Output),
                "The comparison must contain learned predictions, not only starting guesses.");

            viewModel.RestartLessonCommand.Execute(null);
            Assert(viewModel.LessonStep == 1, "Restart guide should return to Step 1.");
            Assert(viewModel.HasTrainingResults,
                "Restart guide must not delete learned weights.");

            viewModel.ResetCommand.Execute(null);
            Assert(viewModel.LessonStep == 1 && !viewModel.HasTrainingResults,
                "Reset must clear the training comparison and restart the guide.");
            Assert(viewModel.Predictions.All(row => row.BeforeOutput == row.Output),
                "Reset should create a fresh baseline.");

            Console.WriteLine("PASS guided lessons, before/after comparison, training and reset");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL WPF UI smoke test: {exception}");
            return 1;
        }
        finally
        {
            window?.Close();
            application?.Shutdown();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
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
