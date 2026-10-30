using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BadWolfAIPlayground.App;
using BadWolfAIPlayground.App.ViewModels;
using BadWolfAIPlayground.App.Search;
using BadWolfAIPlayground.App.Anomaly;
using BadWolfAIPlayground.Anomaly;
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
            Assert(smartSearch.FindName("AnswerModelSelector") is ComboBox modelSelector &&
                   modelSelector.Style is not null &&
                   searchModel.AnswerModelChoices.Count > 0 &&
                   searchModel.SelectedAnswerModel.Name == OllamaAnswerProvider.DefaultModel,
                "The model selector must start with the existing default and a dark readable style.");
            Assert(smartSearch.FindName("RefreshAnswerModelsButton") is Button,
                "The UI must allow refreshing the locally installed Ollama models.");
            Assert(smartSearch.FindName("AnswerMetricsTextBlock") is TextBlock,
                "The UI must provide a visible generation-performance panel.");
            Assert(smartSearch.FindName("AdaptiveContextTextBlock") is TextBlock &&
                   searchModel.AdaptiveContextSummary.Contains("3 short passages", StringComparison.Ordinal),
                "The user must see an accurate adaptive-context summary near timing metrics.");
            Assert(searchModel.ProcessorStatus.Contains("not checked", StringComparison.OrdinalIgnoreCase),
                "CPU/GPU status must be unknown before Ollama provides runtime facts.");
            Console.WriteLine("PASS local model picker and performance panel with no fake CPU/GPU detection");

            // Exercise the actual model picker and local status flow without
            // requiring a Windows runner with Ollama installed.
            using (var fakeHttp = new HttpClient(new LocalOllamaMockHandler())
            {
                BaseAddress = new Uri(OllamaEmbeddingProvider.LocalEndpoint)
            })
            using (var runtime = new OllamaRuntimeClient(fakeHttp))
            using (var answer = new OllamaAnswerProvider())
            using (var selection = new SmartSearchViewModel(
                answerModel: answer, ollamaRuntime: runtime))
            {
                Task refreshing = selection.RefreshAnswerModelsAsync();
                PumpUntil(() => refreshing.IsCompleted, TimeSpan.FromSeconds(12));
                refreshing.GetAwaiter().GetResult();
                Assert(selection.AnswerModelChoices.Count == 2 &&
                       selection.AnswerModelChoices.All(model =>
                           !model.Name.Contains("embeddinggemma", StringComparison.OrdinalIgnoreCase)),
                    "The model selector must show installed chat models but exclude embedding-only models.");
                Assert(selection.ProcessorStatus.Contains("100% CPU", StringComparison.Ordinal),
                    "A running CPU-only Qwen model must be labeled from /api/ps.");
                selection.SelectedAnswerModel = selection.AnswerModelChoices.Single(model =>
                    model.Name == "qwen2.5:0.5b-instruct");
                Assert(answer.ModelName == "qwen2.5:0.5b-instruct" &&
                       selection.SelectedAnswerModel.Name == answer.ModelName &&
                       selection.ProcessorStatus.Contains("not checked", StringComparison.OrdinalIgnoreCase),
                    "Changing the WPF selection must switch the actual chat provider and invalidate old telemetry.");
                Console.WriteLine("PASS local Ollama /api/tags + /api/ps discovery and WPF model switching");
            }
            Assert(smartSearch.FindName("AnswerTextBlock") is TextBlock,
                "The grounded answer must have a dedicated readable area.");
            Assert(smartSearch.FindName("RetrievedEvidenceExpander") is Expander,
                "The QA UI must expose actual model evidence even when no answer can be verified.");
            Assert(searchModel.RetrievedPassages.Count == 0 &&
                   searchModel.RetrievalDetails.Contains("Ask document", StringComparison.Ordinal),
                "Evidence inspector must start empty without fabricated passages.");
            Assert(searchModel.AnswerSources.Count == 0,
                "An unloaded document must not display fabricated citations.");
            Assert(Enum.IsDefined(AnswerOutcome.UnsubstantiatedAnswer),
                "The UI must distinguish an irrelevant verbatim citation from a fabricated quote.");
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

            window.NavigateToAnomalyLab();
            var anomalyView = FindDescendants<AnomalyView>(window).Single();
            AnomalyViewModel analyzer = anomalyView.AnalysisModel;
            Assert(anomalyView.Visibility == Visibility.Visible &&
                   smartSearch.Visibility == Visibility.Collapsed &&
                   anomalyView.FindName("ChooseAnomalySourceButton") is Button &&
                   anomalyView.FindName("UseAnomalyDemoButton") is Button &&
                   anomalyView.FindName("SelectAnomalyLogButton") is Button &&
                   anomalyView.FindName("SelectAnomalyFolderButton") is Button &&
                   anomalyView.FindName("AnomalySourceFiles") is ItemsControl &&
                   anomalyView.FindName("AnomalyExceptionEntries") is ItemsControl &&
                   anomalyView.FindName("AnomalyOperationGroups") is ItemsControl &&
                   anomalyView.FindName("AnomalySkippedFilesExpander") is Expander &&
                   anomalyView.FindName("AnomalyFilesExpander") is Expander &&
                   anomalyView.FindName("EnableAllAnomalyLogsButton") is Button &&
                   anomalyView.FindName("DisableAllAnomalyLogsButton") is Button &&
                   anomalyView.FindName("LocateNotepadPlusPlusButton") is Button &&
                   anomalyView.FindName("AnomalySensitivitySlider") is Slider &&
                   anomalyView.FindName("ReanalyzeLogButton") is Button,
                "Anomaly lab must be visible and allow local log selection and sensitivity.");

            Task demoAnalysis = analyzer.UseDemoAsync();
            PumpUntil(() => demoAnalysis.IsCompleted, TimeSpan.FromSeconds(20));
            demoAnalysis.GetAwaiter().GetResult();
            Assert(analyzer.FindingCount >= 2 && analyzer.EventCount >= 200 &&
                   analyzer.Timeline.Count == 36 &&
                   analyzer.Findings.Any(f => f.Kind == "BURST") &&
                   analyzer.Findings.Any(f => f.Kind == "RARE ERROR") &&
                   analyzer.ExceptionCount == 1 &&
                   analyzer.ExceptionEntries.Count == 1 &&
                   analyzer.ExceptionEntries[0].Context.Contains("System.IO.IOException",
                       StringComparison.Ordinal) &&
                   analyzer.ExceptionEntries[0].SourceLine > 0 &&
                   !analyzer.OpenExceptionCommand.CanExecute(analyzer.ExceptionEntries[0]),
                "The synthetic demo must visibly list one multiline stack trace " +
                "while keeping the external editor disabled for demo-only data.");
            anomalyView.UpdateLayout();
            Assert(FindDescendants<Expander>(anomalyView)
                    .Any(item => item.DataContext is AnomalyExceptionRow row &&
                                 row.Context.Contains("Storage.cs:line 82", StringComparison.Ordinal)),
                "The new exception list must materialize actual WPF expanders.");
            // The same generic scanner can be fed local, non-proprietary
            // source. It never needs to fetch a private repository in CI.
            string contextDir = Path.Combine(Path.GetTempPath(),
                "BadWolfAIPlayground-AnomalyContext-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(contextDir);
            try
            {
                File.WriteAllText(Path.Combine(contextDir, "DemoWorker.cs"),
                    "class DemoWorker { void Run(int id) { " +
                    "logger.Error($\"Retry timeout on connection {id}\"); } }");
                Task sourceLoad = analyzer.LoadSourceContextAsync(contextDir);
                PumpUntil(() => sourceLoad.IsCompleted, TimeSpan.FromSeconds(20));
                sourceLoad.GetAwaiter().GetResult();
                Assert(analyzer.SourceContextStatus.Contains("logging templates",
                           StringComparison.OrdinalIgnoreCase) &&
                       analyzer.Findings.Any(f =>
                           f.Kind == "BURST" &&
                           f.SourceHint.Contains("DemoWorker.cs", StringComparison.Ordinal)),
                    "Optional source context must connect a demo error to its local C# statement.");
            }
            finally
            {
                Directory.Delete(contextDir, recursive: true);
            }

            double before = analyzer.Sensitivity;
            analyzer.Sensitivity = 4.5;
            Task reanalysis = analyzer.AnalyzeAsync();
            PumpUntil(() => reanalysis.IsCompleted, TimeSpan.FromSeconds(20));
            reanalysis.GetAwaiter().GetResult();
            Assert(!analyzer.Busy && analyzer.EventCount >= 200 &&
                   analyzer.Sensitivity > before &&
                   analyzer.StatusText.Contains("locally", StringComparison.OrdinalIgnoreCase),
                "Reanalysis must update UI locally without model inference.");

            // Exception context must be visible even when there is no anomaly.
            Task nonAnomalous = analyzer.ImportTextAsync("Standalone context example",
                "2026-10-29 09:00:00 INFO Single diagnostic\n" +
                "System.InvalidOperationException: Synthetic diagnostics\n" +
                "   at Demo.Engine.Check()\n");
            PumpUntil(() => nonAnomalous.IsCompleted, TimeSpan.FromSeconds(20));
            nonAnomalous.GetAwaiter().GetResult();
            Assert(analyzer.FindingCount == 0 && analyzer.ExceptionCount == 1 &&
                   analyzer.ExceptionEntries.Single().Severity == "INFO" &&
                   analyzer.ExceptionSummary.Contains("not necessarily statistical anomalies",
                       StringComparison.Ordinal),
                "The independent exception list must not depend on anomaly thresholds.");

            // Structured cancellation must be visible even when every event
            // is INFO and there are no statistical findings.
            Task traceTask = analyzer.ImportTextAsync("Synthetic TOM_OP v1",
                "2026-10-30 16:29:00 INFO TOM_OP v=1 event=step step=card_preview_ready session_id=s1 transaction_id=- flow_id=f1\n" +
                "2026-10-30 16:29:10 INFO TOM_OP v=1 event=step step=card_issuing_cancelled session_id=s1 transaction_id=- flow_id=f1 category=issuing reason=user\n" +
                "2026-10-30 16:29:11 INFO TOM_OP v=1 event=step step=card_commit_status session_id=s1 transaction_id=2849 flow_id=f1 category=GenericError success=false\n" +
                "2026-10-30 16:29:12 INFO TOM_OP v=1 event=end operation=CardEmissionCommit session_id=s1 transaction_id=2849 flow_id=f1 result=true");
            PumpUntil(() => traceTask.IsCompleted, TimeSpan.FromSeconds(20));
            traceTask.GetAwaiter().GetResult();
            Assert(analyzer.FindingCount == 0 &&
                   analyzer.OperationGroupCount == 1 &&
                   analyzer.OperationGroups.Single().Outcome == "Cancelled by operator" &&
                   analyzer.OperationGroups.Single().Events.Count == 4 &&
                   analyzer.OperationGroups.Single().Explanation.Contains(
                       "GenericError was committed", StringComparison.Ordinal) &&
                   !analyzer.OpenOperationEventCommand.CanExecute(
                       analyzer.OperationGroups.Single().Events[0]),
                "Structured user cancellation must be visible without statistical anomalies.");
            anomalyView.UpdateLayout();
            Assert(FindDescendants<Expander>(anomalyView).Any(expander =>
                    expander.DataContext is AnomalyOperationGroupRow row &&
                    row.Outcome == "Cancelled by operator"),
                "Lifecycle results must render a real WPF expander.");

            // Test importing a whole application Logs folder, including old/
            // and duplicate NLog App/component targets. No work data in CI.
            string logFolder = Path.Combine(Path.GetTempPath(),
                "BadWolfAIPlayground-MultiLogs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(logFolder);
            try
            {
                Directory.CreateDirectory(Path.Combine(logFolder, "old"));
                var appLog = new StringBuilder();
                var salesLog = new StringBuilder();
                var deviceLog = new StringBuilder();
                for (int minute = 0; minute < 36; minute++)
                {
                    string prefix = $"2026-10-29 09:{minute:00}:";
                    appLog.AppendLine(prefix + "00.0000 INFO App alive");
                    salesLog.AppendLine(prefix + "01.0000 ERROR Connection timeout");
                    deviceLog.AppendLine(prefix + "02.0000 INFO Reader available");
                    if (minute == 24)
                    {
                        for (int k = 0; k < 12; k++)
                        {
                            string eventText =
                                prefix + $"{20 + k:00}.0000 ERROR Sale request failed";
                            appLog.AppendLine(eventText);
                            salesLog.AppendLine(eventText);
                            if (k == 0)
                            {
                                // Synthetic NLog exception continuation in both targets.
                                appLog.AppendLine("System.TimeoutException: Retry exhausted");
                                salesLog.AppendLine("System.TimeoutException: Retry exhausted");
                                appLog.AppendLine("   at Example.Worker.Send()");
                                salesLog.AppendLine("   at Example.Worker.Send()");
                            }
                        }
                    }
                }
                File.WriteAllText(Path.Combine(logFolder, "App_2026-10-29.log"),
                    appLog.ToString());
                File.WriteAllText(Path.Combine(logFolder, "SaleController_2026-10-29.log"),
                    salesLog.ToString());
                File.WriteAllText(Path.Combine(logFolder, "old", "QRReader_2026-10-29.log"),
                    deviceLog.ToString());
                Task folderTask = analyzer.ImportFolderAsync(logFolder);
                PumpUntil(() => folderTask.IsCompleted, TimeSpan.FromSeconds(20));
                folderTask.GetAwaiter().GetResult();
                Assert(analyzer.SourceFiles.Count == 3 &&
                       analyzer.DuplicateCopies == 12 &&
                       analyzer.EventCount == 120 &&
                       analyzer.ProblemCount == 48 &&
                       analyzer.ExceptionCount == 1 &&
                       analyzer.ExceptionEntries.Single().SourceFile.StartsWith(
                           "SaleController_", StringComparison.Ordinal) &&
                       analyzer.OpenExceptionCommand.CanExecute(analyzer.ExceptionEntries[0]) &&
                       analyzer.Findings.Any(f =>
                           f.Kind == "BURST" &&
                           f.Evidence.Contains("SaleController_2026-10-29.log:",
                                StringComparison.Ordinal) &&
                            f.Context.Contains("System.TimeoutException",
                                StringComparison.Ordinal) &&
                            f.ContextVisibility == Visibility.Visible),
                    "Multi-file analysis must keep original source lines and " +
                    "not count duplicated App NLog targets twice.");
                Assert(analyzer.SourceFiles.Any(f =>
                           f.RelativePath.StartsWith(
                               "old" + Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase) &&
                           f.Component == "QRReader"),
                    "WPF per-file list must include archive subfolders.");

                anomalyView.UpdateLayout();
                Assert(FindDescendants<Expander>(anomalyView).Any(expander =>
                    expander.Header is string header &&
                    header.StartsWith("Exception / stack trace", StringComparison.Ordinal) &&
                    expander.DataContext is AnomalyFindingRow row &&
                    row.Context.Contains("System.TimeoutException", StringComparison.Ordinal)),
                    "WPF findings must render expandable exception context.");

                // Verify WPF's real checkbox -> ViewModel selection binding.
                var fileExpander = (Expander)anomalyView.FindName("AnomalyFilesExpander");
                fileExpander.IsExpanded = true;
                anomalyView.UpdateLayout();
                var salesCheck = FindDescendants<CheckBox>(anomalyView)
                    .FirstOrDefault(box =>
                        box.DataContext is AnomalySourceFileRow row &&
                        row.Component == "SaleController");
                Assert(salesCheck is not null && salesCheck.IsChecked == true,
                    "The source file's Include checkbox must be visible and checked.");
                salesCheck!.IsChecked = false;
                PumpUntil(() => !analyzer.Busy && analyzer.EventCount == 84,
                    TimeSpan.FromSeconds(20));
                Assert(analyzer.EventCount == 84 &&
                       analyzer.ProblemCount == 12 &&
                       analyzer.DuplicateCopies == 0 &&
                       analyzer.SourceFiles.Count(f => f.Included) == 2 &&
                       analyzer.ExceptionCount == 1 &&
                       analyzer.ExceptionEntries.Single().SourceFile.StartsWith(
                           "App_", StringComparison.Ordinal) &&
                       analyzer.Findings.Any(f =>
                           f.Evidence.Contains("App_2026-10-29.log:",
                                StringComparison.Ordinal) &&
                            f.Context.Contains("System.TimeoutException",
                                StringComparison.Ordinal)),
                    "Disabling SaleController must retain App's original copy, " +
                    "recompute true event counts and update evidence origin.");

                // Switching from App to Sales must recover Sales' copy in the
                // rebuilt raw-event view, not a stale pre-merged alias.
                Task salesOnly = analyzer.SetIncludedFilesAsync(
                    ["SaleController_2026-10-29.log"]);
                PumpUntil(() => salesOnly.IsCompleted, TimeSpan.FromSeconds(20));
                salesOnly.GetAwaiter().GetResult();
                Assert(analyzer.EventCount == 48 &&
                       analyzer.ProblemCount == 48 &&
                       analyzer.DuplicateCopies == 0 &&
                       analyzer.SourceFiles.Count(f => f.Included) == 1 &&
                       analyzer.ExceptionCount == 1 &&
                       analyzer.ExceptionEntries.Single().SourceFile.StartsWith(
                           "SaleController_", StringComparison.Ordinal) &&
                       analyzer.Findings.Any(f =>
                           f.Evidence.Contains("SaleController_2026-10-29.log:",
                               StringComparison.Ordinal)),
                    "Selecting Sales alone must keep its original errors and source lines.");

                Task noLogs = analyzer.SetIncludedFilesAsync([]);
                PumpUntil(() => noLogs.IsCompleted, TimeSpan.FromSeconds(20));
                noLogs.GetAwaiter().GetResult();
                Assert(analyzer.EventCount == 0 &&
                       analyzer.FindingCount == 0 &&
                       analyzer.ExceptionCount == 0 &&
                       analyzer.ExceptionEntries.Count == 0 &&
                       analyzer.Timeline.Count == 0 &&
                       analyzer.StatusText.Contains("No logs enabled",
                           StringComparison.Ordinal),
                    "Exclude all should visibly show zero events without inventing an incident.");

                Task restore = analyzer.SetIncludedFilesAsync(
                    analyzer.SourceFiles.Select(f => f.RelativePath).ToArray());
                PumpUntil(() => restore.IsCompleted, TimeSpan.FromSeconds(20));
                restore.GetAwaiter().GetResult();
                Assert(analyzer.EventCount == 120 &&
                       analyzer.ProblemCount == 48 &&
                       analyzer.DuplicateCopies == 12 &&
                       analyzer.ExceptionCount == 1 &&
                       analyzer.SourceFiles.All(f => f.Included),
                    "Include all must restore exact pre-filter counts and provenance.");

                // No Notepad++ is started in CI; verify the exact safe
                // command-line construction and original-file resolution.
                (string resolved, int line) = analyzer.ResolveLogLocation(
                    "SaleController_2026-10-29.log", 44);
                var startInfo = NotepadPlusPlusLauncher.CreateStartInfo(
                    @"C:\Program Files\Notepad++\notepad++.exe", resolved, line);
                Assert(resolved == Path.GetFullPath(Path.Combine(
                           logFolder, "SaleController_2026-10-29.log")) &&
                       line == 44 && !startInfo.UseShellExecute &&
                       startInfo.ArgumentList.Count == 2 &&
                       startInfo.ArgumentList[0] == "-n44" &&
                       startInfo.ArgumentList[1] == resolved,
                    "Notepad++ jump must preserve the exact physical line " +
                    "and pass paths as separately quoted arguments.");
                bool rejectedOutside = false;
                try { analyzer.ResolveLogLocation("../secret.log", 1); }
                catch (InvalidOperationException) { rejectedOutside = true; }
                Assert(rejectedOutside, "Open action must reject unimported paths.");

                anomalyView.UpdateLayout();
                Assert(FindDescendants<Button>(anomalyView)
                        .Any(button => button.Content is string label &&
                            label == "Open in Notepad++" &&
                            button.Command is not null),
                    "Each evidence card must expose a real Notepad++ command.");

                // A large profiler must not block valid files, and omitted
                // sources must be disclosed with their original path/size.
                string oversizedProfiler =
                    Path.Combine(logFolder, "WebClientProfiler_2026-10-29.log");
                using (var stream = new FileStream(oversizedProfiler, FileMode.Create))
                    stream.SetLength(LogFolderImporter.MaxFileBytes + 1);
                Task partialFolder = analyzer.ImportFolderAsync(logFolder);
                PumpUntil(() => partialFolder.IsCompleted, TimeSpan.FromSeconds(20));
                partialFolder.GetAwaiter().GetResult();
                Assert(analyzer.EventCount == 120 &&
                       analyzer.SourceFiles.Count == 3 &&
                       analyzer.SkippedSourceFiles.Count == 1 &&
                       analyzer.SkippedSourceFiles[0].RelativePath ==
                           "WebClientProfiler_2026-10-29.log" &&
                       analyzer.SkippedFilesVisibility == Visibility.Visible &&
                       analyzer.SkippedFilesSummary.Contains("NOT analyzed",
                           StringComparison.Ordinal) &&
                       analyzer.SourceFilesSummary.Contains("PARTIAL",
                           StringComparison.Ordinal) &&
                       analyzer.StatusText.Contains("Partial import",
                           StringComparison.Ordinal) &&
                       analyzer.Preview.Contains("PARTIAL IMPORT",
                           StringComparison.Ordinal),
                    "Partial import must retain valid evidence and disclose skipped data.");
                var skippedPanel =
                    (Expander)anomalyView.FindName("AnomalySkippedFilesExpander");
                anomalyView.UpdateLayout();
                Assert(skippedPanel.Visibility == Visibility.Visible &&
                       skippedPanel.IsExpanded,
                    "Skipped data must be shown prominently, not hidden.");
                bool excludedProfiler = false;
                try { analyzer.ResolveLogLocation("WebClientProfiler_2026-10-29.log", 1); }
                catch (InvalidOperationException) { excludedProfiler = true; }
                Assert(excludedProfiler,
                    "A skipped profiler cannot be opened as an imported source.");
                File.Delete(oversizedProfiler);

                Task partialReanalysis = analyzer.AnalyzeAsync();
                PumpUntil(() => partialReanalysis.IsCompleted, TimeSpan.FromSeconds(20));
                partialReanalysis.GetAwaiter().GetResult();
                Assert(analyzer.EventCount == 120 &&
                       analyzer.SkippedSourceFiles.Count == 1 &&
                       analyzer.SkippedFilesVisibility == Visibility.Visible &&
                       analyzer.SkippedFilesSummary.Contains("NOT analyzed",
                           StringComparison.Ordinal),
                    "A later reanalysis must not hide the previously excluded files.");

                int validCount = analyzer.EventCount;
                Task badFolder = analyzer.ImportFolderAsync(
                    Path.Combine(logFolder, "missing"));
                PumpUntil(() => badFolder.IsCompleted, TimeSpan.FromSeconds(20));
                badFolder.GetAwaiter().GetResult();
                Assert(analyzer.EventCount == validCount &&
                       analyzer.SourceFiles.Count == 3 &&
                       analyzer.SkippedSourceFiles.Count == 1 &&
                       analyzer.SkippedFilesVisibility == Visibility.Visible &&
                       analyzer.StatusText.Contains("failed",
                           StringComparison.OrdinalIgnoreCase),
                    "Failed folder import must preserve last valid result and warnings.");

                Task switchToDemo = analyzer.UseDemoAsync();
                PumpUntil(() => switchToDemo.IsCompleted, TimeSpan.FromSeconds(20));
                switchToDemo.GetAwaiter().GetResult();
                Assert(analyzer.SkippedSourceFiles.Count == 0 &&
                       analyzer.SkippedFilesVisibility == Visibility.Collapsed,
                    "A later successful import without exclusions must clear stale warnings.");
            }
            finally
            {
                Directory.Delete(logFolder, recursive: true);
            }

            window.NavigateToNeuralLab();
            Assert(anomalyView.Visibility == Visibility.Collapsed,
                "Anomaly navigation must return to the neural lab.");
            Console.WriteLine("PASS Anomaly Detection WPF demo, full Logs folder, provenance, errors and reanalysis");
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


internal sealed class LocalOllamaMockHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string json = request.RequestUri?.AbsolutePath switch
        {
            "/api/tags" => """
                {"models":[
                    {"name":"embeddinggemma:latest","size":692000000},
                    {"name":"qwen2.5:0.5b-instruct","size":398000000},
                    {"name":"qwen3:4b-instruct","size":3900000000}
                ]}
                """,
            "/api/ps" => """
                {"models":[{"name":"qwen3:4b-instruct",
                    "size":3900000000,"size_vram":0,"context_length":8192}]}
                """,
            _ => throw new InvalidOperationException("Only local Ollama catalogue endpoints are expected.")
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
