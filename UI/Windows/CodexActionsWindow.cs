using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
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
}

internal sealed class CodexActionsWindow : Window
{
    private readonly IList<CodexActionDefinition> _actions;
    private readonly IList<CodexActionDefinition> _projectActions;
    private readonly Func<bool> _saveActions;
    private readonly Func<bool> _saveProjectActions;
    private readonly Action<string> _insertPrompt;
    private readonly bool _projectActionsAvailable;
    private readonly ListBox _list = new()
    {
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderThickness = new Thickness(0)
    };
    private readonly Button _editButton = new() { Content = "Изменить", IsEnabled = false };
    private readonly Button _deleteButton = new() { Content = "Удалить", IsEnabled = false };
    private readonly Button _insertButton = new() { Content = "Вставить в черновик", IsEnabled = false };
    private readonly Button _addLocalButton = MakeButton("＋  Личное действие");
    private readonly Button _addProjectButton = MakeButton("＋  Действие проекта");
    private readonly TextBlock _empty = new()
    {
        Text = "Пока нет сохранённых личных или проектных действий.",
        Foreground = Brush("#A7B5C0"),
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
        Action<string> insertPrompt)
    {
        _actions = actions;
        _projectActions = projectActions;
        _saveActions = saveActions;
        _saveProjectActions = saveProjectActions;
        _projectActionsAvailable = projectActionsAvailable;
        _insertPrompt = insertPrompt;
        Title = "Действия для композера";
        Width = 760;
        Height = 560;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        _list.ItemContainerStyle = CreateActionItemStyle();
        Content = BuildLayout();
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => InsertSelected();
        RefreshList();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
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
            Text = "Личные действия хранятся в CodexGUI. Действия проекта сохраняются в .codex/codexgui-actions.json; добавьте этот файл в Git, чтобы передать шаблоны коллегам. Выбор только вставляет текст в черновик.",
            FontSize = 11,
            Foreground = Brush("#A7B5C0"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 2);
        root.Children.Add(listArea);

        var buttons = new DockPanel { Margin = new Thickness(0, 15, 0, 0), LastChildFill = false };
        var close = MakeButton("Закрыть");
        close.IsCancel = true;
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        _insertButton.Background = Brush("#204139");
        _insertButton.Foreground = Brush("#92E5C7");
        _insertButton.BorderBrush = Brush("#365C53");
        _insertButton.BorderThickness = new Thickness(1);
        _insertButton.Margin = new Thickness(0, 0, 8, 0);
        _insertButton.Click += (_, _) => InsertSelected();
        DockPanel.SetDock(_insertButton, Dock.Right);
        buttons.Children.Add(_insertButton);
        _deleteButton.Margin = new Thickness(8, 0, 0, 0);
        _deleteButton.Click += (_, _) => DeleteSelected();
        buttons.Children.Add(_deleteButton);
        _editButton.Margin = new Thickness(8, 0, 0, 0);
        _editButton.Click += (_, _) => EditSelected();
        buttons.Children.Add(_editButton);
        _addProjectButton.IsEnabled = _projectActionsAvailable;
        _addProjectButton.ToolTip = _projectActionsAvailable
            ? "Сохранить действие в .codex/codexgui-actions.json выбранного проекта"
            : "Папка выбранного проекта недоступна";
        _addProjectButton.Click += (_, _) => AddAction(isProject: true);
        buttons.Children.Add(_addProjectButton);
        _addLocalButton.Click += (_, _) => AddAction(isProject: false);
        buttons.Children.Add(_addLocalButton);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        return root;
    }

    private void RefreshList(string? selectId = null, bool selectProjectAction = false)
    {
        _list.Items.Clear();
        foreach (var entry in GetActionEntries()
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
                Foreground = Brush("#ECF2F5"),
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
                Text = action.Prompt,
                FontSize = 11,
                Foreground = Brush("#A7B5C0"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 38,
                Margin = new Thickness(0, 4, 0, 0)
            });
            var item = new ListBoxItem
            {
                Content = content,
                Tag = action,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Background = Brush("#16212B"),
                BorderBrush = Brush("#2A3946"),
                BorderThickness = new Thickness(1)
            };
            _list.Items.Add(item);
            if (action.Id == selectId && entry.IsProject == selectProjectAction) _list.SelectedItem = item;
        }
        _empty.Visibility = _actions.Count + _projectActions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void AddAction(bool isProject)
    {
        var editor = new CodexActionEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        if (NameExists(result.Name, null)) return;
        var action = new CodexActionDefinition { Name = result.Name, Prompt = result.Prompt };
        var actions = isProject ? _projectActions : _actions;
        actions.Add(action);
        if (!SaveActions(isProject))
        {
            actions.Remove(action);
            return;
        }
        RefreshList(action.Id, isProject);
    }

    private void EditSelected()
    {
        if (SelectedEntry() is not { } entry) return;
        var action = entry.Action;
        var editor = new CodexActionEditorWindow(action) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        if (NameExists(result.Name, action)) return;
        var oldName = action.Name;
        var oldPrompt = action.Prompt;
        action.Name = result.Name;
        action.Prompt = result.Prompt;
        if (!SaveActions(entry.IsProject))
        {
            action.Name = oldName;
            action.Prompt = oldPrompt;
            return;
        }
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

    private bool NameExists(string name, CodexActionDefinition? exceptAction)
    {
        if (!GetActionEntries().Any(entry => !ReferenceEquals(entry.Action, exceptAction)
            && string.Equals(entry.Action.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        CodexMessageDialog.Show(this, "Действие с таким названием уже есть.", "Переименуйте действие",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void InsertSelected()
    {
        if (SelectedEntry() is not { } entry) return;
        _insertPrompt(entry.Action.Prompt);
        Close();
    }

    private IEnumerable<ActionEntry> GetActionEntries()
        => _actions.Select(action => new ActionEntry(action, IsProject: false))
            .Concat(_projectActions.Select(action => new ActionEntry(action, IsProject: true)));

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
        var hasSelection = SelectedEntry() is not null;
        _editButton.IsEnabled = hasSelection;
        _deleteButton.IsEnabled = hasSelection;
        _insertButton.IsEnabled = hasSelection;
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
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
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
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#20303D" /></Trigger>
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
    private readonly TextBox _prompt = new();

    public (string Name, string Prompt)? Result { get; private set; }

    public CodexActionEditorWindow(CodexActionDefinition? source)
    {
        _source = source;
        Title = source is null ? "Новое действие" : "Изменить действие";
        Width = 620;
        Height = 510;
        MinWidth = 500;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
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
        form.Children.Add(_name);
        AddLabel(form, "Текст для композера", new Thickness(0, 15, 0, 6));
        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _prompt.MinHeight = 210;
        _prompt.Padding = new Thickness(10, 8, 10, 8);
        form.Children.Add(_prompt);
        form.Children.Add(new TextBlock
        {
            Text = "Шаблон только вставляется в черновик; перед отправкой его можно изменить.",
            Foreground = Brush("#A7B5C0"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return root;
    }

    private void LoadValues()
    {
        _name.Text = _source?.Name ?? "";
        _prompt.Text = _source?.Prompt ?? "";
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        var prompt = _prompt.Text.Trim();
        if (name.Length == 0 || prompt.Length == 0)
        {
            CodexMessageDialog.Show(this, "Заполните название и текст действия.", "Проверьте действие",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (prompt.Length > 12000)
        {
            CodexMessageDialog.Show(this, "Текст действия не должен превышать 12 000 символов.", "Слишком длинный текст",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = (name, prompt);
        DialogResult = true;
    }

    private static void AddLabel(Panel parent, string text, Thickness? margin = null)
        => parent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#A7B5C0"),
            Margin = margin ?? new Thickness(0, 0, 0, 6)
        });

    private static Button CodexActionsWindowButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
