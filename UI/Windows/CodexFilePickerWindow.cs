using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexFilePickerWindow : Window
{
    private enum PickerMode
    {
        Folder,
        OpenFile,
        SaveFile
    }

    private sealed record PickerEntry(string Name, string Path, bool IsDirectory, string Details);

    private readonly PickerMode _mode;
    private readonly string[] _extensions;
    private readonly string _defaultExtension;
    private readonly TextBox _location = new();
    private readonly ListBox _entries = new();
    private readonly TextBox _fileName = new();
    private readonly TextBlock _status = new();
    private readonly Button _upButton = new();
    private Button _confirmButton = new();
    private string _currentDirectory;

    public string? SelectedPath { get; private set; }

    public static string? ChooseFolder(Window owner, string title, string initialDirectory)
        => ShowDialog(owner, new CodexFilePickerWindow(title, initialDirectory, [], "", "", "folder"));

    public static string? OpenFile(Window owner, string title, string initialDirectory,
        string[] extensions, string? initialFileName = null)
        => ShowDialog(owner, new CodexFilePickerWindow(title, initialDirectory, extensions, "", initialFileName ?? "", "open"));

    public static string? SaveFile(Window owner, string title, string initialDirectory,
        string initialFileName, string defaultExtension)
        => ShowDialog(owner, new CodexFilePickerWindow(title, initialDirectory, [], defaultExtension, initialFileName, "save"));

    public CodexFilePickerWindow(string title, string initialDirectory, string[] extensions,
        string defaultExtension, string fileName, string mode)
    {
        _mode = mode switch
        {
            "folder" => PickerMode.Folder,
            "open" => PickerMode.OpenFile,
            "save" => PickerMode.SaveFile,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        _extensions = (extensions ?? []).Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _defaultExtension = NormalizeExtension(defaultExtension);
        if (!string.IsNullOrWhiteSpace(fileName) && Path.IsPathRooted(fileName))
        {
            var fileDirectory = Path.GetDirectoryName(fileName);
            if (!string.IsNullOrWhiteSpace(fileDirectory) && Directory.Exists(fileDirectory))
                initialDirectory = fileDirectory;
            fileName = Path.GetFileName(fileName);
        }

        _currentDirectory = Directory.Exists(initialDirectory)
            ? Path.GetFullPath(initialDirectory)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(_currentDirectory))
            _currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Title = title;
        Width = 760;
        Height = 590;
        MinWidth = 580;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout(title, fileName);
        RefreshEntries();
    }

    private static string? ShowDialog(Window owner, CodexFilePickerWindow dialog)
    {
        dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.SelectedPath : null;
    }

    private UIElement BuildLayout(string title, string fileName)
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = _mode == PickerMode.Folder
                ? "Перейдите в папку и выберите её."
                : "Перейдите к файлу или укажите путь к папке.",
            Foreground = Brush("#B0B5BE"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var navigation = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _upButton.Content = "↑";
        _upButton.Width = 36;
        _upButton.Margin = new Thickness(0, 0, 8, 0);
        _upButton.Background = Brush("#1D1F23");
        _upButton.Foreground = Brush("#B0B5BE");
        _upButton.BorderBrush = Brush("#34373E");
        _upButton.BorderThickness = new Thickness(1);
        _upButton.ToolTip = "Вверх на один уровень";
        AutomationProperties.SetName(_upButton, "Вверх на один уровень");
        _upButton.Click += (_, _) => NavigateToParent();
        navigation.Children.Add(_upButton);

        var locationBorder = CreateInputBorder(_location);
        Grid.SetColumn(locationBorder, 1);
        navigation.Children.Add(locationBorder);
        _location.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            NavigateToLocation();
            e.Handled = true;
        };

        var go = new Button
        {
            Content = "Перейти",
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1)
        };
        go.Click += (_, _) => NavigateToLocation();
        Grid.SetColumn(go, 2);
        navigation.Children.Add(go);
        Grid.SetRow(navigation, 1);
        root.Children.Add(navigation);

        _entries.Background = Brush("#0E1720");
        _entries.Foreground = Brush("#F1F3F5");
        _entries.BorderBrush = Brush("#34373E");
        _entries.BorderThickness = new Thickness(1);
        _entries.Padding = new Thickness(6);
        _entries.SelectionChanged += (_, _) => SelectionChanged();
        _entries.MouseDoubleClick += (_, _) => OpenSelectedEntry();
        _entries.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            OpenSelectedEntry();
            e.Handled = true;
        };
        Grid.SetRow(_entries, 2);
        root.Children.Add(_entries);

        var fileNamePanel = new Grid { Margin = new Thickness(0, 11, 0, 0) };
        fileNamePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        fileNamePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fileNameLabel = new TextBlock
        {
            Text = "Имя файла",
            FontSize = 11,
            Foreground = Brush("#B0B5BE"),
            VerticalAlignment = VerticalAlignment.Center
        };
        fileNamePanel.Children.Add(fileNameLabel);
        var fileNameBorder = CreateInputBorder(_fileName);
        Grid.SetColumn(fileNameBorder, 1);
        fileNamePanel.Children.Add(fileNameBorder);
        _fileName.Text = fileName;
        _fileName.TextChanged += (_, _) => UpdateConfirmButton();
        _fileName.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ConfirmSelection();
            e.Handled = true;
        };
        fileNamePanel.Visibility = _mode == PickerMode.Folder ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(fileNamePanel, 3);
        root.Children.Add(fileNamePanel);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = Brush("#8D95A2");
        _status.FontSize = 10;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.ToolTip = _currentDirectory;
        footer.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button
        {
            Content = "Отмена",
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1)
        };
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel);
        _confirmButton = new Button
        {
            Content = _mode switch
            {
                PickerMode.Folder => "Выбрать папку",
                PickerMode.SaveFile => "Сохранить",
                _ => "Открыть"
            },
            IsDefault = true,
            Background = Brush("#204139"),
            Foreground = Brush("#92E5C7"),
            BorderBrush = Brush("#365C53")
        };
        _confirmButton.Click += (_, _) => ConfirmSelection();
        actions.Children.Add(_confirmButton);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        return root;
    }

    private static Border CreateInputBorder(TextBox input)
    {
        input.Padding = new Thickness(9, 6, 9, 6);
        input.Background = Brush("#181A1E");
        input.Foreground = Brush("#F1F3F5");
        input.BorderThickness = new Thickness(0);
        input.CaretBrush = Brush("#F1F3F5");
        return new Border
        {
            Background = Brush("#181A1E"),
            BorderBrush = Brush("#314454"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = input
        };
    }

    private void RefreshEntries()
    {
        _entries.Items.Clear();
        _location.Text = _currentDirectory;
        _upButton.IsEnabled = Directory.GetParent(_currentDirectory) is not null;
        try
        {
            var entries = Directory.EnumerateDirectories(_currentDirectory)
                .Select(path => new PickerEntry(Path.GetFileName(path), path, true, "Папка"))
                .ToList();
            if (_mode != PickerMode.Folder)
            {
                entries.AddRange(Directory.EnumerateFiles(_currentDirectory)
                    .Where(IsAllowedFile)
                    .Select(path => new PickerEntry(Path.GetFileName(path), path, false, DescribeFile(path))));
            }

            foreach (var entry in entries.OrderByDescending(entry => entry.IsDirectory)
                         .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
                _entries.Items.Add(CreateEntryItem(entry));

            _status.Foreground = Brush("#8D95A2");
            _status.Text = entries.Count == 1 ? "1 элемент" : $"Элементов: {entries.Count}";
            _status.ToolTip = _currentDirectory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Foreground = Brush("#F28B82");
            _status.Text = "Не удалось прочитать папку: " + error.Message;
            _status.ToolTip = _currentDirectory;
        }
        UpdateConfirmButton();
    }

    private ListBoxItem CreateEntryItem(PickerEntry entry)
    {
        var row = new Grid { Height = 30 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = entry.IsDirectory ? "▰" : "▤",
            Foreground = Brush(entry.IsDirectory ? "#92E5C7" : "#B0B5BE"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var name = new TextBlock
        {
            Text = entry.Name,
            Foreground = Brush("#F1F3F5"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var details = new TextBlock
        {
            Text = entry.Details,
            Foreground = Brush("#8D95A2"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0)
        };
        Grid.SetColumn(details, 2);
        row.Children.Add(details);
        return new ListBoxItem
        {
            Tag = entry,
            Content = row,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private void NavigateToLocation()
    {
        var path = _location.Text.Trim();
        if (!Directory.Exists(path))
        {
            _status.Foreground = Brush("#F28B82");
            _status.Text = "Папка не найдена.";
            return;
        }
        _currentDirectory = Path.GetFullPath(path);
        RefreshEntries();
    }

    private void NavigateToParent()
    {
        if (Directory.GetParent(_currentDirectory) is { } parent)
        {
            _currentDirectory = parent.FullName;
            RefreshEntries();
        }
    }

    private void SelectionChanged()
    {
        if (_entries.SelectedItem is ListBoxItem { Tag: PickerEntry entry } && !entry.IsDirectory && _mode != PickerMode.Folder)
            _fileName.Text = entry.Name;
        UpdateConfirmButton();
    }

    private void OpenSelectedEntry()
    {
        if (_entries.SelectedItem is not ListBoxItem { Tag: PickerEntry entry }) return;
        if (entry.IsDirectory)
        {
            _currentDirectory = entry.Path;
            RefreshEntries();
        }
        else if (_mode == PickerMode.OpenFile)
        {
            SelectedPath = entry.Path;
            DialogResult = true;
        }
        else
        {
            _fileName.Text = entry.Name;
        }
    }

    private void ConfirmSelection()
    {
        if (_mode == PickerMode.Folder)
        {
            SelectedPath = _entries.SelectedItem is ListBoxItem { Tag: PickerEntry { IsDirectory: true } entry }
                ? entry.Path
                : _currentDirectory;
            DialogResult = true;
            return;
        }

        if (_mode == PickerMode.OpenFile && _entries.SelectedItem is ListBoxItem { Tag: PickerEntry { IsDirectory: true } selectedDirectory })
        {
            _currentDirectory = selectedDirectory.Path;
            RefreshEntries();
            return;
        }

        var inputName = _fileName.Text.Trim();
        if (inputName.Length == 0)
        {
            ShowError("Укажите имя файла.");
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(Path.IsPathRooted(inputName) ? inputName : Path.Combine(_currentDirectory, inputName));
            if (_mode == PickerMode.SaveFile && Path.GetExtension(path).Length == 0 && _defaultExtension.Length > 0)
                path += _defaultExtension;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError(error.Message);
            return;
        }

        if (_mode == PickerMode.OpenFile)
        {
            if (!File.Exists(path))
            {
                ShowError("Файл не найден.");
                return;
            }
            if (!IsAllowedFile(path))
            {
                ShowError("Тип файла не поддерживается.");
                return;
            }
        }
        else
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                ShowError("Папка для сохранения не найдена.");
                return;
            }
            if (Directory.Exists(path))
            {
                ShowError("По указанному пути находится папка.");
                return;
            }
            if (File.Exists(path) && CodexMessageDialog.Show(this,
                    $"Файл «{Path.GetFileName(path)}» уже существует. Перезаписать его?",
                    "Подтвердите перезапись", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        SelectedPath = path;
        DialogResult = true;
    }

    private void UpdateConfirmButton()
    {
        _confirmButton.IsEnabled = _mode == PickerMode.Folder || !string.IsNullOrWhiteSpace(_fileName.Text);
    }

    private void ShowError(string message)
    {
        _status.Foreground = Brush("#F28B82");
        _status.Text = message;
    }

    private bool IsAllowedFile(string path)
        => _extensions.Length == 0 || _extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string DescribeFile(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length < 1024 ? $"{length} Б" : length < 1024 * 1024
                ? $"{length / 1024d:0.#} КБ"
                : $"{length / (1024d * 1024d):0.#} МБ";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Файл";
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension.Trim();
        if (value.Length == 0) return "";
        return value.StartsWith('.') ? value : "." + value;
    }

    private static SolidColorBrush Brush(string color)
        => new((Color)ColorConverter.ConvertFromString(color));
}
