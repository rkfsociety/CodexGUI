using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace CodexGUI;

internal static class ChatMarkdownRenderer
{
    private static readonly Regex InlineTokens = new(@"(?<code>`[^`]+`)|(?<bold>\*\*.+?\*\*)|(?<italic>(?<!\*)\*[^*]+\*(?!\*))|\[(?<label>[^\]]+)\]\((?<url>https?://[^)\s]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Heading = new(@"^(#{1,3})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*(?:[-*+]\s+|\d+[.)]\s+)(.+)$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorCell = new(@"^\s*:?-{3,}:?\s*$", RegexOptions.Compiled);

    public static StackPanel Render(string markdown)
    {
        var content = new StackPanel();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var code = new System.Text.StringBuilder();
        var inCode = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    AddCodeBlock(content, code.ToString().TrimEnd('\n'));
                    code.Clear();
                    inCode = false;
                }
                else inCode = true;
                continue;
            }

            if (inCode)
            {
                code.AppendLine(line);
                continue;
            }

            if (line.Length == 0)
            {
                content.Children.Add(new Border { Height = 7 });
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var size = heading.Groups[1].Length switch { 1 => 20d, 2 => 17d, _ => 15d };
                content.Children.Add(CreateTextBlock(heading.Groups[2].Value, size, FontWeights.SemiBold,
                    new Thickness(0, 6, 0, 3)));
                continue;
            }

            if (index + 1 < lines.Length && ContainsTablePipes(line) && IsTableSeparator(lines[index + 1]))
            {
                var rows = new List<IReadOnlyList<string>> { SplitTableRow(line) };
                var rowIndex = index + 2;
                while (rowIndex < lines.Length && ContainsTablePipes(lines[rowIndex]))
                    rows.Add(SplitTableRow(lines[rowIndex++]));
                content.Children.Add(CreateTable(rows));
                index = rowIndex - 1;
                continue;
            }

            var quote = line.TrimStart().StartsWith(">", StringComparison.Ordinal);
            var listItem = ListItem.Match(line);
            var itemText = listItem.Success ? listItem.Groups[1].Value : line;
            var markerEnd = listItem.Success ? line.IndexOf(itemText, StringComparison.Ordinal) : 0;
            var marker = listItem.Success ? line[..markerEnd].Trim() : "";
            var text = quote
                ? line.TrimStart().TrimStart('>').TrimStart()
                : listItem.Success
                    ? (marker.Length > 0 && char.IsDigit(marker[0]) ? marker + "  " : "•  ") + itemText
                    : line;
            var paragraph = CreateTextBlock(text, 15, FontWeights.Normal,
                new Thickness(quote ? 13 : listItem.Success ? 5 : 0, 1, 0, 2));
            if (quote)
            {
                paragraph.Foreground = Brush("#B2C0CA");
                paragraph.FontStyle = FontStyles.Italic;
            }
            AppendInlineFormatting(paragraph, text);
            content.Children.Add(paragraph);
        }

        if (inCode)
            AddCodeBlock(content, code.ToString().TrimEnd('\n'));
        return content;
    }

    private static TextBlock CreateTextBlock(string text, double size, FontWeight weight, Thickness margin) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        LineHeight = 25,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#F1F3F5"),
        Margin = margin
    };

    private static void AppendInlineFormatting(TextBlock block, string text)
    {
        block.Inlines.Clear();
        var offset = 0;
        foreach (Match match in InlineTokens.Matches(text))
        {
            if (match.Index > offset)
                block.Inlines.Add(new Run(text[offset..match.Index]));

            if (match.Groups["code"].Success)
                block.Inlines.Add(new Run(match.Value[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    Foreground = Brush("#A9E3CE"),
                    Background = Brush("#24272D")
                });
            else if (match.Groups["bold"].Success)
                block.Inlines.Add(new Run(match.Value[2..^2]) { FontWeight = FontWeights.SemiBold });
            else if (match.Groups["italic"].Success)
                block.Inlines.Add(new Run(match.Value[1..^1]) { FontStyle = FontStyles.Italic });
            else
            {
                var link = new Hyperlink(new Run(match.Groups["label"].Value))
                {
                    NavigateUri = new Uri(match.Groups["url"].Value, UriKind.Absolute),
                    Foreground = Brush("#92E5C7"),
                    TextDecorations = TextDecorations.Underline
                };
                link.RequestNavigate += (_, args) => OpenLink(block, args);
                block.Inlines.Add(link);
            }
            offset = match.Index + match.Length;
        }
        if (offset < text.Length)
            block.Inlines.Add(new Run(text[offset..]));
    }

    private static bool ContainsTablePipes(string line) => line.Contains('|');

    private static bool IsTableSeparator(string line)
    {
        if (!ContainsTablePipes(line)) return false;
        var cells = SplitTableRow(line);
        return cells.Count > 1 && cells.All(cell => TableSeparatorCell.IsMatch(cell));
    }

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private static FrameworkElement CreateTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columnCount = rows.Max(row => row.Count);
        var grid = new Grid { MinWidth = Math.Max(480, columnCount * 140) };
        for (var column = 0; column < columnCount; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < rows[row].Count ? rows[row][column] : "";
                var text = new TextBlock
                {
                    FontSize = 12,
                    FontWeight = row == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Brush(row == 0 ? "#F1F3F5" : "#D5E1E8"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                };
                AppendInlineFormatting(text, cellText);
                var cell = new Border
                {
                    Background = Brush(row == 0 ? "#2C3037" : row % 2 == 0 ? "#202227" : "#191B1F"),
                    BorderBrush = Brush("#34373E"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = text
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid
        };
        return new Border
        {
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 7, 0, 9),
            ClipToBounds = true,
            Child = scroll
        };
    }

    private static void OpenLink(TextBlock source, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        if (args.Uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            var message = "Не удалось открыть ссылку: " + error.Message;
            if (Window.GetWindow(source) is { } owner)
                CodexMessageDialog.Show(owner, message, "Не удалось открыть ссылку", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                CodexMessageDialog.Show(message, "Не удалось открыть ссылку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void AddCodeBlock(Panel content, string code)
    {
        var text = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            Background = Brushes.Transparent,
            Foreground = Brush("#D5E1E8"),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(0)
        };
        content.Children.Add(new Border
        {
            Background = Brush("#14161A"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 7, 0, 9),
            Child = text
        });
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
