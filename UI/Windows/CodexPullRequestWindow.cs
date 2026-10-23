using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed record GitHubPullRequestDraft(string Title, string Body, bool IsDraft);

internal sealed class CodexPullRequestWindow : Window
{
    private readonly TextBox _title = new();
    private readonly TextBox _body = new();
    private readonly CheckBox _draft = new() { Content = "Создать как черновик" };

    public GitHubPullRequestDraft? Result { get; private set; }

    public CodexPullRequestWindow(string branch)
    {
        Title = "Создать Pull Request";
        Width = 680;
        Height = 560;
        MinWidth = 520;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout(branch);
        Loaded += (_, _) =>
        {
            _title.Focus();
            _title.SelectAll();
        };
    }

    private UIElement BuildLayout(string branch)
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Создать Pull Request",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });

        var titlePanel = new StackPanel { Margin = new Thickness(0, 15, 0, 12) };
        titlePanel.Children.Add(new TextBlock
        {
            Text = $"Ветка: {branch}",
            FontSize = 10,
            Foreground = Brush("#B0B5BE"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = branch,
            Margin = new Thickness(0, 0, 0, 9)
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "ЗАГОЛОВОК",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8D95A2"),
            Margin = new Thickness(0, 0, 0, 5)
        });
        _title.Height = 38;
        _title.MaxLength = 256;
        _title.Padding = new Thickness(10, 7, 10, 5);
        _title.Background = Brush("#181A1E");
        _title.Foreground = Brush("#F1F3F5");
        _title.BorderBrush = Brush("#314454");
        _title.BorderThickness = new Thickness(1);
        _title.Text = ToTitle(branch);
        titlePanel.Children.Add(_title);
        Grid.SetRow(titlePanel, 1);
        root.Children.Add(titlePanel);

        var bodyPanel = new Grid();
        bodyPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        bodyPanel.Children.Add(new TextBlock
        {
            Text = "ОПИСАНИЕ",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8D95A2"),
            Margin = new Thickness(0, 0, 0, 5)
        });
        _body.AcceptsReturn = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.Padding = new Thickness(10, 8, 10, 8);
        _body.Background = Brush("#181A1E");
        _body.Foreground = Brush("#F1F3F5");
        _body.BorderBrush = Brush("#314454");
        _body.BorderThickness = new Thickness(1);
        _body.Text = "## Summary\n\n## Testing\n";
        Grid.SetRow(_body, 1);
        bodyPanel.Children.Add(_body);
        Grid.SetRow(bodyPanel, 2);
        root.Children.Add(bodyPanel);

        _draft.Foreground = Brush("#C5D0D8");
        _draft.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(_draft, 3);
        root.Children.Add(_draft);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = MakeButton("Отмена");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var create = MakeButton("Создать PR");
        create.IsDefault = true;
        create.Background = Brush("#204139");
        create.Foreground = Brush("#92E5C7");
        create.Click += (_, _) => Create();
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    private void Create()
    {
        var title = _title.Text.Trim();
        if (title.Length == 0)
        {
            CodexMessageDialog.Show(this, "Укажите заголовок Pull Request.", "Проверьте заголовок",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = new GitHubPullRequestDraft(title, _body.Text.Trim(), _draft.IsChecked == true);
        DialogResult = true;
    }

    private static string ToTitle(string branch)
    {
        var title = branch.Replace('-', ' ').Replace('_', ' ').Trim();
        return title.Length == 0 ? "Изменения" : char.ToUpperInvariant(title[0]) + title[1..];
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Background = Brush("#1D1F23"),
        Foreground = Brush("#F1F3F5"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
