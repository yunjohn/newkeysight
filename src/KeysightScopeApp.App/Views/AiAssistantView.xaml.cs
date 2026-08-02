using System.Windows;
using System.Windows.Controls;
using KeysightScopeApp.App.ViewModels;

namespace KeysightScopeApp.App.Views;

public partial class AiAssistantView : UserControl
{
    private readonly AiAssistantViewModel viewModel;

    public AiAssistantView(AiAssistantViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += AiAssistantView_Loaded;
    }

    private void AiAssistantView_Loaded(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = viewModel.GetApiKey();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) viewModel.SetApiKey(box.Password);
    }

    private async void HistoricalWaveform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try { await viewModel.RefreshInputChannelsAsync(); }
        catch { /* 请求时会显示具体的文件读取错误。 */ }
    }

    private async void InputSource_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.RefreshInputChannelsAsync(); }
        catch { /* 请求时会显示具体的文件读取错误。 */ }
    }
}
