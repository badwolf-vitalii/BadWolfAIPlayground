using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BadWolfAIPlayground.App;
using BadWolfAIPlayground.App.ViewModels;
using BadWolfAIPlayground.App.Search;
using BadWolfAIPlayground.Search;

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

            int wrongThreadNotifications = 0;
            Dispatcher uiDispatcher = window.Dispatcher;
            viewModel.PropertyChanged += (_, _) =>
            {
                if (!uiDispatcher.CheckAccess())
                {
                    Interlocked.Increment(ref wrongThreadNotifications);
                }
            };
            viewModel.Predictions.CollectionChanged += (_, _) =>
            {
                if (!uiDispatcher.CheckAccess())
                {
                    Interlocked.Increment(ref wrongThreadNotifications);
                }
            };

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
            Assert(wrongThreadNotifications == 0,
                "WPF-bound properties and predictions must only change on the UI dispatcher.");
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

            // Navigation and the keyword-only path must work without Ollama installed.
            window.NavigateToSmartSearch();
            var smartSearch = FindDescendants<SmartSearchView>(window).Single();
            Assert(smartSearch.Visibility == Visibility.Visible, "Smart Search navigation failed.");
            SmartSearchViewModel searchModel = smartSearch.SearchModel;
            ComboBox encodingSelector = smartSearch.FindName("FileEncodingSelector") as ComboBox
                ?? throw new InvalidOperationException("Smart Search file encoding selector is missing.");
            Assert(encodingSelector.SelectedItem is FileEncodingChoice initialChoice &&
                   initialChoice.Encoding == TextFileEncoding.Auto,
                "The file encoding selector must be visible and default to strict UTF-8 / BOM detection.");
            Assert(encodingSelector.Style is not null &&
                   encodingSelector.Style.BasedOn is null,
                "File encoding must use the dedicated dark ComboBox style.");
            encodingSelector.ApplyTemplate();
            Assert(encodingSelector.Template.FindName("DropDownToggle", encodingSelector) is ToggleButton,
                "The dark ComboBox template must render a visible dropdown arrow.");
            Assert(encodingSelector.Template.FindName("ContentSite", encodingSelector) is ContentPresenter,
                "The selected encoding must remain visible on the dark ComboBox surface.");
            Assert(encodingSelector.Foreground is SolidColorBrush foreground &&
                   foreground.Color.R > 190 && foreground.Color.G > 190 &&
                   encodingSelector.Background is SolidColorBrush background &&
                   background.Color.R < 70 && background.Color.G < 90,
                "File encoding must have legible light text on a dark background.");
            Assert(encodingSelector.ItemContainerStyle is not null &&
                   encodingSelector.ItemContainerStyle.Setters.OfType<Setter>().Any(setter =>
                       setter.Property == Control.ForegroundProperty),
                "Every dropdown option must use a high-contrast foreground.");
            Console.WriteLine("PASS dark file encoding selector and dropdown item styling");
            searchModel.LoadDemoCommand.Execute(null);
            Assert(searchModel.HasDocuments && searchModel.DocumentNames.Count == 4,
                "The local demonstration corpus did not load.");
            Assert(!searchModel.HasIndex, "A demo must not pretend to have generated AI embeddings.");
            Assert(!searchModel.AskDocumentCommand.CanExecute(null),
                "Answer generation must not run before a real semantic index exists.");
            Assert(smartSearch.FindName("AskDocumentButton") is Button,
                "The Smart Search Lab must expose a separate Ask document action.");
            Assert(smartSearch.FindName("AnswerTextBlock") is TextBlock,
                "The grounded answer must have a dedicated readable area.");
            Assert(smartSearch.FindName("RetrievedEvidenceExpander") is Expander,
                "The QA UI must expose actual model evidence even when no answer can be verified.");
            Assert(searchModel.RetrievedPassages.Count == 0 &&
                   searchModel.RetrievalDetails.Contains("Ask document", StringComparison.Ordinal),
                "Evidence inspector must start empty without fabricated passages.");
            Assert(searchModel.AnswerSources.Count == 0,
                "An unloaded document must not display fabricated citations.");
            Console.WriteLine("PASS separate grounded-answer UI and no fabricated pre-index answer");
            searchModel.Query = "SQLite locked";
            searchModel.CompareCommand.Execute(null);
            PumpUntil(() => !searchModel.IsBusy, TimeSpan.FromSeconds(20));
            Assert(searchModel.KeywordResults.Count > 0 &&
                   searchModel.KeywordResults[0].Title.Contains("database_errors", StringComparison.Ordinal),
                "Keyword ranking did not find the database troubleshooting note.");
            Assert(searchModel.SemanticResults.Count == 0,
                "AI matches must not be faked when the model is unavailable.");

            // Regression: a real 3.5 MiB file must import without blocking the dispatcher.
            string largePath = Path.Combine(Path.GetTempPath(), $"badwolf-ui-{Guid.NewGuid():N}.txt");
            int searchWrongThreadNotifications = 0;
            searchModel.PropertyChanged += (_, _) =>
            {
                if (!uiDispatcher.CheckAccess())
                {
                    Interlocked.Increment(ref searchWrongThreadNotifications);
                }
            };
            searchModel.DocumentNames.CollectionChanged += (_, _) =>
            {
                if (!uiDispatcher.CheckAccess())
                {
                    Interlocked.Increment(ref searchWrongThreadNotifications);
                }
            };
            try
            {
                File.WriteAllText(largePath, new string('a', 3_670_016) + "\nFINAL_SEARCH_MARKER\n");
                Task importing = searchModel.ImportFilesAsync([largePath]);
                PumpUntil(() => importing.IsCompleted, TimeSpan.FromSeconds(30));
                importing.GetAwaiter().GetResult();
                Assert(searchModel.HasDocuments && searchModel.DocumentNames.Count == 1,
                    "The multi-megabyte file was not imported.");
                Assert(searchModel.ImportProgress == 100 &&
                       searchModel.ImportLabel.StartsWith("Imported", StringComparison.Ordinal),
                    "Large import did not report successful completion.");
                Assert(searchModel.DocumentsLabel.Contains("chunks", StringComparison.Ordinal) &&
                       searchModel.IndexLabel.Contains("batches", StringComparison.Ordinal),
                    "The UI should show the chunk and batch counts before starting Ollama.");
                Assert(!searchModel.HasIndex,
                    "Importing text must not manufacture semantic vectors without Ollama.");

                searchModel.Query = "FINAL_SEARCH_MARKER";
                searchModel.CompareCommand.Execute(null);
                PumpUntil(() => !searchModel.IsBusy, TimeSpan.FromSeconds(20));
                Assert(searchModel.KeywordResults.Count > 0 &&
                       searchModel.KeywordResults[0].Snippet.Contains("FINAL_SEARCH_MARKER", StringComparison.Ordinal),
                    "Keyword search must find the end of a multi-megabyte file.");
                Assert(searchWrongThreadNotifications == 0,
                    "Large-file import and searches must update the ViewModel on the WPF UI thread.");
                Console.WriteLine("PASS 3.5 MiB import, progress, preserved tail and large-file keyword search");

                // A legacy Windows TXT must display its real error in the import card
                // while keeping the previous large file, then succeed after choosing
                // the original code page without converting the file manually.
                byte[] legacyText = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1251)!
                    .GetBytes("Помилка запису в базу даних. З'єднання закрито.");
                File.WriteAllBytes(largePath, legacyText);
                searchModel.SelectedFileEncoding = searchModel.FileEncodingChoices.Single(
                    choice => choice.Encoding == TextFileEncoding.Auto);
                Task invalidImport = searchModel.ImportFilesAsync([largePath]);
                PumpUntil(() => invalidImport.IsCompleted, TimeSpan.FromSeconds(20));
                invalidImport.GetAwaiter().GetResult();
                Assert(searchModel.HasImportError &&
                       searchModel.ImportLabel.Contains("Import failed:", StringComparison.Ordinal) &&
                       searchModel.ImportLabel.Contains("Windows-1251", StringComparison.Ordinal),
                    "The import card must show the actual file decoding error and a usable retry suggestion.");
                Assert(searchModel.HasDocuments && searchModel.DocumentNames.Count == 1 &&
                       searchModel.DocumentsLabel.Contains("chunks", StringComparison.Ordinal),
                    "A rejected import must preserve the previous searchable document.");
                searchModel.SelectedFileEncoding = searchModel.FileEncodingChoices.Single(
                    choice => choice.Encoding == TextFileEncoding.Windows1251);
                Assert(encodingSelector.SelectedItem is FileEncodingChoice selected &&
                       selected.Encoding == TextFileEncoding.Windows1251,
                    "The encoding choice must remain synchronized with the WPF selector.");
                Task legacyImport = searchModel.ImportFilesAsync([largePath]);
                PumpUntil(() => legacyImport.IsCompleted, TimeSpan.FromSeconds(20));
                legacyImport.GetAwaiter().GetResult();
                Assert(!searchModel.HasImportError && searchModel.ImportProgress == 100 &&
                       searchModel.ImportLabel.StartsWith("Imported", StringComparison.Ordinal),
                    "Retrying with Windows-1251 should import the original file without errors.");
                searchModel.Query = "Помилка";
                searchModel.CompareCommand.Execute(null);
                PumpUntil(() => !searchModel.IsBusy, TimeSpan.FromSeconds(20));
                Assert(searchModel.KeywordResults.Count > 0 &&
                       searchModel.KeywordResults[0].Snippet.Contains("Помилка", StringComparison.Ordinal),
                    "The correctly decoded legacy file must be searchable.");
                Assert(searchWrongThreadNotifications == 0,
                    "Decoding errors, encoding selection and retry must remain on the WPF dispatcher.");
                Assert(searchModel.HasPreviousFiles && searchModel.RestoreFilesCommand.CanExecute(null),
                    "A successful file import must remember source paths for the next session.");

                // Simulate restarting: a brand-new ViewModel must restore file paths
                // and the encoding choice without reading source content automatically.
                using (var restarted = new SmartSearchViewModel())
                {
                    Assert(restarted.HasPreviousFiles && !restarted.HasDocuments,
                        "A new session must offer explicit restore without auto-opening private files.");
                    Task restoring = restarted.RestorePreviousFilesAsync();
                    PumpUntil(() => restoring.IsCompleted, TimeSpan.FromSeconds(20));
                    restoring.GetAwaiter().GetResult();
                    Assert(restarted.HasDocuments && !restarted.HasImportError &&
                           restarted.SelectedFileEncoding.Encoding == TextFileEncoding.Windows1251,
                        "Restored files must preserve the original decoding choice.");
                    restarted.Query = "Помилка";
                    restarted.CompareCommand.Execute(null);
                    PumpUntil(() => !restarted.IsBusy, TimeSpan.FromSeconds(20));
                    Assert(restarted.KeywordResults.Count > 0,
                        "Restored files must be searchable without a new file picker.");
                }

                // Regression: a UTF-8 Ukrainian story must not be accepted as
                // Windows-1251 just because the old file used that codepage.
                const string foxText = "Жив собі Лис Микита, хитрий та розумний. ";
                File.WriteAllText(largePath, string.Concat(Enumerable.Repeat(foxText, 30)),
                    new System.Text.UTF8Encoding(false));
                searchModel.SelectedFileEncoding = searchModel.FileEncodingChoices.Single(
                    choice => choice.Encoding == TextFileEncoding.Windows1251);
                Task wrongStory = searchModel.ImportFilesAsync([largePath]);
                PumpUntil(() => wrongStory.IsCompleted, TimeSpan.FromSeconds(20));
                wrongStory.GetAwaiter().GetResult();
                Assert(searchModel.HasImportError &&
                       searchModel.ImportLabel.Contains("UTF-8", StringComparison.Ordinal) &&
                       searchModel.DocumentPreview.Contains("Помилка", StringComparison.Ordinal),
                    "Wrong legacy decoding must fail without replacing the old documents or their preview.");

                searchModel.SelectedFileEncoding = searchModel.FileEncodingChoices.Single(
                    choice => choice.Encoding == TextFileEncoding.Auto);
                Task correctStory = searchModel.ImportFilesAsync([largePath]);
                PumpUntil(() => correctStory.IsCompleted, TimeSpan.FromSeconds(20));
                correctStory.GetAwaiter().GetResult();
                Assert(!searchModel.HasImportError &&
                       searchModel.DocumentPreview.Contains("Лис Микита", StringComparison.Ordinal) &&
                       !searchModel.DocumentPreview.Contains("Рџ", StringComparison.Ordinal),
                    "A correctly decoded Ukrainian TXT must be readable before indexing.");
                Assert(smartSearch.FindName("DocumentPreviewText") is TextBlock preview &&
                       preview.Text.Contains("Лис Микита", StringComparison.Ordinal),
                    "WPF must show a readable decoded sample so the user can verify it.");
                Assert(smartSearch.FindName("DeepSearchToggle") is CheckBox deepToggle &&
                       deepToggle.IsChecked == false,
                    "Fast one-call document answering must be the default.");
                Console.WriteLine("PASS UTF-8 vs Windows-1251 detection and readable preview in WPF");
                Console.WriteLine("PASS saved file selection and restore after restarting the ViewModel");
                Console.WriteLine("PASS failed TXT import diagnostics and Windows-1251 retry in WPF");
            }
            finally
            {
                File.Delete(largePath);
            }
            window.NavigateToNeuralLab();
            Assert(smartSearch.Visibility == Visibility.Collapsed,
                "Neural Network Lab navigation failed.");
            Console.WriteLine("PASS Smart Search navigation, demo and offline keyword comparison");
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

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!completed())
        {
            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s while pumping WPF.");
            }
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(2);
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
