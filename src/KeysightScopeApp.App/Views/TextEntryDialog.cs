using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace KeysightScopeApp.App.Views;

internal sealed class TextEntryDialog : Window
{
    private readonly TextBox input;

    public TextEntryDialog(
        Window? owner,
        string title,
        string prompt,
        string initialValue)
    {
        Owner = owner;
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8)
        });

        input = new TextBox
        {
            Text = initialValue,
            MinWidth = 380,
            MaxLength = 200
        };
        Grid.SetRow(input, 1);
        root.Children.Add(input);

        var buttons = new UniformGrid
        {
            Columns = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = 200,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var confirm = new Button { Content = "确定", IsDefault = true };
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            DialogResult = true;
        };
        var cancel = new Button { Content = "取消", IsCancel = true };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            DialogResult = false;
            e.Handled = true;
        };
    }

    public string Value => input.Text.Trim();
}
