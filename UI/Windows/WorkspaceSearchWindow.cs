using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;

namespace CodexGUI;

internal sealed class WorkspaceSearchWindow : Window
{
    private CancellationTokenSource? _search;

    internal WorkspaceSearchWindow(string directory, Func<string, int, Task> open)
    {
        Title = "Поиск по проекту"; Width = 860; Height = 610;
        MinWidth = 480; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel();
        var input = new TextBox { MinHeight = 32, MaxLength = WorkspaceFileSearch.MaximumQueryLength, ToolTip = "Текст или имя файла" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 8) };
        var search = new Button { Content = "Найти", IsDefault = true, IsEnabled = false };
        var clear = new Button { Content = "Очистить", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Очистить запрос и результаты" };
        var cancel = new Button { Content = "Остановить", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(search);
        actions.Children.Add(cancel);
        actions.Children.Add(clear);
        var status = new TextBlock { Text = "Поиск текста и имён файлов. До 200 результатов; регистр не учитывается.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(input); header.Children.Add(actions); header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var list = new ListBox { DisplayMemberPath = nameof(WorkspaceSearchHit.Display) };
        panel.Children.Add(list); Content = panel;
        AutomationProperties.SetName(input, "Поисковый запрос");
        AutomationProperties.SetName(list, "Результаты поиска по проекту");
        input.TextChanged += (_, _) =>
        {
            search.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            clear.IsEnabled = input.Text.Length > 0;
        };
        clear.Click += (_, _) =>
        {
            _search?.Cancel();
            list.ItemsSource = null;
            input.Clear();
            input.Focus();
            status.Text = "Поиск текста и имён файлов. До 200 результатов; регистр не учитывается.";
        };
        search.Click += async (_, _) =>
        {
            _search?.Cancel();
            cancel.IsEnabled = false;
            // Do not leave results from a previous query visible while a new one is running.
            list.ItemsSource = null;
            var query = input.Text.Trim();
            if (query.Length == 0)
            {
                status.Text = "Введите текст или имя файла для поиска.";
                return;
            }
            using var cancellation = new CancellationTokenSource();
            _search = cancellation;
            cancel.IsEnabled = true;
            status.Text = "Поиск…";
            try
            {
                var result = await Task.Run(() => WorkspaceFileSearch.Find(directory, query, cancellation.Token));
                if (cancellation.IsCancellationRequested) return;
                list.ItemsSource = result.Hits;
                status.Text = (result.Hits.Count == 0 ? "Совпадений не найдено. Попробуйте другой запрос. " : "")
                    + $"Найдено: {result.Hits.Count}. Просмотрено файлов: {result.Scanned}. Пропущено: {result.Skipped}."
                    + (result.Limited ? " Достигнут предел поиска; уточните запрос." : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                // Any failure escaping this asynchronous UI handler could terminate the app.
                // A cancelled search must not overwrite the status of a newer request.
                if (!cancellation.IsCancellationRequested)
                    status.Text = "Не удалось выполнить поиск: " + error.Message;
            }
            finally
            {
                if (ReferenceEquals(_search, cancellation))
                {
                    _search = null;
                    cancel.IsEnabled = false;
                }
            }
        };
        void StopActiveSearch()
        {
            if (_search is null || _search.IsCancellationRequested) return;
            _search.Cancel();
            cancel.IsEnabled = false;
            status.Text = "Поиск остановлен.";
        }
        cancel.Click += (_, _) => StopActiveSearch();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                input.Focus();
                input.SelectAll();
                args.Handled = true;
                return;
            }
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            if (_search is { IsCancellationRequested: false })
                StopActiveSearch();
            else if (input.Text.Length > 0)
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else
                Close();
        };
        async Task OpenSelection()
        {
            if (list.SelectedItem is not WorkspaceSearchHit hit) return;
            try { await open(hit.Path, hit.Line); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                // Exceptions from the caller must not escape the asynchronous UI event handler.
                status.Text = "Не удалось открыть файл: " + error.Message;
            }
        }
        list.MouseDoubleClick += async (_, args) =>
        {
            // A double-click in blank list space must not reopen the previously selected hit.
            if (args.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(list, source) is not ListBoxItem item
                || !ReferenceEquals(item.Content, list.SelectedItem))
                return;
            await OpenSelection();
        };
        list.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true; await OpenSelection();
        };
        Closed += (_, _) => _search?.Cancel();
        Loaded += (_, _) => input.Focus();
    }
}

internal sealed record WorkspaceSearchHit(string Path, int Line, string Display);
internal sealed record WorkspaceSearchResult(IReadOnlyList<WorkspaceSearchHit> Hits, int Scanned, int Skipped, bool Limited);

internal static class WorkspaceFileSearch
{
    // A file can grow after its size is checked; keep the actual read bounded too.
    private const int MaximumTextBytes = 1024 * 1024;
    internal const int MaximumQueryLength = 512;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs"
    };
    // Keep filename matches, but never decode common binary formats as source text.
    // This avoids irrelevant previews and unnecessary reads of large binary files.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".pdf",
        ".zip", ".7z", ".rar", ".gz", ".exe", ".dll", ".pdb",
        ".mp3", ".mp4", ".mov", ".wav", ".woff", ".woff2", ".ttf", ".otf",
        ".sqlite", ".db3", ".jar", ".class", ".wasm", ".glb",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".psd",
        ".heic", ".avif", ".webm", ".mkv", ".flac", ".ogg", ".iso",
        ".tar", ".bz2", ".xz", ".zst"
    };
    // Unknown extensions may still contain binary data or invalid UTF-8.
    // Do not show accidental text matches from those files; filename matches remain available.
    private static bool ContainsBinaryCharacters(ReadOnlySpan<char> content)
    {
        foreach (var character in content)
        {
            if (character == '\uFFFD' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f')))
                return true;
        }
        return false;
    }

    internal static WorkspaceSearchResult Find(string root, string query, CancellationToken cancellation,
        int maximumDirectories = 10000)
    {
        if (maximumDirectories < 1) throw new ArgumentOutOfRangeException(nameof(maximumDirectories));
        if (query.Length > MaximumQueryLength)
            throw new ArgumentException("Поисковый запрос слишком длинный (максимум 512 символов).", nameof(query));

        // Child links are excluded below; reject a linked workspace root or ancestor too.
        for (string? current = Path.GetFullPath(root); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Поиск по папке, ведущей через символическую ссылку, не поддерживается.");

        var hits = new List<WorkspaceSearchHit>();
        var pending = new Stack<string>(); pending.Push(root);
        var scanned = 0; var skipped = 0; var directories = 0;
        // Count discovered entries as well as visited folders so a single huge directory
        // cannot fill the pending stack before the traversal limit is checked.
        var discoveredDirectories = 1;
        var directoryLimitReached = false;
        char[]? textBuffer = null;
        while (pending.Count > 0 && hits.Count < 200 && scanned < 5000 && directories < maximumDirectories)
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = pending.Pop(); directories++;
            try
            {
                // A broken or inaccessible child directory must not prevent searching
                // readable files in its parent.
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // Excluded and linked folders are not search candidates and must not
                        // consume the traversal budget before eligible folders are discovered.
                        if (ExcludedDirectories.Contains(Path.GetFileName(child))) continue;
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                            if (discoveredDirectories >= maximumDirectories)
                            {
                                directoryLimitReached = true;
                                break;
                            }
                            discoveredDirectories++;
                            pending.Push(child);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            skipped++;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (hits.Count >= 200 || scanned >= 5000) break;
                    // Linked Git worktrees store their private gitdir path in a .git file,
                    // not a directory. Exclude that metadata just like .git directories.
                    if (string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                    scanned++;
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        var relative = Path.GetRelativePath(root, file);
                        if (relative.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            hits.Add(new WorkspaceSearchHit(file, 1, relative));
                            // The result limit may be reached by filename matches alone.
                            if (hits.Count >= 200) break;
                        }
                        if (BinaryExtensions.Contains(Path.GetExtension(file))) { skipped++; continue; }
                        // Share the file with editors and bound reads even if it grows during search.
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
                        if (stream.Length > MaximumTextBytes) { skipped++; continue; }
                        textBuffer ??= new char[MaximumTextBytes + 1];
                        using var reader = new StreamReader(stream);
                        var textLength = 0;
                        while (textLength < textBuffer.Length)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var read = reader.Read(textBuffer, textLength,
                                Math.Min(8192, textBuffer.Length - textLength));
                            if (read == 0) break;
                            textLength += read;
                            if (stream.Position > MaximumTextBytes) break;
                        }
                        if (textLength > MaximumTextBytes || stream.Position > MaximumTextBytes)
                        {
                            skipped++;
                            continue;
                        }
                        // Search the reusable character buffer first: most files do not match,
                        // so avoid allocating a full string or scanning for binary characters.
                        var content = textBuffer.AsSpan(0, textLength);
                        if (content.IndexOf(query.AsSpan(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (ContainsBinaryCharacters(content)) { skipped++; continue; }
                        // Scan lines directly in the reusable buffer. A single match must not
                        // allocate an entire megabyte of strings just to show a short preview.
                        // Count CR, LF and CRLF as one line break each.
                        var lineStart = 0;
                        var lineNumber = 1;
                        while (lineStart <= content.Length && hits.Count < 200)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var separatorOffset = content[lineStart..].IndexOfAny('\r', '\n');
                            var lineEnd = separatorOffset < 0 ? content.Length : lineStart + separatorOffset;
                            var line = content[lineStart..lineEnd].Trim();
                            var matchIndex = line.IndexOf(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
                            if (matchIndex >= 0)
                            {
                                string preview;
                                if (line.Length <= 180)
                                    preview = new string(line);
                                else
                                {
                                    var previewStart = Math.Max(0, matchIndex - 60);
                                    if (previewStart + 180 > line.Length) previewStart = line.Length - 180;
                                    preview = (previewStart > 0 ? "…" : "")
                                        + new string(line.Slice(previewStart, 180))
                                        + (previewStart + 180 < line.Length ? "…" : "");
                                }
                                hits.Add(new WorkspaceSearchHit(file, lineNumber,
                                    $"{relative}:{lineNumber}   {preview}"));
                            }
                            if (separatorOffset < 0) break;
                            lineStart = lineEnd + 1;
                            if (content[lineEnd] == '\r' && lineStart < content.Length && content[lineStart] == '\n')
                                lineStart++;
                            lineNumber++;
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new WorkspaceSearchResult(hits, scanned, skipped,
            hits.Count >= 200 || scanned >= 5000 || directories >= maximumDirectories || directoryLimitReached);
    }
}
