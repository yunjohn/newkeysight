using System.Windows;
using KeysightScopeApp.Core;
using KeysightScopeApp.App.ViewModels;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.App.Views;

public partial class AdvancedAnalysisView : System.Windows.Controls.UserControl
{
    private readonly AdvancedAnalysisViewModel viewModel;
    private bool initialized;

    public AdvancedAnalysisView(AdvancedAnalysisViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            await viewModel.InitializeAsync();
        };
    }
    public void SetBundle(WaveformBundle bundle, string instrumentId = "")
    {
        viewModel.InstrumentId = instrumentId;
        viewModel.SetBundle(bundle);
    }

    public void Dispose() => viewModel.Dispose();
}
