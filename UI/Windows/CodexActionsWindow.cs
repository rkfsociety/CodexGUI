using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexActionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Command { get; set; } = "";

    [JsonIgnore]
    public bool IsCommand => !string.IsNullOrWhiteSpace(Command);

    [JsonIgnore]
    public bool HasValidPayload
    {
        get
        {
            var prompt = !string.IsNullOrWhiteSpace(Prompt);
            var command = !string.IsNullOrWhiteSpace(Command);
            var contentLength = prompt ? Prompt?.Length ?? 0 : Command?.Length ?? 0;
            return prompt != command
                && contentLength <= 12000
                && (prompt ? Command == "" : Prompt == "");
        }
    }
}

internal sealed class CodexActionsWindow : Window
{
    private readonly IList<CodexActionDefinition> _actions;
    private readonly IList<CodexActionDefinition> _projectActions;
    private readonly Func<bool> _saveActions;
    private readonly Func<bool> _saveProjectActions;
    private readonly Action<string> _insertPrompt;
    private readonly Action<string, string> _runCommand;
    private readonly bool _projectActionsAvailable;
    private readonly TextBox _search = new();
    private readonly ListBox _list = new()
    {
        Background = Brush("#1D1F23"),
        Foreground = Brush("#F1F3F5"),
        BorderThickness = new Thickness(0)
    };
    private readonly Button _editButton = new() { Content = "Изменить", IsEnabled = false };
    private readonly Button _deleteButton = new() { Content = "Удалить", IsEnabled = false };
    private readonly Button _actionButton = new() { Content = "Вставить в черновик", IsEnabled = false };
    private readonly Button _copyButton = MakeButton("Копировать");
    private readonly Button _addLocalButton = MakeButton("＋  Личное");
    private readonly Button _addProjectButton = MakeButton("＋  Проект");
    private readonly TextBlock _empty = new()
    {
        Text = "Пока нет сохранённых личных или проектных действий.",
        Foreground = Brush("#B0B5BE"),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(8, 8, 8, 0)
    };

    public CodexActionsWindow(
        IList<CodexActionDefinition> actions,
        IList<CodexActionDefinition> projectActions,
        Func<bool> saveActions,
        Func<bool> saveProjectActions,
        bool projectActionsAvailable,
        Action<string> insertPrompt,
        Action<string, string> runCommand)
    {
        _actions = actions;
        _projectActions = projectActions;
        _saveActions = saveActions;
        _saveProjectActions = saveProjectActions;
        _projectActionsAvailable = projectActionsAvailable;
        _insertPrompt = insertPrompt;
        _runCommand = runCommand;
        Title = "Действия CodexGUI";
        Width = 760;
        Height = 560;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        _list.ItemContainerStyle = CreateActionItemStyle();
        Content = BuildLayout();
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, e) =>
        {
            // Double-clicking blank space must not execute a previously selected action.
            if (e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(_list, source) is not ListBoxItem item
                || !ReferenceEquals(item, _list.SelectedItem))
                return;
            InvokeSelected();
        };
        _search.TextChanged += (_, _) => RefreshList();
        RefreshList();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Сохранённые действия",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        var note = new TextBlock
        {
            Text = "Личные действия хранятся в CodexGUI. Действия проекта сохраняются в .codex/codexgui-actions.json; добавляйте команды в Git только после проверки их содержимого. Prompt-действия вставляют текст в черновик. Команды PowerShell запускаются только по явному выбору и открывают терминал.",
            FontSize = 11,
            Foreground = Brush("#B0B5BE"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var filter = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filter.Children.Add(new TextBlock
        {
            Text = "Поиск",
            Foreground = Brush("#B0B5BE"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        _search.Height = 36;
        _search.Padding = new Thickness(10, 8, 10, 5);
        _search.Background = Brush("#25272C");
        _search.Foreground = Brush("#F1F3F5");
        _search.BorderBrush = Brush("#34373E");
        _search.ToolTip = "Фильтр по названию, тексту и области действия";
        AutomationProperties.SetName(_search, "Фильтр действий по названию, тексту и области");
        Grid.SetColumn(_search, 1);
        filter.Children.Add(_search);
        Grid.SetRow(filter, 2);
        root.Children.Add(filter);

        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 3);
        root.Children.Add(listArea);

        var buttons = new DockPanel { Margin = new Thickness(0, 15, 0, 0), LastChildFill = false };
        var close = MakeButton("Закрыть");
        close.IsCancel = true;
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        _actionButton.Background = Brush("#204139");
        _actionButton.Foreground = Brush("#92E5C7");
        _actionButton.BorderBrush = Brush("#365C53");
        _actionButton.BorderThickness = new Thickness(1);
        _actionButton.Margin = new Thickness(0, 0, 8, 0);
        _actionButton.Click += (_, _) => InvokeSelected();
        DockPanel.SetDock(_actionButton, Dock.Right);
        buttons.Children.Add(_actionButton);
        _deleteButton.Margin = new Thickness(8, 0, 0, 0);
        _deleteButton.Click += (_, _) => DeleteSelected();
        buttons.Children.Add(_deleteButton);
        _editButton.Margin = new Thickness(8, 0, 0, 0);
        _editButton.Click += (_, _) => EditSelected();
        buttons.Children.Add(_editButton);
        _copyButton.Margin = new Thickness(8, 0, 0, 0);
        _copyButton.Click += (_, _) => CopySelected();
        buttons.Children.Add(_copyButton);
        _addProjectButton.IsEnabled = _projectActionsAvailable;
        _addProjectButton.ToolTip = _projectActionsAvailable
            ? "Сохранить действие в .codex/codexgui-actions.json выбранного проекта"
            : "Папка выбранного проекта недоступна";
        _addProjectButton.Click += (_, _) => AddAction(isProject: true);
        buttons.Children.Add(_addProjectButton);
        _addLocalButton.ToolTip = "Сохранить действие в личной библиотеке CodexGUI";
        _addLocalButton.Click += (_, _) => AddAction(isProject: false);
        buttons.Children.Add(_addLocalButton);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    private void RefreshList(string? selectId = null, bool selectProjectAction = false)
    {
        _list.Items.Clear();
        var query = _search.Text.Trim();
        foreach (var entry in GetActionEntries()
                     .Where(entry => MatchesSearch(entry, query))
                     .OrderBy(item => item.Action.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.IsProject))
        {
            var action = entry.Action;
            var content = new StackPanel();
            var title = new Grid();
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.Children.Add(new TextBlock
            {
                Text = action.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#F1F3F5"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var scope = new TextBlock
            {
                Text = entry.IsProject ? "ПРОЕКТ" : "ЛИЧНОЕ",
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(entry.IsProject ? "#92E5C7" : "#81909C"),
                Margin = new Thickness(12, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(scope, 1);
            title.Children.Add(scope);
            content.Children.Add(title);
            content.Children.Add(new TextBlock
            {
                Text = action.IsCommand ? "PowerShell: " + action.Command : action.Prompt,
                FontSize = 11,
                Foreground = Brush("#B0B5BE"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 38,
                Margin = new Thickness(0, 4, 0, 0)
            });
            var item = new ListBoxItem
            {
                Content = content,
                Tag = entry,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Background = Brush("#1D1F23"),
                BorderBrush = Brush("#34373E"),
                BorderThickness = new Thickness(1)
            };
            _list.Items.Add(item);
            if (action.Id == selectId && entry.IsProject == selectProjectAction) _list.SelectedItem = item;
        }
        _empty.Text = _actions.Count + _projectActions.Count == 0
            ? "Пока нет сохранённых личных или проектных действий."
            : "Ничего не найдено. Измените запрос поиска.";
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private static bool MatchesSearch(ActionEntry entry, string query)
        => query.Length == 0
            || entry.Action.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || entry.Action.Prompt.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || entry.Action.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (entry.IsProject ? "проект" : "личное").Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void AddAction(bool isProject)
    {
        var editor = new CodexActionEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        if (NameExists(result.Name, isProject, null)) return;
        var action = new CodexActionDefinition { Name = result.Name, Prompt = result.Prompt, Command = result.Command };
        var actions = isProject ? _projectActions : _actions;
        actions.Add(action);
        if (!SaveActions(isProject))
        {
            actions.Remove(action);
            return;
        }
        ClearSearchFilter();
        RefreshList(action.Id, isProject);
    }

    private void EditSelected()
    {
        if (SelectedEntry() is not { } entry) return;
        var action = entry.Action;
        var editor = new CodexActionEditorWindow(action) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        if (NameExists(result.Name, entry.IsProject, action)) return;
        var oldName = action.Name;
        var oldPrompt = action.Prompt;
        var oldCommand = action.Command;
        action.Name = result.Name;
        action.Prompt = result.Prompt;
        action.Command = result.Command;
        if (!SaveActions(entry.IsProject))
        {
            action.Name = oldName;
            action.Prompt = oldPrompt;
            action.Command = oldCommand;
            return;
        }
        ClearSearchFilter();
        RefreshList(action.Id, entry.IsProject);
    }

    private void DeleteSelected()
    {
        if (SelectedEntry() is not { } entry) return;
        var action = entry.Action;
        if (CodexMessageDialog.Show(this, $"Удалить действие «{action.Name}»?", "Удалить действие",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var actions = entry.IsProject ? _projectActions : _actions;
        var index = actions.IndexOf(action);
        actions.RemoveAt(index);
        if (!SaveActions(entry.IsProject))
        {
            actions.Insert(index, action);
            return;
        }
        RefreshList();
    }

    private void CopySelected()
    {
        if (SelectedEntry() is not { } entry) return;
        var targetIsProject = !entry.IsProject;
        if (targetIsProject && !_projectActionsAvailable) return;
        if (NameExists(entry.Action.Name, targetIsProject, exceptAction: null)) return;

        var targetActions = targetIsProject ? _projectActions : _actions;
        var copy = new CodexActionDefinition
        {
            Name = entry.Action.Name,
            Prompt = entry.Action.Prompt,
            Command = entry.Action.Command
        };
        targetActions.Add(copy);
        if (!SaveActions(targetIsProject))
        {
            targetActions.Remove(copy);
            return;
        }
        ClearSearchFilter();
        RefreshList(copy.Id, targetIsProject);
    }

    private bool NameExists(string name, bool isProject, CodexActionDefinition? exceptAction)
    {
        var actions = isProject ? _projectActions : _actions;
        if (!actions.Any(action => !ReferenceEquals(action, exceptAction)
            && string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        CodexMessageDialog.Show(this, "Действие с таким названием уже есть в этой библиотеке.", "Переименуйте действие",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void InvokeSelected()
    {
        if (SelectedEntry() is not { } entry) return;
        if (entry.Action.IsCommand)
        {
            var action = entry.Action;
            Close();
            _runCommand(action.Name, action.Command);
            return;
        }
        _insertPrompt(entry.Action.Prompt);
        Close();
    }

    private IEnumerable<ActionEntry> GetActionEntries()
        => _actions.Select(action => new ActionEntry(action, IsProject: false))
            .Concat(_projectActions.Select(action => new ActionEntry(action, IsProject: true)));

    private void ClearSearchFilter()
    {
        if (_search.Text.Length > 0) _search.Clear();
    }

    private ActionEntry? SelectedEntry() => (_list.SelectedItem as ListBoxItem)?.Tag as ActionEntry;

    private bool SaveActions(bool isProject)
    {
        try
        {
            var saved = isProject ? _saveProjectActions() : _saveActions();
            if (!saved) ShowSaveError(isProject);
            return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NotSupportedException)
        {
            ShowSaveError(isProject, error.Message);
            return false;
        }
    }

    private void UpdateButtons()
    {
        var selected = SelectedEntry();
        var hasSelection = selected is not null;
        _editButton.IsEnabled = hasSelection;
        _deleteButton.IsEnabled = hasSelection;
        _actionButton.IsEnabled = hasSelection;
        _actionButton.Content = selected?.Action.IsCommand == true ? "Запустить в PowerShell" : "Вставить в черновик";
        _actionButton.ToolTip = selected?.Action.IsCommand == true
            ? "Запустить команду в терминале текущего проекта или worktree"
            : "Вставить prompt в черновик без отправки запроса";
        _copyButton.Content = selected is { IsProject: true }
            ? "Копия в личные"
            : selected is not null ? "Копия в проект" : "Копировать";
        _copyButton.IsEnabled = selected is { IsProject: true }
            || selected is { IsProject: false } && _projectActionsAvailable;
        _copyButton.ToolTip = selected is null
            ? "Выберите действие для копирования"
            : selected.IsProject
                ? "Создать личную копию выбранного проектного действия"
                : _projectActionsAvailable
                    ? "Создать копию выбранного личного действия в текущем проекте"
                    : "Папка выбранного проекта недоступна";
    }

    private void ShowSaveError(bool isProject, string? detail = null)
    {
        var message = isProject
            ? "Не удалось сохранить .codex/codexgui-actions.json выбранного проекта. Проверьте права доступа к папке проекта."
            : "CodexGUI не смог сохранить личный список действий. Проверьте состояние файлов приложения.";
        if (!string.IsNullOrWhiteSpace(detail)) message += Environment.NewLine + detail;
        CodexMessageDialog.Show(this, message, "Не удалось сохранить действия",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed record ActionEntry(CodexActionDefinition Action, bool IsProject);

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(11, 7, 11, 7),
        Background = Brush("#1D1F23"),
        Foreground = Brush("#F1F3F5"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1)
    };

    private static Style CreateActionItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{x:Type ListBoxItem}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#282B30" /></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#203A34" /><Setter TargetName="Chrome" Property="BorderBrush" Value="#4B7668" /></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#92E5C7" /></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """)));
        return style;
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class CodexActionEditorWindow : Window
{
    private readonly CodexActionDefinition? _source;
    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly TextBlock _payloadLabel = new();
    private readonly TextBox _prompt = new();
    private readonly TextBox _command = new();
    private readonly TextBlock _payloadHelp = new();

    public (string Name, string Prompt, string Command)? Result { get; private set; }

    public CodexActionEditorWindow(CodexActionDefinition? source)
    {
        _source = source;
        Title = source is null ? "Новое действие" : "Изменить действие";
        Width = 620;
        Height = 570;
        MinWidth = 500;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        _type.SelectionChanged += (_, _) => UpdatePayloadEditor();
        LoadValues();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = CodexActionsWindowButton("Отмена");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = CodexActionsWindowButton("Сохранить");
        save.IsDefault = true;
        save.Background = Brush("#204139");
        save.Foreground = Brush("#92E5C7");
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        var form = new StackPanel();
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form });
        AddLabel(form, "Название действия");
        _name.Height = 38;
        _name.MaxLength = 80;
        _name.Padding = new Thickness(10, 7, 10, 5);
        _name.Background = Brush("#25272C");
        _name.Foreground = Brush("#F1F3F5");
        _name.BorderBrush = Brush("#34373E");
        _name.BorderThickness = new Thickness(1);
        form.Children.Add(_name);
        AddLabel(form, "Тип действия", new Thickness(0, 15, 0, 6));
        _type.Items.Add("Текст для композера");
        _type.Items.Add("Команда PowerShell");
        _type.SelectedIndex = 0;
        _type.Height = 36;
        AutomationProperties.SetName(_type, "Тип действия");
        form.Children.Add(_type);
        _payloadLabel.Text = "Текст для композера";
        _payloadLabel.FontSize = 11;
        _payloadLabel.FontWeight = FontWeights.SemiBold;
        _payloadLabel.Foreground = Brush("#B0B5BE");
        _payloadLabel.Margin = new Thickness(0, 15, 0, 6);
        form.Children.Add(_payloadLabel);
        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _prompt.MaxLength = 12000;
        _prompt.MinHeight = 210;
        _prompt.Padding = new Thickness(10, 8, 10, 8);
        _prompt.Background = Brush("#25272C");
        _prompt.Foreground = Brush("#F1F3F5");
        _prompt.BorderBrush = Brush("#34373E");
        _prompt.BorderThickness = new Thickness(1);
        AutomationProperties.SetName(_prompt, "Текст prompt-действия");
        form.Children.Add(_prompt);
        _command.AcceptsReturn = true;
        _command.TextWrapping = TextWrapping.Wrap;
        _command.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _command.MaxLength = 12000;
        _command.MinHeight = 210;
        _command.Padding = new Thickness(10, 8, 10, 8);
        _command.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _command.Background = Brush("#25272C");
        _command.Foreground = Brush("#F1F3F5");
        _command.BorderBrush = Brush("#34373E");
        _command.BorderThickness = new Thickness(1);
        _command.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_command, "Команда PowerShell");
        form.Children.Add(_command);
        _payloadHelp.Foreground = Brush("#B0B5BE");
        _payloadHelp.FontSize = 10;
        _payloadHelp.TextWrapping = TextWrapping.Wrap;
        _payloadHelp.Margin = new Thickness(0, 8, 0, 0);
        form.Children.Add(_payloadHelp);
        UpdatePayloadEditor();
        return root;
    }

    private void LoadValues()
    {
        _name.Text = _source?.Name ?? "";
        _prompt.Text = _source?.Prompt ?? "";
        _command.Text = _source?.Command ?? "";
        _type.SelectedIndex = _source?.IsCommand == true ? 1 : 0;
        UpdatePayloadEditor();
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        var isCommand = _type.SelectedIndex == 1;
        var prompt = isCommand ? "" : _prompt.Text.Trim();
        var command = isCommand ? _command.Text : "";
        var payload = isCommand ? command : prompt;
        if (name.Length == 0 || string.IsNullOrWhiteSpace(payload))
        {
            CodexMessageDialog.Show(this, "Заполните название и содержимое действия.", "Проверьте действие",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (payload.Length > 12000)
        {
            CodexMessageDialog.Show(this, "Содержимое действия не должно превышать 12 000 символов.", "Слишком длинное действие",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = (name, prompt, command);
        DialogResult = true;
    }

    private void UpdatePayloadEditor()
    {
        var isCommand = _type.SelectedIndex == 1;
        _payloadLabel.Text = isCommand ? "Команда PowerShell" : "Текст для композера";
        _prompt.Visibility = isCommand ? Visibility.Collapsed : Visibility.Visible;
        _command.Visibility = isCommand ? Visibility.Visible : Visibility.Collapsed;
        _payloadHelp.Text = isCommand
            ? "Запуск возможен только по явному выбору действия. Терминал покажет вывод и запросит разрешение, если оно требуется режимом доступа проекта."
            : "Шаблон вставляется в черновик; перед отправкой его можно изменить.";
    }

    private static void AddLabel(Panel parent, string text, Thickness? margin = null)
        => parent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#B0B5BE"),
            Margin = margin ?? new Thickness(0, 0, 0, 6)
        });

    private static Button CodexActionsWindowButton(string text) => new()
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
