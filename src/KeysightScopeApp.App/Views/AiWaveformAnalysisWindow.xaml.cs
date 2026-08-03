using System.Windows;
using KeysightScopeApp.App.ViewModels;

namespace KeysightScopeApp.App.Views;

public partial class AiWaveformAnalysisWindow : Window
{
    public AiWaveformAnalysisWindow(AiWaveformAnalysisViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
