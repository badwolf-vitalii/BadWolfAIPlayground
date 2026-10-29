using System.Windows;
using BadWolfAIPlayground.App.Services;
using BadWolfAIPlayground.App.ViewModels;

namespace BadWolfAIPlayground.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new ModelFileService());
    }
}
