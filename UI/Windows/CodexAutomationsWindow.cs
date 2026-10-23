using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed record CodexAutomationChat(string ProjectId, string ThreadId, string Title, string ProjectPath, string? WorktreePath)
{
    public string WorkingDirectory => WorktreePath ?? ProjectPath;
}

internal sealed class CodexAutomationsWindow : Window
{
    private readonly IList<CodexAutomationDefinition> _definitions;
    private readonly IList<CodexAutomationRun> _runs;
    private readonly IReadOnlyList<CodexAutomationProject> _projects;
    private readonly IReadOnlyList<CodexAutomationChat> _chats;
    private readonly IReadOnlyList<ModelOption> _models;
    private readonly string? _defaultProjectId;
    private readonly Action _saveDefinitions;
    private readonly Func<CodexAutomationDefinition, Task> _runNow;
    private readonly Action<CodexAutomationRun> _openRun;
    private readonly Action<CodexAutomationRun> _markReviewed;
    private readonly ListBox _definitionList = new() { Background = new SolidColorBrush(Color.FromRgb(23, 30, 39)), BorderThickness = new Thickness(0) };
    private readonly ListBox _runList = new() { Background = new SolidColorBrush(Color.FromRgb(23, 30, 39)), BorderThickness = new Thickness(0) };
    private readonly TextBlock _definitionDetails = new();
    private readonly TextBlock _runDetails = new();
    private readonly Button _editButton = new() { Content = "Изменить" };
    private readonly Button _deleteButton = new() { Content = "Удалить" };
    private readonly Button _runButton = new() { Content = "Запустить сейчас" };
    private readonly Button _openRunButton = new() { Content = "Открыть чат" };
    private readonly Button _reviewButton = new() { Content = "Отметить проверенным" };

    public CodexAutomationsWindow(IList<CodexAutomationDefinition> definitions, IList<CodexAutomationRun> runs,
        IReadOnlyList<CodexAutomationProject> projects, IReadOnlyList<CodexAutomationChat> chats,
        IReadOnlyList<ModelOption> models, string? defaultProjectId, Action saveDefinitions,
        Func<CodexAutomationDefinition, Task> runNow, Action<CodexAutomationRun> openRun,
        Action<CodexAutomationRun> markReviewed)
    {
        _definitions = definitions;
        _runs = runs;
        _projects = projects;
        _chats = chats;
        _models = models;
        _defaultProjectId = defaultProjectId;
        _saveDefinitions = saveDefinitions;
        _runNow = runNow;
        _openRun = openRun;
        _markReviewed = markReviewed;

        Title = "Расписания Codex";
        Width = 1000;
        Height = 690;
        MinWidth = 850;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        _definitionList.SelectionChanged += (_, _) => UpdateDefinitionSelection();
        _runList.SelectionChanged += (_, _) => UpdateRunSelection();
        UpdateLists();
    }

    public void UpdateLists()
    {
        var selectedDefinitionId = (_definitionList.SelectedItem as ListBoxItem)?.Tag as string;
        var selectedRunId = (_runList.SelectedItem as ListBoxItem)?.Tag as string;
        _definitionList.Items.Clear();
        foreach (var definition in _definitions.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ListBoxItem
            {
                Tag = definition.Id,
                Padding = new Thickness(10, 8, 10, 8),
                Content = BuildDefinitionItem(definition)
            };
            _definitionList.Items.Add(item);
            if (definition.Id == selectedDefinitionId) _definitionList.SelectedItem = item;
        }

        _runList.Items.Clear();
        foreach (var run in _runs.OrderByDescending(item => item.StartedAtUtc))
        {
            var item = new ListBoxItem
            {
                Tag = run.Id,
                Padding = new Thickness(10, 8, 10, 8),
                Content = BuildRunItem(run)
            };
            _runList.Items.Add(item);
            if (run.Id == selectedRunId) _runList.SelectedItem = item;
        }
        if (_definitionList.SelectedItem is null && _definitionList.Items.Count > 0) _definitionList.SelectedIndex = 0;
        if (_runList.SelectedItem is null && _runList.Items.Count > 0) _runList.SelectedIndex = 0;
        UpdateDefinitionSelection();
        UpdateRunSelection();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock
        {
            Text = "Автоматизации",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 3)
        });
        var note = new TextBlock
        {
            Text = "Локальные задачи выполняются по расписанию, пока CodexGUI запущен. Результаты остаются в очереди до проверки.",
            Foreground = Brush("#A6B1BF"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 16)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 2);
        root.Children.Add(content);
        content.Children.Add(BuildDefinitionsPanel());
        var runsPanel = BuildRunsPanel();
        Grid.SetColumn(runsPanel, 1);
        content.Children.Add(runsPanel);
        return root;
    }

    private UIElement BuildDefinitionsPanel()
    {
        var panel = new Grid { Margin = new Thickness(0, 0, 9, 0) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(SectionTitle("РАСПИСАНИЯ"));
        var listBorder = Card(_definitionList);
        Grid.SetRow(listBorder, 1);
        panel.Children.Add(listBorder);

        var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var add = MakeButton("＋ Создать", accent: true);
        add.Click += async (_, _) => await EditDefinitionAsync(null);
        buttons.Children.Add(add);
        _editButton.Margin = new Thickness(6, 0, 0, 0);
        _editButton.Click += async (_, _) => await EditDefinitionAsync(SelectedDefinition());
        buttons.Children.Add(_editButton);
        _deleteButton.Margin = new Thickness(6, 0, 0, 0);
        _deleteButton.Click += (_, _) => DeleteDefinition();
        buttons.Children.Add(_deleteButton);
        _runButton.Margin = new Thickness(6, 0, 0, 0);
        _runButton.Click += async (_, _) =>
        {
            if (SelectedDefinition() is not { } definition) return;
            _runButton.IsEnabled = false;
            try { await _runNow(definition); }
            catch (Exception error) { CodexMessageDialog.Show(this, error.Message, "Не удалось запустить автоматизацию", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _runButton.IsEnabled = true; UpdateLists(); }
        };
        buttons.Children.Add(_runButton);
        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);

        _definitionDetails.Foreground = Brush("#A6B1BF");
        _definitionDetails.FontSize = 10;
        _definitionDetails.TextWrapping = TextWrapping.Wrap;
        _definitionDetails.Margin = new Thickness(3, 8, 3, 0);
        Grid.SetRow(_definitionDetails, 3);
        panel.Children.Add(_definitionDetails);
        return panel;
    }

    private UIElement BuildRunsPanel()
    {
        var panel = new Grid { Margin = new Thickness(9, 0, 0, 0) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(SectionTitle("РЕЗУЛЬТАТЫ · ОЧЕРЕДЬ ПРОВЕРКИ"));
        var listBorder = Card(_runList);
        Grid.SetRow(listBorder, 1);
        panel.Children.Add(listBorder);
        var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        _openRunButton.Click += (_, _) => { if (SelectedRun() is { } run) _openRun(run); };
        buttons.Children.Add(_openRunButton);
        _reviewButton.Margin = new Thickness(6, 0, 0, 0);
        _reviewButton.Click += (_, _) => { if (SelectedRun() is { } run) { _markReviewed(run); UpdateLists(); } };
        buttons.Children.Add(_reviewButton);
        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);
        _runDetails.Foreground = Brush("#A6B1BF");
        _runDetails.FontSize = 10;
        _runDetails.TextWrapping = TextWrapping.Wrap;
        _runDetails.Margin = new Thickness(3, 8, 3, 0);
        Grid.SetRow(_runDetails, 3);
        panel.Children.Add(_runDetails);
        return panel;
    }

    private UIElement BuildDefinitionItem(CodexAutomationDefinition definition)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = definition.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        IEnumerable<string> projectIds = definition.ProjectIds.Count > 0
            ? definition.ProjectIds
            : new[] { definition.ProjectId };
        var projectNames = projectIds.Select(id => _projects.FirstOrDefault(item => item.Id == id)?.Name
            ?? (definition.ProjectPaths.TryGetValue(id, out var path) ? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) : id))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        var project = projectNames.Length switch
        {
            0 => definition.ProjectPath,
            <= 2 => string.Join(", ", projectNames),
            _ => $"{projectNames.Length} проекта"
        };
        var cadence = definition.Cadence switch
        {
            "weekly" => $"Еженедельно · {DayName(definition.DayOfWeek)} в {definition.TimeOfDay}",
            "monthly" => $"Ежемесячно · {definition.DayOfMonth}-е число в {definition.TimeOfDay}",
            "interval" => $"Каждые {definition.IntervalMinutes} мин.",
            "rrule" => $"RRULE · {definition.RecurrenceRule}",
            _ => $"Ежедневно · {definition.TimeOfDay}"
        };
        stack.Children.Add(new TextBlock
        {
            Text = $"{(definition.RecurrenceExhausted ? "Завершено" : definition.Enabled ? "Включено" : "Выключено")} · {cadence} · {project}",
            Foreground = Brush("#A6B1BF"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        });
        return stack;
    }

    private static UIElement BuildRunItem(CodexAutomationRun run)
    {
        var stack = new StackPanel();
        var status = run.Status switch
        {
            "preparing" => "Подготовка",
            "running" => "Выполняется",
            "completed" => run.Reviewed ? "Проверено" : "Готово · нужна проверка",
            "failed" => "Ошибка",
            _ => run.Status
        };
        stack.Children.Add(new TextBlock { Text = run.AutomationName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock
        {
            Text = $"{status} · {System.IO.Path.GetFileName(run.ProjectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar))} · {run.StartedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}",
            Foreground = Brush(run.Status == "failed" ? "#E78C8C" : run.Status == "completed" && !run.Reviewed ? "#E6BC70" : "#A6B1BF"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0)
        });
        return stack;
    }

    private async Task EditDefinitionAsync(CodexAutomationDefinition? existing)
    {
        if (_projects.Count == 0)
        {
            CodexMessageDialog.Show(this, "Сначала добавьте проект в боковой панели CodexGUI.", "Нет проекта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var defaultProject = _projects.FirstOrDefault(item => item.Id == _defaultProjectId) ?? _projects[0];
        var defaultUseWorktree = existing?.UseWorktree ?? false;
        if (existing is null)
        {
            try
            {
                var repository = await GitRepositoryInspector.ReadAsync(defaultProject.Path, System.Threading.CancellationToken.None);
                defaultUseWorktree = repository.IsRepository;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Trace.TraceWarning("Не удалось определить тип проекта для расписания: {0}", error.Message);
            }
        }
        var editor = new CodexAutomationEditorWindow(_projects, _chats, _models, existing, defaultProject.Id, defaultUseWorktree) { Owner = this };
        if (editor.ShowDialog() != true || editor.Definition is not { } edited) return;
        var index = existing is null ? -1 : _definitions.ToList().FindIndex(item => item.Id == existing.Id);
        if (index >= 0) _definitions[index] = edited;
        else _definitions.Add(edited);
        try { _saveDefinitions(); }
        catch (Exception error)
        {
            if (index >= 0) _definitions[index] = existing!;
            else _definitions.Remove(edited);
            CodexMessageDialog.Show(this, error.Message, "Не удалось сохранить расписание", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        UpdateLists();
    }

    private void DeleteDefinition()
    {
        if (SelectedDefinition() is not { } definition) return;
        var answer = CodexMessageDialog.Show(this, $"Удалить расписание «{definition.Name}»? История выполнений останется.",
            "Удалить расписание", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _definitions.Remove(definition);
        try { _saveDefinitions(); }
        catch (Exception error)
        {
            _definitions.Add(definition);
            CodexMessageDialog.Show(this, error.Message, "Не удалось удалить расписание", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateLists();
    }

    private CodexAutomationDefinition? SelectedDefinition()
    {
        var id = (_definitionList.SelectedItem as ListBoxItem)?.Tag as string;
        return _definitions.FirstOrDefault(item => item.Id == id);
    }

    private CodexAutomationRun? SelectedRun()
    {
        var id = (_runList.SelectedItem as ListBoxItem)?.Tag as string;
        return _runs.FirstOrDefault(item => item.Id == id);
    }

    private void UpdateDefinitionSelection()
    {
        var selected = SelectedDefinition();
        _editButton.IsEnabled = selected is not null;
        _deleteButton.IsEnabled = selected is not null;
        _runButton.IsEnabled = selected is not null;
        if (selected is null) { _definitionDetails.Text = "Создайте расписание для одного или нескольких проектов."; return; }
        var next = selected.RecurrenceExhausted ? "повторы завершены"
            : selected.NextRunAtUtc == default ? "будет рассчитан при запуске"
            : selected.NextRunAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        _definitionDetails.Text = $"Следующий запуск: {next}. {(selected.UseWorktree ? "Задача будет работать в отдельном Git worktree." : "Задача будет работать непосредственно в папке проекта.")}";
    }

    private void UpdateRunSelection()
    {
        var selected = SelectedRun();
        _openRunButton.IsEnabled = selected is not null
            && !string.IsNullOrWhiteSpace(selected.ThreadId)
            && selected.Status is not ("running" or "preparing");
        _reviewButton.IsEnabled = selected is { Status: "completed", Reviewed: false };
        if (selected is null) { _runDetails.Text = "Здесь появятся завершённые результаты и ошибки расписаний."; return; }
        var project = System.IO.Path.GetFileName(selected.ProjectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var result = string.IsNullOrWhiteSpace(selected.Error) ? selected.Summary : selected.Error;
        _runDetails.Text = string.IsNullOrWhiteSpace(project) ? result : $"Проект: {project}\n{result}";
    }

    private static Border Card(UIElement content) => new()
    {
        Background = Brush("#1D1F23"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11),
        Padding = new Thickness(4),
        Child = content
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush("#8795A6"),
        Margin = new Thickness(3, 0, 0, 7)
    };

    private static Button MakeButton(string text, bool accent = false) => new()
    {
        Content = text,
        Padding = new Thickness(10, 6, 10, 6),
        Background = accent ? Brush("#223B38") : Brush("#282B30"),
        Foreground = accent ? Brush("#92E5C7") : Brush("#D3DCE6"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "понедельник",
        DayOfWeek.Tuesday => "вторник",
        DayOfWeek.Wednesday => "среду",
        DayOfWeek.Thursday => "четверг",
        DayOfWeek.Friday => "пятницу",
        DayOfWeek.Saturday => "субботу",
        DayOfWeek.Sunday => "воскресенье",
        _ => day.ToString()
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}

internal sealed class CodexAutomationEditorWindow : Window
{
    private readonly IReadOnlyList<CodexAutomationProject> _projectChoices;
    private readonly IReadOnlyList<CodexAutomationChat> _chats;
    private readonly IReadOnlyList<ModelOption> _models;
    private readonly string _defaultProjectId;
    private readonly CodexAutomationDefinition _source;
    private readonly TextBox _name = new();
    private readonly StackPanel _projectChecks = new();
    private readonly List<(CodexAutomationProject Project, CheckBox CheckBox)> _projectOptions = [];
    private readonly ComboBox _existingThread = new();
    private readonly TextBlock _existingThreadNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(154, 168, 184)), Margin = new Thickness(1, 5, 1, 0) };
    private readonly ComboBox _cadence = new();
    private readonly TextBox _time = new();
    private readonly TextBox _interval = new();
    private readonly DatePicker _ruleStartDate = new();
    private readonly TextBox _ruleText = new();
    private TextBlock _timeLabel = new();
    private TextBlock _intervalLabel = new();
    private TextBlock _ruleStartDateLabel = new();
    private TextBlock _ruleLabel = new();
    private readonly ComboBox _day = new();
    private readonly ComboBox _monthDay = new();
    private TextBlock _monthDayLabel = new();
    private readonly ComboBox _model = new();
    private readonly ComboBox _effort = new();
    private readonly TextBox _prompt = new();
    private readonly CheckBox _worktree = new() { Content = "Выполнять в отдельном Git worktree", IsChecked = true };
    private readonly CheckBox _enabled = new() { Content = "Расписание включено", IsChecked = true };
    private readonly StackPanel _dayRow = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _dayLabel = new()
    {
        Text = "День недели",
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 191)),
        Margin = new Thickness(0, 11, 0, 5)
    };
    private bool _loadingValues;

    public CodexAutomationDefinition? Definition { get; private set; }

    public CodexAutomationEditorWindow(IReadOnlyList<CodexAutomationProject> projects,
        IReadOnlyList<CodexAutomationChat> chats, IReadOnlyList<ModelOption> models,
        CodexAutomationDefinition? existing, string defaultProjectId, bool defaultUseWorktree)
    {
        _chats = chats;
        _models = models;
        _defaultProjectId = defaultProjectId;
        _source = existing is null ? new CodexAutomationDefinition() : Copy(existing);
        _projectChoices = CreateProjectChoices(projects, _source);
        if (existing is null) _source.UseWorktree = defaultUseWorktree;
        Title = existing is null ? "Новое расписание" : "Изменить расписание";
        Width = 560;
        Height = 700;
        MinWidth = 500;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildEditor();
        _loadingValues = true;
        LoadValues();
        _loadingValues = false;
    }

    private UIElement BuildEditor()
    {
        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Сохранить", Padding = new Thickness(14, 7, 14, 7), IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(34, 59, 56)), Foreground = new SolidColorBrush(Color.FromRgb(168, 230, 207)) };
        save.Click += (_, _) => SaveDefinition();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        var form = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form };
        root.Children.Add(scroll);

        AddField(form, "Название", _name);
        foreach (var project in _projectChoices)
        {
            var checkBox = new CheckBox
            {
                Content = project.IsRegistered ? project.Name : $"{project.Name} · сохранённый проект",
                Tag = project.Id,
                Margin = new Thickness(4, 3, 4, 3),
                Foreground = new SolidColorBrush(Color.FromRgb(230, 235, 241)),
                ToolTip = project.Path
            };
            checkBox.Checked += (_, _) => { if (!_loadingValues) OnProjectSelectionChanged(); };
            checkBox.Unchecked += (_, _) => { if (!_loadingValues) OnProjectSelectionChanged(); };
            _projectOptions.Add((project, checkBox));
            _projectChecks.Children.Add(checkBox);
        }
        var projectList = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 36, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 70, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MaxHeight = 116,
            Child = new ScrollViewer { Content = _projectChecks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        AddField(form, "Проекты (можно выбрать несколько)", projectList);
        AddField(form, "Продолжать существующий чат (опционально)", _existingThread);
        form.Children.Add(_existingThreadNote);
        AddField(form, "Периодичность", _cadence);
        _timeLabel = AddField(form, "Время по местному часовому поясу (ЧЧ:ММ)", _time);
        _intervalLabel = AddField(form, "Интервал в минутах", _interval);
        _interval.ToolTip = "Укажите положительное целое число минут";
        _ruleStartDateLabel = AddField(form, "Начальная дата (DTSTART)", _ruleStartDate);
        _ruleLabel = AddField(form, "Правило повторения (RFC 5545 RRULE)", _ruleText);
        _ruleText.ToolTip = "Например: RRULE:FREQ=MONTHLY;BYMONTHDAY=1;BYHOUR=9;BYMINUTE=0";
        _day.ItemsSource = new[]
        {
            new DayChoice(DayOfWeek.Monday, "Понедельник"), new DayChoice(DayOfWeek.Tuesday, "Вторник"),
            new DayChoice(DayOfWeek.Wednesday, "Среда"), new DayChoice(DayOfWeek.Thursday, "Четверг"),
            new DayChoice(DayOfWeek.Friday, "Пятница"), new DayChoice(DayOfWeek.Saturday, "Суббота"),
            new DayChoice(DayOfWeek.Sunday, "Воскресенье")
        };
        _day.DisplayMemberPath = nameof(DayChoice.Label);
        _day.Padding = new Thickness(8, 6, 8, 6);
        _day.Background = new SolidColorBrush(Color.FromRgb(28, 36, 48));
        _day.Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 241));
        _day.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 70, 90));
        _dayRow.Children.Add(_day);
        form.Children.Add(_dayLabel);
        form.Children.Add(_dayRow);
        _monthDay.ItemsSource = Enumerable.Range(1, 31)
            .Select(day => new MonthDayChoice(day, $"{day}-е число")).ToArray();
        _monthDay.DisplayMemberPath = nameof(MonthDayChoice.Label);
        _monthDayLabel = AddField(form, "Число месяца", _monthDay);
        AddField(form, "Модель", _model);
        AddField(form, "Уровень усилий", _effort);
        AddField(form, "Задача для Codex", _prompt);
        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _prompt.MinHeight = 115;
        _prompt.Padding = new Thickness(8);
        form.Children.Add(_worktree);
        form.Children.Add(_enabled);

        _cadence.ItemsSource = new[]
        {
            new CadenceChoice("daily", "Каждый день"),
            new CadenceChoice("weekly", "Каждую неделю"),
            new CadenceChoice("monthly", "Каждый месяц"),
            new CadenceChoice("interval", "Через интервал"),
            new CadenceChoice("rrule", "Расширенное правило (RRULE)")
        };
        _cadence.DisplayMemberPath = nameof(CadenceChoice.Label);
        _cadence.SelectionChanged += (_, _) =>
        {
            var cadence = SelectedCadence();
            var weeklyVisibility = cadence == "weekly" ? Visibility.Visible : Visibility.Collapsed;
            var monthlyVisibility = cadence == "monthly" ? Visibility.Visible : Visibility.Collapsed;
            var intervalVisibility = cadence == "interval" ? Visibility.Visible : Visibility.Collapsed;
            var ruleVisibility = cadence == "rrule" ? Visibility.Visible : Visibility.Collapsed;
            _timeLabel.Visibility = intervalVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _time.Visibility = _timeLabel.Visibility;
            _intervalLabel.Visibility = intervalVisibility;
            _interval.Visibility = intervalVisibility;
            _ruleStartDateLabel.Visibility = ruleVisibility;
            _ruleStartDate.Visibility = ruleVisibility;
            _ruleLabel.Visibility = ruleVisibility;
            _ruleText.Visibility = ruleVisibility;
            _dayLabel.Visibility = weeklyVisibility;
            _dayRow.Visibility = weeklyVisibility;
            _monthDayLabel.Visibility = monthlyVisibility;
            _monthDay.Visibility = monthlyVisibility;
        };
        _existingThread.DisplayMemberPath = nameof(ExistingThreadChoice.Label);
        _existingThread.SelectionChanged += (_, _) => UpdateExistingThreadUi();
        _model.SelectionChanged += (_, _) => RefreshEffortChoices();
        return root;
    }

    private void LoadValues()
    {
        _name.Text = _source.Name;
        var selectedProjectIds = _source.ProjectIds.Count > 0
            ? _source.ProjectIds.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (selectedProjectIds.Count == 0 && _source.ProjectId.Length > 0)
            selectedProjectIds.Add(_source.ProjectId);
        if (selectedProjectIds.Count == 0 && _projectChoices.FirstOrDefault(item => item.Id == _defaultProjectId) is { } defaultProject)
            selectedProjectIds.Add(defaultProject.Id);
        if (selectedProjectIds.Count == 0 && _projectChoices.FirstOrDefault() is { } firstProject)
            selectedProjectIds.Add(firstProject.Id);
        foreach (var option in _projectOptions)
            option.CheckBox.IsChecked = selectedProjectIds.Contains(option.Project.Id);
        _cadence.SelectedItem = _cadence.Items.Cast<CadenceChoice>().FirstOrDefault(item => item.Id == _source.Cadence)
            ?? _cadence.Items.Cast<CadenceChoice>().First();
        _time.Text = _source.TimeOfDay;
        _interval.Text = _source.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        _ruleStartDate.SelectedDate = _source.RecurrenceStartLocal?.Date ?? DateTime.Today;
        _ruleText.Text = _source.RecurrenceRule ?? "";
        _day.SelectedItem = _day.Items.Cast<DayChoice>().FirstOrDefault(item => item.Day == _source.DayOfWeek);
        if (_day.SelectedItem is null) _day.SelectedIndex = 0;
        _monthDay.SelectedItem = _monthDay.Items.Cast<MonthDayChoice>()
            .FirstOrDefault(item => item.Day == _source.DayOfMonth) ?? _monthDay.Items[0];
        foreach (var model in _models)
            _model.Items.Add(new ModelChoice(model.Slug, model.DisplayName, model.DefaultReasoningLevel, model.ReasoningLevels));
        if (_model.Items.Count == 0)
            _model.Items.Add(new ModelChoice("gpt-6-luna", "GPT-6 Luna", "high", ["low", "medium", "high", "xhigh"]));
        _model.DisplayMemberPath = nameof(ModelChoice.Label);
        _model.SelectedItem = _model.Items.Cast<ModelChoice>().FirstOrDefault(item => item.Id == _source.Model)
            ?? _model.Items.Cast<ModelChoice>().First();
        RefreshEffortChoices(_source.Effort);
        _prompt.Text = _source.Prompt;
        _worktree.IsChecked = _source.UseWorktree;
        _enabled.IsChecked = _source.Enabled;
        var weeklyVisibility = _source.Cadence == "weekly" ? Visibility.Visible : Visibility.Collapsed;
        var monthlyVisibility = _source.Cadence == "monthly" ? Visibility.Visible : Visibility.Collapsed;
        var intervalVisibility = _source.Cadence == "interval" ? Visibility.Visible : Visibility.Collapsed;
        var ruleVisibility = _source.Cadence == "rrule" ? Visibility.Visible : Visibility.Collapsed;
        _timeLabel.Visibility = intervalVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _time.Visibility = _timeLabel.Visibility;
        _intervalLabel.Visibility = intervalVisibility;
        _interval.Visibility = intervalVisibility;
        _ruleStartDateLabel.Visibility = ruleVisibility;
        _ruleStartDate.Visibility = ruleVisibility;
        _ruleLabel.Visibility = ruleVisibility;
        _ruleText.Visibility = ruleVisibility;
        _dayRow.Visibility = weeklyVisibility;
        _dayLabel.Visibility = weeklyVisibility;
        _monthDayLabel.Visibility = monthlyVisibility;
        _monthDay.Visibility = monthlyVisibility;
        RefreshExistingThreadChoices(_source.ExistingThreadId);
    }

    private IReadOnlyList<CodexAutomationProject> SelectedProjects() => _projectOptions
        .Where(option => option.CheckBox.IsChecked == true)
        .Select(option => option.Project)
        .ToArray();

    private void OnProjectSelectionChanged() => RefreshExistingThreadChoices();

    private void RefreshExistingThreadChoices(string? requestedThreadId = null)
    {
        var selectedProjects = SelectedProjects();
        var projectId = selectedProjects.Count == 1 ? selectedProjects[0].Id : null;
        var choices = new List<ExistingThreadChoice>
        {
            new(null, null, null, null, "Начинать отдельный чат в каждом запуске")
        };
        if (projectId is not null)
            choices.AddRange(_chats.Where(chat => chat.ProjectId == projectId)
                .Select(chat => new ExistingThreadChoice(chat, chat.ThreadId, chat.WorkingDirectory, chat.WorktreePath,
                    $"{chat.Title} · {(chat.WorktreePath is null ? "папка проекта" : "worktree")}")));
        if (projectId is not null
            && !string.IsNullOrWhiteSpace(requestedThreadId)
            && choices.All(choice => choice.ThreadId != requestedThreadId))
        {
            choices.Add(new ExistingThreadChoice(null, requestedThreadId,
                _source.ExistingThreadWorkingDirectory ?? _source.ProjectPath,
                _source.ExistingThreadWorktreePath,
                $"Сохранённый чат недоступен · {requestedThreadId}"));
        }
        _existingThread.ItemsSource = choices;
        _existingThread.SelectedItem = choices.FirstOrDefault(choice => choice.ThreadId == requestedThreadId)
            ?? choices[0];
        _existingThread.IsEnabled = projectId is not null;
        UpdateExistingThreadUi();
        if (projectId is null)
            _existingThreadNote.Text = selectedProjects.Count > 1
                ? "Для нескольких проектов каждый запуск создаёт отдельный чат в каждом проекте. Продолжение существующего чата доступно для одного проекта."
                : "Выберите проект, чтобы продолжить существующий чат.";
    }

    private void UpdateExistingThreadUi()
    {
        var choice = _existingThread.SelectedItem as ExistingThreadChoice;
        var chat = choice?.Chat;
        _worktree.IsEnabled = choice?.ThreadId is null;
        if (choice?.ThreadId is null)
        {
            _worktree.IsChecked = _source.UseWorktree;
            _existingThreadNote.Text = "Без выбранного чата каждый запуск создаёт новую сессию. Для Git-проекта запуск по умолчанию использует отдельный worktree.";
        }
        else if (chat is null)
        {
            _worktree.IsChecked = false;
            _existingThreadNote.Text = "Сохранённый чат не найден среди доступных. Его ID сохранён; выберите другой чат или отдельный чат для запуска, чтобы изменить настройку.";
        }
        else
        {
            _worktree.IsChecked = false;
            _existingThreadNote.Text = chat.WorktreePath is null
                ? "Продолжит контекст чата в его текущей папке. Изменения будут записываться прямо в папку проекта."
                : $"Продолжит чат в его существующем worktree: {chat.WorktreePath}";
        }
    }

    private void RefreshEffortChoices(string? requested = null)
    {
        if (_model.SelectedItem is not ModelChoice model) return;
        _effort.ItemsSource = model.Efforts;
        _effort.SelectedItem = model.Efforts.Contains(requested ?? _source.Effort, StringComparer.Ordinal)
            ? requested ?? _source.Effort
            : model.DefaultEffort;
    }

    private void SaveDefinition()
    {
        var selectedProjects = SelectedProjects();
        var existingThread = _existingThread.SelectedItem as ExistingThreadChoice;
        if (selectedProjects.Count == 0
            || _model.SelectedItem is not ModelChoice model
            || _day.SelectedItem is not DayChoice day
            || SelectedCadence() == "monthly" && _monthDay.SelectedItem is not MonthDayChoice
            || _effort.SelectedItem is not string effort)
        {
            CodexMessageDialog.Show(this, "Выберите хотя бы один проект, модель, усилия и параметры периодичности.", "Проверьте расписание", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (existingThread?.ThreadId is not null && selectedProjects.Count != 1)
        {
            CodexMessageDialog.Show(this, "Продолжение существующего чата поддерживается для одного проекта.", "Проверьте расписание", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = _name.Text.Trim();
        var prompt = _prompt.Text.Trim();
        if (name.Length == 0 || prompt.Length == 0)
        {
            CodexMessageDialog.Show(this, "Укажите название расписания и задачу для Codex.", "Проверьте расписание", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cadence = SelectedCadence();
        if (cadence != "interval" && !TimeOnly.TryParseExact(_time.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            CodexMessageDialog.Show(this, "Укажите время в формате ЧЧ:ММ, например 09:30.", "Проверьте время", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var intervalMinutesValue = _source.IntervalMinutes;
        if (cadence == "interval"
            && (!int.TryParse(_interval.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out intervalMinutesValue)
                || intervalMinutesValue < 1))
        {
            CodexMessageDialog.Show(this, "Интервал должен быть положительным целым числом минут.", "Проверьте интервал", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? recurrenceRule = null;
        DateTime? recurrenceStartLocal = null;
        var recurrenceOccurrencesBeforeNext = 0L;
        var recurrenceExhausted = false;
        var nextRunAtUtc = _source.NextRunAtUtc;
        if (cadence == "rrule")
        {
            recurrenceRule = _ruleText.Text.Trim();
            if (_ruleStartDate.SelectedDate is not { } startDate)
            {
                CodexMessageDialog.Show(this, "Выберите начальную дату RRULE.", "Проверьте расписание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var startTime = TimeOnly.ParseExact(_time.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture);
            recurrenceStartLocal = DateTime.SpecifyKind(startDate.Date.Add(startTime.ToTimeSpan()), DateTimeKind.Unspecified);
            try { CodexRecurrenceRule.Validate(recurrenceRule); }
            catch (InvalidDataException error)
            {
                CodexMessageDialog.Show(this, error.Message, "Проверьте RRULE", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sameRule = _source.Cadence == "rrule"
                && string.Equals(recurrenceRule, _source.RecurrenceRule, StringComparison.OrdinalIgnoreCase)
                && recurrenceStartLocal == _source.RecurrenceStartLocal;
            if (sameRule && _source.RecurrenceExhausted)
            {
                if (_enabled.IsChecked == true)
                {
                    CodexMessageDialog.Show(this, "В этом RRULE уже исчерпан COUNT или наступил UNTIL. Измените правило либо начальную дату.",
                        "Повторы завершены", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                recurrenceOccurrencesBeforeNext = _source.RecurrenceOccurrencesBeforeNext;
                recurrenceExhausted = true;
                nextRunAtUtc = default;
            }
            else if (sameRule && _source.NextRunAtUtc != default)
            {
                recurrenceOccurrencesBeforeNext = _source.RecurrenceOccurrencesBeforeNext;
                nextRunAtUtc = _source.NextRunAtUtc;
            }
            else
            {
                CodexRecurrenceCalculation calculated;
                try
                {
                    calculated = CodexRecurrenceRule.CalculateNext(recurrenceRule, recurrenceStartLocal.Value,
                        DateTimeOffset.UtcNow, previousOccurrenceUtc: null, occurrencesBeforeNext: 0);
                }
                catch (InvalidDataException error)
                {
                    CodexMessageDialog.Show(this, error.Message, "Не удалось рассчитать RRULE", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (calculated.NextRunAtUtc is not { } next)
                {
                    CodexMessageDialog.Show(this, "Для выбранного DTSTART в RRULE нет будущих запусков. Проверьте COUNT и UNTIL.",
                        "Повторов нет", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                nextRunAtUtc = next;
                recurrenceOccurrencesBeforeNext = calculated.OccurrencesBeforeNext;
            }
        }
        var firstProject = selectedProjects[0];
        Definition = new CodexAutomationDefinition
        {
            Id = _source.Id,
            Name = name,
            ProjectId = firstProject.Id,
            ProjectPath = firstProject.Path,
            ProjectIds = selectedProjects.Select(project => project.Id).Distinct(StringComparer.Ordinal).ToList(),
            ProjectPaths = selectedProjects.ToDictionary(project => project.Id, project => project.Path, StringComparer.Ordinal),
            ExistingThreadId = existingThread?.ThreadId,
            ExistingThreadWorkingDirectory = existingThread?.WorkingDirectory,
            ExistingThreadWorktreePath = existingThread?.WorktreePath,
            Prompt = prompt,
            Model = model.Id,
            Effort = effort,
            Cadence = cadence,
            TimeOfDay = _time.Text.Trim(),
            IntervalMinutes = intervalMinutesValue,
            RecurrenceRule = recurrenceRule,
            RecurrenceStartLocal = recurrenceStartLocal,
            RecurrenceOccurrencesBeforeNext = recurrenceOccurrencesBeforeNext,
            RecurrenceExhausted = recurrenceExhausted,
            DayOfWeek = day.Day,
            DayOfMonth = (_monthDay.SelectedItem as MonthDayChoice)?.Day ?? _source.DayOfMonth,
            UseWorktree = existingThread?.ThreadId is null && _worktree.IsChecked == true,
            Enabled = _enabled.IsChecked == true,
            NextRunAtUtc = nextRunAtUtc
        };
        if (cadence != "rrule") Definition.NextRunAtUtc = CodexAutomationStore.CalculateNextRun(Definition, DateTimeOffset.UtcNow);
        DialogResult = true;
    }

    private string SelectedCadence() => (_cadence.SelectedItem as CadenceChoice)?.Id ?? "daily";

    private static TextBlock AddField(Panel panel, string label, UIElement input)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(166, 177, 191)),
            Margin = new Thickness(0, 11, 0, 5)
        };
        panel.Children.Add(labelBlock);
        if (input is Control control)
        {
            control.Padding = new Thickness(8, 6, 8, 6);
            control.Background = new SolidColorBrush(Color.FromRgb(28, 36, 48));
            control.Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 241));
            control.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 70, 90));
        }
        panel.Children.Add(input);
        return labelBlock;
    }

    private static IReadOnlyList<CodexAutomationProject> CreateProjectChoices(
        IReadOnlyList<CodexAutomationProject> projects, CodexAutomationDefinition source)
    {
        var result = projects.ToList();
        IEnumerable<string> selectedIds = source.ProjectIds.Count > 0
            ? source.ProjectIds
            : string.IsNullOrWhiteSpace(source.ProjectId) ? Array.Empty<string>() : new[] { source.ProjectId };
        foreach (var projectId in selectedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            if (result.Any(project => project.Id == projectId)) continue;
            var path = source.ProjectPaths.TryGetValue(projectId, out var savedPath)
                ? savedPath
                : projectId == source.ProjectId ? source.ProjectPath : "";
            if (string.IsNullOrWhiteSpace(path)) continue;
            var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            result.Add(new CodexAutomationProject(projectId, name.Length > 0 ? name : path, path, IsRegistered: false));
        }
        return result;
    }

    private static CodexAutomationDefinition Copy(CodexAutomationDefinition source)
    {
        var projectPaths = new Dictionary<string, string>(source.ProjectPaths, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(source.ProjectId) && !string.IsNullOrWhiteSpace(source.ProjectPath))
            projectPaths.TryAdd(source.ProjectId, source.ProjectPath);
        return new CodexAutomationDefinition
        {
            Id = source.Id,
            Name = source.Name,
            ProjectId = source.ProjectId,
            ProjectPath = source.ProjectPath,
            ProjectIds = source.ProjectIds.Count > 0 ? source.ProjectIds.ToList()
                : string.IsNullOrWhiteSpace(source.ProjectId) ? new List<string>() : new List<string> { source.ProjectId },
            ProjectPaths = projectPaths,
            ExistingThreadId = source.ExistingThreadId,
            ExistingThreadWorkingDirectory = source.ExistingThreadWorkingDirectory,
            ExistingThreadWorktreePath = source.ExistingThreadWorktreePath,
            Prompt = source.Prompt,
            Model = source.Model,
            Effort = source.Effort,
            Cadence = source.Cadence,
            TimeOfDay = source.TimeOfDay,
            IntervalMinutes = source.IntervalMinutes,
            RecurrenceRule = source.RecurrenceRule,
            RecurrenceStartLocal = source.RecurrenceStartLocal,
            RecurrenceOccurrencesBeforeNext = source.RecurrenceOccurrencesBeforeNext,
            RecurrenceExhausted = source.RecurrenceExhausted,
            DayOfWeek = source.DayOfWeek,
            DayOfMonth = source.DayOfMonth,
            UseWorktree = source.UseWorktree,
            Enabled = source.Enabled,
            NextRunAtUtc = source.NextRunAtUtc
        };
    }

    public sealed record ModelChoice(string Id, string Label, string DefaultEffort, IReadOnlyList<string> Efforts)
    {
        public override string ToString() => Label;
    }

    public sealed record ExistingThreadChoice(CodexAutomationChat? Chat, string? ThreadId,
        string? WorkingDirectory, string? WorktreePath, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record CadenceChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record DayChoice(DayOfWeek Day, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record MonthDayChoice(int Day, string Label)
    {
        public override string ToString() => Label;
    }
}
