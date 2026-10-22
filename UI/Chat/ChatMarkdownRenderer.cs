using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CodexGUI;

internal static class ChatMarkdownRenderer
{
    private static readonly Regex InlineTokens = new(@"(`[^`]+`|\*\*.+?\*\*|(?<!\*)\*[^*]+\*(?!\*))", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(#{1,3})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*(?:[-*+]\s+|\d+[.)]\s+)(.+)$", RegexOptions.Compiled);

    public static StackPanel Render(string markdown)
    {
        var content = new StackPanel();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var code = new System.Text.StringBuilder();
        var inCode = false;

        foreach (var line in lines)
        {
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
        Foreground = Brush("#ECF2F5"),
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

            var token = match.Value;
            if (token[0] == '`')
                block.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    Foreground = Brush("#A9E3CE"),
                    Background = Brush("#1B2935")
                });
            else if (token.StartsWith("**", StringComparison.Ordinal))
                block.Inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeights.SemiBold });
            else
                block.Inlines.Add(new Run(token[1..^1]) { FontStyle = FontStyles.Italic });
            offset = match.Index + match.Length;
        }
        if (offset < text.Length)
            block.Inlines.Add(new Run(text[offset..]));
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
            Background = Brush("#0D151D"),
            BorderBrush = Brush("#2A3946"),
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
