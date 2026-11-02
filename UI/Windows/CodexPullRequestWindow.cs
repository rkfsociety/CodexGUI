using System;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace CodexGUI;

internal sealed record GitHubPullRequestDraft(string Title, string Body, bool IsDraft);

internal sealed class CodexPullRequestWindow : Window
{
    private readonly TextBox _title = new();
    private readonly TextBox _body = new();
    private readonly CheckBox _draft = new() { Content = "Создать как черновик" };
    private readonly TextBlock _titleCounter = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush("#B0B5BE"), Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock _bodyCounter = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush("#B0B5BE"), Margin = new Thickness(0, 5, 0, 0) };
    private Button _createButton = null!;

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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _createButton.IsEnabled)
            {
                Create();
                e.Handled = true;
            }
        };
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
            FontSize = 12,
            Foreground = Brush("#B0B5BE"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = branch,
            Margin = new Thickness(0, 0, 0, 9)
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Заголовок",
            FontSize = 11,
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
        AutomationProperties.SetName(_title, "Заголовок Pull Request");
        titlePanel.Children.Add(_title);
        titlePanel.Children.Add(_titleCounter);
        Grid.SetRow(titlePanel, 1);
        root.Children.Add(titlePanel);

        var bodyPanel = new Grid();
        bodyPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        bodyPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyPanel.Children.Add(new TextBlock
        {
            Text = "Описание",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8D95A2"),
            Margin = new Thickness(0, 0, 0, 5)
        });
        _body.AcceptsReturn = true;
        _body.MaxLength = 60000;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.Padding = new Thickness(10, 8, 10, 8);
        _body.Background = Brush("#181A1E");
        _body.Foreground = Brush("#F1F3F5");
        _body.BorderBrush = Brush("#314454");
        _body.BorderThickness = new Thickness(1);
        _body.Text = "## Summary\n\n## Testing\n";
        _body.ToolTip = "Описание поддерживает Markdown. Ctrl+Enter — создать Pull Request.";
        AutomationProperties.SetName(_body, "Описание Pull Request");
        Grid.SetRow(_body, 1);
        bodyPanel.Children.Add(_body);
        Grid.SetRow(_bodyCounter, 2);
        bodyPanel.Children.Add(_bodyCounter);
        Grid.SetRow(bodyPanel, 2);
        root.Children.Add(bodyPanel);

        _draft.Foreground = Brush("#C5D0D8");
        _draft.Margin = new Thickness(0, 12, 0, 0);
        _draft.ToolTip = "Черновик можно дополнить до отправки на проверку.";
        Grid.SetRow(_draft, 3);
        root.Children.Add(_draft);

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = MakeButton("Отмена");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        _createButton = MakeButton("Создать Pull Request");
        _createButton.IsDefault = true;
        _createButton.Background = Brush("#204139");
        _createButton.Foreground = Brush("#92E5C7");
        _createButton.ToolTip = "Создать Pull Request (Ctrl+Enter)";
        _createButton.Click += (_, _) => Create();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_createButton);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        _title.TextChanged += (_, _) => UpdateFormState();
        _body.TextChanged += (_, _) => UpdateFormState();
        UpdateFormState();
        return root;
    }

    private void UpdateFormState()
    {
        _titleCounter.Text = $"{_title.Text.Length} / {_title.MaxLength}";
        _bodyCounter.Text = $"{_body.Text.Length:N0} / {_body.MaxLength:N0}";
        _createButton.IsEnabled = !string.IsNullOrWhiteSpace(_title.Text);
        _createButton.ToolTip = _createButton.IsEnabled ? null : "Укажите заголовок Pull Request";
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
