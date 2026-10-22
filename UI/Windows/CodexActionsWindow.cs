using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Func<bool> _saveActions;
    private readonly Action<string> _insertPrompt;
    private readonly ListBox _list = new()
    {
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderThickness = new Thickness(0)
    };
    private readonly Button _editButton = new() { Content = "Изменить", IsEnabled = false };
    private readonly Button _deleteButton = new() { Content = "Удалить", IsEnabled = false };
    private readonly Button _insertButton = new() { Content = "Вставить в черновик", IsEnabled = false };
    private readonly TextBlock _empty = new()
    {
        Text = "Сохранённых действий пока нет. Создайте шаблон для повторяющихся запросов.",
        Foreground = Brush("#A7B5C0"),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(8, 8, 8, 0)
    };

    public CodexActionsWindow(IList<CodexActionDefinition> actions, Func<bool> saveActions, Action<string> insertPrompt)
    {
        _actions = actions;
        _saveActions = saveActions;
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
            Text = "Выберите шаблон, чтобы добавить его в черновик. Запрос не отправится, пока вы сами не нажмёте кнопку отправки.",
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
        var add = MakeButton("＋  Новое действие");
        add.Click += (_, _) => AddAction();
        buttons.Children.Add(add);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        return root;
    }

    private void RefreshList(string? selectId = null)
    {
        _list.Items.Clear();
        foreach (var action in _actions.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = action.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#ECF2F5"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
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
            if (action.Id == selectId) _list.SelectedItem = item;
        }
        _empty.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void AddAction()
    {
        var editor = new CodexActionEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        if (NameExists(result.Name, null)) return;
        var action = new CodexActionDefinition { Name = result.Name, Prompt = result.Prompt };
        _actions.Add(action);
        if (!_saveActions())
        {
            _actions.Remove(action);
            ShowSaveError();
            return;
        }
        RefreshList(action.Id);
    }

    private void EditSelected()
    {
        if (SelectedAction() is not { } action) return;
        var editor = new CodexActionEditorWindow(action) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        if (NameExists(result.Name, action.Id)) return;
        var oldName = action.Name;
        var oldPrompt = action.Prompt;
        action.Name = result.Name;
        action.Prompt = result.Prompt;
        if (!_saveActions())
        {
            action.Name = oldName;
            action.Prompt = oldPrompt;
            ShowSaveError();
            return;
        }
        RefreshList(action.Id);
    }

    private void DeleteSelected()
    {
        if (SelectedAction() is not { } action) return;
        if (CodexMessageDialog.Show(this, $"Удалить действие «{action.Name}»?", "Удалить действие",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var index = _actions.IndexOf(action);
        _actions.RemoveAt(index);
        if (!_saveActions())
        {
            _actions.Insert(index, action);
            ShowSaveError();
            return;
        }
        RefreshList();
    }

    private bool NameExists(string name, string? exceptId)
    {
        if (!_actions.Any(action => action.Id != exceptId && string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        CodexMessageDialog.Show(this, "Действие с таким названием уже есть.", "Переименуйте действие",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void InsertSelected()
    {
        if (SelectedAction() is not { } action) return;
        _insertPrompt(action.Prompt);
        Close();
    }

    private CodexActionDefinition? SelectedAction() => (_list.SelectedItem as ListBoxItem)?.Tag as CodexActionDefinition;

    private void UpdateButtons()
    {
        var hasSelection = SelectedAction() is not null;
        _editButton.IsEnabled = hasSelection;
        _deleteButton.IsEnabled = hasSelection;
        _insertButton.IsEnabled = hasSelection;
    }

    private void ShowSaveError()
        => CodexMessageDialog.Show(this, "CodexGUI не смог сохранить список действий. Проверьте состояние файлов приложения.",
            "Не удалось сохранить действия", MessageBoxButton.OK, MessageBoxImage.Error);

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
