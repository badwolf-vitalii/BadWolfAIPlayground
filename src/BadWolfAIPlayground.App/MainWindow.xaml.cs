using System.Windows;
using System.Reflection;
using BadWolfAIPlayground.App.Services;
using BadWolfAIPlayground.App.Search;
using BadWolfAIPlayground.App.Anomaly;
using System.Windows.Media;
using BadWolfAIPlayground.App.ViewModels;

namespace BadWolfAIPlayground.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Use assembly metadata instead of a hard-coded sidebar release label.
        var assembly = typeof(MainWindow).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        string version = informationalVersion?.Split('+')[0] ??
            assembly.GetName().Version?.ToString(3) ?? "unknown";
        AppVersionText.Text = $"v{version}  ·  .NET 8";
        DataContext = new MainViewModel(new ModelFileService());
        Closed += (_, _) =>
        {
            SmartSearchContent.SearchModel.Dispose();
            AnomalyContent.AnalysisModel.Dispose();
        };
    }

    private void ShowNeuralLab_Click(object sender, RoutedEventArgs e) => NavigateToNeuralLab();

    private void ShowSmartSearch_Click(object sender, RoutedEventArgs e) => NavigateToSmartSearch();

    private void ShowAnomaly_Click(object sender, RoutedEventArgs e) => NavigateToAnomalyLab();

    public void NavigateToNeuralLab()
    {
        NeuralLabContent.Visibility = Visibility.Visible;
        SmartSearchContent.Visibility = Visibility.Collapsed;
        AnomalyContent.Visibility = Visibility.Collapsed;
        NeuralNavButton.Background = new SolidColorBrush(Color.FromRgb(23, 56, 93));
        SmartNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
        AnomalyNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
    }

    public void NavigateToSmartSearch()
    {
        NeuralLabContent.Visibility = Visibility.Collapsed;
        SmartSearchContent.Visibility = Visibility.Visible;
        AnomalyContent.Visibility = Visibility.Collapsed;
        NeuralNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
        SmartNavButton.Background = new SolidColorBrush(Color.FromRgb(23, 56, 93));
        AnomalyNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
    }

    public void NavigateToAnomalyLab()
    {
        NeuralLabContent.Visibility = Visibility.Collapsed;
        SmartSearchContent.Visibility = Visibility.Collapsed;
        AnomalyContent.Visibility = Visibility.Visible;
        NeuralNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
        SmartNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
        AnomalyNavButton.Background = new SolidColorBrush(Color.FromRgb(23, 56, 93));
    }
}
