using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        var input = new TextBox { MinHeight = 32, ToolTip = "Текст или имя файла" };
        var search = new Button { Content = "Найти", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 8) };
        var status = new TextBlock { Text = "Поиск текста и имён файлов. До 200 результатов; регистр не учитывается.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(input); header.Children.Add(search); header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var list = new ListBox { DisplayMemberPath = nameof(WorkspaceSearchHit.Display) };
        panel.Children.Add(list); Content = panel;
        search.Click += async (_, _) =>
        {
            _search?.Cancel();
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
            status.Text = "Поиск…";
            try
            {
                var result = await Task.Run(() => WorkspaceFileSearch.Find(directory, query, cancellation.Token));
                if (cancellation.IsCancellationRequested) return;
                list.ItemsSource = result.Hits;
                status.Text = $"Найдено: {result.Hits.Count}. Просмотрено файлов: {result.Scanned}. Пропущено: {result.Skipped}."
                    + (result.Limited ? " Достигнут предел поиска; уточните запрос." : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A cancelled search must not overwrite the status of a newer request.
                if (!cancellation.IsCancellationRequested)
                    status.Text = "Не удалось выполнить поиск: " + error.Message;
            }
            finally { if (ReferenceEquals(_search, cancellation)) _search = null; }
        };
        async Task OpenSelection()
        {
            if (list.SelectedItem is not WorkspaceSearchHit hit) return;
            try { await open(hit.Path, hit.Line); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
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
    internal static WorkspaceSearchResult Find(string root, string query, CancellationToken cancellation,
        int maximumDirectories = 10000)
    {
        if (maximumDirectories < 1) throw new ArgumentOutOfRangeException(nameof(maximumDirectories));
        var hits = new List<WorkspaceSearchHit>();
        var pending = new Stack<string>(); pending.Push(root);
        var scanned = 0; var skipped = 0; var directories = 0;
        // Count discovered entries as well as visited folders so a single huge directory
        // cannot fill the pending stack before the traversal limit is checked.
        var discoveredDirectories = 1;
        var directoryLimitReached = false;
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
                        if (new[] { ".git", "bin", "obj", "node_modules", ".vs" }.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
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
                        if (new FileInfo(file).Length > 1024 * 1024) { skipped++; continue; }
                        var content = File.ReadAllText(file);
                        if (content.Contains('\0')) { skipped++; continue; }
                        var lines = content.Split('\n');
                        for (var i = 0; i < lines.Length && hits.Count < 200; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var line = lines[i].Trim();
                            var matchIndex = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                            if (matchIndex < 0) continue;
                            var preview = line;
                            if (line.Length > 180)
                            {
                                // Keep the matching text visible even when it occurs far into a long line.
                                var start = Math.Max(0, matchIndex - 60);
                                if (start + 180 > line.Length) start = line.Length - 180;
                                var end = start + 180;
                                preview = (start > 0 ? "…" : "") + line[start..end]
                                    + (end < line.Length ? "…" : "");
                            }
                            hits.Add(new WorkspaceSearchHit(file, i + 1, $"{relative}:{i + 1}   {preview}"));
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
