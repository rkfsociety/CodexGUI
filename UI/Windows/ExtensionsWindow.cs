using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class ExtensionsWindow : Window
{
    private readonly CancellationTokenSource _rpcCancellation = new();
    private readonly CodexAppServerClient _client;
    private readonly string _projectPath;
    private readonly Action<CodexExtension> _useSkill;
    private readonly TextBox _search = new() { Padding = new Thickness(10, 8, 10, 8), Background = Color("#1D1F23"), MaxLength = 512 };
    private readonly Button _clearSearch = new() { Content = "Очистить", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Очистить поиск" };
    private readonly TextBlock _resultCount = new() { Foreground = Color("#A6B1BF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TabControl _tabs = new() { Background = Color("#101114"), BorderThickness = new Thickness(0) };
    private readonly ListBox _plugins = CreateList();
    private readonly ListBox _skills = CreateList();
    private readonly CheckBox _installedOnly = new() { Content = "Только установленные", IsChecked = true, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Color("#B0B5BE"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _manageMarketplaces = new() { Content = "Источники", Margin = new Thickness(8, 0, 0, 0) };
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
        Closed += (_, _) => { _rpcCancellation.Cancel(); _rpcCancellation.Dispose(); };
        _projectPath = projectPath;
        _useSkill = useSkill;
        _client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _rpcCancellation.Token);
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
        var header = new Grid();
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Плагины и скиллы", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var headerActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        _refresh.Margin = new Thickness(0);
        headerActions.Children.Add(_refresh);
        _manageMarketplaces.ToolTip = "Посмотреть и удалить источники маркетплейсов";
        headerActions.Children.Add(_manageMarketplaces);
        _addMarketplace.ToolTip = "Добавить источник плагинов в общий профиль Codex";
        headerActions.Children.Add(_addMarketplace);
        _upgradeMarketplaces.ToolTip = "Обновить все настроенные Git-маркетплейсы через Codex App Server";
        headerActions.Children.Add(_upgradeMarketplaces);
        Grid.SetRow(headerActions, 1);
        header.Children.Add(headerActions);
        Add(layout, header, 0);
        var searchRow = new DockPanel { Margin = new Thickness(0, 12, 0, 10), LastChildFill = true };
        DockPanel.SetDock(_clearSearch, Dock.Right);
        searchRow.Children.Add(_clearSearch);
        searchRow.Children.Add(_search);
        Add(layout, searchRow, 1);
        var filterRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_resultCount, Dock.Right);
        filterRow.Children.Add(_resultCount);
        filterRow.Children.Add(_installedOnly);
        Add(layout, filterRow, 2);
        _search.ToolTip = "Поиск по названию, описанию и источнику";
        AutomationProperties.SetName(_search, "Поиск по плагинам и скиллам");
        AutomationProperties.SetName(_plugins, "Список плагинов");
        AutomationProperties.SetName(_skills, "Список скиллов");
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
        _clearSearch.Click += (_, _) => { _search.Clear(); _search.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _search.IsKeyboardFocusWithin && _search.Text.Length > 0)
            {
                _search.Clear();
                e.Handled = true;
            }
        };
        _installedOnly.Checked += (_, _) => Filter();
        _installedOnly.Unchecked += (_, _) => Filter();
        _tabs.SelectionChanged += (_, _) => UpdateSelection();
        _plugins.SelectionChanged += (_, _) => UpdateSelection();
        _skills.SelectionChanged += (_, _) => UpdateSelection();
        _refresh.Click += async (_, _) => await ReloadAsync();
        _manageMarketplaces.Click += async (_, _) => await ManageMarketplaceSourcesAsync();
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
        _clearSearch.IsEnabled = _search.Text.Length > 0;
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
        _manageMarketplaces.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _addMarketplace.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _upgradeMarketplaces.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _resultCount.Text = ShowingSkills
            ? $"Скиллы: {_skills.Items.Count} из {_skillItems.Count}"
            : $"Плагины: {_plugins.Items.Count} из {_pluginItems.Count}";
        var item = Selected;
        _details.Text = item is null ? EmptyDetailsMessage() :
            $"{item.Name}\n{(item.Installed ? item.Enabled ? "Включён" : "Выключен" : "Не установлен")}\n{item.Description}"
            + (ShowingSkills ? (item.PluginId is { } pluginId ? $"\n{pluginId}" : "") + $"\n{item.Path}" : $"\n{item.Id}")
            + (item.Name.Contains("imagegen", StringComparison.OrdinalIgnoreCase)
                ? "\nImagegen требует доступного встроенного image_gen. Наличие скилла само по себе не подтверждает доступ к генерации; платный API автоматически не используется." : "")
            + (item.Name.Contains("computer-use", StringComparison.OrdinalIgnoreCase)
                ? "\nComputer Use требует runtime Codex и инструмента node_repl с @oai/sky. Разрешения на управление приложениями задаются в Codex." : "");
        _install.Visibility = ShowingSkills ? Visibility.Collapsed : Visibility.Visible;
        _install.IsEnabled = !_busy && item is { Installed: false, CanInstall: true };
        _toggle.Content = item?.Enabled == true ? "Отключить" : "Включить";
        _toggle.IsEnabled = !_busy && item is { Installed: true } && (!ShowingSkills || !string.IsNullOrWhiteSpace(item.Path));
        _use.Visibility = ShowingSkills ? Visibility.Visible : Visibility.Collapsed;
        _use.IsEnabled = !_busy && item is { Enabled: true };
    }

    private string EmptyDetailsMessage()
    {
        if (!string.IsNullOrWhiteSpace(_search.Text))
            return "По запросу ничего не найдено. Измените поиск или нажмите «Очистить».";
        if (!ShowingSkills && _installedOnly.IsChecked == true && _pluginItems.Count > 0)
            return "Установленные плагины не найдены. Снимите флажок «Только установленные», чтобы увидеть весь каталог.";
        return ShowingSkills ? "Скиллы не найдены." : "Плагины не найдены.";
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

    private async Task ManageMarketplaceSourcesAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var marketplaces = await CodexMarketplaceCatalog.LoadAsync();
            var window = new CodexMarketplaceManagerWindow(marketplaces, RemoveMarketplaceAsync) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception error)
        {
            CodexMessageDialog.Show(this, error.Message, "Не удалось загрузить список маркетплейсов",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task RemoveMarketplaceAsync(CodexMarketplace marketplace)
    {
        var response = await _client.RemoveMarketplaceAsync(marketplace.Name);
        var notice = FormatMarketplaceRemoveResult(response);
        await ReloadAsync(notice, keepBusy: true);
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

    private static string FormatMarketplaceRemoveResult(JsonElement response)
    {
        if (!response.TryGetProperty("marketplaceName", out var name) || name.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Codex App Server вернул некорректное имя удалённого маркетплейса.");
        var hasInstalledRoot = response.TryGetProperty("installedRoot", out var root) && root.ValueKind == JsonValueKind.String;
        return hasInstalledRoot
            ? $"Маркетплейс «{name.GetString()}» и локальный каталог удалены."
            : $"Маркетплейс «{name.GetString()}» удалён; локального каталога не было.";
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

        const int maximumVisibleErrors = 20;
        var errors = response.GetProperty("errors").EnumerateArray()
            .Take(maximumVisibleErrors)
            .Select(error => $"{ReadJsonString(error, "marketplaceName")}: {ReadJsonString(error, "message")}")
            .ToArray();
        var partialSummary = selectedCount == 0
            ? "Обновление каталогов завершилось с ошибками."
            : $"Обновление каталогов завершилось с ошибками. Выбрано: {selectedCount}; обновлено корней: {upgradedCount}.";
        var hiddenErrors = errorsLength - errors.Length;
        return partialSummary + Environment.NewLine + string.Join(Environment.NewLine, errors)
            + (hiddenErrors > 0 ? Environment.NewLine + $"Не показано ещё {hiddenErrors} ошибок маркетплейсов." : "");
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
        _manageMarketplaces.IsEnabled = !busy;
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
            Background = Color("#1D1F23"), Foreground = Color("#F1F3F5"),
            BorderThickness = new Thickness(0)
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Color("#F1F3F5")));
        list.ItemContainerStyle = itemStyle;
        return list;
    }

    private static TabItem CreateTab(string title, ListBox content) => new()
    {
        Header = title,
        Content = content,
        Style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TabItem">
              <Setter Property="Foreground" Value="#B0B5BE"/>
              <Setter Property="Template"><Setter.Value>
                <ControlTemplate TargetType="TabItem">
                  <Border x:Name="TabBorder" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="#1D1F23" BorderBrush="#34373E" BorderThickness="1" CornerRadius="7" Margin="0,0,4,0" Padding="14,8">
                    <ContentPresenter ContentSource="Header"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="TabBorder" Property="Background" Value="#30353B"/></Trigger>
                    <Trigger Property="IsSelected" Value="True"><Setter TargetName="TabBorder" Property="Background" Value="#282B30"/><Setter Property="Foreground" Value="#92E5C7"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value></Setter>
            </Style>
            """)
    };

    private static SolidColorBrush Color(string value) => new((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
    private static void Add(Grid grid, UIElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
}
