using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class ExtensionsWindow : Window
{
    private readonly CodexAppServerClient _client;
    private readonly string _projectPath;
    private readonly Action<CodexExtension> _useSkill;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 12, 0, 10), Padding = new Thickness(10, 8, 10, 8), Background = Color("#16212B") };
    private readonly TabControl _tabs = new() { Background = Color("#0B1117"), BorderThickness = new Thickness(0) };
    private readonly ListBox _plugins = CreateList();
    private readonly ListBox _skills = CreateList();
    private readonly CheckBox _installedOnly = new() { Content = "Только установленные", IsChecked = true, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Color("#A7B5C0"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _addMarketplace = new() { Content = "Добавить маркетплейс", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _upgradeMarketplaces = new() { Content = "Обновить каталоги", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _refresh = new() { Content = "Обновить", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _install = new() { Content = "Установить", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _toggle = new() { Content = "Включить", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _use = new() { Content = "В запрос" };
    private IReadOnlyList<CodexExtension> _pluginItems = [];
    private IReadOnlyList<CodexExtension> _skillItems = [];
    private bool _busy;

    public ExtensionsWindow(string projectPath, Action<CodexExtension> useSkill, bool showSkills)
    {
        _projectPath = projectPath;
        _useSkill = useSkill;
        _client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable());
        Title = "Плагины и скиллы Codex";
        Width = 800;
        Height = 690;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        var layout = new Grid { Margin = new Thickness(20), Background = Background };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(140), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new DockPanel();
        DockPanel.SetDock(_refresh, Dock.Right);
        header.Children.Add(_refresh);
        DockPanel.SetDock(_upgradeMarketplaces, Dock.Right);
        _upgradeMarketplaces.ToolTip = "Обновить все настроенные Git-маркетплейсы через Codex App Server";
        header.Children.Add(_upgradeMarketplaces);
        DockPanel.SetDock(_addMarketplace, Dock.Right);
        _addMarketplace.ToolTip = "Добавить источник плагинов в общий профиль Codex";
        header.Children.Add(_addMarketplace);
        header.Children.Add(new TextBlock { Text = "Плагины и скиллы", FontSize = 21, FontWeight = FontWeights.SemiBold });
        Add(layout, header, 0);
        Add(layout, _search, 1);
        Add(layout, _installedOnly, 2);
        _search.ToolTip = "Поиск по названию, описанию и источнику";
        _tabs.Items.Add(CreateTab("Плагины", _plugins));
        _tabs.Items.Add(CreateTab("Все скиллы", _skills));
        Add(layout, _tabs, 3);
        Add(layout, new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 4);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_install);
        actions.Children.Add(_toggle);
        actions.Children.Add(_use);
        Add(layout, actions, 5);
        Add(layout, new ScrollViewer { Content = _status, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 6);
        Content = layout;
        _search.TextChanged += (_, _) => Filter();
        _installedOnly.Checked += (_, _) => Filter();
        _installedOnly.Unchecked += (_, _) => Filter();
        _tabs.SelectionChanged += (_, _) => UpdateSelection();
        _plugins.SelectionChanged += (_, _) => UpdateSelection();
        _skills.SelectionChanged += (_, _) => UpdateSelection();
        _refresh.Click += async (_, _) => await ReloadAsync();
        _addMarketplace.Click += async (_, _) => await AddMarketplaceAsync();
        _upgradeMarketplaces.Click += async (_, _) => await UpgradeMarketplacesAsync();
        _install.Click += async (_, _) => await ChangeAsync(install: true);
        _toggle.Click += async (_, _) => await ChangeAsync(install: false);
        _use.Click += (_, _) =>
        {
            if (Selected is not { Enabled: true } skill || !ShowingSkills) return;
            _useSkill(skill);
            Close();
        };
        _tabs.SelectedIndex = showSkills ? 1 : 0;
        Loaded += async (_, _) => await ReloadAsync();
        UpdateSelection();
    }

    private bool ShowingSkills => _tabs.SelectedIndex == 1;
    private CodexExtension? Selected => (ShowingSkills ? _skills : _plugins).SelectedItem as CodexExtension;

    private async Task ReloadAsync(string? notice = null, bool keepBusy = false)
    {
        if (!keepBusy) SetBusy(true);
        _status.Text = "Загрузка каталогов Codex…";
        var errors = new List<string>();
        async Task LoadPlugins()
        {
            try { _pluginItems = await CodexExtensionCatalog.LoadPluginsAsync(); }
            catch (Exception error) { _pluginItems = []; errors.Add("Плагины: " + error.Message); }
        }
        async Task LoadSkills()
        {
            try
            {
                var result = CodexExtensionCatalog.ParseSkills(await _client.ReadSkillsAsync(_projectPath));
                _skillItems = result.Skills;
                errors.AddRange(result.Errors);
            }
            catch (Exception error) { _skillItems = []; errors.Add("Скиллы: " + error.Message); }
        }
        await Task.WhenAll(LoadPlugins(), LoadSkills());
        Filter();
        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(notice)) messages.Add(notice);
        messages.AddRange(errors);
        _status.Text = messages.Count > 0
            ? string.Join("\n", messages)
            : $"Плагинов: {_pluginItems.Count}; скиллов: {_skillItems.Count}. Imagegen и Computer Use используют инструменты и runtime установленного Codex. Изменения применяются к следующим запросам; активные запуски продолжают работать с прежней конфигурацией.";
        if (!keepBusy) SetBusy(false);
    }

    private void Filter()
    {
        var query = _search.Text.Trim();
        bool Matches(CodexExtension item) => $"{item.Name} {item.Id} {item.Description} {item.PluginId}".Contains(query, StringComparison.OrdinalIgnoreCase);
        var pluginId = (_plugins.SelectedItem as CodexExtension)?.Id;
        var skillId = (_skills.SelectedItem as CodexExtension)?.Id;
        _plugins.ItemsSource = _pluginItems.Where(item => (_installedOnly.IsChecked != true || item.Installed) && Matches(item)).ToArray();
        _skills.ItemsSource = _skillItems.Where(Matches).ToArray();
        Select(_plugins, pluginId);
        Select(_skills, skillId);
        UpdateSelection();
    }

    private static void Select(ListBox list, string? id) =>
        list.SelectedItem = list.Items.Cast<CodexExtension>().FirstOrDefault(item => item.Id == id) ?? list.Items.Cast<CodexExtension>().FirstOrDefault();

    private void UpdateSelection()
    {
        _installedOnly.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _addMarketplace.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _upgradeMarketplaces.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        var item = Selected;
        _details.Text = item is null ? "Ничего не найдено." :
            $"{item.Name}\n{(item.Installed ? item.Enabled ? "Включён" : "Выключен" : "Не установлен")}\n{item.Description}"
            + (ShowingSkills ? (item.PluginId is { } pluginId ? $"\n{pluginId}" : "") + $"\n{item.Path}" : $"\n{item.Id}")
            + (item.Name.Contains("imagegen", StringComparison.OrdinalIgnoreCase)
                ? "\nImagegen требует доступного встроенного image_gen. Наличие скилла само по себе не подтверждает доступ к генерации; платный API автоматически не используется." : "")
            + (item.Name.Contains("computer-use", StringComparison.OrdinalIgnoreCase)
                ? "\nComputer Use требует runtime Codex и инструмента node_repl с @oai/sky. Разрешения на управление приложениями задаются в Codex." : "");
        _install.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _install.IsEnabled = !_busy && item is { Installed: false, CanInstall: true };
        _toggle.Content = item?.Enabled == true ? "Отключить" : "Включить";
        _toggle.IsEnabled = !_busy && item is { Installed: true };
        _use.Visibility = ShowingSkills ? Visibility.Visible : Visibility.Collapsed;
        _use.IsEnabled = !_busy && item is { Enabled: true };
    }

    private async Task ChangeAsync(bool install)
    {
        if (_busy || Selected is not { } item) return;
        SetBusy(true);
        _status.Text = install ? "Установка плагина…" : "Сохранение настройки…";
        try
        {
            string? notice = null;
            if (install)
            {
                await CodexCliService.RunPluginCommandAsync("add", item.Id, "--json");
                notice = "Установка завершена. При необходимости завершите подключение сервиса в оригинальном Codex. Новые скиллы доступны в следующем запросе.";
            }
            else if (ShowingSkills)
                await _client.SetSkillEnabledAsync(item.Path!, !item.Enabled);
            else
                await _client.SetPluginEnabledAsync(item.Id, !item.Enabled);
            await ReloadAsync(notice);
        }
        catch (Exception error)
        {
            _status.Text = "Не удалось выполнить операцию: " + error.Message;
        }
        finally { SetBusy(false); }
    }

    private async Task UpgradeMarketplacesAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _status.Text = "Обновление Git-маркетплейсов…";
        try
        {
            var response = await _client.UpgradeMarketplacesAsync();
            var notice = FormatMarketplaceUpgradeResult(response);
            await ReloadAsync(notice, keepBusy: true);
        }
        catch (Exception error)
        {
            _status.Text = "Не удалось обновить каталоги маркетплейсов: " + error.Message;
        }
        finally { SetBusy(false); }
    }

    private async Task AddMarketplaceAsync()
    {
        if (_busy) return;
        var editor = new CodexMarketplaceAddWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } request) return;

        SetBusy(true);
        _status.Text = "Добавление маркетплейса…";
        try
        {
            var response = await _client.AddMarketplaceAsync(request.Source, request.RefName, request.SparsePaths);
            var notice = FormatMarketplaceAddResult(response);
            await ReloadAsync(notice, keepBusy: true);
        }
        catch (Exception error)
        {
            _status.Text = "Не удалось добавить маркетплейс: " + error.Message;
        }
        finally { SetBusy(false); }
    }

    private static string FormatMarketplaceAddResult(JsonElement response)
    {
        if (!response.TryGetProperty("marketplaceName", out var name) || name.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Codex App Server вернул некорректное имя маркетплейса.");
        if (!response.TryGetProperty("alreadyAdded", out var alreadyAdded)
            || alreadyAdded.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Codex App Server вернул некорректный статус маркетплейса.");
        return alreadyAdded.GetBoolean()
            ? $"Маркетплейс «{name.GetString()}» уже добавлен."
            : $"Маркетплейс «{name.GetString()}» добавлен.";
    }

    private static string FormatMarketplaceUpgradeResult(JsonElement response)
    {
        var selectedCount = ReadArrayLength(response, "selectedMarketplaces");
        var upgradedCount = ReadArrayLength(response, "upgradedRoots");
        var errorsLength = ReadArrayLength(response, "errors");
        if (errorsLength == 0)
            return selectedCount == 0
                ? "Нет настроенных Git-маркетплейсов для обновления."
                : $"Обновление каталогов завершено. Выбрано: {selectedCount}; обновлено корней: {upgradedCount}.";

        var errors = response.GetProperty("errors").EnumerateArray()
            .Select(error => $"{ReadJsonString(error, "marketplaceName")}: {ReadJsonString(error, "message")}")
            .ToArray();
        var partialSummary = selectedCount == 0
            ? "Обновление каталогов завершилось с ошибками."
            : $"Обновление каталогов завершилось с ошибками. Выбрано: {selectedCount}; обновлено корней: {upgradedCount}.";
        return partialSummary + Environment.NewLine + string.Join(Environment.NewLine, errors);
    }

    private static int ReadArrayLength(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Codex App Server вернул некорректное поле {propertyName} для обновления маркетплейсов.");
        return array.GetArrayLength();
    }

    private static string ReadJsonString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Codex App Server вернул некорректное поле {propertyName} в ошибке обновления маркетплейса.");
        return text.GetString()!;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.IsEnabled = !busy;
        _addMarketplace.IsEnabled = !busy;
        _upgradeMarketplaces.IsEnabled = !busy;
        _tabs.IsEnabled = !busy;
        UpdateSelection();
    }

    private static ListBox CreateList()
    {
        var list = new ListBox
        {
            DisplayMemberPath = nameof(CodexExtension.DisplayLabel),
            Background = Color("#16212B"), Foreground = Color("#ECF2F5"),
            BorderThickness = new Thickness(0)
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Color("#ECF2F5")));
        list.ItemContainerStyle = itemStyle;
        return list;
    }

    private static TabItem CreateTab(string title, ListBox content) => new()
    {
        Header = title,
        Content = content,
        Style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TabItem">
              <Setter Property="Foreground" Value="#A7B5C0"/>
              <Setter Property="Template"><Setter.Value>
                <ControlTemplate TargetType="TabItem">
                  <Border x:Name="TabBorder" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="#16212B" BorderBrush="#2A3946" BorderThickness="1" Padding="14,8">
                    <ContentPresenter ContentSource="Header"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsSelected" Value="True"><Setter TargetName="TabBorder" Property="Background" Value="#20303D"/><Setter Property="Foreground" Value="#92E5C7"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value></Setter>
            </Style>
            """)
    };

    private static SolidColorBrush Color(string value) => new((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
    private static void Add(Grid grid, UIElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
}
