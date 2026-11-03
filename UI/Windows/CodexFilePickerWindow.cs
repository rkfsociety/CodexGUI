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

    private const int MaximumVisibleEntries = 1000;
    private const int MaximumScannedFiles = 5000;

    private sealed record PickerEntry(string Name, string Path, bool IsDirectory, string Details);

    private readonly PickerMode _mode;
    private readonly bool _allowOverwrite;
    private readonly string[] _extensions;
    private readonly string _defaultExtension;
    private readonly TextBox _location = new();
    private readonly ListBox _entries = new();
    private readonly TextBox _fileName = new();
    private readonly TextBox _filter = new() { MaxLength = 256, ToolTip = "Поиск по именам в показанном списке файлов и папок" };
    private readonly Button _clearFilter = new() { Content = "Очистить", IsEnabled = false };
    private readonly TextBlock _filterSummary = new() { FontSize = 11, Foreground = Brush("#B0B5BE"), VerticalAlignment = VerticalAlignment.Center };
    private List<PickerEntry> _loadedEntries = [];
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
        string initialFileName, string defaultExtension, bool allowOverwrite = true)
        => ShowDialog(owner, new CodexFilePickerWindow(title, initialDirectory, [], defaultExtension, initialFileName, "save", allowOverwrite));

    public CodexFilePickerWindow(string title, string initialDirectory, string[] extensions,
        string defaultExtension, string fileName, string mode, bool allowOverwrite = true)
    {
        _allowOverwrite = allowOverwrite;
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
        _filter.TextChanged += (_, _) => RefreshFilteredEntries();
        // Keep Enter in the search field from accepting the dialog's default button.
        // Move to the results instead, so keyboard users can inspect the match first.
        _filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter && e.Key != Key.Down) return;
            if (_entries.Items.Count > 0)
            {
                if (_entries.SelectedIndex < 0) _entries.SelectedIndex = 0;
                _entries.Focus();
                if (_entries.ItemContainerGenerator.ContainerFromIndex(_entries.SelectedIndex) is ListBoxItem item)
                    item.Focus();
            }
            e.Handled = true;
        };
        _clearFilter.Click += (_, _) => { _filter.Clear(); _filter.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.L)
            {
                _location.Focus();
                _location.SelectAll();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                _filter.Focus();
                _filter.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _filter.IsKeyboardFocusWithin && _filter.Text.Length > 0)
            {
                _filter.Clear();
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0
                     && (e.Key == Key.Up || e.SystemKey == Key.Up))
            {
                NavigateToParent();
                e.Handled = true;
            }
        };
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
        AutomationProperties.SetName(_location, "Путь к папке");
        locationBorder.ToolTip = "Ctrl+L — перейти к вводу пути; Alt+↑ — на уровень выше";
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

        var filterRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.Children.Add(new TextBlock
        {
            Text = "Фильтр списка",
            Foreground = Brush("#B0B5BE"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        var filterBorder = CreateInputBorder(_filter);
        AutomationProperties.SetName(_filter, "Фильтр показанных файлов и папок");
        Grid.SetColumn(filterBorder, 1);
        filterRow.Children.Add(filterBorder);
        _clearFilter.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(_clearFilter, "Очистить фильтр файлов и папок");
        Grid.SetColumn(_clearFilter, 2);
        filterRow.Children.Add(_clearFilter);
        _filterSummary.Margin = new Thickness(10, 0, 0, 0);
        AutomationProperties.SetName(_filterSummary, "Количество найденных файлов и папок");
        AutomationProperties.SetLiveSetting(_filterSummary, AutomationLiveSetting.Polite);
        Grid.SetColumn(_filterSummary, 3);
        filterRow.Children.Add(_filterSummary);
        Grid.SetRow(filterRow, 2);
        root.Children.Add(filterRow);

        _entries.Background = Brush("#151619");
        AutomationProperties.SetName(_entries, "Содержимое текущей папки");
        _entries.Foreground = Brush("#F1F3F5");
        _entries.BorderBrush = Brush("#34373E");
        _entries.BorderThickness = new Thickness(1);
        _entries.Padding = new Thickness(6);
        _entries.SelectionChanged += (_, _) => SelectionChanged();
        _entries.MouseDoubleClick += (_, e) =>
        {
            // A double-click on empty list space must not activate a previously selected entry.
            if (e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(_entries, source) is not ListBoxItem item
                || !ReferenceEquals(item, _entries.SelectedItem))
                return;
            OpenSelectedEntry();
        };
        _entries.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            OpenSelectedEntry();
            e.Handled = true;
        };
        Grid.SetRow(_entries, 3);
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
        AutomationProperties.SetName(_fileName, "Имя файла");
        _fileName.TextChanged += (_, _) =>
        {
            // A typed file name takes precedence over a previously highlighted folder.
            // Otherwise ConfirmSelection navigates into that folder instead of opening the file.
            if (_mode == PickerMode.OpenFile
                && _entries.SelectedItem is ListBoxItem { Tag: PickerEntry { IsDirectory: true } })
                _entries.SelectedItem = null;
            UpdateConfirmButton();
        };
        _fileName.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ConfirmSelection();
            e.Handled = true;
        };
        fileNamePanel.Visibility = _mode == PickerMode.Folder ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(fileNamePanel, 4);
        root.Children.Add(fileNamePanel);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = Brush("#8D95A2");
        AutomationProperties.SetName(_status, "Состояние списка файлов и папок");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
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
        Grid.SetRow(footer, 5);
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
        _loadedEntries.Clear();
        _location.Text = _currentDirectory;
        _upButton.IsEnabled = Directory.GetParent(_currentDirectory) is not null;
        try
        {
            // A huge folder must not allocate thousands of WPF controls or block the picker.
            // In file modes, reserve some visible slots for files even when the
            // folder contains more subdirectories than the list can display.
            var directoryLimit = _mode == PickerMode.Folder
                ? MaximumVisibleEntries : MaximumVisibleEntries * 7 / 10;
            var directoryPaths = CollectPaths(
                () => Directory.EnumerateDirectories(_currentDirectory),
                directoryLimit + 1, out var directoryScanInterrupted);
            var truncated = directoryScanInterrupted || directoryPaths.Count > directoryLimit;
            var entries = directoryPaths.Take(directoryLimit)
                .Select(path => new PickerEntry(Path.GetFileName(path), path, true, "Папка"))
                .ToList();
            if (_mode != PickerMode.Folder)
            {
                var remaining = MaximumVisibleEntries - entries.Count;
                // Bound the scan even if most files do not match the extension filter.
                // With a single extension filter, let the filesystem select matching
                // names before applying the scan limit; unrelated files should not
                // crowd out the requested type in a large directory.
                List<string> filePaths;
                if (_extensions.Length > 1)
                {
                    // Give every requested type a share of the scan budget. Otherwise
                    // thousands of files of the first type can hide all other types.
                    var perExtension = Math.Max(1, MaximumScannedFiles / _extensions.Length);
                    var groups = new List<List<string>>();
                    foreach (var extension in _extensions.Take(MaximumScannedFiles))
                    {
                        var paths = CollectPaths(
                            () => Directory.EnumerateFiles(_currentDirectory, "*" + extension),
                            perExtension + 1, out var extensionScanInterrupted);
                        if (extensionScanInterrupted || paths.Count > perExtension) truncated = true;
                        groups.Add(paths.Take(perExtension).ToList());
                    }
                    if (_extensions.Length > MaximumScannedFiles) truncated = true;

                    // Interleave file types before applying the visible-entry limit.
                    // Concatenating groups first would let a common extension consume
                    // every visible slot, hiding other requested types entirely.
                    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    filePaths = new List<string>();
                    for (var index = 0; index < perExtension && filePaths.Count < MaximumScannedFiles; index++)
                        foreach (var group in groups)
                            if (index < group.Count && seenPaths.Add(group[index]))
                                filePaths.Add(group[index]);
                }
                else
                {
                    filePaths = CollectPaths(
                        () => _extensions.Length == 1
                            ? Directory.EnumerateFiles(_currentDirectory, "*" + _extensions[0])
                            : Directory.EnumerateFiles(_currentDirectory),
                        MaximumScannedFiles + 1, out var fileScanInterrupted);
                    if (fileScanInterrupted || filePaths.Count > MaximumScannedFiles) truncated = true;
                }
                var matchingFiles = filePaths.Take(MaximumScannedFiles)
                    .Where(IsAllowedFile).Take(remaining + 1).ToList();
                if (matchingFiles.Count > remaining) truncated = true;
                entries.AddRange(matchingFiles.Take(remaining)
                    .Select(path => new PickerEntry(Path.GetFileName(path), path, false, DescribeFile(path))));
            }

            _loadedEntries = entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            _status.Foreground = Brush("#8D95A2");
            _status.Text = truncated
                ? $"Показано: {entries.Count}. Список неполный — укажите путь вручную."
                : entries.Count == 1 ? "1 элемент" : $"Элементов: {entries.Count}";
            _status.ToolTip = truncated ? _status.Text + "\n" + _currentDirectory : _currentDirectory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Foreground = Brush("#F28B82");
            _status.Text = "Не удалось прочитать папку: " + error.Message;
            _status.ToolTip = _currentDirectory;
        }
        RefreshFilteredEntries();
    }

    private void RefreshFilteredEntries()
    {
        var selectedPath = _entries.SelectedItem is ListBoxItem { Tag: PickerEntry selected }
            ? selected.Path : null;
        _entries.Items.Clear();
        var query = _filter.Text.Trim();
        var matches = _loadedEntries.Where(entry => query.Length == 0
            || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var entry in matches)
        {
            var item = CreateEntryItem(entry);
            _entries.Items.Add(item);
            if (selectedPath is not null && string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                _entries.SelectedItem = item;
        }
        _clearFilter.IsEnabled = query.Length > 0;
        _filterSummary.Text = query.Length == 0 ? $"{_loadedEntries.Count} в списке"
            : matches.Count == 0 ? "Нет совпадений" : $"Найдено: {matches.Count}";
        UpdateConfirmButton();
    }

    // A folder can change or become inaccessible during enumeration. Keep the
    // entries already found and mark the list as incomplete rather than clearing it.
    private static List<string> CollectPaths(Func<IEnumerable<string>> enumerate, int limit, out bool interrupted)
    {
        var paths = new List<string>(Math.Min(limit, MaximumVisibleEntries + 1));
        interrupted = false;
        try
        {
            foreach (var path in enumerate())
            {
                paths.Add(path);
                if (paths.Count >= limit) break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            interrupted = true;
        }
        return paths;
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
        var item = new ListBoxItem
        {
            Tag = entry,
            Content = row,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = entry.Path
        };
        AutomationProperties.SetName(item, entry.IsDirectory
            ? "Папка " + entry.Name
            : "Файл " + entry.Name + ", " + entry.Details);
        return item;
    }

    private void NavigateToLocation()
    {
        // Resolve relative paths against the folder shown in the picker, not the app's working directory.
        string path;
        try { path = Path.GetFullPath(_location.Text.Trim(), _currentDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError("Укажите корректный путь к папке.");
            return;
        }
        if (!Directory.Exists(path))
        {
            // Pasting an existing file path into the location bar should open its
            // parent folder and prefill the name, without selecting it prematurely.
            if (_mode != PickerMode.Folder && File.Exists(path)
                && Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
            {
                NavigateToDirectory(parent);
                _fileName.Text = Path.GetFileName(path);
                return;
            }
            ShowError("Папка не найдена.");
            return;
        }
        NavigateToDirectory(path);
    }

    private void NavigateToDirectory(string directory)
    {
        _currentDirectory = directory;
        if (_filter.Text.Length > 0) _filter.Clear();
        RefreshEntries();
        // A file name selected in the previous folder must not silently carry over
        // when opening a different folder. Save dialogs keep the typed name.
        if (_mode == PickerMode.OpenFile)
            _fileName.Clear();
    }

    private void NavigateToParent()
    {
        if (Directory.GetParent(_currentDirectory) is { } parent)
        {
            NavigateToDirectory(parent.FullName);
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
            NavigateToDirectory(entry.Path);
        }
        else if (_mode == PickerMode.OpenFile)
        {
            // Route double-click through the same existence/type checks as the Open button.
            // The file may have been removed since the directory was listed.
            _fileName.Text = entry.Name;
            ConfirmSelection();
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
            var selectedDirectory = _entries.SelectedItem is ListBoxItem { Tag: PickerEntry { IsDirectory: true } entry }
                ? entry.Path
                : _currentDirectory;
            if (!Directory.Exists(selectedDirectory))
            {
                ShowError("Выбранная папка больше не существует или недоступна.");
                return;
            }
            SelectedPath = selectedDirectory;
            DialogResult = true;
            return;
        }

        if (_mode == PickerMode.OpenFile && _entries.SelectedItem is ListBoxItem { Tag: PickerEntry { IsDirectory: true } selectedFolder })
        {
            NavigateToDirectory(selectedFolder.Path);
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
            // Do not turn an existing folder into a sibling file by appending the default extension.
            if (_mode == PickerMode.SaveFile && Directory.Exists(path))
            {
                ShowError("По указанному пути находится папка.");
                return;
            }
            if (_mode == PickerMode.SaveFile && Path.GetExtension(path).Length == 0 && _defaultExtension.Length > 0)
            {
                // Avoid producing names such as "report..txt" from "report.".
                if (Path.GetFileName(path).EndsWith(".", StringComparison.Ordinal))
                    path = path.TrimEnd('.');
                path += _defaultExtension;
            }
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
            if (File.Exists(path))
            {
                if (!_allowOverwrite)
                {
                    ShowError("Файл уже существует. Укажите другое имя.");
                    return;
                }
                if (CodexMessageDialog.Show(this,
                        $"Файл «{Path.GetFileName(path)}» уже существует. Перезаписать его?",
                        "Подтвердите перезапись", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
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
