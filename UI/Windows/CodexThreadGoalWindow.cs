using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed record CodexThreadGoal(string Objective, string Status, ulong? TokenBudget,
    ulong TokensUsed, ulong TimeUsedSeconds);

internal sealed class CodexThreadGoalWindow : Window
{
    private readonly CodexThreadGoal? _source;
    private readonly bool _isPending;
    private readonly TextBox _objective = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _tokenBudget = new();
    private readonly TextBlock _usage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 10, Margin = new Thickness(1, 9, 1, 0) };

    public bool ClearRequested { get; private set; }
    public string? Objective { get; private set; }
    public string? GoalStatus { get; private set; }
    public ulong? TokenBudget { get; private set; }

    public CodexThreadGoalWindow(CodexThreadGoal? existing, string? initialObjective = null, string? initialStatus = null,
        bool isPending = false)
    {
        _source = existing;
        _isPending = isPending;
        Title = "Цель чата";
        Width = 560;
        Height = 570;
        MinWidth = 500;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        LoadValues(initialObjective, initialStatus);
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 15, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        if (_source is not null)
        {
            var clear = MakeButton("Очистить цель", "#4A2A31");
            clear.Margin = new Thickness(0, 0, 8, 0);
            clear.Click += (_, _) => ClearGoal();
            buttons.Children.Add(clear);
        }
        var cancel = MakeButton("Отмена", "#263344");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        var save = MakeButton("Сохранить", "#223B38");
        save.Foreground = Brush("#92E5C7");
        save.IsDefault = true;
        save.Click += (_, _) => SaveGoal();
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        var form = new StackPanel();
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form });
        form.Children.Add(new TextBlock
        {
            Text = _isPending
                ? "Черновик пока хранится в CodexGUI на этом компьютере. Перед ближайшим ходом он будет отправлен в App Server и станет доступен другим клиентам Codex."
                : "Цель сохраняется в истории App Server и доступна другим клиентам Codex для этого чата.",
            Foreground = Brush("#A6B1BF"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 0, 1, 3)
        });
        AddField(form, "Цель", _objective);
        _objective.AcceptsReturn = true;
        _objective.TextWrapping = TextWrapping.Wrap;
        _objective.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _objective.MinHeight = 125;
        _objective.MaxHeight = 210;
        AddField(form, "Статус", _status);
        _status.ItemsSource = new[]
        {
            new GoalStatusChoice("active", "Активна"),
            new GoalStatusChoice("paused", "Приостановлена"),
            new GoalStatusChoice("blocked", "Заблокирована"),
            new GoalStatusChoice("usageLimited", "Лимит использования"),
            new GoalStatusChoice("budgetLimited", "Лимит бюджета"),
            new GoalStatusChoice("complete", "Завершена")
        };
        _status.DisplayMemberPath = nameof(GoalStatusChoice.Label);
        AddField(form, "Лимит токенов (необязательно)", _tokenBudget);
        _tokenBudget.ToolTip = "Оставьте пустым, чтобы не задавать лимит";
        _usage.Foreground = Brush("#A6B1BF");
        form.Children.Add(_usage);
        return root;
    }

    private void LoadValues(string? initialObjective, string? initialStatus)
    {
        _objective.Text = initialObjective ?? _source?.Objective ?? "";
        _status.SelectedItem = _status.Items.Cast<GoalStatusChoice>()
            .FirstOrDefault(item => item.Id == (initialStatus ?? _source?.Status))
            ?? _status.Items.Cast<GoalStatusChoice>().First(item => item.Id == "active");
        _tokenBudget.Text = _source?.TokenBudget?.ToString(CultureInfo.InvariantCulture) ?? "";
        _usage.Text = _source is null
            ? "Цель ещё не задана."
            : _isPending
                ? "Черновик ещё не отправлен в App Server."
            : $"Использовано: {_source.TokensUsed:N0} токенов · время: {FormatDuration(_source.TimeUsedSeconds)}";
    }

    private void SaveGoal()
    {
        var objective = _objective.Text.Trim();
        if (objective.Length == 0 || objective.Length > 4000)
        {
            CodexMessageDialog.Show(this, "Цель должна содержать от 1 до 4000 символов.", "Проверьте цель",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_status.SelectedItem is not GoalStatusChoice status)
        {
            CodexMessageDialog.Show(this, "Выберите статус цели.", "Проверьте цель",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ulong? tokenBudget = null;
        var budgetText = _tokenBudget.Text.Trim();
        if (budgetText.Length > 0)
        {
            if (!ulong.TryParse(budgetText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                CodexMessageDialog.Show(this, "Лимит должен быть целым неотрицательным числом.", "Проверьте лимит",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            tokenBudget = parsed;
        }

        Objective = objective;
        GoalStatus = status.Id;
        TokenBudget = tokenBudget;
        DialogResult = true;
    }

    private void ClearGoal()
    {
        var answer = CodexMessageDialog.Show(this,
            _isPending ? "Удалить черновик цели этого чата?" : "Удалить сохранённую цель этого чата?", "Очистить цель",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        ClearRequested = true;
        DialogResult = true;
    }

    private static string FormatDuration(ulong totalSeconds)
    {
        var totalMinutes = totalSeconds / 60;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours} ч {minutes} мин" : $"{minutes} мин";
    }

    private static TextBlock AddField(Panel panel, string label, UIElement input)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#A6B1BF"),
            Margin = new Thickness(0, 12, 0, 5)
        };
        panel.Children.Add(labelBlock);
        if (input is Control control)
        {
            control.Padding = new Thickness(8, 6, 8, 6);
            control.Background = Brush("#16212B");
            control.Foreground = Brush("#ECF2F5");
            control.BorderBrush = Brush("#2A3946");
        }
        panel.Children.Add(input);
        return labelBlock;
    }

    private static Button MakeButton(string content, string background) => new()
    {
        Content = content,
        Padding = new Thickness(12, 7, 12, 7),
        Background = Brush(background),
        Foreground = Brush("#E2E8F0"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private sealed record GoalStatusChoice(string Id, string Label);
}
