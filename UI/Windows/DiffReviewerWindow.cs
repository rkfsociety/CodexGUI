using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class DiffCommentThread
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? LineAnchor { get; init; }
    public string SelectedDiff { get; init; } = "";
    public string Comment { get; init; } = "";
    public List<string> Replies { get; } = [];
}

internal sealed class DiffReviewerWindow : Window
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "class", "struct", "record", "interface", "public", "private", "protected", "internal", "static", "void", "async", "await",
        "return", "if", "else", "switch", "case", "for", "foreach", "while", "try", "catch", "throw", "new", "var", "const", "let",
        "function", "def", "import", "from", "using", "namespace", "true", "false", "null", "None", "bool", "string", "int", "long",
        "Task", "extends", "implements", "package", "export", "default", "yield", "in", "is", "as"
    };

    private readonly string _diff;
    private readonly string _relativePath;
    private readonly RepositoryDiffScope _diffScope;
    private readonly IReadOnlyList<RepositoryDiffHunk> _hunks;
    private readonly bool _hunkActionsAvailable;
    private readonly Func<string, GitHunkAction, Task<bool>> _applyGitHunk;
    private readonly Func<IReadOnlyList<DiffCommentThread>, bool> _attachThreads;
    private readonly ListBox _threadList = new()
    {
        Background = Brush("#1D1F23"),
        Foreground = Brush("#E2E8F0"),
        BorderThickness = new Thickness(0)
    };
    private readonly StackPanel _conversation = new();
    private readonly TextBox _draft = new();
    private readonly Button _newThreadButton = new() { Content = "Новая ветка" };
    private readonly Button _replyButton = new() { Content = "Ответить" };
    private readonly Button _attachButton = new() { Content = "Добавить ветки в запрос" };
    private readonly Button _stageHunkButton = new() { Content = "Добавить блок" };
    private readonly Button _unstageHunkButton = new() { Content = "Снять блок" };
    private readonly Button _revertHunkButton = new() { Content = "Отменить блок" };
    private readonly List<DiffCommentThread> _threads = [];
    private IReadOnlyList<Paragraph> _diffLines = [];
    private RichTextBox? _diffBox;
    private DiffCommentThread? _selectedThread;

    public DiffReviewerWindow(Window owner, string repositoryRoot, string relativePath, string diff,
        RepositoryDiffScope diffScope, bool hunkActionsAvailable,
        Func<string, GitHunkAction, Task<bool>> applyGitHunk,
        Func<IReadOnlyList<DiffCommentThread>, bool> attachThreads)
    {
        Owner = owner;
        _diff = diff;
        _relativePath = relativePath;
        _diffScope = diffScope;
        _hunks = GitRepositoryInspector.ParseDiffHunks(diff);
        _hunkActionsAvailable = hunkActionsAvailable && _hunks.Count > 0
            && !diff.Contains("Diff сокращён: вывод превышает", StringComparison.Ordinal)
            && !HasUnsupportedHunkMetadata(diff);
        _applyGitHunk = applyGitHunk;
        _attachThreads = attachThreads;
        Title = $"Изменения · {System.IO.Path.GetFileName(relativePath)}";
        Width = 1180;
        Height = 720;
        MinWidth = 860;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout(repositoryRoot, relativePath);
        _threadList.SelectionChanged += (_, _) =>
        {
            _selectedThread = (_threadList.SelectedItem as ListBoxItem)?.Tag as DiffCommentThread;
            RenderConversation();
            UpdateActionState();
        };
        _draft.TextChanged += (_, _) => UpdateActionState();
        UpdateActionState();
    }

    private UIElement BuildLayout(string repositoryRoot, string relativePath)
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var gitActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (_diffScope == RepositoryDiffScope.Unstaged)
        {
            StyleHunkButton(_stageHunkButton, "#223B38", "Выберите строки одного блока и добавьте его в индекс");
            _stageHunkButton.Click += async (_, _) => await ApplySelectedHunkAsync(GitHunkAction.Stage);
            gitActions.Children.Add(_stageHunkButton);
            _revertHunkButton.Margin = new Thickness(6, 0, 0, 0);
            StyleHunkButton(_revertHunkButton, "#4A2A31", "Выберите строки одного блока и отмените незастейдженные изменения");
            _revertHunkButton.Click += async (_, _) => await ApplySelectedHunkAsync(GitHunkAction.Revert);
            gitActions.Children.Add(_revertHunkButton);
        }
        else
        {
            StyleHunkButton(_unstageHunkButton, "#263344", "Выберите строки одного блока, чтобы снять его с индекса");
            _unstageHunkButton.Click += async (_, _) => await ApplySelectedHunkAsync(GitHunkAction.Unstage);
            gitActions.Children.Add(_unstageHunkButton);
        }
        foreach (var action in gitActions.Children.OfType<Button>())
            action.Visibility = _hunkActionsAvailable ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(gitActions, Dock.Right);
        header.Children.Add(gitActions);
        header.Children.Add(new TextBlock
        {
            Text = $"{System.IO.Path.Combine(repositoryRoot, relativePath)} · {(_diffScope == RepositoryDiffScope.Staged ? "В ИНДЕКСЕ" : "НЕ В ИНДЕКСЕ")}",
            Foreground = Brush("#A4B1C2"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = System.IO.Path.Combine(repositoryRoot, relativePath)
        });
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _diffBox = new RichTextBox
        {
            Document = BuildHighlightedDiff(_diff, out _diffLines),
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("#1D1F23"),
            Foreground = Brush("#D2D9E3"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1),
            ToolTip = _hunkActionsAvailable
                ? "Выделите строки одного diff-блока для stage, unstage или отмены"
                : "Блочные действия недоступны для этого типа diff; используйте Git-действия файла"
        };
        _diffBox.SelectionChanged += (_, _) => UpdateActionState();
        var diffBorder = Card(_diffBox);
        diffBorder.Margin = new Thickness(0, 0, 9, 0);
        body.Children.Add(diffBorder);
        var commentsPanel = BuildCommentPanel();
        Grid.SetColumn(commentsPanel, 1);
        body.Children.Add(commentsPanel);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _attachButton.Padding = new Thickness(12, 7, 12, 7);
        _attachButton.Background = Brush("#223B38");
        _attachButton.Foreground = Brush("#92E5C7");
        _attachButton.Click += (_, _) =>
        {
            if (_attachThreads(_threads)) DialogResult = true;
        };
        footer.Children.Add(_attachButton);
        var close = MakeButton("Закрыть", "#263344");
        close.Margin = new Thickness(8, 0, 0, 0);
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildCommentPanel()
    {
        var grid = new Grid { Margin = new Thickness(9, 0, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "ВЕТКИ ЗАМЕЧАНИЙ",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8795A6"),
            Margin = new Thickness(4, 2, 0, 7)
        });
        var listCard = Card(_threadList);
        Grid.SetRow(listCard, 1);
        grid.Children.Add(listCard);
        var conversationScroll = new ScrollViewer
        {
            Content = _conversation,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(conversationScroll, 2);
        grid.Children.Add(conversationScroll);
        _draft.MinHeight = 60;
        _draft.MaxHeight = 80;
        _draft.AcceptsReturn = true;
        _draft.TextWrapping = TextWrapping.Wrap;
        _draft.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _draft.Padding = new Thickness(9, 7, 9, 7);
        _draft.Margin = new Thickness(0, 8, 0, 0);
        _draft.ToolTip = "Выделите строки и напишите замечание, либо выберите ветку и ответьте";
        StyleInput(_draft);
        Grid.SetRow(_draft, 3);
        grid.Children.Add(_draft);
        var actions = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        _newThreadButton.Padding = new Thickness(9, 5, 9, 5);
        _newThreadButton.Background = Brush("#223B38");
        _newThreadButton.Foreground = Brush("#92E5C7");
        _newThreadButton.Click += (_, _) => AddThread();
        actions.Children.Add(_newThreadButton);
        _replyButton.Padding = new Thickness(9, 5, 9, 5);
        _replyButton.Margin = new Thickness(6, 0, 0, 0);
        _replyButton.Background = Brush("#263344");
        _replyButton.Foreground = Brush("#D3DCE6");
        _replyButton.Click += (_, _) => AddReply();
        actions.Children.Add(_replyButton);
        Grid.SetRow(actions, 4);
        grid.Children.Add(actions);
        return Card(grid);
    }

    private void AddThread()
    {
        if (_diffBox is null || _diffBox.Selection.IsEmpty || string.IsNullOrWhiteSpace(_draft.Text)) return;
        var selectedDiff = new TextRange(_diffBox.Selection.Start, _diffBox.Selection.End).Text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(selectedDiff)) return;
        var selectedLines = GetSelectedDiffLineIndexes(_diffBox, _diffLines);
        _threads.Add(new DiffCommentThread
        {
            LineAnchor = GetDiffLineAnchor(selectedLines),
            SelectedDiff = selectedDiff,
            Comment = _draft.Text.Trim()
        });
        _draft.Clear();
        RenderThreadList();
        _threadList.SelectedIndex = _threadList.Items.Count - 1;
    }

    private void AddReply()
    {
        if (_selectedThread is null || string.IsNullOrWhiteSpace(_draft.Text)) return;
        _selectedThread.Replies.Add(_draft.Text.Trim());
        _draft.Clear();
        RenderConversation();
        RenderThreadList();
    }

    private void RenderThreadList()
    {
        var selectedId = _selectedThread?.Id;
        _threadList.Items.Clear();
        foreach (var thread in _threads)
        {
            var card = new StackPanel();
            card.Children.Add(new TextBlock
            {
                Text = thread.LineAnchor ?? "Замечание к diff",
                Foreground = Brush("#92E5C7"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            });
            card.Children.Add(new TextBlock
            {
                Text = thread.Comment,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 38,
                Margin = new Thickness(0, 4, 0, 0)
            });
            if (thread.Replies.Count > 0)
                card.Children.Add(new TextBlock
                {
                    Text = $"Ответов: {thread.Replies.Count}",
                    FontSize = 9,
                    Foreground = Brush("#9AA8B8"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            var item = new ListBoxItem { Tag = thread, Padding = new Thickness(8, 7, 8, 7), Content = card };
            _threadList.Items.Add(item);
            if (thread.Id == selectedId) _threadList.SelectedItem = item;
        }
        if (_threadList.SelectedItem is null && _threadList.Items.Count > 0)
            _threadList.SelectedIndex = _threadList.Items.Count - 1;
    }

    private void RenderConversation()
    {
        _conversation.Children.Clear();
        if (_selectedThread is null) return;
        AddBubble("Замечание", _selectedThread.Comment, accent: true);
        foreach (var reply in _selectedThread.Replies) AddBubble("Ответ", reply, accent: false);

        void AddBubble(string label, string content, bool accent)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(accent ? "#92E5C7" : "#9BAAC0")
            });
            stack.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            _conversation.Children.Add(new Border
            {
                Background = Brush(accent ? "#1B2A28" : "#202936"),
                BorderBrush = Brush("#34373E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 7, 0, 0),
                Child = stack
            });
        }
    }

    private void UpdateActionState()
    {
        _newThreadButton.IsEnabled = _diffBox is not null && !_diffBox.Selection.IsEmpty && !string.IsNullOrWhiteSpace(_draft.Text);
        _replyButton.IsEnabled = _selectedThread is not null && !string.IsNullOrWhiteSpace(_draft.Text);
        _attachButton.IsEnabled = _threads.Count > 0;
        var hasSelectedHunk = _hunkActionsAvailable && SelectedHunk() is not null;
        _stageHunkButton.IsEnabled = hasSelectedHunk && _diffScope == RepositoryDiffScope.Unstaged;
        _revertHunkButton.IsEnabled = hasSelectedHunk && _diffScope == RepositoryDiffScope.Unstaged;
        _unstageHunkButton.IsEnabled = hasSelectedHunk && _diffScope == RepositoryDiffScope.Staged;
    }

    private RepositoryDiffHunk? SelectedHunk()
    {
        if (_diffBox is null || _diffBox.Selection.IsEmpty) return null;
        var selectedLines = GetSelectedDiffLineIndexes(_diffBox, _diffLines);
        var matchingHunks = _hunks.Where(hunk =>
            selectedLines.Any(line => line >= hunk.StartLine && line <= hunk.EndLine)).Take(2).ToArray();
        return matchingHunks.Length == 1 ? matchingHunks[0] : null;
    }

    private async Task ApplySelectedHunkAsync(GitHunkAction action)
    {
        if (SelectedHunk() is not { } hunk) return;
        if (action == GitHunkAction.Revert)
        {
            var answer = CodexMessageDialog.Show(this,
                $"Отменить незастейдженные изменения в блоке файла «{_relativePath}»? Изменения этого блока будут удалены.",
                "Отменить изменения блока", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        _stageHunkButton.IsEnabled = false;
        _unstageHunkButton.IsEnabled = false;
        _revertHunkButton.IsEnabled = false;
        if (!await _applyGitHunk(hunk.Patch, action)) { UpdateActionState(); return; }
        Close();
    }

    private static void StyleHunkButton(Button button, string background, string tooltip)
    {
        button.Padding = new Thickness(9, 5, 9, 5);
        button.Background = Brush(background);
        button.Foreground = Brush("#E2E8F0");
        button.BorderBrush = Brush("#34373E");
        button.BorderThickness = new Thickness(1);
        button.ToolTip = tooltip;
    }

    private static bool HasUnsupportedHunkMetadata(string diff) =>
        diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Any(line =>
            line.StartsWith("rename from ", StringComparison.Ordinal)
            || line.StartsWith("rename to ", StringComparison.Ordinal)
            || line.StartsWith("new file mode ", StringComparison.Ordinal)
            || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
            || line.StartsWith("old mode ", StringComparison.Ordinal)
            || line.StartsWith("new mode ", StringComparison.Ordinal));

    private FlowDocument BuildHighlightedDiff(string diff, out IReadOnlyList<Paragraph> lines)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(12),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = Brush("#1D1F23")
        };
        var paragraphs = new List<Paragraph>();
        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var added = line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal);
            var deleted = line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal);
            var hunk = line.StartsWith("@@", StringComparison.Ordinal);
            var metadata = !added && !deleted && (line.StartsWith("diff ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("\\", StringComparison.Ordinal));
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
            if (added) paragraph.Background = Brush("#1C3029");
            else if (deleted) paragraph.Background = Brush("#39242A");
            else if (hunk) paragraph.Background = Brush("#202B38");

            if (added || deleted)
            {
                paragraph.Inlines.Add(new Run(line[..1]) { Foreground = Brush(added ? "#73D49A" : "#F08A91") });
                AddSyntaxRuns(paragraph, line[1..], Brush(added ? "#C7E9D3" : "#F2C7C9"));
            }
            else if (hunk)
                paragraph.Inlines.Add(new Run(line) { Foreground = Brush("#80C8E8"), FontWeight = FontWeights.SemiBold });
            else if (metadata)
                paragraph.Inlines.Add(new Run(line) { Foreground = Brush("#8D9BAE") });
            else if (line.StartsWith(' '))
            {
                paragraph.Inlines.Add(new Run(" ") { Foreground = Brush("#64748B") });
                AddSyntaxRuns(paragraph, line[1..], Brush("#D2D9E3"));
            }
            else AddSyntaxRuns(paragraph, line, Brush("#D2D9E3"));
            if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run(" "));
            document.Blocks.Add(paragraph);
            paragraphs.Add(paragraph);
        }
        lines = paragraphs;
        return document;
    }

    private static void AddSyntaxRuns(Paragraph paragraph, string text, Brush baseBrush)
    {
        var index = 0;
        while (index < text.Length)
        {
            if ((text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/') || text[index] == '#')
            {
                paragraph.Inlines.Add(new Run(text[index..]) { Foreground = Brush("#6A9955") });
                return;
            }
            if (text[index] is '\'' or '"' or '`')
            {
                var quote = text[index];
                var end = index + 1;
                while (end < text.Length)
                {
                    if (text[end] == '\\') { end = Math.Min(end + 2, text.Length); continue; }
                    if (text[end++] == quote) break;
                }
                paragraph.Inlines.Add(new Run(text[index..end]) { Foreground = Brush("#CE9178") });
                index = end;
                continue;
            }
            if (char.IsDigit(text[index]))
            {
                var end = index + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or '_')) end++;
                paragraph.Inlines.Add(new Run(text[index..end]) { Foreground = Brush("#B5CEA8") });
                index = end;
                continue;
            }
            if (char.IsLetter(text[index]) || text[index] == '_')
            {
                var end = index + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                var token = text[index..end];
                paragraph.Inlines.Add(new Run(token) { Foreground = Keywords.Contains(token) ? Brush("#569CD6") : baseBrush });
                index = end;
                continue;
            }
            var segmentStart = index++;
            while (index < text.Length
                && text[index] is not ('\'' or '"' or '`')
                && !char.IsDigit(text[index])
                && !(char.IsLetter(text[index]) || text[index] == '_')
                && !(text[index] == '#' || text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/'))
                index++;
            paragraph.Inlines.Add(new Run(text[segmentStart..index]) { Foreground = baseBrush });
        }
    }

    private static HashSet<int> GetSelectedDiffLineIndexes(RichTextBox diffBox, IReadOnlyList<Paragraph> lines)
    {
        var selected = new HashSet<int>();
        if (diffBox.Selection.IsEmpty) return selected;
        var selectionStart = diffBox.Selection.Start;
        var selectionEnd = diffBox.Selection.End;
        for (var index = 0; index < lines.Count; index++)
            if (lines[index].ContentStart.CompareTo(selectionEnd) < 0 && lines[index].ContentEnd.CompareTo(selectionStart) > 0)
                selected.Add(index);
        return selected;
    }

    private string? GetDiffLineAnchor(ISet<int> selectedLines)
    {
        if (selectedLines.Count == 0) return null;
        var oldLine = 0;
        var newLine = 0;
        var oldLines = new SortedSet<int>();
        var newLines = new SortedSet<int>();
        var lineStart = 0;
        var lineIndex = 0;
        while (lineStart <= _diff.Length)
        {
            var lineEnd = _diff.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = _diff.Length;
            var line = _diff[lineStart..lineEnd].TrimEnd('\r');
            var selected = selectedLines.Contains(lineIndex);
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var oldMarker = line.IndexOf('-', StringComparison.Ordinal);
                var newMarker = line.IndexOf('+', StringComparison.Ordinal);
                var oldEnd = oldMarker >= 0 ? line.IndexOfAny([',', ' '], oldMarker + 1) : -1;
                var newEnd = newMarker >= 0 ? line.IndexOfAny([',', ' '], newMarker + 1) : -1;
                if (oldMarker >= 0 && oldEnd > oldMarker && int.TryParse(line[(oldMarker + 1)..oldEnd], out var oldStart)) oldLine = oldStart;
                if (newMarker >= 0 && newEnd > newMarker && int.TryParse(line[(newMarker + 1)..newEnd], out var newStart)) newLine = newStart;
            }
            else if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                // File headers are not source lines in a diff hunk.
            }
            else if (line.StartsWith('+'))
            {
                if (selected && newLine > 0) newLines.Add(newLine);
                if (newLine > 0) newLine++;
            }
            else if (line.StartsWith('-'))
            {
                if (selected && oldLine > 0) oldLines.Add(oldLine);
                if (oldLine > 0) oldLine++;
            }
            else if (line.StartsWith(' ') && oldLine > 0 && newLine > 0)
            {
                if (selected) { oldLines.Add(oldLine); newLines.Add(newLine); }
                oldLine++;
                newLine++;
            }
            if (lineEnd == _diff.Length) break;
            lineStart = lineEnd + 1;
            lineIndex++;
        }
        var locations = new List<string>();
        if (oldLines.Count > 0) locations.Add("old " + FormatLineNumbers(oldLines));
        if (newLines.Count > 0) locations.Add("new " + FormatLineNumbers(newLines));
        return locations.Count == 0 ? null : string.Join("; ", locations);
    }

    private static string FormatLineNumbers(IEnumerable<int> lines)
    {
        var ordered = lines.Order().ToArray();
        if (ordered.Length == 0) return "";
        var ranges = new List<string>();
        var start = ordered[0];
        var end = start;
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index] == end + 1) { end = ordered[index]; continue; }
            ranges.Add(start == end ? $"L{start}" : $"L{start}-L{end}");
            start = end = ordered[index];
        }
        ranges.Add(start == end ? $"L{start}" : $"L{start}-L{end}");
        return string.Join(", ", ranges);
    }

    private static Border Card(UIElement child) => new()
    {
        Background = Brush("#141A22"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(4),
        Child = child
    };

    private static void StyleInput(Control input)
    {
        input.Padding = new Thickness(9, 7, 9, 7);
        input.Background = Brush("#1D1F23");
        input.Foreground = Brush("#D2D9E3");
        input.BorderBrush = Brush("#34373E");
        input.BorderThickness = new Thickness(1);
    }

    private static Button MakeButton(string text, string background) => new()
    {
        Content = text,
        Padding = new Thickness(12, 7, 12, 7),
        Background = Brush(background),
        Foreground = Brush("#E2E8F0"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
