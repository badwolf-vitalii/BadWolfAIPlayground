using System.Windows.Controls;

namespace BadWolfAIPlayground.App.Search;

public partial class SmartSearchView : UserControl
{
    public SmartSearchViewModel SearchModel { get; }

    public SmartSearchView()
    {
        InitializeComponent();
        SearchModel = new SmartSearchViewModel();
        DataContext = SearchModel;
    }
}
