using System.Windows;
using BadWolfAIPlayground.App.Services;
using BadWolfAIPlayground.App.Search;
using System.Windows.Media;
using BadWolfAIPlayground.App.ViewModels;

namespace BadWolfAIPlayground.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new ModelFileService());
        Closed += (_, _) => SmartSearchContent.SearchModel.Dispose();
    }

    private void ShowNeuralLab_Click(object sender, RoutedEventArgs e) => NavigateToNeuralLab();

    private void ShowSmartSearch_Click(object sender, RoutedEventArgs e) => NavigateToSmartSearch();

    public void NavigateToNeuralLab()
    {
        NeuralLabContent.Visibility = Visibility.Visible;
        SmartSearchContent.Visibility = Visibility.Collapsed;
        NeuralNavButton.Background = new SolidColorBrush(Color.FromRgb(23, 56, 93));
        SmartNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
    }

    public void NavigateToSmartSearch()
    {
        NeuralLabContent.Visibility = Visibility.Collapsed;
        SmartSearchContent.Visibility = Visibility.Visible;
        NeuralNavButton.Background = new SolidColorBrush(Color.FromRgb(12, 23, 41));
        SmartNavButton.Background = new SolidColorBrush(Color.FromRgb(23, 56, 93));
    }
}
