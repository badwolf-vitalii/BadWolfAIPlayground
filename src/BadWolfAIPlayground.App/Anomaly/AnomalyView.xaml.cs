using System.Windows.Controls;

namespace BadWolfAIPlayground.App.Anomaly;

public partial class AnomalyView : UserControl
{
    public AnomalyViewModel AnalysisModel { get; }

    public AnomalyView()
    {
        InitializeComponent();
        AnalysisModel = new AnomalyViewModel();
        DataContext = AnalysisModel;
    }
}
