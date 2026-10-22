using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexAppServerRequestDialog : Window
{
    private static readonly Dictionary<string, CodexAppServerRequestDialog> PendingDialogs = new(StringComparer.Ordinal);
    private readonly string _method;
    private readonly JsonElement _parameters;
    private readonly List<UserInputQuestion> _questions = [];
    private readonly List<ElicitationField> _elicitationFields = [];
    private readonly CheckBox _sessionScope = new() { Content = "Разрешить для последующих ходов этого чата" };
    private readonly CheckBox _grantNetwork = new() { Content = "Разрешить сетевой доступ" };
    private readonly CheckBox _grantFileSystem = new() { Content = "Разрешить запрошенный доступ к файлам" };
    private readonly StackPanel _body = new();
    private readonly StackPanel _form = new();
    private TextBox? _rawJsonInput;
    private string? _openedUrl;
    private bool _isRawJsonForm;

    private CodexAppServerRequestDialog(string method, JsonElement parameters)
    {
        _method = method;
        _parameters = parameters.Clone();
        Title = method switch
        {
            "item/tool/requestUserInput" => "Вопрос Codex",
            "mcpServer/elicitation/request" => "Запрос MCP-сервера",
            "item/permissions/requestApproval" => "Дополнительный доступ",
            _ => "Запрос Codex"
        };
        Width = 570;
        MinWidth = 480;
        MaxHeight = 760;
        MinHeight = 220;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        Closed += (_, _) => ResponseJson ??= JsonSerializer.Serialize(BuildDeclineResponse());
    }

    public string? ResponseJson { get; private set; }

    public static string Show(Window owner, string key, string method, JsonElement parameters)
    {
        var dialog = new CodexAppServerRequestDialog(method, parameters) { Owner = owner };
        PendingDialogs[key] = dialog;
        try { dialog.ShowDialog(); }
        finally { PendingDialogs.Remove(key); }
        return dialog.ResponseJson ?? JsonSerializer.Serialize(dialog.BuildDeclineResponse());
    }

    public static void ClosePending(string key)
    {
        if (PendingDialogs.TryGetValue(key, out var dialog) && dialog.IsVisible)
            dialog.Close();
    }

    public static string BuildSafeCancellationResponse(string method, JsonElement parameters)
    {
        if (method == "item/tool/requestUserInput")
        {
            var answers = new Dictionary<string, object>(StringComparer.Ordinal);
            if (parameters.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
                foreach (var question in questions.EnumerateArray())
                    if (GetString(question, "id") is { Length: > 0 } id)
                        answers[id] = new { answers = Array.Empty<string>() };
            return JsonSerializer.Serialize(new { answers });
        }
        if (method == "mcpServer/elicitation/request")
            return JsonSerializer.Serialize(new { action = "cancel", content = (object?)null, _meta = (object?)null });
        if (method == "item/permissions/requestApproval")
            return JsonSerializer.Serialize(new { permissions = new { }, scope = "turn" });
        return "{}";
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        if (_method == "mcpServer/elicitation/request" && GetString(_parameters, "mode") == "url")
        {
            var openUrl = MakeButton("Открыть ссылку", "#20303D");
            openUrl.Margin = new Thickness(0, 0, 8, 0);
            openUrl.Click += (_, _) => OpenRequestedUrl();
            actions.Children.Add(openUrl);
        }
        var cancel = MakeButton(_method == "item/permissions/requestApproval" ? "Отклонить" : "Отмена", "#4A2A31");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        actions.Children.Add(cancel);
        var submit = MakeButton(SubmitLabel(), "#2B5A4A");
        submit.IsDefault = true;
        submit.IsEnabled = _method != "mcpServer/elicitation/request" || GetString(_parameters, "mode") != "url";
        submit.Click += (_, _) => Accept();
        actions.Children.Add(submit);
        root.Children.Add(actions);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 650,
            Content = _body
        };
        root.Children.Add(scroll);
        BuildBody();
        return root;
    }

    private void BuildBody()
    {
        var title = _method switch
        {
            "item/tool/requestUserInput" => "Codex задаёт вопрос",
            "mcpServer/elicitation/request" => $"Запрос от {GetString(_parameters, "serverName") ?? "MCP-сервера"}",
            "item/permissions/requestApproval" => "Codex запрашивает дополнительные разрешения",
            _ => "Codex запрашивает данные"
        };
        _body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        switch (_method)
        {
            case "item/tool/requestUserInput": BuildUserInput(); break;
            case "mcpServer/elicitation/request": BuildElicitation(); break;
            case "item/permissions/requestApproval": BuildPermissionRequest(); break;
        }
    }

    private void BuildUserInput()
    {
        if (!string.IsNullOrWhiteSpace(GetString(_parameters, "itemId")))
            AddMuted($"Шаг Codex · {GetString(_parameters, "itemId")}");
        if (!_parameters.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            AddMuted("Для этого запроса не передан список вопросов.");
            return;
        }

        foreach (var question in questions.EnumerateArray())
        {
            var id = GetString(question, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var entry = new UserInputQuestion(id);
            _questions.Add(entry);
            var header = GetString(question, "header");
            if (!string.IsNullOrWhiteSpace(header))
                _body.Children.Add(new TextBlock
                {
                    Text = header,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#92E5C7"),
                    Margin = new Thickness(0, 13, 0, 4)
                });
            _body.Children.Add(new TextBlock
            {
                Text = GetString(question, "question") ?? "Введите ответ",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            });

            if (question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array && options.GetArrayLength() > 0)
            {
                foreach (var option in options.EnumerateArray())
                {
                    var label = GetString(option, "label");
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    var check = new CheckBox
                    {
                        Content = BuildOptionContent(label, GetString(option, "description")),
                        Tag = label,
                        Margin = new Thickness(2, 3, 2, 3),
                        Foreground = Brush("#E2E8F0")
                    };
                    entry.Options.Add(check);
                    _body.Children.Add(check);
                }
            }

            if (GetBoolean(question, "isOther") || entry.Options.Count == 0)
            {
                if (GetBoolean(question, "isSecret"))
                {
                    entry.SecretInput = new PasswordBox();
                    StyleInput(entry.SecretInput);
                    _body.Children.Add(entry.SecretInput);
                }
                else
                {
                    entry.TextInput = new TextBox { MinHeight = 34, Padding = new Thickness(8, 6, 8, 6) };
                    StyleInput(entry.TextInput);
                    _body.Children.Add(entry.TextInput);
                }
            }
        }
    }

    private void BuildElicitation()
    {
        AddMuted(GetString(_parameters, "message") ?? "MCP-сервер просит предоставить данные.");
        var mode = GetString(_parameters, "mode") ?? "";
        if (mode == "url")
        {
            var url = GetString(_parameters, "url") ?? "";
            _openedUrl = url;
            _body.Children.Add(new Border
            {
                Background = Brush("#20303D"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock
                {
                    Text = url,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("#B6C4D3")
                }
            });
            return;
        }

        if (!_parameters.TryGetProperty("requestedSchema", out var schema) || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            AddRawJsonForm(_parameters.TryGetProperty("requestedSchema", out schema) ? schema : default);
            return;
        }

        var required = schema.TryGetProperty("required", out var requiredValues) && requiredValues.ValueKind == JsonValueKind.Array
            ? requiredValues.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in properties.EnumerateObject())
        {
            if (!TryBuildElicitationField(property.Name, property.Value, required.Contains(property.Name), out var field))
            {
                _elicitationFields.Clear();
                _form.Children.Clear();
                AddRawJsonForm(schema);
                return;
            }
            _elicitationFields.Add(field);
            _form.Children.Add(FieldLabel(field.Label, field.Description));
            _form.Children.Add(field.Input);
        }
        _body.Children.Add(_form);
    }

    private void BuildPermissionRequest()
    {
        var reason = GetString(_parameters, "reason");
        AddMuted(string.IsNullOrWhiteSpace(reason) ? "Проверьте запрошенный доступ перед продолжением." : reason);
        var cwd = GetString(_parameters, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd)) AddDetail("Папка проекта", cwd);
        if (_parameters.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object)
        {
            if (permissions.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object
                && network.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
            {
                _grantNetwork.Margin = new Thickness(0, 12, 0, 0);
                _body.Children.Add(_grantNetwork);
            }
            if (permissions.TryGetProperty("fileSystem", out var fileSystem) && fileSystem.ValueKind == JsonValueKind.Object)
            {
                _grantFileSystem.Margin = new Thickness(0, 7, 0, 0);
                _body.Children.Add(_grantFileSystem);
            }
            foreach (var permission in permissions.EnumerateObject())
            {
                if (permission.Value.ValueKind is JsonValueKind.Null or JsonValueKind.False) continue;
                var details = JsonSerializer.Serialize(permission.Value, new JsonSerializerOptions { WriteIndented = true });
                AddDetail(permission.Name == "network" ? "Сеть" : permission.Name == "fileSystem" ? "Файлы" : permission.Name, details);
            }
        }
        _sessionScope.Margin = new Thickness(0, 12, 0, 0);
        _body.Children.Add(_sessionScope);
    }

    private bool TryBuildElicitationField(string name, JsonElement schema, bool required, out ElicitationField field)
    {
        field = default!;
        var type = GetString(schema, "type");
        var label = GetString(schema, "title") ?? name;
        var description = GetString(schema, "description");
        Control input;
        string kind;
        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array
            && enumValues.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String))
        {
            kind = "enum";
            var choices = enumValues.EnumerateArray().Select(value => value.GetString() ?? "").ToList();
            if (!required) choices.Insert(0, "");
            var selected = GetString(schema, "default");
            var combo = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = selected is null ? (required ? -1 : 0) : choices.FindIndex(value => value == selected)
            };
            input = combo;
        }
        else if (type == "boolean")
        {
            kind = "boolean";
            var choices = new List<BooleanChoice> { new(null, "Выберите…"), new(true, "Да"), new(false, "Нет") };
            var hasDefault = schema.TryGetProperty("default", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var defaultChoice = hasDefault ? choices.FindIndex(choice => choice.Value == value.GetBoolean()) : 0;
            var combo = new ComboBox { ItemsSource = choices, SelectedIndex = defaultChoice };
            combo.DisplayMemberPath = nameof(BooleanChoice.Label);
            input = combo;
        }
        else if (type is "string" or "number" or "integer")
        {
            kind = type;
            var initial = GetString(schema, "default") ?? "";
            var format = GetString(schema, "format");
            if (type == "string" && format == "password")
            {
                var password = new PasswordBox { Password = initial };
                input = password;
                kind = "password";
            }
            else input = new TextBox { Text = initial, MinHeight = 34, Padding = new Thickness(8, 6, 8, 6) };
        }
        else
        {
            return false;
        }

        StyleInput(input);
        field = new ElicitationField(name, label, description, required, kind, input);
        return true;
    }

    private void AddRawJsonForm(JsonElement schema)
    {
        AddMuted("Схема формы не сводится к обычным полям. Введите JSON-объект ответа, соответствующий этой схеме.");
        if (schema.ValueKind != JsonValueKind.Undefined)
            AddDetail("Схема", JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true }));
        _rawJsonInput = new TextBox
        {
            Text = "{}",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 110,
            Padding = new Thickness(8),
            FontFamily = new FontFamily("Consolas")
        };
        StyleInput(_rawJsonInput);
        _body.Children.Add(_rawJsonInput);
        _isRawJsonForm = true;
    }

    private void Accept()
    {
        try
        {
            ResponseJson = _method switch
            {
                "item/tool/requestUserInput" => JsonSerializer.Serialize(BuildUserInputResponse()),
                "mcpServer/elicitation/request" => JsonSerializer.Serialize(BuildElicitationResponse()),
                "item/permissions/requestApproval" => JsonSerializer.Serialize(BuildPermissionResponse(grant: true)),
                _ => "{}"
            };
            DialogResult = true;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidDataException)
        {
            CodexMessageDialog.Show(this, error.Message, "Проверьте ответ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private object BuildUserInputResponse()
    {
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var question in _questions)
        {
            var values = question.Options.Where(option => option.IsChecked == true)
                .Select(option => option.Tag as string ?? "").Where(value => value.Length > 0).ToList();
            var text = question.SecretInput?.Password ?? question.TextInput?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(text)) values.Add(text.Trim());
            answers[question.Id] = new { answers = values };
        }
        return new { answers };
    }

    private object BuildElicitationResponse()
    {
        if (GetString(_parameters, "mode") == "url")
        {
            if (string.IsNullOrWhiteSpace(_openedUrl)) throw new InvalidDataException("В запросе MCP нет URL.");
            return new { action = "accept", content = (object?)null, _meta = (object?)null };
        }

        object content;
        if (_isRawJsonForm)
        {
            using var document = JsonDocument.Parse(_rawJsonInput?.Text ?? "{}");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ответ должен быть JSON-объектом.");
            content = document.RootElement.Clone();
        }
        else
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in _elicitationFields)
            {
                if ((field.Kind is "number" or "integer") && field.Input is TextBox numericInput
                    && string.IsNullOrWhiteSpace(numericInput.Text))
                {
                    if (field.Required) throw new InvalidDataException($"Заполните обязательное поле «{field.Label}».");
                    continue;
                }
                object? value = field.Kind switch
                {
                    "boolean" => (field.Input as ComboBox)?.SelectedItem is BooleanChoice choice ? choice.Value : null,
                    "enum" => (field.Input as ComboBox)?.SelectedItem as string,
                    "password" => (field.Input as PasswordBox)?.Password,
                    "string" => (field.Input as TextBox)?.Text,
                    "number" => ReadNumber((field.Input as TextBox)?.Text, integer: false),
                    "integer" => ReadNumber((field.Input as TextBox)?.Text, integer: true),
                    _ => null
                };

                var isEmpty = value is null || value is string stringValue && string.IsNullOrWhiteSpace(stringValue);
                if (isEmpty)
                {
                    if (field.Required) throw new InvalidDataException($"Заполните обязательное поле «{field.Label}».");
                    continue;
                }
                values[field.Name] = value;
            }
            content = values;
        }

        return new { action = "accept", content, _meta = (object?)null };
    }

    private object BuildPermissionResponse(bool grant)
    {
        var granted = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (grant && _parameters.TryGetProperty("permissions", out var requested) && requested.ValueKind == JsonValueKind.Object)
        {
            if (_grantNetwork.IsChecked == true && requested.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object)
                granted["network"] = network.Clone();
            if (_grantFileSystem.IsChecked == true && requested.TryGetProperty("fileSystem", out var fileSystem) && fileSystem.ValueKind == JsonValueKind.Object)
                granted["fileSystem"] = fileSystem.Clone();
        }
        return new
        {
            permissions = granted,
            scope = grant && granted.Count > 0 && _sessionScope.IsChecked == true ? "session" : "turn"
        };
    }

    private object BuildDeclineResponse()
    {
        using var document = JsonDocument.Parse(BuildSafeCancellationResponse(_method, _parameters));
        return document.RootElement.Clone();
    }

    private void OpenRequestedUrl()
    {
        if (!Uri.TryCreate(_openedUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            CodexMessageDialog.Show(this, "Ссылка MCP должна использовать HTTP или HTTPS.", "Недопустимая ссылка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            if (Content is DockPanel root && root.Children.OfType<StackPanel>().FirstOrDefault(panel => DockPanel.GetDock(panel) == Dock.Bottom) is { } actions
                && actions.Children.OfType<Button>().LastOrDefault() is { } submit)
                submit.IsEnabled = true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CodexMessageDialog.Show(this, error.Message, "Не удалось открыть ссылку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static object ReadNumber(string? text, bool integer)
    {
        if (integer)
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integral)
                ? integral
                : throw new FormatException("Введите целое число.");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException("Введите число.");
    }

    private void AddMuted(string text) => _body.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#B6C4D3"),
        Margin = new Thickness(0, 0, 0, 10)
    });

    private void AddDetail(string label, string value)
    {
        _body.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8795A6"),
            Margin = new Thickness(0, 10, 0, 4)
        });
        _body.Children.Add(new Border
        {
            Background = Brush("#20303D"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9),
            Child = new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Brush("#E2E8F0")
            }
        });
    }

    private static UIElement BuildOptionContent(string label, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = Brush("#A6B1BF") }
            }
        };
    }

    private static UIElement FieldLabel(string label, string? description)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(description))
            stack.Children.Add(new TextBlock { Text = description, FontSize = 10, Foreground = Brush("#A6B1BF"), TextWrapping = TextWrapping.Wrap });
        return stack;
    }

    private static void StyleInput(Control input)
    {
        input.Padding = new Thickness(8, 6, 8, 6);
        input.Background = Brush("#20303D");
        input.Foreground = Brush("#ECF2F5");
        input.BorderBrush = Brush("#2A3946");
        input.Margin = new Thickness(0, 3, 0, 7);
    }

    private string SubmitLabel() => _method switch
    {
        "item/tool/requestUserInput" => "Отправить ответ",
        "mcpServer/elicitation/request" when GetString(_parameters, "mode") == "url" => "Продолжить",
        "mcpServer/elicitation/request" => "Отправить данные",
        "item/permissions/requestApproval" => "Отправить выбор",
        _ => "Продолжить"
    };

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static Button MakeButton(string text, string background) => new()
    {
        Content = text,
        MinHeight = 34,
        Padding = new Thickness(11, 0, 11, 0),
        Background = Brush(background),
        Foreground = Brush("#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private sealed class UserInputQuestion(string id)
    {
        public string Id { get; } = id;
        public List<CheckBox> Options { get; } = [];
        public TextBox? TextInput { get; set; }
        public PasswordBox? SecretInput { get; set; }
    }

    private sealed record ElicitationField(string Name, string Label, string? Description, bool Required, string Kind, Control Input);
    private sealed record BooleanChoice(bool? Value, string Label);
}
