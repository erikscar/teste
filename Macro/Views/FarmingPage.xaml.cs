using Macro.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Macro.Services;
using System.Globalization;

namespace Macro.Views;

public partial class FarmingPage : Page
{
    private void RoutineInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ToolTip is ToolTip tip)
        {
            tip.PlacementTarget = button;
            tip.IsOpen = true;
        }
    }

    private int _normalRaidImageIndex;
    private int _bossRaidImageIndex;
    private CancellationTokenSource? _runCancellation;
    private readonly WindowClickService _windowClickService = new();
    private readonly WindowPreviewService _windowCaptureService = new();
    private readonly TemplateSearchService _templateSearchService = new();
    private readonly MissionCountReader _missionCountReader = new();
    private readonly HashSet<string> _completedMissionLaunchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _missionCountStatuses = new();

    private readonly FarmingPresetService _presetService = new();
    private List<FarmingPreset> _presets = [];
    private bool _loadingPreset;
    private bool _syncingMissionMapSelection;
    private bool _presetsAvailable = true;
    public FarmingConfiguration Configuration { get; private set; } = new();
    public string[] Launchers { get; } = ["MIR4 Launcher 1", "MIR4 Launcher 2", "MIR4 Steam"];
    public string[] GuestLaunchers { get; } = ["Não utilizar", "MIR4 Launcher 1", "MIR4 Launcher 2", "MIR4 Steam"];
    public string[] MissionMaps { get; } = ["Campo", "Vale Oculto", "Miragem do Navio"];
    public string[] DailyMissionMaps { get; } =
    [
        "Ginkgo Valley", "Bicheon Castle", "Bicheon Town", "Bicheon Valley", "Bicheon Labyrinth",
        "Nefariox Ruins", "Nefariox Necropolis", "Crystalline Forest", "Demon Bull Temple",
        "Snake Pit", "Viperbeast Plain", "Snake Pit Labyrinth", "Death Gorge", "Abandoned Mine",
        "Sinner's Shire", "Snake Valley", "Secret Mine", "Spiritual Center", "Phantom Woods",
        "Heaven's Way Peak", "Redmoon Mountain", "Redmoon Valley", "Desert Road", "Phantasia Desert",
        "Ant Hole", "Phantasia Valley", "Sabuk Province", "Sabuk Castle"
    ];
    public IReadOnlyList<RaidConfiguration> NormalRaids { get; private set; }
    public IReadOnlyList<RaidConfiguration> BossRaids { get; private set; }

    public FarmingPage()
    {
        InitializeComponent();
        NormalRaids = [Configuration.Normal, new() { Name = "Raide 2" }, new() { Name = "Raide 3" }];
        BossRaids = [Configuration.Boss, new() { Name = "Boss 2" }, new() { Name = "Boss 3" }];
        DataContext = this;
        Loaded += (_, _) => RefreshPresetList();
        RefreshPresetList();
    }

    private void RefreshPresetList()
    {
        var selectedName = (PresetSelector.SelectedItem as FarmingPreset)?.Name;
        _loadingPreset = true;
        try
        {
            _presets = _presetService.Load();
            PresetSelector.ItemsSource = _presets;
            PresetSelector.SelectedItem = _presets.FirstOrDefault(p => p.Name == selectedName);
            _presetsAvailable = true;
        }
        catch (Exception ex)
        {
            _presetsAvailable = false;
            AppendLog($"Não foi possível carregar os presets: {ex.Message}");
        }
        finally { _loadingPreset = false; }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null) return;
        var name = PresetName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            AppendLog("Informe um nome para salvar o preset.");
            SaveNotification.Show("Informe um nome para o preset.", error: true);
            PresetName.Focus();
            return;
        }
        if (!_presetsAvailable)
        {
            AppendLog("Corrija o erro de leitura dos presets antes de salvar para preservar os fluxos existentes.");
            return;
        }
        try
        {
            Configuration.DailyScrollLaunchers ??= [];
            var preset = new FarmingPreset { Name = name, Configuration = FarmingPresetService.Copy(Configuration) };
            var updated = _presetService.Load().Where(item => !string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            updated.Add(preset);
            updated = updated.OrderBy(item => item.Name).ToList();
            _presetService.Save(updated);
            _presets = updated;
            _loadingPreset = true;
            PresetSelector.ItemsSource = _presets;
            PresetSelector.SelectedItem = preset;
            AppendLog($"Preset '{name}' salvo com todas as configurações atuais.");
            SaveNotification.Show($"Preset '{name}' salvo com sucesso.");
        }
        catch (Exception ex)
        {
            AppendLog($"Não foi possível salvar o preset: {ex.Message}");
            SaveNotification.Show("Não foi possível salvar o preset. Confira o log.", error: true);
        }
        finally { _loadingPreset = false; }
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPreset || _runCancellation is not null || PresetSelector.SelectedItem is not FarmingPreset preset) return;
        try { ApplyPreset(preset); }
        catch (Exception ex) { AppendLog($"Não foi possível carregar o preset: {ex.Message}"); }
    }

    private void ApplyPreset(FarmingPreset preset)
    {
        ApplyConfiguration(preset.Configuration, preset.Name);
        AppendLog($"Preset '{preset.Name}' carregado. Pressione Iniciar para executar o fluxo.");
    }

    private void ClearConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        ApplyConfiguration(new FarmingConfiguration(), "");
        PresetSelector.SelectedItem = null;
        AppendLog("Configurações do Farming limpas. Os presets salvos foram preservados.");
        SaveNotification.Show("Configurações limpas com sucesso.");
    }

    private void ApplyConfiguration(FarmingConfiguration configuration, string presetName)
    {
        _loadingPreset = true;
        try
        {
            var loaded = FarmingPresetService.Copy(configuration);
            if (loaded.DailyItems is not { Count: 3 } ||
                !loaded.DailyItems.Select(item => item.Name).SequenceEqual(new[] { "Platina", "Aço", "Óleo" }, StringComparer.OrdinalIgnoreCase))
                loaded.DailyItems = FarmingConfiguration.CreateDailyMissionItems();
            var assignedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in loaded.DailyItems)
            {
                if (!item.AvailableGrades.Contains(item.Grade, StringComparer.OrdinalIgnoreCase))
                    item.Grade = "Rara";
                item.SelectedMaps ??= [];
                item.SelectedMaps = item.SelectedMaps.Where(DailyMissionMaps.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Where(assignedMaps.Add).Take(10).ToList();
            }
            // Cada rotina é ativada somente pela própria seleção de launchers.
            loaded.DailyScrollLaunchers ??= [];
            loaded.DarkSteelMiningLaunchers = (loaded.DarkSteelMiningLaunchers ?? [])
                .Where(Launchers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            loaded.DailyTenOfTenLaunchers ??= [];
            loaded.DailyTenOfTenLaunchers = loaded.DailyTenOfTenLaunchers
                .Where(Launchers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            loaded.DailyFavoriteLaunchers ??= loaded.DailyFavorites ? [.. loaded.DailyLaunchers] : [];
            loaded.DailyCompletionLauncher1 ??= new();
            loaded.DailyCompletionLauncher2 ??= new();
            loaded.DailyCompletionSteam ??= new();
            foreach (var launcher in loaded.DailyFavoriteLaunchers)
                if (!loaded.DailyLaunchers.Contains(launcher, StringComparer.OrdinalIgnoreCase))
                    loaded.DailyLaunchers.Add(launcher);
            loaded.DailyFavorites = loaded.DailyFavoriteLaunchers.Count > 0;
            Configuration = loaded;
            NormalRaids = [Configuration.Normal, new() { Name = "Raide 2" }, new() { Name = "Raide 3" }];
            BossRaids = [Configuration.Boss, new() { Name = "Boss 2" }, new() { Name = "Boss 3" }];
            DataContext = null;
            DataContext = this;
            foreach (var checkbox in new[] { DonateLauncher1, DonateLauncher2, DonateSteam })
                checkbox.IsChecked = Configuration.DonationLaunchers.Contains((string)checkbox.Tag);
            foreach (var checkbox in new[] { ScrollLauncher1, ScrollLauncher2, ScrollSteam })
                checkbox.IsChecked = Configuration.DailyScrollLaunchers.Contains((string)checkbox.Tag);
            foreach (var checkbox in new[] { TenOfTenLauncher1, TenOfTenLauncher2, TenOfTenSteam })
                checkbox.IsChecked = Configuration.DailyTenOfTenLaunchers.Contains((string)checkbox.Tag);
            foreach (var checkbox in new[] { DarkSteelLauncher1, DarkSteelLauncher2, DarkSteelSteam })
                checkbox.IsChecked = Configuration.DarkSteelMiningLaunchers.Contains((string)checkbox.Tag);
            foreach (var checkbox in new[] { DailyLauncher1, DailyLauncher2, DailySteam })
                checkbox.IsChecked = Configuration.DailyLaunchers.Contains((string)checkbox.Tag);
            foreach (var checkbox in new[] { DailyFavoriteLauncher1, DailyFavoriteLauncher2, DailyFavoriteSteam })
                checkbox.IsChecked = Configuration.DailyFavoriteLaunchers.Contains((string)checkbox.Tag);
            foreach (var combo in new[] { DailyMapLauncher1, DailyMapLauncher2, DailyMapSteam })
                combo.SelectedItem = GetDailyMap((string)combo.Tag);
            PresetName.Text = presetName;
        }
        finally { _loadingPreset = false; }
    }

    public bool IsRunning => _runCancellation is not null;
    public string RunStatus => StatusText.Text;
    public string RunLog => ExecutionLog.Text;
    public void StopMacro() => _runCancellation?.Cancel();

    public async Task StartPresetAsync(FarmingPreset preset)
    {
        if (IsRunning) throw new InvalidOperationException("Já existe uma rotina em execução.");
        ApplyPreset(preset);
        await RunConfiguredAsync();
    }

    private void PreviousNormalRaidImage_Click(object sender, RoutedEventArgs e) => ChangeNormalRaidImage(-1);
    private void NextNormalRaidImage_Click(object sender, RoutedEventArgs e) => ChangeNormalRaidImage(1);
    private void PreviousBossRaidImage_Click(object sender, RoutedEventArgs e) => ChangeBossRaidImage(-1);
    private void NextBossRaidImage_Click(object sender, RoutedEventArgs e) => ChangeBossRaidImage(1);

    private void ChangeNormalRaidImage(int direction)
    {
        _normalRaidImageIndex = (_normalRaidImageIndex + direction + 2) % 2;
        AnimateCarouselImage(NormalRaidImage, $"pack://application:,,,/Assets/raid-slide-{_normalRaidImageIndex + 1}.png", direction);
        NormalRaidImageCounter.Text = $"{_normalRaidImageIndex + 1} / 2";
    }

    private void ChangeBossRaidImage(int direction)
    {
        _bossRaidImageIndex = (_bossRaidImageIndex + direction + 2) % 2;
        AnimateCarouselImage(BossRaidImage, $"pack://application:,,,/Assets/boss-slide-{_bossRaidImageIndex + 1}.png", direction);
        BossRaidImageCounter.Text = $"{_bossRaidImageIndex + 1} / 2";
    }

    private static void AnimateCarouselImage(Image image, string source, int direction)
    {
        image.Source = new BitmapImage(new Uri(source));
        image.BeginAnimation(UIElement.OpacityProperty, null);
        var translation = new TranslateTransform();
        image.RenderTransform = translation;
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(240);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        image.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.25, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        translation.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 18, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    // A interface permanece pronta para receber uma nova implementação.
    private void DonationLauncher_Checked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DonationLaunchers, sender, true);
    private void DonationLauncher_Unchecked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DonationLaunchers, sender, false);
    private void ScrollLauncher_Checked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DailyScrollLaunchers ??= [], sender, true);
    private void ScrollLauncher_Unchecked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DailyScrollLaunchers ??= [], sender, false);
    private void TenOfTenLauncher_Checked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DailyTenOfTenLaunchers, sender, true);
    private void TenOfTenLauncher_Unchecked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DailyTenOfTenLaunchers, sender, false);
    private void DarkSteelLauncher_Checked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DarkSteelMiningLaunchers, sender, true);
    private void DarkSteelLauncher_Unchecked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DarkSteelMiningLaunchers, sender, false);
    private void DailyLauncher_Checked(object sender, RoutedEventArgs e) => UpdateLauncherSelection(Configuration.DailyLaunchers, sender, true);
    private void DailyLauncher_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loadingPreset) return;
        UpdateLauncherSelection(Configuration.DailyLaunchers, sender, false);
        if (sender is not CheckBox { Tag: string launcher }) return;
        Configuration.DailyFavoriteLaunchers ??= [];
        Configuration.DailyFavoriteLaunchers.RemoveAll(name => string.Equals(name, launcher, StringComparison.OrdinalIgnoreCase));
        Configuration.DailyFavorites = Configuration.DailyFavoriteLaunchers.Count > 0;
        GetDailyFavoriteCheckBox(launcher).IsChecked = false;
    }
    private void DailyFavoriteLauncher_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingPreset || sender is not CheckBox { Tag: string launcher }) return;
        Configuration.DailyFavoriteLaunchers ??= [];
        UpdateLauncherSelection(Configuration.DailyFavoriteLaunchers, sender, true);
        GetDailyLauncherCheckBox(launcher).IsChecked = true;
        Configuration.DailyFavorites = Configuration.DailyFavoriteLaunchers.Count > 0;
    }
    private void DailyFavoriteLauncher_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loadingPreset || sender is not CheckBox { Tag: string launcher }) return;
        Configuration.DailyFavoriteLaunchers ??= [];
        UpdateLauncherSelection(Configuration.DailyFavoriteLaunchers, sender, false);
        Configuration.DailyFavorites = Configuration.DailyFavoriteLaunchers.Count > 0;
    }

    private CheckBox GetDailyLauncherCheckBox(string launcher) => launcher switch
    {
        "MIR4 Launcher 1" => DailyLauncher1,
        "MIR4 Launcher 2" => DailyLauncher2,
        "MIR4 Steam" => DailySteam,
        _ => throw new InvalidOperationException($"Launcher desconhecido: {launcher}.")
    };

    private CheckBox GetDailyFavoriteCheckBox(string launcher) => launcher switch
    {
        "MIR4 Launcher 1" => DailyFavoriteLauncher1,
        "MIR4 Launcher 2" => DailyFavoriteLauncher2,
        "MIR4 Steam" => DailyFavoriteSteam,
        _ => throw new InvalidOperationException($"Launcher desconhecido: {launcher}.")
    };

    private string GetDailyMap(string launcher) =>
        Configuration.DailyLauncherMaps.TryGetValue(launcher, out var map) && MissionMaps.Contains(map) ? map : "Campo";

    private void DailyMap_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPreset || sender is not ComboBox { Tag: string launcher, SelectedItem: string map }) return;
        Configuration.DailyLauncherMaps[launcher] = map;
    }

    private static bool IsValidCompletionLimitInput(TextBox input, string insertion)
    {
        var candidate = input.Text.Remove(input.SelectionStart, input.SelectionLength)
            .Insert(input.SelectionStart, insertion);
        return candidate.Length == 0 ||
            (candidate.All(character => character is >= '0' and <= '9') &&
             int.TryParse(candidate, out var limit) && limit is >= 0 and <= 30);
    }

    private void CompletionLimit_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (sender is TextBox input) e.Handled = !IsValidCompletionLimitInput(input, e.Text);
    }

    private void CompletionLimit_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Space) e.Handled = true;
    }

    private void CompletionLimit_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox input || e.DataObject.GetData(DataFormats.UnicodeText) is not string text ||
            !IsValidCompletionLimitInput(input, text))
            e.CancelCommand();
    }

    private void CompletionLimit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox input) return;
        var limit = int.TryParse(input.Text, out var value) ? Math.Clamp(value, 0, 30) : 0;
        input.SetCurrentValue(TextBox.TextProperty, limit.ToString(CultureInfo.InvariantCulture));
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void DailyMissionMaps_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || list.DataContext is not MissionItemConfiguration item) return;
        _syncingMissionMapSelection = true;
        try
        {
            var selected = item.SelectedMaps.Where(DailyMissionMaps.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
            item.SelectedMaps = [.. selected];
            list.SelectedItems.Clear();
            foreach (var map in selected) list.SelectedItems.Add(map);
        }
        finally { _syncingMissionMapSelection = false; }
    }

    private void DailyMissionMaps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMissionMapSelection || sender is not ListBox list || list.DataContext is not MissionItemConfiguration item)
            return;
        var addedMaps = e.AddedItems.Cast<string>().ToArray();
        var duplicateMaps = addedMaps.Where(map => Configuration.DailyItems.Any(other =>
            !ReferenceEquals(other, item) && other.SelectedMaps.Contains(map, StringComparer.OrdinalIgnoreCase))).ToArray();
        var rejectedMaps = duplicateMaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exceedsLimit = list.SelectedItems.Count > 10;
        if (exceedsLimit)
            rejectedMaps.UnionWith(addedMaps);
        if (rejectedMaps.Count > 0)
        {
            _syncingMissionMapSelection = true;
            try
            {
                foreach (var map in rejectedMaps) list.SelectedItems.Remove(map);
            }
            finally { _syncingMissionMapSelection = false; }
            if (duplicateMaps.Length > 0)
                AppendLog($"{item.Name}: mapa já atribuído a outra missão; cada mapa só pode ser usado uma vez.");
            if (exceedsLimit)
                AppendLog($"{item.Name}: é possível selecionar no máximo 10 mapas.");
        }
        item.SelectedMaps = list.SelectedItems.Cast<string>().ToList();
    }

    private void UpdateLauncherSelection(List<string> selection, object sender, bool enabled)
    {
        if (_loadingPreset) return;
        if (sender is not CheckBox { Tag: string launcher }) return;
        if (enabled && !selection.Contains(launcher, StringComparer.OrdinalIgnoreCase)) selection.Add(launcher);
        else if (!enabled) selection.RemoveAll(item => string.Equals(item, launcher, StringComparison.OrdinalIgnoreCase));
    }

    private async void BtnDoArena_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Configuration.ArenaRepeatCountText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var repeatCount) || repeatCount is < 1 or > 100)
        {
            AppendLog("DailyArena: informe uma quantidade entre 1 e 100.");
            return;
        }

        if (_runCancellation is not null)
        {
            AppendLog("Já existe uma rotina em execução.");
            return;
        }

        _runCancellation = new CancellationTokenSource();
        var cancellationToken = _runCancellation.Token;
        BtnDoArena.IsEnabled = false;
        ConfigurationPanel.IsEnabled = false;
        ConfigurationExpander.IsExpanded = false;
        BtnStart.IsEnabled = false;
        StatusText.Text = "Em execução";
        try
        {
            for (var iteration = 1; iteration <= repeatCount; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendLog($"DailyArena: execução {iteration}/{repeatCount}.");
                await DailyArena(cancellationToken);
            }
            AppendLog($"DailyArena finalizada após {repeatCount} execução(ões).");
        }
        catch (OperationCanceledException) { AppendLog("DailyArena cancelada."); }
        catch (Exception ex) { AppendLog($"DailyArena: falha — {ex.Message}"); }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            BtnDoArena.IsEnabled = true;
            ConfigurationPanel.IsEnabled = true;
            BtnStart.IsEnabled = true;
            StatusText.Text = "Pronto";
        }
    }

    private async Task DailyArena(CancellationToken cancellationToken)
    {
        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        if (!targets.TryGetValue(Configuration.ArenaStarter, out var starter))
            throw new InvalidOperationException($"starter {Configuration.ArenaStarter} não está aberto.");
        if (!targets.TryGetValue(Configuration.ArenaInviter, out var guest))
            throw new InvalidOperationException($"convidado {Configuration.ArenaInviter} não está aberto.");
        if (ReferenceEquals(starter, guest))
            throw new InvalidOperationException("Selecione launchers diferentes para o starter e o convidado da Arena.");

        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates");
        var raidDirectory = Path.Combine(directory, "DailyRaid");
        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ícone Guerra"] = Path.Combine(directory, "daily-arena-war-icon.png"),
            ["Arena"] = Path.Combine(directory, "daily-arena-label.png"),
            ["Criar Arena (menu)"] = Path.Combine(directory, "daily-arena-create-menu.png"),
            ["Criar Arena"] = Path.Combine(directory, "daily-arena-create.png"),
            ["Privado"] = Path.Combine(directory, "daily-arena-private.png"),
            ["campo da senha"] = Path.Combine(directory, "daily-arena-password.png"),
            ["botão 2"] = Path.Combine(directory, "daily-arena-two.png"),
            ["Entrada completa"] = Path.Combine(directory, "daily-arena-entry-complete.png"),
            ["adicionar convidados"] = Path.Combine(directory, "daily-arena-add.png"),
            ["Convidar todos"] = Path.Combine(directory, "daily-arena-invite-all.png"),
            ["Aceitar convite"] = Path.Combine(raidDirectory, "daily-raid-accept.png"),
            ["Entrar na raide"] = Path.Combine(raidDirectory, "daily-raid-enter.png"),
            ["Iniciar Arena"] = Path.Combine(directory, "daily-arena-start-confirm.png"),
            ["ícone pós-início"] = Path.Combine(directory, "daily-arena-post-start-icon.png"),
            ["Confirmar"] = Path.Combine(directory, "daily-arena-confirm.png"),
            ["Sair"] = Path.Combine(directory, "daily-arena-exit.png")
        };
        if (templates.Values.Any(path => !File.Exists(path)))
            throw new InvalidOperationException("Faltam imagens de referência da Arena em Assets\\Templates.");

        var regions = new Dictionary<string, RelativeSearchRegion>(StringComparer.Ordinal)
        {
            ["ícone Guerra"] = new(0.8330, 0.4890, 0.1670, 0.1793),
            ["Arena"] = new(0.8068, 0.6807, 0.1506, 0.1204),
            ["Criar Arena (menu)"] = new(0.5953, 0.8305, 0.4047, 0.1695),
            ["Criar Arena"] = new(0.4059, 0.7494, 0.1774, 0.1534),
            ["Privado"] = new(0.2416, 0.5112, 0.2722, 0.1204),
            ["campo da senha"] = new(0.6631, 0.5112, 0.1202, 0.1130),
            ["botão 2"] = new(0.3922, 0.2680, 0.2156, 0.3881),
            ["Entrada completa"] = new(0.4834, 0.7642, 0.1575, 0.1695),
            ["adicionar convidados"] = new(0.5166, 0.4399, 0.0746, 0.1449),
            ["Convidar todos"] = new(0.4903, 0.1623, 0.1603, 0.1548),
            ["Aceitar convite"] = new(0.0509, 0.4252, 0.1893, 0.0933),
            ["Entrar na raide"] = new(0.3439, 0.5750, 0.3454, 0.1548),
            ["Iniciar Arena"] = new(0.3673, 0.8280, 0.3081, 0.1720),
            ["ícone pós-início"] = new(0.6796, 0.0886, 0.1133, 0.2014),
            ["Confirmar"] = new(0.3632, 0.6512, 0.2998, 0.1720),
            ["Sair"] = new(0.3300, 0.8575, 0.3731, 0.1425)
        };

        AppendLog($"DailyArena: starter {Configuration.ArenaStarter}; convidado {Configuration.ArenaInviter}.");
        await PrepareRoutineWindowAsync(starter, cancellationToken);
        await WaitWithCountdownAsync(TimeSpan.FromSeconds(5), "Arena: aguardando para abrir F9.", cancellationToken);
        await _windowClickService.PressKeyAsync(starter, 0x78, "F9", cancellationToken, AppendLog);
        await Task.Delay(350, cancellationToken);
        var steps = new[] { "ícone Guerra", "Arena", "Criar Arena (menu)", "Privado", "campo da senha", "botão 2", "Entrada completa", "Criar Arena", "adicionar convidados", "Convidar todos" };
        foreach (var name in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clickCount = name == "botão 2" ? 4 : 1;
            var threshold = name switch
            {
                "adicionar convidados" => 0.50,
                "Criar Arena" => 0.50,
                "botão 2" => 0.72,
                _ => 0.78
            };
            var found = await FindAndClickTemplateAsync(starter, templates[name], regions[name], null,
                name, threshold, cancellationToken, clickCount: clickCount);
            if (!found) throw new InvalidOperationException($"não foi possível concluir a etapa {name}.");
            if (name == "campo da senha")
                await _windowClickService.ReplaceTextAsync(starter, "0000", cancellationToken, AppendLog);
            await Task.Delay(350, cancellationToken);
        }

        AppendLog($"DailyArena: convite enviado; mudando para {Configuration.ArenaInviter} para aceitar o pedido como na rotina de boss.");
        await PrepareRoutineWindowAsync(guest, cancellationToken);
        await _windowClickService.PressCtrlNumberAsync(guest, 0x31, cancellationToken, AppendLog);
        foreach (var guestStep in new[] { "Aceitar convite", "Entrar na raide" })
        {
            var accepted = await FindAndClickTemplateAsync(guest, templates[guestStep],
                regions[guestStep], null, guestStep, 0.78, cancellationToken);
            if (!accepted) throw new InvalidOperationException($"{Configuration.ArenaInviter}: não foi possível concluir {guestStep}.");
            await Task.Delay(350, cancellationToken);
        }

        await PrepareRoutineWindowAsync(starter, cancellationToken);
        AppendLog($"DailyArena: convite aceito em {Configuration.ArenaInviter}; retornando ao starter, pressionando Esc uma vez e iniciando a Arena.");
        await _windowClickService.PressKeyAsync(starter, 0x1B, "Esc", cancellationToken, AppendLog);
        await Task.Delay(700, cancellationToken);
        var started = await FindAndClickTemplateAsync(starter, templates["Iniciar Arena"],
            regions["Iniciar Arena"], null, "Iniciar Arena", 0.70, cancellationToken);
        if (!started) throw new InvalidOperationException("não foi possível clicar em Iniciar Arena.");
        AppendLog("DailyArena: Arena iniciada pelo starter.");
        await Task.Delay(350, cancellationToken);
        var postStartIcon = await FindAndClickTemplateAsync(starter, templates["ícone pós-início"],
            regions["ícone pós-início"], null, "ícone pós-início", 0.72, cancellationToken);
        if (!postStartIcon) throw new InvalidOperationException("não foi possível localizar ou clicar no ícone após iniciar a Arena.");
        await Task.Delay(350, cancellationToken);
        var confirmed = await FindAndClickTemplateAsync(starter, templates["Confirmar"],
            regions["Confirmar"], null, "Confirmar", 0.78, cancellationToken);
        if (!confirmed) throw new InvalidOperationException("não foi possível localizar ou clicar em Confirmar após iniciar a Arena.");
        AppendLog("DailyArena: etapa Confirmar concluída.");
        await PrepareRoutineWindowAsync(guest, cancellationToken);
        await Task.Delay(350, cancellationToken);
        var exited = await FindAndClickTemplateAsync(guest, templates["Sair"],
            regions["Sair"], null, "Sair", 0.78, cancellationToken);
        if (!exited) throw new InvalidOperationException($"{Configuration.ArenaInviter}: não foi possível localizar ou clicar em Sair.");
        AppendLog($"DailyArena: clique em Sair concluído na janela {Configuration.ArenaInviter}.");
    }
    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        AppendLog("Parada solicitada.");
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
        => await RunConfiguredAsync();

    private async Task RunConfiguredAsync()
    {
        if (_runCancellation is not null)
        {
            AppendLog("A rotina já está em execução.");
            return;
        }

        DateTime? scheduledStart = null;
        var requestedTime = Configuration.StartTime?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedTime))
        {
            if (!TimeOnly.TryParseExact(requestedTime, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var startTime))
            {
                AppendLog("Horário inválido. Informe no formato 24 horas HH:mm, por exemplo 21:30.");
                MessageBox.Show("Informe o horário no formato HH:mm (24 horas), por exemplo 21:30.",
                    "Horário inválido", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            scheduledStart = DateTime.Today.Add(startTime.ToTimeSpan());
            if (scheduledStart <= DateTime.Now)
                scheduledStart = scheduledStart.Value.AddDays(1);
        }

        _runCancellation = new CancellationTokenSource();
        var cancellationToken = _runCancellation.Token;
        BtnStart.IsEnabled = false;
        _completedMissionLaunchers.Clear();
        _missionCountStatuses.Clear();
        MissionCountImage.Source = null;
        foreach (var launcher in Launchers.Where(launcher => Configuration.CompletionCheckFor(launcher).Enabled))
            UpdateMissionCountStatus(launcher, null, "aguardando conferência");
        if (_missionCountStatuses.Count == 0)
            MissionCountStatusText.Text = "Missões: conferência desabilitada";
        try
        {
            ConfigurationPanel.IsEnabled = false;
            ConfigurationExpander.IsExpanded = false;
            if (scheduledStart is DateTime startAt)
            {
                var delay = startAt - DateTime.Now;
                if (delay > TimeSpan.Zero)
                {
                    StatusText.Text = "Aguardando horário";
                    AppendLog($"Início agendado para {startAt:dd/MM/yyyy HH:mm}. Use Stop para cancelar a espera.");
                    await WaitWithCountdownAsync(delay, $"Início do macro agendado para {startAt:dd/MM/yyyy HH:mm}.", cancellationToken);
                }
                AppendLog("Horário atingido; iniciando as rotinas selecionadas.");
            }
            else
                AppendLog("Macro iniciado imediatamente.");

            StatusText.Text = "Em execução";
            foreach (var action in Configuration.OrderedActions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (action)
                {
                    case "Doação diária" when Configuration.DailyDonation: await DailyDonate(cancellationToken); break;
                    case "Pergaminho diário" when Configuration.DailyScroll: await DailyScroll(cancellationToken); break;
                    case "Diárias 10/10" when Configuration.DailyTenOfTenEnabled: await DailyTenOfTen(cancellationToken); break;
                    case "Minerar Dark Steel" when Configuration.DarkSteelMiningEnabled: await MineDarkSteel(cancellationToken); break;
                    case "Raids normais" when Configuration.Normal.IsEnabled: await DailyFavoriteRaid(cancellationToken); break;
                    case "Boss" when Configuration.Boss.IsEnabled: await DailyRaidBoss(cancellationToken); break;
                    case "Missões favoritas" when Configuration.DailyMissionLaunchersToRun.Any():
                        await DailyFavoriteMissions(cancellationToken);
                        break;
                    case "Arena" when Configuration.ArenaEnabled:
                        await WaitForArenaTimeAsync(cancellationToken);
                        if (!int.TryParse(Configuration.ArenaRepeatCountText, out var count) || count is < 1 or > 100)
                            throw new InvalidOperationException("Arena: informe entre 1 e 100 repetições.");
                        for (var iteration = 1; iteration <= count; iteration++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            AppendLog($"DailyArena: execução {iteration}/{count}.");
                            await DailyArena(cancellationToken);
                        }
                        break;
                }
            }
            await MonitorDailyMissionCompletionAsync(cancellationToken);
            if (!Launchers.Any(launcher => Configuration.CompletionCheckFor(launcher).Enabled) &&
                !Configuration.DailyDonation && !Configuration.DailyScroll && !Configuration.DailyTenOfTenEnabled && !Configuration.DarkSteelMiningEnabled &&
                Configuration.DailyFavoriteLaunchers is not { Count: > 0 } &&
                !Configuration.Normal.IsEnabled && !Configuration.Boss.IsEnabled && !Configuration.ArenaEnabled)
                AppendLog("Nenhuma rotina diária está ativada; nenhuma ação executada.");
            AppendLog("Macro finalizado.");
        }
        catch (OperationCanceledException) { AppendLog("Macro cancelado."); }
        catch (Exception ex) { AppendLog($"Erro no macro: {ex.Message}"); }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            BtnStart.IsEnabled = true;
            ConfigurationPanel.IsEnabled = true;
            StatusText.Text = "Pronto";
        }
    }

    private async Task WaitForArenaTimeAsync(CancellationToken cancellationToken)
    {
        var requestedTime = Configuration.ArenaStartTime?.Trim();
        if (string.IsNullOrWhiteSpace(requestedTime)) return;
        if (!TimeOnly.TryParseExact(requestedTime, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var arenaTime))
            throw new InvalidOperationException("Horário da Arena inválido. Informe no formato 24 horas HH:mm, por exemplo 21:30.");

        var startAt = DateTime.Today.Add(arenaTime.ToTimeSpan());
        if (startAt <= DateTime.Now) startAt = startAt.AddDays(1);
        var delay = startAt - DateTime.Now;
        if (delay <= TimeSpan.Zero) return;
        StatusText.Text = "Aguardando Arena";
        AppendLog($"Arena agendada para {startAt:dd/MM/yyyy HH:mm}. Use Stop para cancelar a espera.");
        await WaitWithCountdownAsync(delay, $"Arena agendada para {startAt:dd/MM/yyyy HH:mm}.", cancellationToken);
        AppendLog("Horário da Arena atingido; iniciando a Arena.");
        StatusText.Text = "Em execução";
    }

    private async Task WaitWithCountdownAsync(TimeSpan duration, string activity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero) return;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        WaitActivityText.Text = activity;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = duration - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
                WaitCountdownText.Text = seconds >= 3600
                    ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"
                    : $"{seconds / 60:00}:{seconds % 60:00}";
                WaitProgress.Value = Math.Clamp(elapsed.Elapsed.TotalMilliseconds / duration.TotalMilliseconds * 100, 0, 100);
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), token);
            }
        }
        finally
        {
            WaitCountdownText.Text = "--:--";
            WaitActivityText.Text = token.IsCancellationRequested ? "Espera cancelada" : "Nenhuma espera em andamento";
            WaitProgress.Value = 0;
        }
    }

    private async Task DelayRoutineStartAsync(string routine, CancellationToken cancellationToken)
    {
        AppendLog($"{routine}: aguardando 4 segundos antes de iniciar.");
        await WaitWithCountdownAsync(TimeSpan.FromSeconds(4), $"{routine}: aguardando 4 segundos antes de iniciar.", cancellationToken);
    }

    private async Task DailyDonate(CancellationToken cancellationToken)
    {
        if (Configuration.DonationLaunchers.Count == 0)
        {
            AppendLog("Doação diária: selecione pelo menos um launcher.");
            return;
        }

        var windows = new WindowPreviewService().ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        var templatesDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DailyDonate");
        var warehouseTemplatePath = Path.Combine(templatesDirectory, "daily-donate-warehouse.png");
        var donateButtonTemplatePath = Path.Combine(templatesDirectory, "daily-donate-button.png");
        var flowTemplates = Enumerable.Range(1, 12)
            .Select(index => Path.Combine(templatesDirectory, $"daily-flow-{index:D2}-" + new[]
            {
                "cobre", "max", "doar", "doar", "aco-negro", "max", "doar", "doar", "energia", "max", "doar", "doar"
            }[index - 1] + ".png"))
            .ToArray();
        if (new[] { warehouseTemplatePath, donateButtonTemplatePath }.Any(path => !File.Exists(path)) ||
            flowTemplates.Any(path => !File.Exists(path)))
        {
            AppendLog("Uma das imagens da sequência diária não foi encontrada em Assets\\Templates\\DailyDonate.");
            return;
        }

        // Abre o menu com F5 e procura a opção Armazém.
        var warehouseRegion = new RelativeSearchRegion(0.6451, 0.7101, 0.1479, 0.2727);
        var donateButtonRegion = new RelativeSearchRegion(0.3425, 0.7912, 0.3399, 0.2088);
        const double confidenceThreshold = 0.82;
        FarmingScroll.ScrollToBottom();
        foreach (var launcher in Launchers.Where(Configuration.DonationLaunchers.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(launcher, out var target))
            {
                AppendLog($"{launcher}: processo MIR4 correspondente não está aberto.");
                continue;
            }

            try
            {
                await DelayRoutineStartAsync($"Doação diária — {launcher}", cancellationToken);
                AppendLog($"{launcher}: ajustando a área para 1280×720 e ativando {target.ProcessName} (PID {target.ProcessId}).");
                await PrepareRoutineWindowAsync(target, cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x74, "F5", cancellationToken, AppendLog);

                AppendLog($"{launcher}: menu de doação aberto.");
                await Task.Delay(350, cancellationToken);
                var warehouseClicked = await FindAndClickTemplateAsync(target, warehouseTemplatePath, warehouseRegion,
                    new Int32Rect(34, 48, 102, 103), "Armazém", confidenceThreshold, cancellationToken);
                if (!warehouseClicked) continue;

                AppendLog($"{launcher}: Armazém selecionado.");
                await Task.Delay(350, cancellationToken);
                var donationMenuOpened = await FindAndClickTemplateAsync(target, donateButtonTemplatePath, donateButtonRegion,
                    null, "botão Doar", confidenceThreshold, cancellationToken);
                if (!donationMenuOpened) continue;

                // Regiões capturadas na ordem de Cobre, Aço Negro e Energia.
                var flowSteps = new (int Template, RelativeSearchRegion Region, Int32Rect? Crop, string Name)[]
                {
                    (0, new(0.1822, 0.1697, 0.2349, 0.1867), null, "Cobre"),
                    (1, new(0.3770, 0.5750, 0.3966, 0.1842), null, "MAX após Cobre"),
                    (2, new(0.5705, 0.7175, 0.2349, 0.1769), null, "Doar 1 após Cobre"),
                    (3, new(0.4199, 0.4866, 0.2874, 0.2457), null, "Doar 2 após Cobre"),
                    (4, new(0.2002, 0.2901, 0.1962, 0.1965), null, "Aço Negro"),
                    (5, new(0.3770, 0.5726, 0.4049, 0.1793), null, "MAX após Aço Negro"),
                    (6, new(0.5677, 0.6880, 0.2391, 0.2162), null, "Doar 1 após Aço Negro"),
                    (7, new(0.3508, 0.5333, 0.3441, 0.2039), null, "Doar 2 após Aço Negro"),
                    (8, new(0.1905, 0.4227, 0.2156, 0.2063), null, "Energia"),
                    (9, new(0.4281, 0.5480, 0.3620, 0.2088), null, "MAX após Energia"),
                    (10, new(0.5663, 0.6954, 0.2418, 0.2162), null, "Doar 1 após Energia"),
                    (11, new(0.4682, 0.4915, 0.2169, 0.2653), null, "Doar 2 após Energia")
                };
                foreach (var step in flowSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stepThreshold = step.Name switch
                    {
                        "Cobre" => 0.48,
                        "Energia" => 0.70,
                        _ => confidenceThreshold
                    };
                    var completed = await FindAndClickTemplateAsync(target, flowTemplates[step.Template], step.Region, step.Crop,
                        step.Name, stepThreshold, cancellationToken);
                    if (!completed)
                    {
                        AppendLog($"{launcher}: sequência interrompida em {step.Name}; os passos seguintes foram ignorados.");
                        break;
                    }
                    await Task.Delay(250, cancellationToken);
                }

                AppendLog($"{launcher}: encerrando a rotina de doação com cinco pressionamentos de Esc.");
                await _windowClickService.PressEscapeTimesAsync(target, 5, cancellationToken, AppendLog);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog($"{launcher}: falha ao clicar — {ex.Message}"); }
        }
    }

    private async Task CleanDailyScrollBackpackAsync(PreviewWindow target, string launcher, CancellationToken token)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DailyScroll");
        var steps = new (string Image, string Name, RelativeSearchRegion Region)[]
        {
            ("backpack-sell-menu.png", "abrir Vender", new(0.7061, 0.8605, 0.1476, 0.1324)),
            ("backpack-auto-select.png", "Seleção autom.", new(0.3098, 0.8534, 0.1782, 0.1324)),
            ("backpack-sell.png", "Vender selecionados", new(0.8218, 0.8629, 0.1782, 0.1324)),
            ("backpack-confirm.png", "confirmar Vender", new(0.4934, 0.5414, 0.1529, 0.1537))
        };
        foreach (var step in steps)
            if (!File.Exists(Path.Combine(directory, step.Image)))
                throw new FileNotFoundException($"Limpar mochila: imagem ausente — {step.Image}.");

        AppendLog($"Limpar mochila — {launcher}: iniciando antes da compra; usando a seleção automática configurada no jogo.");
        await _windowClickService.PressKeyAsync(target, 0x76, "F7", token, AppendLog);
        await Task.Delay(500, token);
        foreach (var step in steps)
        {
            if (!await FindAndClickTemplateAsync(target, Path.Combine(directory, step.Image), step.Region,
                null, $"Limpar mochila: {step.Name}", 0.78, token))
                throw new InvalidOperationException($"Limpar mochila: {step.Name} não encontrado; limpeza e compra interrompidas neste launcher.");
            await Task.Delay(500, token);
        }
        await _windowClickService.PressEscapeTimesAsync(target, 3, token, AppendLog);
        await WaitWithCountdownAsync(TimeSpan.FromSeconds(4),
            $"Limpar mochila — {launcher}: aguardando 4 segundos antes de comprar o pergaminho.", token);
        AppendLog($"Limpar mochila — {launcher}: sequência de venda enviada; continuando para comprar o pergaminho.");
    }

    private async Task DailyScroll(CancellationToken cancellationToken)
    {
        if (Configuration.DailyScrollLaunchers is not { Count: > 0 })
        {
            AppendLog("DailyScroll: selecione pelo menos um launcher.");
            return;
        }

        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        var templatesDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DailyScroll");
        var mapTemplate = Path.Combine(templatesDirectory, "daily-scroll-map.png");
        var currencyTemplate = Path.Combine(templatesDirectory, "daily-scroll-currency.png");
        var itemTemplate = Path.Combine(templatesDirectory, "daily-scroll-item.png");
        var buyTemplate = Path.Combine(templatesDirectory, "daily-scroll-buy.png");
        var targetTemplate = Path.Combine(templatesDirectory, "daily-scroll-target.png");
        var fiveTemplate = Path.Combine(templatesDirectory, "daily-scroll-five.png");
        var buy150kTemplate = Path.Combine(templatesDirectory, "daily-scroll-150k-buy.png");
        if (new[] { mapTemplate, currencyTemplate, itemTemplate, buyTemplate, targetTemplate, fiveTemplate, buy150kTemplate }.Any(path => !File.Exists(path)))
        {
            AppendLog("DailyScroll: uma das imagens de referência não foi encontrada em Assets\\Templates\\DailyScroll.");
            return;
        }

        var firstRegion = new RelativeSearchRegion(0.5912, 0.1476, 0.1092, 0.1498);
        var secondRegion = new RelativeSearchRegion(0.2844, 0.1206, 0.1644, 0.1548);
        var thirdRegion = new RelativeSearchRegion(0.2554, 0.8108, 0.2846, 0.1892);
        var buyButtonRegion = new RelativeSearchRegion(0.2979, 0.2813, 0.1383, 0.1442);
        var scrollListRegion = new RelativeSearchRegion(0.6023, 0.0985, 0.3977, 0.8942);
        var quantityFiveRegion = new RelativeSearchRegion(0.3024, 0.4424, 0.4118, 0.1892);
        var buy150kRegion = new RelativeSearchRegion(0.4793, 0.5529, 0.2681, 0.2727);
        var steps = new (string Template, RelativeSearchRegion Region, string Name, double? ClickX, double? ClickY)[]
        {
            (mapTemplate, firstRegion, "ícone de mapa", null, null),
            (currencyTemplate, secondRegion, "ícone dourado", null, null),
            (itemTemplate, thirdRegion, "pergaminho de deslocamento rápido", 0.70, null),
            (buyTemplate, buyButtonRegion, "Comprar", null, null)
        };

        foreach (var launcher in Launchers.Where(Configuration.DailyScrollLaunchers!.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(launcher, out var target))
            {
                AppendLog($"DailyScroll — {launcher}: processo MIR4 correspondente não está aberto.");
                continue;
            }

            try
            {
                await DelayRoutineStartAsync($"DailyScroll — {launcher}", cancellationToken);
                AppendLog($"DailyScroll — {launcher}: ajustando para 1280×720.");
                await PrepareRoutineWindowAsync(target, cancellationToken);
                var cleanBackpack = launcher switch
                {
                    "MIR4 Launcher 1" => Configuration.DailyScrollCleanBackpackLauncher1,
                    "MIR4 Launcher 2" => Configuration.DailyScrollCleanBackpackLauncher2,
                    "MIR4 Steam" => Configuration.DailyScrollCleanBackpackSteam,
                    _ => false
                };
                if (cleanBackpack)
                    await CleanDailyScrollBackpackAsync(target, launcher, cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
                await Task.Delay(500, cancellationToken);

                var sequenceCompleted = true;
                foreach (var step in steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // O ícone é fixo; a quantidade do item varia conforme o inventário.
                    Int32Rect? crop = step.Name == "pergaminho de deslocamento rápido"
                        ? new Int32Rect(22, 22, 48, 48)
                        : null;
                    var threshold = step.Name switch
                    {
                        "pergaminho de deslocamento rápido" => 0.70,
                        "Comprar" => 0.70,
                        _ => 0.78
                    };
                    if (step.Name == "Comprar")
                    {
                        AppendLog($"DailyScroll — {launcher}: aguardando 5 segundos antes de procurar o botão Comprar.");
                        await WaitWithCountdownAsync(TimeSpan.FromSeconds(5), $"DailyScroll — {launcher}: aguardando 5 segundos antes de procurar o botão Comprar.", cancellationToken);
                    }
                    bool found;
                    if (step.Name == "Comprar")
                    {
                        found = false;
                        for (var attempt = 0; attempt < 3 && !found; attempt++)
                        {
                            if (attempt > 0)
                            {
                                AppendLog($"DailyScroll — {launcher}: botão Comprar não encontrado; pressionando Z ({attempt}/2) e aguardando 4 segundos.");
                                await _windowClickService.PressKeyAsync(target, 0x5A, "Z", cancellationToken, AppendLog);
                                await WaitWithCountdownAsync(TimeSpan.FromSeconds(4), $"DailyScroll — {launcher}: botão Comprar não encontrado; pressionando Z ({attempt}/2) e aguardando 4 segundos.", cancellationToken);
                            }

                            found = await FindAndClickTemplateAsync(target, step.Template, step.Region, crop,
                                step.Name, threshold, cancellationToken, step.ClickX, step.ClickY,
                                timeoutOverride: TimeSpan.FromSeconds(4), clickCount: 2);
                        }
                    }
                    else
                    {
                        found = await FindAndClickTemplateAsync(target, step.Template, step.Region, crop,
                            step.Name, threshold, cancellationToken, step.ClickX, step.ClickY);
                    }
                    if (!found)
                    {
                        AppendLog($"DailyScroll — {launcher}: sequência interrompida em {step.Name}; os passos seguintes foram ignorados.");
                        sequenceCompleted = false;
                        break;
                    }
                    await Task.Delay(350, cancellationToken);
                }

                if (sequenceCompleted)
                {
                    var targetFound = await FindWhileScrollingAsync(target, targetTemplate, scrollListRegion,
                        "item DailyScroll", 0.70, cancellationToken, new Int32Rect(22, 22, 48, 48), clickRightMargin: 0.20);
                    if (targetFound)
                    {
                        await Task.Delay(350, cancellationToken);
                        AppendLog($"DailyScroll — {launcher}: selecionando quantidade 5 em X={quantityFiveRegion.X:P2}, Y={quantityFiveRegion.Y:P2}, {quantityFiveRegion.Width:P2} × {quantityFiveRegion.Height:P2}.");
                        var fiveSelected = await FindAndClickTemplateAsync(target, fiveTemplate, quantityFiveRegion, null,
                            "quantidade 5", 0.78, cancellationToken);
                        if (fiveSelected)
                        {
                            await Task.Delay(300, cancellationToken);
                            await FindAndClickTemplateAsync(target, buy150kTemplate, buy150kRegion, null,
                                "150K Comprar", 0.78, cancellationToken);
                        }
                        else
                            AppendLog($"DailyScroll — {launcher}: compra interrompida porque a opção 5 não foi encontrada.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog($"DailyScroll — {launcher}: falha — {ex.Message}"); }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        AppendLog($"DailyScroll — {launcher}: encerrando a rotina com três pressionamentos de Esc.");
                        await _windowClickService.PressEscapeTimesAsync(target, 3, CancellationToken.None, AppendLog);
                    }
                    catch (Exception ex) { AppendLog($"DailyScroll — {launcher}: não foi possível enviar Esc — {ex.Message}"); }
                }
            }
        }
    }

    private async Task MineDarkSteel(CancellationToken cancellationToken)
    {
        if (Configuration.DarkSteelMiningLaunchers is not { Count: > 0 })
        {
            AppendLog("Minerar Dark Steel: selecione pelo menos um launcher.");
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DarkSteelMining");
        var locationTemplate = Path.Combine(directory, "dark-steel-location.png");
        var pickaxeTemplate = Path.Combine(directory, "dark-steel-pickaxe.png");
        var steelTemplate = Path.Combine(directory, "dark-steel-ore.png");
        var moveTemplate = Path.Combine(directory, "dark-steel-move.png");
        var moveOverlayTemplate = Path.Combine(directory, "dark-steel-move-overlay.png");
        foreach (var template in new[] { locationTemplate, pickaxeTemplate, steelTemplate, moveTemplate, moveOverlayTemplate })
            if (!File.Exists(template))
                throw new FileNotFoundException("Minerar Dark Steel: imagem de referência ausente.", template);

        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        var steps = new (string Template, RelativeSearchRegion Region, string Name)[]
        {
            (locationTemplate, new(0.6051, 0.1608, 0.0705, 0.1064), "localização da mina"),
            (pickaxeTemplate, new(0.3006, 0.1036, 0.3989, 0.7412), "ícone de mineração"),
            (steelTemplate, new(0.3047, 0.8521, 0.2036, 0.0985), "Dark Steel")
        };

        foreach (var launcher in Launchers.Where(Configuration.DarkSteelMiningLaunchers.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(launcher, out var target))
            {
                AppendLog($"Minerar Dark Steel — {launcher}: janela não encontrada.");
                continue;
            }
            var started = false;
            try
            {
                AppendLog($"Minerar Dark Steel — {launcher}: aguardando 4 segundos antes de iniciar a rotina.");
                await WaitWithCountdownAsync(TimeSpan.FromSeconds(4), $"Minerar Dark Steel — {launcher}: aguardando 4 segundos antes de iniciar a rotina.", cancellationToken);
                AppendLog($"Minerar Dark Steel — PID {target.ProcessId}: iniciando com F10.");
                await PrepareRoutineWindowAsync(target, cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
                started = true;
                await Task.Delay(500, cancellationToken);

                var sequenceCompleted = true;
                foreach (var step in steps)
                {
                    if (!await FindAndClickTemplateAsync(target, step.Template, step.Region, null,
                        step.Name, 0.70, cancellationToken))
                    {
                        AppendLog($"Minerar Dark Steel — PID {target.ProcessId}: sequência interrompida; {step.Name} não encontrado.");
                        sequenceCompleted = false;
                        break;
                    }
                    await Task.Delay(350, cancellationToken);
                }
                if (!sequenceCompleted) continue;

                AppendLog($"Minerar Dark Steel — PID {target.ProcessId}: aguardando 7 segundos antes de abrir F10 e selecionar o destino.");
                await WaitWithCountdownAsync(TimeSpan.FromSeconds(7), $"Minerar Dark Steel — PID {target.ProcessId}: aguardando 7 segundos antes de abrir F10 e selecionar o destino.", cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
                await Task.Delay(500, cancellationToken);
                // Centro da região X=34,71%, Y=44,44%, largura=1,6%, altura=2,84%.
                await _windowClickService.ClickRelativeAsync(target, 0.3551, 0.4586, cancellationToken, AppendLog);
                await Task.Delay(350, cancellationToken);
                var moveRegion = new RelativeSearchRegion(0.3059, 0.3333, 0.0798, 0.1749);
                var moveClicked = await FindAndClickTemplateAsync(target, moveTemplate, moveRegion, null,
                    "Mover", 0.78, cancellationToken, timeoutOverride: TimeSpan.FromSeconds(3), logFailure: false);
                if (!moveClicked)
                {
                    AppendLog($"Minerar Dark Steel — {launcher}: procurando Mover com o marcador sobreposto.");
                    moveClicked = await FindAndClickTemplateAsync(target, moveOverlayTemplate, moveRegion, null,
                        "Mover com marcador", 0.78, cancellationToken, clickAnchorX: 0.5, clickAnchorY: 0.23);
                }
                if (!moveClicked)
                {
                    AppendLog($"Minerar Dark Steel — {launcher}: botão Mover não encontrado; sequência interrompida.");
                    continue;
                }
                AppendLog($"Minerar Dark Steel — {launcher}: aguardando 4 segundos após Mover antes de N.");
                await WaitWithCountdownAsync(TimeSpan.FromSeconds(4), $"Minerar Dark Steel — {launcher}: aguardando 4 segundos após Mover antes de N.", cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x4E, "N", cancellationToken, AppendLog);
                started = false;
                AppendLog($"Minerar Dark Steel — PID {target.ProcessId}: sequência concluída.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog($"Minerar Dark Steel — PID {target.ProcessId}: falha — {ex.Message}"); }
            finally
            {
                if (started && !cancellationToken.IsCancellationRequested)
                {
                    try { await _windowClickService.PressEscapeTimesAsync(target, 4, cancellationToken, AppendLog); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AppendLog($"Minerar Dark Steel — PID {target.ProcessId}: falha ao encerrar com Esc — {ex.Message}"); }
                }
            }
        }
    }

    private async Task DailyTenOfTen(CancellationToken cancellationToken)
    {
        if (Configuration.DailyTenOfTenLaunchers is not { Count: > 0 })
        {
            AppendLog("Diárias 10/10: selecione pelo menos um launcher.");
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DailyTenOfTen");
        var steps = new (string Image, string Name, RelativeSearchRegion Region)[]
        {
            ("01-crafting.png", "ícone de criação", new(0.7570, 0.2090, 0.0829, 0.1572)),
            ("02-create-menu.png", "menu Criar", new(0.6603, 0.3539, 0.0953, 0.0712)),
            ("03-create-material.png", "Criar Material", new(0.2568, 0.0027, 0.1603, 0.0860)),
            ("04-equipment-material.png", "Material de Equipamento", new(0.1407, 0.0813, 0.0995, 0.0860)),
            ("05-rare.png", "Raro", new(0.0026, 0.4841, 0.1023, 0.0933)),
            ("06-create-button.png", "botão Criar", new(0.8109, 0.8723, 0.1658, 0.1204))
        };
        var enchantSteps = new (string Image, string Name, RelativeSearchRegion Region)[]
        {
            ("07-enchant-menu.png", "menu Encantar", new(0.0122, 0.3859, 0.1202, 0.0983)),
            ("08-enchant-button.png", "primeiro botão Encantar", new(0.7625, 0.8428, 0.2280, 0.1474)),
            ("09-enchant-confirm.png", "segundo botão Encantar", new(0.3397, 0.8600, 0.2239, 0.1302)),
            ("10-close.png", "fechar encantamento", new(0.9159, 0.0149, 0.0841, 0.1081)),
            ("11-ok.png", "OK do encantamento", new(0.4945, 0.6094, 0.1589, 0.1204))
        };
        var resourceIconRegion = new RelativeSearchRegion(0.3019, 0.1135, 0.3963, 0.6052);
        const double resourceIconConfidence = 0.70;
        var travelSteps = new (string Image, string Name, RelativeSearchRegion Region)[]
        {
            ("12-map.png", "ícone de localização", new(0.6064, 0.1648, 0.0705, 0.1130)),
            ("13-green-icon.png", "ícone de energia", resourceIconRegion),
            ("14-fast-travel.png", "Deslocamento rápido", new(0.2969, 0.8354, 0.2128, 0.1327))
        };
        var herbSteps = new (string Image, string Name, RelativeSearchRegion Region)[]
        {
            ("15-herb-map.png", "localização para ervas", new(0.6078, 0.1574, 0.0663, 0.1228)),
            ("16-herb-icon.png", "ícone de ervas", resourceIconRegion),
            ("17-herb-travel.png", "Deslocamento rápido para ervas", new(0.2996, 0.8330, 0.2114, 0.1302))
        };
        var miningSteps = new (string Image, string Name, RelativeSearchRegion Region)[]
        {
            ("18-mining-map.png", "localização para minério", new(0.6105, 0.1672, 0.0663, 0.1056)),
            ("19-mining-icon.png", "ícone de mineração", resourceIconRegion),
            ("20-mining-travel.png", "Deslocamento rápido para minério", new(0.2913, 0.8280, 0.2197, 0.1498))
        };
        foreach (var step in steps.Concat(enchantSteps).Concat(travelSteps).Concat(herbSteps).Concat(miningSteps))
            if (!File.Exists(Path.Combine(directory, step.Image)))
                throw new FileNotFoundException($"Diárias 10/10: falta a imagem de {step.Name}.", Path.Combine(directory, step.Image));

        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        foreach (var launcher in Launchers.Where(Configuration.DailyTenOfTenLaunchers.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(launcher, out var target))
            {
                AppendLog($"Diárias 10/10 — {launcher}: janela não encontrada.");
                continue;
            }
            var started = false;
            try
            {
                await DelayRoutineStartAsync($"Diárias 10/10 — {launcher}", cancellationToken);
                await PrepareRoutineWindowAsync(target, cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x78, "F9", cancellationToken, AppendLog);
                started = true;
                await Task.Delay(500, cancellationToken);
                var completed = true;
                foreach (var step in steps)
                {
                    if (!await FindAndClickTemplateAsync(target, Path.Combine(directory, step.Image), step.Region,
                        null, $"10/10: {step.Name}", 0.78, cancellationToken))
                    {
                        AppendLog($"Diárias 10/10 — {launcher}: interrompida em {step.Name}; próximos cliques ignorados.");
                        completed = false;
                        break;
                    }
                    await Task.Delay(500, cancellationToken);
                }
                if (completed)
                {
                    AppendLog($"Diárias 10/10 — {launcher}: criação enviada; voltando com Esc 4× antes de F7.");
                    await _windowClickService.PressEscapeTimesAsync(target, 4, cancellationToken, AppendLog);
                    started = false;
                    await Task.Delay(500, cancellationToken);
                    await _windowClickService.PressKeyAsync(target, 0x76, "F7", cancellationToken, AppendLog);
                    started = true;
                    await Task.Delay(500, cancellationToken);
                    // Centro da primeira região: X=8,55%, Y=23,85%, 11,05% × 16,46%.
                    await _windowClickService.ClickRelativeAsync(target, 0.14075, 0.3208, cancellationToken, AppendLog);
                    await Task.Delay(500, cancellationToken);
                    foreach (var step in enchantSteps)
                    {
                        if (step.Image == "10-close.png")
                        {
                            AppendLog($"Diárias 10/10 — {launcher}: aguardando 3 segundos antes de fechar o encantamento (X).");
                            await WaitWithCountdownAsync(TimeSpan.FromSeconds(3), $"Diárias 10/10 — {launcher}: aguardando 3 segundos antes de fechar o encantamento (X).", cancellationToken);
                        }
                        if (!await FindAndClickTemplateAsync(target, Path.Combine(directory, step.Image), step.Region,
                            null, $"10/10: {step.Name}", 0.78, cancellationToken))
                        {
                            AppendLog($"Diárias 10/10 — {launcher}: encantamento interrompido em {step.Name}; próximos cliques ignorados.");
                            completed = false;
                            break;
                        }
                        await Task.Delay(500, cancellationToken);
                    }
                    if (completed)
                    {
                        AppendLog($"Diárias 10/10 — {launcher}: voltando com Esc 4× antes de F10.");
                        await _windowClickService.PressEscapeTimesAsync(target, 4, cancellationToken, AppendLog);
                        started = false;
                        await Task.Delay(500, cancellationToken);
                        await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
                        started = true;
                        await Task.Delay(500, cancellationToken);
                        foreach (var step in travelSteps)
                        {
                            if (!await FindAndClickTemplateAsync(target, Path.Combine(directory, step.Image), step.Region,
                                null, $"10/10: {step.Name}", step.Image == "13-green-icon.png" ? resourceIconConfidence : 0.78, cancellationToken))
                            {
                                AppendLog($"Diárias 10/10 — {launcher}: deslocamento interrompido em {step.Name}; próximos cliques e N ignorados.");
                                completed = false;
                                break;
                            }
                            // A espera após o último clique é feita abaixo, antes de N.
                            if (step.Image != "14-fast-travel.png") await Task.Delay(500, cancellationToken);
                        }
                        if (completed)
                        {
                            // Não enviar Esc após o deslocamento nem após N.
                            started = false;
                            AppendLog($"Diárias 10/10 — {launcher}: aguardando 10 segundos após Deslocamento rápido antes de Ctrl + 6.");
                            await WaitWithCountdownAsync(TimeSpan.FromSeconds(10), $"Diárias 10/10 — {launcher}: aguardando 10 segundos após Deslocamento rápido antes de Ctrl + 6.", cancellationToken);
                            await _windowClickService.PressCtrlNumberAsync(target, 0x36, cancellationToken, AppendLog);
                            await WaitWithCountdownAsync(TimeSpan.FromSeconds(1), $"Diárias 10/10 — {launcher}: próximo passo — pressionar N.", cancellationToken);
                            await _windowClickService.PressKeyAsync(target, 0x4E, "N", cancellationToken, AppendLog);
                            AppendLog($"Diárias 10/10 — {launcher}: aguardando 1min30s após Ctrl + 6 antes de F10.");
                            await WaitWithCountdownAsync(TimeSpan.FromSeconds(90), $"Diárias 10/10 — {launcher}: aguardando 1min30s após Ctrl + 6 antes de F10.", cancellationToken);
                            await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
                            started = true;
                            await Task.Delay(500, cancellationToken);
                            foreach (var step in herbSteps)
                            {
                                if (!await FindAndClickTemplateAsync(target, Path.Combine(directory, step.Image), step.Region,
                                    null, $"10/10: {step.Name}", step.Image == "16-herb-icon.png" ? resourceIconConfidence : 0.78, cancellationToken))
                                {
                                    AppendLog($"Diárias 10/10 — {launcher}: etapa de ervas interrompida em {step.Name}; próximos cliques e atalhos ignorados.");
                                    completed = false;
                                    break;
                                }
                                if (step.Image != "17-herb-travel.png") await Task.Delay(500, cancellationToken);
                            }
                            if (completed)
                            {
                                started = false;
                                AppendLog($"Diárias 10/10 — {launcher}: aguardando 10 segundos após deslocamento para ervas antes de Ctrl + 5.");
                                await WaitWithCountdownAsync(TimeSpan.FromSeconds(10), $"Diárias 10/10 — {launcher}: aguardando 10 segundos após deslocamento para ervas antes de Ctrl + 5.", cancellationToken);
                                await _windowClickService.PressCtrlNumberAsync(target, 0x35, cancellationToken, AppendLog);
                                await WaitWithCountdownAsync(TimeSpan.FromSeconds(1), $"Diárias 10/10 — {launcher}: próximo passo — pressionar N.", cancellationToken);
                                await _windowClickService.PressKeyAsync(target, 0x4E, "N", cancellationToken, AppendLog);
                                AppendLog($"Diárias 10/10 — {launcher}: aguardando 3 minutos após Ctrl + 5 antes de F10.");
                                await WaitWithCountdownAsync(TimeSpan.FromMinutes(3), $"Diárias 10/10 — {launcher}: aguardando 3 minutos após Ctrl + 5 antes de F10.", cancellationToken);
                                await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
                                started = true;
                                await Task.Delay(500, cancellationToken);
                                foreach (var step in miningSteps)
                                {
                                    if (!await FindAndClickTemplateAsync(target, Path.Combine(directory, step.Image), step.Region,
                        null, $"10/10: {step.Name}", step.Image == "19-mining-icon.png" ? resourceIconConfidence : 0.78, cancellationToken,
                        matchColor: step.Image == "19-mining-icon.png"))
                                    {
                                        AppendLog($"Diárias 10/10 — {launcher}: mineração interrompida em {step.Name}; próximos cliques e atalhos ignorados.");
                                        completed = false;
                                        break;
                                    }
                                    if (step.Image != "20-mining-travel.png") await Task.Delay(500, cancellationToken);
                                }
                                if (completed)
                                {
                                    started = false;
                                    AppendLog($"Diárias 10/10 — {launcher}: aguardando 10 segundos após deslocamento para minério antes de Ctrl + 3.");
                                    await WaitWithCountdownAsync(TimeSpan.FromSeconds(10), $"Diárias 10/10 — {launcher}: aguardando 10 segundos após deslocamento para minério antes de Ctrl + 3.", cancellationToken);
                                    await _windowClickService.PressCtrlNumberAsync(target, 0x33, cancellationToken, AppendLog);
                                    await WaitWithCountdownAsync(TimeSpan.FromSeconds(1), $"Diárias 10/10 — {launcher}: próximo passo — pressionar N.", cancellationToken);
                                    await _windowClickService.PressKeyAsync(target, 0x4E, "N", cancellationToken, AppendLog);
                                    AppendLog($"Diárias 10/10 — {launcher}: sequência enviada até Ctrl + 3. A conclusão das 10 missões ainda não é verificada.");
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog($"Diárias 10/10 — {launcher}: {ex.Message}"); }
            finally
            {
                if (started && !cancellationToken.IsCancellationRequested)
                {
                    try { await _windowClickService.PressEscapeTimesAsync(target, 4, cancellationToken, AppendLog); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AppendLog($"Diárias 10/10 — {launcher}: falha ao encerrar com Esc: {ex.Message}"); }
                }
            }
        }
    }

    private async Task DailyFavoriteMissions(CancellationToken cancellationToken, string? onlyLauncher = null)
    {
        if (onlyLauncher is null && !Configuration.DailyMissionLaunchersToRun.Any())
        {
            AppendLog("Missões favoritas: marque ao menos um launcher para executar essa rotina.");
            return;
        }

        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        var templatesDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates");
        var fieldTemplate = Path.Combine(templatesDirectory, "daily-favorite-field-name.png");
        var acceptTemplate = Path.Combine(templatesDirectory, "daily-favorite-accept.png");
        var acceptExitTemplate = Path.Combine(templatesDirectory, "daily-favorite-accept-exit.png");
        var autoTemplate = Path.Combine(templatesDirectory, "daily-favorite-auto.png");
        var checkTemplate = Path.Combine(templatesDirectory, "daily-favorite-check.png");
        var startTemplate = Path.Combine(templatesDirectory, "daily-favorite-start.png");
        var travelTemplate = Path.Combine(templatesDirectory, "daily-favorite-travel.png");
        var travelItemTemplate = Path.Combine(templatesDirectory, "daily-favorite-travel-item.png");
        var energyTemplate = Path.Combine(templatesDirectory, "daily-favorite-energy.png");
        if (new[] { acceptTemplate, acceptExitTemplate, autoTemplate, checkTemplate, startTemplate,
                travelTemplate, travelItemTemplate, energyTemplate }
            .Any(path => !File.Exists(path)))
        {
            AppendLog("Missões favoritas: não foi encontrada uma das imagens em Assets\\Templates.");
            return;
        }

        var mapRegion = new RelativeSearchRegion(0.0026, 0.0567, 0.6522, 0.2211);
        var acceptRegion = new RelativeSearchRegion(0.8247, 0.2581, 0.1753, 0.7419);
        var acceptExitRegion = new RelativeSearchRegion(0.4973, 0.5579, 0.1423, 0.1229);
        var autoRegion = new RelativeSearchRegion(0.6423, 0.1427, 0.2612, 0.1597);
        var checkRegion = new RelativeSearchRegion(0.0896, 0.1304, 0.1175, 0.1400);
        var startRegion = new RelativeSearchRegion(0.7197, 0.7666, 0.2017, 0.1769);
        var travelRegion = new RelativeSearchRegion(0.6175, 0.4203, 0.2985, 0.2850);
        var travelItemRegion = new RelativeSearchRegion(0.3632, 0.6610, 0.3109, 0.1572);
        var energyRegion = new RelativeSearchRegion(0.1615, 0.6585, 0.0912, 0.1646);

        var selectedLaunchers = Launchers.Where(launcher => onlyLauncher is not null
            ? launcher == onlyLauncher
            : Configuration.DailyMissionLaunchersToRun.Contains(launcher));
        foreach (var launcher in selectedLaunchers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(launcher, out var target))
            {
                AppendLog($"Missões favoritas — {launcher}: processo MIR4 correspondente não está aberto.");
                continue;
            }

            try
            {
                await DelayRoutineStartAsync($"Missões favoritas — {launcher}", cancellationToken);
                AppendLog($"Missões favoritas — {launcher}: ajustando a área para 1280×720 e pressionando F6.");
                await PrepareRoutineWindowAsync(target, cancellationToken);
                await _windowClickService.PressKeyAsync(target, 0x75, "F6", cancellationToken, AppendLog);
                await Task.Delay(350, cancellationToken);
                // A meta é conferida pelo monitor e também quando nenhuma missão é aceita.
                await ClaimDailyMissionRewardsAsync(target, launcher, cancellationToken);
                var selectedMap = GetDailyMap(launcher);
                var mapTemplate = selectedMap switch
                {
                    "Campo" => fieldTemplate,
                    "Vale Oculto" => Path.Combine(templatesDirectory, "daily-favorite-map-hidden-valley.png"),
                    "Miragem do Navio" => Path.Combine(templatesDirectory, "daily-favorite-map-ship-mirage.png"),
                    _ => throw new InvalidOperationException($"Mapa desconhecido: {selectedMap}.")
                };
                if (!File.Exists(mapTemplate))
                {
                    AppendLog($"Missões favoritas — {launcher}: falta a imagem de referência do mapa '{selectedMap}'.");
                    continue;
                }
                AppendLog($"Missões favoritas — {launcher}: mapa {selectedMap} selecionado.");
                // Compara somente o nome; exclui o contador variável, inclusive a linha (3) da Miragem.
                Int32Rect? mapCrop = selectedMap switch
                {
                    "Campo" => null,
                    "Vale Oculto" => new Int32Rect(13, 13, 110, 23),
                    "Miragem do Navio" => new Int32Rect(25, 7, 109, 17),
                    _ => throw new InvalidOperationException($"Mapa desconhecido: {selectedMap}.")
                };
                var mapSelected = await FindAndClickTemplateAsync(target, mapTemplate, mapRegion,
                    mapCrop,
                    selectedMap, 0.75, cancellationToken);
                if (!mapSelected) continue;

                var acceptedCount = 0;
                var emptyScrolls = 0;
                var totalScrolls = 0;
                const int maxEmptyScrolls = 5;
                const int maxMissionScrolls = 60;
                while (emptyScrolls < maxEmptyScrolls && totalScrolls < maxMissionScrolls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(150, cancellationToken);
                    var attemptName = $"Aceitar missão ({acceptedCount + 1})";
                    var accepted = await FindAndClickTemplateAsync(target, acceptTemplate, acceptRegion,
                        new Int32Rect(12, 17, 112, 34),
                        attemptName, 0.72, cancellationToken, timeoutOverride: TimeSpan.FromSeconds(0.8),
                        logFailure: false);
                    if (!accepted)
                    {
                        emptyScrolls++;
                        if (emptyScrolls < maxEmptyScrolls)
                        {
                            await _windowClickService.ScrollDownAtRelativeAsync(target,
                                acceptRegion.X + acceptRegion.Width / 2, acceptRegion.Y + acceptRegion.Height / 2,
                                cancellationToken, notches: 5);
                            totalScrolls++;
                            await Task.Delay(100, cancellationToken);
                        }
                        continue;
                    }

                    acceptedCount++;
                    emptyScrolls = 0;
                    AppendLog($"Missões favoritas — {launcher}: clique {acceptedCount} em Aceitar missão concluído.");
                    await Task.Delay(250, cancellationToken);
                    var dismissed = await FindAndClickTemplateAsync(target, acceptExitTemplate, acceptExitRegion,
                        null, "Sair após aceitar missão", 0.78, cancellationToken,
                        timeoutOverride: TimeSpan.FromSeconds(2), logFailure: false);
                    if (dismissed)
                        await Task.Delay(250, cancellationToken);
                }
                AppendLog($"Missões favoritas — {launcher}: busca encerrada após {acceptedCount} missão(ões) aceita(s) e {totalScrolls} rolagem(ns)." +
                    (totalScrolls >= maxMissionScrolls ? " Limite de rolagens atingido." : " Nenhum botão foi detectado após rolar a lista."));

                if (acceptedCount == 0)
                {
                    if (Configuration.CompletionCheckFor(launcher).Enabled)
                    {
                        AppendLog($"Missões favoritas — {launcher}: nenhuma missão aceita; conferindo a meta antes da sequência alternativa.");
                        await _windowClickService.PressEscapeTimesAsync(target, 2, cancellationToken, AppendLog);
                        await _windowClickService.PressKeyAsync(target, 0x75, "F6", cancellationToken, AppendLog);
                        await Task.Delay(350, cancellationToken);
                        var result = await CheckOpenDailyMissionCountAsync(target, launcher, cancellationToken);
                        if (result != MissionCheckResult.BelowTarget)
                            continue;
                    }
                    await RecoverWhenNoMissionAcceptedAsync(target, launcher, templatesDirectory,
                        travelItemTemplate, cancellationToken);
                    continue;
                }

                await Task.Delay(300, cancellationToken);
                var favoriteSteps = new (string Template, RelativeSearchRegion Region, string Name)[]
                {
                    (autoTemplate, autoRegion, "Jogar autom."),
                    (checkTemplate, checkRegion, "confirmação da missão"),
                    (startTemplate, startRegion, "Iniciar"),
                    (travelTemplate, travelRegion, "Deslocamento rápido"),
                    (travelItemTemplate, travelItemRegion, "item Deslocamento rápido")
                };
                var favoriteStepsCompleted = true;
                foreach (var step in favoriteSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Int32Rect? crop = step.Name == "item Deslocamento rápido"
                        ? new Int32Rect(7, 9, 32, 34)
                        : null;
                    var threshold = step.Name switch
                    {
                        "Deslocamento rápido" => 0.60,
                        "Jogar autom." => 0.72,
                        "item Deslocamento rápido" => 0.70,
                        _ => 0.78
                    };
                    var completed = await FindAndClickTemplateAsync(target, step.Template, step.Region, crop,
                        step.Name, threshold, cancellationToken);
                    if (!completed)
                    {
                        if (step.Name.Contains("Deslocamento rápido", StringComparison.Ordinal))
                        {
                            AppendLog($"Missões favoritas — {launcher}: {step.Name} não encontrado; continuando para a próxima etapa.");
                            await Task.Delay(300, cancellationToken);
                            continue;
                        }
                        AppendLog($"Missões favoritas — {launcher}: sequência interrompida em {step.Name}.");
                        favoriteStepsCompleted = false;
                        break;
                    }
                    await Task.Delay(300, cancellationToken);
                }
                if (favoriteStepsCompleted)
                {
                    AppendLog($"Missões favoritas — {launcher}: aguardando 7 segundos antes de abrir Poupança de energia com F9.");
                    await WaitWithCountdownAsync(TimeSpan.FromSeconds(7), $"Missões favoritas — {launcher}: aguardando 7 segundos antes de abrir Poupança de energia com F9.", cancellationToken);
                    await _windowClickService.PressKeyAsync(target, 0x78, "F9", cancellationToken, AppendLog);
                    await Task.Delay(350, cancellationToken);
                    var energySelected = await FindAndClickTemplateAsync(target, energyTemplate, energyRegion, null,
                        "Poupança de energia", 0.78, cancellationToken);
                    if (!energySelected)
                        AppendLog($"Missões favoritas — {launcher}: não foi possível selecionar Poupança de energia.");
                    else
                    {
                        AppendLog($"Missões favoritas — {launcher}: Poupança de energia ativada; mantendo o tamanho atual da janela.");
                        await _windowClickService.ActivateAsync(target, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog($"Missões favoritas — {launcher}: falha — {ex.Message}"); }
        }

    }

    private enum MissionCheckResult { Unknown, BelowTarget, Complete }

    private void UpdateMissionCountStatus(string launcher, int? count, string status)
    {
        var limit = Configuration.CompletionCheckFor(launcher).Limit;
        _missionCountStatuses[launcher] = $"{launcher}: quantidade lida: {count?.ToString() ?? "não confirmada"} / 30 · Meta: {limit} · {status}";
        MissionCountStatusText.Text = string.Join(Environment.NewLine, _missionCountStatuses.Values);
    }

    private async Task<int?> ReadStableMissionCountAsync(PreviewWindow target, string launcher, CancellationToken token)
    {
        var region = new RelativeSearchRegion(0.3501, 0.1769, 0.1247, 0.0920);
        int? previous = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var frame = await _windowCaptureService.CaptureAsync(target, token);
            var count = await _missionCountReader.ReadAsync(frame, region, token);
            ScreenshotImage.Source = frame;
            MissionCountImage.Source = new CroppedBitmap(frame, new Int32Rect(
                (int)(frame.PixelWidth * region.X), (int)(frame.PixelHeight * region.Y),
                (int)(frame.PixelWidth * region.Width), (int)(frame.PixelHeight * region.Height)));
            UpdateMissionCountStatus(launcher, count, count.HasValue && count == previous
                ? (count.Value >= Configuration.CompletionCheckFor(launcher).Limit ? "meta atingida" : "abaixo da meta")
                : "confirmando leitura");
            DetectionStatusText.Text = count.HasValue ? $"MISSÕES / {count}/30" : "MISSÕES / Aguardando leitura da contagem";
            if (count.HasValue && count == previous) return count;
            previous = count;
            await Task.Delay(250, token);
        }
        UpdateMissionCountStatus(launcher, null, "falha na leitura; meta não confirmada");
        return null;
    }

    private async Task ClaimDailyMissionRewardsAsync(PreviewWindow target, string launcher,
        CancellationToken token)
    {
        AppendLog($"Missões diárias — {launcher}: procurando Obter tudo após abrir F6.");
        var claimTemplate = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "daily-favorite-claim-all.png");
        var claimed = await FindAndClickTemplateAsync(target, claimTemplate,
            new RelativeSearchRegion(0.8457, 0.1726, 0.1463, 0.1182), null, "Obter tudo", 0.78, token,
            timeoutOverride: TimeSpan.FromSeconds(3), logFailure: false);
        if (claimed)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            await _windowClickService.PressEscapeTimesAsync(target, 1, token, AppendLog);
        }
        else
            AppendLog($"Missões diárias — {launcher}: Obter tudo não encontrado; continuando a rotina.");
    }

    private async Task<MissionCheckResult> CheckOpenDailyMissionCountAsync(PreviewWindow target, string launcher,
        CancellationToken token)
    {
        var limit = Configuration.CompletionCheckFor(launcher).Limit;
        await ClaimDailyMissionRewardsAsync(target, launcher, token);
        var count = await ReadStableMissionCountAsync(target, launcher, token);
        if (!count.HasValue)
        {
            AppendLog($"Conferência — {launcher}: leitura falhou; reabrindo F6 para tentar novamente agora. Meta: {limit}.");
            await _windowClickService.PressEscapeTimesAsync(target, 2, token, AppendLog);
            await _windowClickService.PressKeyAsync(target, 0x75, "F6", token, AppendLog);
            await Task.Delay(1000, token);
            count = await ReadStableMissionCountAsync(target, launcher, token);
        }
        AppendLog($"Conferência — {launcher}: quantidade lida: {count?.ToString() ?? "não confirmada"}/30; meta configurada: {limit}.");
        await _windowClickService.PressEscapeTimesAsync(target, 2, token, AppendLog);
        await _windowClickService.PressKeyAsync(target, 0x78, "F9", token, AppendLog);
        await Task.Delay(350, token);
        var energyTemplate = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "daily-favorite-check-energy.png");
        await FindAndClickTemplateAsync(target, energyTemplate,
            new RelativeSearchRegion(0.1676, 0.6714, 0.0811, 0.1158), null,
            "Poupança de energia após conferência", 0.78, token);
        if (!count.HasValue)
        {
            AppendLog($"Conferência — {launcher}: contagem não confirmada; nova tentativa em 40 minutos.");
            return MissionCheckResult.Unknown;
        }
        if (count.Value >= limit)
        {
            _completedMissionLaunchers.Add(launcher);
            AppendLog($"Conferência — {launcher}: meta atingida ({count}/30 ≥ {limit}); conferência encerrada.");
            return MissionCheckResult.Complete;
        }
        AppendLog($"Conferência — {launcher}: {count}/30; ainda não atingiu {limit}. Nova conferência em 40 minutos.");
        return MissionCheckResult.BelowTarget;
    }

    private async Task MonitorDailyMissionCompletionAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromMinutes(40);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var nextCheck = interval;
        while (true)
        {
            var pending = Launchers.Where(launcher => Configuration.CompletionCheckFor(launcher).Enabled &&
                !_completedMissionLaunchers.Contains(launcher)).ToArray();
            if (pending.Length == 0) return;
            StatusText.Text = "Aguardando conferência";
            AppendLog($"Conferência: próxima rodada em 40 minutos para {string.Join(", ", pending)}. Use Stop para cancelar.");
            var delay = nextCheck - timer.Elapsed;
            if (delay > TimeSpan.Zero) await WaitWithCountdownAsync(delay, $"Próxima conferência das missões — {string.Join(", ", pending)}.", token);
            nextCheck = timer.Elapsed + interval;
            StatusText.Text = "Conferindo missões";
            foreach (var launcher in pending)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Abaixo da meta, fecha a conferência e reinicia as missões apenas deste launcher.
                    var windows = _windowCaptureService.ListWindows();
                    var target = LauncherWindowResolver.Resolve(windows).GetValueOrDefault(launcher);
                    if (target is null)
                    {
                        AppendLog($"Conferência — {launcher}: janela não encontrada; nova tentativa na próxima rodada.");
                        continue;
                    }
                    await PrepareRoutineWindowAsync(target, token);
                    await _windowClickService.PressKeyAsync(target, 0x75, "F6", token, AppendLog);
                    await Task.Delay(350, token);
                    var result = await CheckOpenDailyMissionCountAsync(target, launcher, token);
                    if (result == MissionCheckResult.BelowTarget)
                    {
                        AppendLog($"Conferência — {launcher}: reiniciando as missões diárias para alcançar a meta.");
                        await DailyFavoriteMissions(token, launcher);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppendLog($"Conferência — {launcher}: {ex.Message}. Nova tentativa na próxima rodada."); }
            }
        }
    }

    private async Task RecoverWhenNoMissionAcceptedAsync(PreviewWindow target, string launcher,
        string templatesDirectory, string travelItemTemplate, CancellationToken cancellationToken)
    {
        AppendLog($"Missões favoritas — {launcher}: nenhum clique em Aceitar missão; iniciando sequência alternativa.");
        await _windowClickService.PressEscapeTimesAsync(target, 3, cancellationToken, AppendLog);
        await _windowClickService.PressKeyAsync(target, 0x79, "F10", cancellationToken, AppendLog);
        await Task.Delay(350, cancellationToken);

        var mapIconTemplate = Path.Combine(templatesDirectory, "daily-favorite-map-icon.png");
        var pickaxeTemplate = Path.Combine(templatesDirectory, "daily-favorite-pickaxe.png");
        var energyIconTemplate = Path.Combine(templatesDirectory, "daily-favorite-energy-icon.png");
        var steps = new (string Template, RelativeSearchRegion Region, Int32Rect? Crop, string Name)[]
        {
            (mapIconTemplate, new(0.5940, 0.1746, 0.0940, 0.1007), null, "ícone do mapa"),
            (pickaxeTemplate, new(0.2955, 0.1034, 0.4035, 0.7271), null, "ícone de picareta"),
            (travelItemTemplate, new(0.2969, 0.8330, 0.4090, 0.1449), new Int32Rect(7, 9, 32, 34), "item Deslocamento rápido")
        };
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = await FindAndClickTemplateAsync(target, step.Template, step.Region, step.Crop,
                step.Name, 0.72, cancellationToken);
            if (!found)
                throw new InvalidOperationException($"não foi possível localizar {step.Name} na sequência alternativa.");
            await Task.Delay(350, cancellationToken);
        }

        AppendLog($"Missões favoritas — {launcher}: aguardando 7 segundos antes de pressionar N e F9.");
        await WaitWithCountdownAsync(TimeSpan.FromSeconds(7), $"Missões favoritas — {launcher}: aguardando 7 segundos antes de pressionar N e F9.", cancellationToken);
        await _windowClickService.PressKeyAsync(target, 0x4E, "N", cancellationToken, AppendLog);
        await Task.Delay(350, cancellationToken);
        await _windowClickService.PressKeyAsync(target, 0x78, "F9", cancellationToken, AppendLog);
        await Task.Delay(350, cancellationToken);
        var energyRegion = new RelativeSearchRegion(0.1490, 0.6585, 0.1133, 0.1695);
        var energySelected = await FindAndClickTemplateAsync(target, energyIconTemplate, energyRegion, null,
            "ícone de Poupança de energia", 0.72, cancellationToken);
        if (!energySelected)
            throw new InvalidOperationException("não foi possível localizar o ícone de Poupança de energia.");
        AppendLog($"Missões favoritas — {launcher}: Poupança de energia ativada; mantendo o tamanho atual da janela.");
        await _windowClickService.ActivateAsync(target, cancellationToken);
    }

    private async Task DailyFavoriteRaid(CancellationToken cancellationToken)
    {
        if (!int.TryParse(Configuration.Normal.RepeatCountText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var raidCount) || raidCount is < 1 or > 100)
        {
            AppendLog("DailyFavoriteRaid: informe entre 1 e 100 repetições na configuração da Raide Normal.");
            return;
        }

        var launcherGroup = Configuration.NormalLaunchers;
        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        if (!targets.TryGetValue(launcherGroup.Starter, out var starter))
        {
            AppendLog($"DailyFavoriteRaid: starter {launcherGroup.Starter} não está aberto.");
            return;
        }

        var guests = new[] { launcherGroup.Guest1, launcherGroup.Guest2 }
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "Não utilizar" &&
                !string.Equals(name, launcherGroup.Starter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var guest in guests)
        {
            if (targets.ContainsKey(guest))
                AppendLog($"DailyFavoriteRaid: {guest} está aberto e será incluído pelo comando Convidar todos.");
            else
                AppendLog($"DailyFavoriteRaid: launcher convidado {guest} não está aberto; continuando com os que estiverem disponíveis.");
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DailyRaid");
        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ícone Raide"] = Path.Combine(directory, "daily-raid-icon-only.png"),
            ["opção Raide"] = Path.Combine(directory, "daily-raid-label.png"),
            ["Criar um Raide (menu)"] = Path.Combine(directory, "daily-raid-create-banner.png"),
            ["Privado"] = Path.Combine(directory, "daily-raid-private.png"),
            ["campo da senha"] = Path.Combine(directory, "daily-raid-password.png"),
            ["botão 2"] = Path.Combine(directory, "daily-raid-two.png"),
            ["Entrada completa"] = Path.Combine(directory, "daily-raid-entry-complete.png"),
            ["Criar um Raide"] = Path.Combine(directory, "daily-raid-create.png"),
            ["adicionar convidados"] = Path.Combine(directory, "daily-raid-add.png"),
            ["Convidar todos"] = Path.Combine(directory, "daily-raid-invite-all.png"),
            ["Aceitar convite"] = Path.Combine(directory, "daily-raid-accept.png"),
            ["Entrar na raide"] = Path.Combine(directory, "daily-raid-enter.png"),
            ["Iniciar Raide"] = Path.Combine(directory, "daily-raid-start.png"),
            ["OK"] = Path.Combine(directory, "daily-raid-ok.png")
        };
        if (templates.Values.Any(path => !File.Exists(path)))
        {
            var missingTemplates = templates.Values.Where(path => !File.Exists(path))
                .Select(Path.GetFileName).ToArray();
            AppendLog($"DailyFavoriteRaid: faltam imagens em Assets\\Templates\\DailyRaid: {string.Join(", ", missingTemplates)}.");
            return;
        }

        var regions = new Dictionary<string, RelativeSearchRegion>(StringComparer.Ordinal)
        {
            ["ícone Raide"] = new(0.6796, 0.5062, 0.3164, 0.1695),
            ["opção Raide"] = new(0.6644, 0.6561, 0.3356, 0.1179),
            ["Criar um Raide (menu)"] = new(0.5014, 0.8428, 0.4986, 0.1548),
            ["Privado"] = new(0.2430, 0.4645, 0.5223, 0.1597),
            ["campo da senha"] = new(0.5152, 0.5333, 0.2418, 0.0712),
            ["botão 2"] = new(0.3908, 0.2680, 0.2183, 0.3881),
            ["Entrada completa"] = new(0.3784, 0.7740, 0.2432, 0.1474),
            ["Criar um Raide"] = new(0.2458, 0.7543, 0.5113, 0.1400),
            ["adicionar convidados"] = new(0.1366, 0.3245, 0.3620, 0.1277),
            ["Convidar todos"] = new(0.4572, 0.1844, 0.2197, 0.1155),
            ["Aceitar convite"] = new(0.0509, 0.4252, 0.1893, 0.0933),
            ["Entrar na raide"] = new(0.3439, 0.5750, 0.3454, 0.1548),
            ["Iniciar Raide"] = new(0.5111, 0.7740, 0.3800, 0.1548),
            ["OK"] = new(0.2513, 0.8158, 0.5196, 0.1793)
        };

        AppendLog($"DailyFavoriteRaid: criando {raidCount} raide(s) pelo starter {launcherGroup.Starter}.");
        try
        {
            await DelayRoutineStartAsync($"DailyFavoriteRaid — {launcherGroup.Starter}", cancellationToken);
            await PrepareRoutineWindowAsync(starter, cancellationToken);
            for (var raid = 1; raid <= raidCount; raid++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendLog($"DailyFavoriteRaid — {launcherGroup.Starter}: iniciando raide {raid}/{raidCount}.");
                if (raid > 1)
                    await DelayRoutineStartAsync($"DailyFavoriteRaid — raide {raid}/{raidCount}", cancellationToken);
                AppendLog($"DailyFavoriteRaid — raide {raid}/{raidCount}: aguardando 2 segundos antes de F9.");
                await WaitWithCountdownAsync(TimeSpan.FromSeconds(2), $"DailyFavoriteRaid — raide {raid}/{raidCount}: aguardando 2 segundos antes de F9.", cancellationToken);
                await _windowClickService.PressKeyAsync(starter, 0x78, "F9", cancellationToken, AppendLog);
                await Task.Delay(350, cancellationToken);
                var steps = new[] { "ícone Raide", "opção Raide", "Criar um Raide (menu)", "Privado", "campo da senha", "botão 2", "Entrada completa", "Criar um Raide", "adicionar convidados", "Convidar todos" };
                foreach (var name in steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var clicks = name == "botão 2" ? 4 : 1;
                    var threshold = name switch
                    {
                        "botão 2" => 0.72,
                        "opção Raide" => 0.72,
                        "adicionar convidados" => 0.50,
                        _ => 0.78
                    };
                    var found = await FindAndClickTemplateAsync(starter, templates[name], regions[name], null,
                        name, threshold, cancellationToken, clickCount: clicks);
                    if (!found)
                        throw new InvalidOperationException($"sequência interrompida em {name} (raide {raid}/{raidCount}).");
                    if (name == "campo da senha")
                        await _windowClickService.ReplaceTextAsync(starter, "0000", cancellationToken, AppendLog);
                    await Task.Delay(350, cancellationToken);
                }
                AppendLog($"DailyFavoriteRaid — raide {raid}/{raidCount}: convites enviados pelo comando Convidar todos.");

                foreach (var guestName in guests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!targets.TryGetValue(guestName, out var guest)) continue;
                    AppendLog($"DailyFavoriteRaid — raide {raid}/{raidCount}: ativando launcher convidado {guestName}.");
                    await PrepareRoutineWindowAsync(guest, cancellationToken);
                    await _windowClickService.PressCtrlNumberAsync(guest, 0x31, cancellationToken, AppendLog);
                    foreach (var guestStep in new[] { "Aceitar convite", "Entrar na raide" })
                    {
                        var guestStepFound = await FindAndClickTemplateAsync(guest, templates[guestStep], regions[guestStep],
                            null, guestStep, 0.78, cancellationToken);
                        if (!guestStepFound)
                            throw new InvalidOperationException($"{guestName}: não foi possível concluir {guestStep}.");
                        await Task.Delay(350, cancellationToken);
                    }
                }

                await PrepareRoutineWindowAsync(starter, cancellationToken);
                AppendLog("DailyFavoriteRaid: enviando Esc uma vez para fechar a janela da raide.");
                await _windowClickService.PressKeyAsync(starter, 0x1B, "Esc", cancellationToken, AppendLog);
                await Task.Delay(350, cancellationToken);

                var started = await FindAndClickTemplateAsync(starter, templates["Iniciar Raide"],
                    regions["Iniciar Raide"], null, "Iniciar Raide", 0.78, cancellationToken);
                if (!started)
                    throw new InvalidOperationException($"não foi possível iniciar a raide {raid}/{raidCount} no starter.");
                AppendLog($"DailyFavoriteRaid — raide {raid}/{raidCount}: iniciada pelo starter {launcherGroup.Starter}.");
                await WaitAndClickTemplateIndefinitelyAsync(starter, templates["OK"], regions["OK"],
                    cancellationToken);

                foreach (var guestName in guests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!targets.TryGetValue(guestName, out var guest)) continue;
                    AppendLog($"DailyFavoriteRaid — OK do starter clicado; mudando para {guestName} para procurar o OK na mesma região.");
                    await PrepareRoutineWindowAsync(guest, cancellationToken, dismissOk: false);
                    await WaitAndClickTemplateIndefinitelyAsync(guest, templates["OK"], regions["OK"],
                        cancellationToken);
                    AppendLog($"DailyFavoriteRaid — OK clicado em {guestName}.");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppendLog($"DailyFavoriteRaid — {launcherGroup.Starter}: {ex.Message}"); }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    AppendLog($"DailyFavoriteRaid — {launcherGroup.Starter}: rotina encerrada.");
                }
                catch (Exception ex) { AppendLog($"DailyFavoriteRaid — falha ao enviar Esc 2x: {ex.Message}"); }
            }
        }
    }

    private async Task DailyRaidBoss(CancellationToken cancellationToken)
    {
        if (!int.TryParse(Configuration.Boss.RepeatCountText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var raidCount) || raidCount is < 1 or > 100)
        {
            AppendLog("DailyRaidBoss: informe entre 1 e 100 repetições na configuração de Raids Boss.");
            return;
        }

        var launcherGroup = Configuration.BossLaunchers;
        var windows = _windowCaptureService.ListWindows();
        var targets = LauncherWindowResolver.Resolve(windows);

        if (!targets.TryGetValue(launcherGroup.Starter, out var starter))
        {
            AppendLog($"DailyRaidBoss: starter {launcherGroup.Starter} não está aberto.");
            return;
        }

        var guests = new[] { launcherGroup.Guest1, launcherGroup.Guest2 }
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "Não utilizar" &&
                !string.Equals(name, launcherGroup.Starter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "DailyRaid");
        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ícone Raide"] = Path.Combine(directory, "daily-raid-icon-only.png"),
            ["Raide de Boss"] = Path.Combine(directory, "daily-raid-boss-label.png"),
            ["Criar um Raide (menu)"] = Path.Combine(directory, "daily-raid-create-banner.png"),
            ["Criar um Raide"] = Path.Combine(directory, "daily-raid-boss-create.png"),
            ["adicionar convidados"] = Path.Combine(directory, "daily-raid-add.png"),
            ["Convidar todos"] = Path.Combine(directory, "daily-raid-invite-all.png"),
            ["Aceitar convite"] = Path.Combine(directory, "daily-raid-accept.png"),
            ["Entrar na raide"] = Path.Combine(directory, "daily-raid-enter.png"),
            ["OK"] = Path.Combine(directory, "daily-raid-ok.png")
        };
        if (templates.Values.Any(path => !File.Exists(path)))
        {
            AppendLog("DailyRaidBoss: faltam imagens em Assets\\Templates.");
            return;
        }

        var regions = new Dictionary<string, RelativeSearchRegion>(StringComparer.Ordinal)
        {
            ["ícone Raide"] = new(0.6796, 0.5062, 0.3164, 0.1695),
            ["Raide de Boss"] = new(0.6644, 0.6561, 0.3356, 0.1179),
            ["Criar um Raide (menu)"] = new(0.5014, 0.8428, 0.4986, 0.1548),
            ["Criar um Raide"] = new(0.2458, 0.7543, 0.5113, 0.1400),
            ["adicionar convidados"] = new(0.1366, 0.3245, 0.3620, 0.1277),
            ["Convidar todos"] = new(0.4572, 0.1844, 0.2197, 0.1155),
            ["Aceitar convite"] = new(0.0509, 0.4252, 0.1893, 0.0933),
            ["Entrar na raide"] = new(0.3439, 0.5750, 0.3454, 0.1548),
            ["OK"] = new(0.2513, 0.8158, 0.5196, 0.1793)
        };

        AppendLog($"DailyRaidBoss: criando {raidCount} raide(s) pelo starter {launcherGroup.Starter}.");
        try
        {
            await DelayRoutineStartAsync($"DailyRaidBoss — {launcherGroup.Starter}", cancellationToken);
            await PrepareRoutineWindowAsync(starter, cancellationToken);
            for (var raid = 1; raid <= raidCount; raid++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendLog($"DailyRaidBoss — {launcherGroup.Starter}: iniciando raide {raid}/{raidCount}.");
                if (raid > 1)
                    await DelayRoutineStartAsync($"DailyRaidBoss — raide {raid}/{raidCount}", cancellationToken);
                AppendLog($"DailyRaidBoss — raide {raid}/{raidCount}: aguardando 2 segundos antes de F9.");
                await WaitWithCountdownAsync(TimeSpan.FromSeconds(2), $"DailyRaidBoss — raide {raid}/{raidCount}: aguardando 2 segundos antes de F9.", cancellationToken);
                await _windowClickService.PressKeyAsync(starter, 0x78, "F9", cancellationToken, AppendLog);
                await Task.Delay(350, cancellationToken);
                foreach (var name in new[] { "ícone Raide", "Raide de Boss", "Criar um Raide (menu)", "Criar um Raide", "adicionar convidados", "Convidar todos" })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var threshold = name switch
                    {
                        "adicionar convidados" => 0.50,
                        "Raide de Boss" => 0.72,
                        _ => 0.78
                    };
                    var found = await FindAndClickTemplateAsync(starter, templates[name], regions[name], null,
                        name, threshold, cancellationToken);
                    if (!found)
                        throw new InvalidOperationException($"sequência interrompida em {name} (raide {raid}/{raidCount}).");
                    await Task.Delay(350, cancellationToken);
                }

                foreach (var guestName in guests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!targets.TryGetValue(guestName, out var guest))
                    {
                        AppendLog($"DailyRaidBoss — launcher convidado {guestName} não está aberto; pulando esse launcher.");
                        continue;
                    }

                    AppendLog($"DailyRaidBoss — raide {raid}/{raidCount}: aceitando convite em {guestName}.");
                    await PrepareRoutineWindowAsync(guest, cancellationToken);
                    await _windowClickService.PressCtrlNumberAsync(guest, 0x31, cancellationToken, AppendLog);
                    foreach (var guestStep in new[] { "Aceitar convite", "Entrar na raide" })
                    {
                        var guestStepFound = await FindAndClickTemplateAsync(guest, templates[guestStep],
                            regions[guestStep], null, guestStep, 0.78, cancellationToken);
                        if (!guestStepFound)
                            throw new InvalidOperationException($"{guestName}: não foi possível concluir {guestStep}.");
                        await Task.Delay(350, cancellationToken);
                    }
                }

                await PrepareRoutineWindowAsync(starter, cancellationToken);
                AppendLog($"DailyRaidBoss — convidados processados; retornando ao starter {launcherGroup.Starter} para aguardar o OK final da raide.");
                await WaitAndClickTemplateIndefinitelyAsync(starter, templates["OK"], regions["OK"],
                    cancellationToken, "DailyRaidBoss");
                foreach (var guestName in guests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!targets.TryGetValue(guestName, out var guest)) continue;
                    AppendLog($"DailyRaidBoss — starter confirmou o OK; aguardando o mesmo OK em {guestName}.");
                    await PrepareRoutineWindowAsync(guest, cancellationToken, dismissOk: false);
                    await WaitAndClickTemplateIndefinitelyAsync(guest, templates["OK"], regions["OK"],
                        cancellationToken, "DailyRaidBoss");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppendLog($"DailyRaidBoss — {launcherGroup.Starter}: {ex.Message}"); }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                AppendLog($"DailyRaidBoss — {launcherGroup.Starter}: rotina encerrada.");
        }
    }

    private async Task WaitAndClickTemplateIndefinitelyAsync(PreviewWindow target, string templatePath,
        RelativeSearchRegion region, CancellationToken cancellationToken, string routineName = "DailyFavoriteRaid")
    {
        AppendLog($"{routineName} — aguardando confirmação. Use Stop para cancelar.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var consecutiveMatches = 0;
        OpenCvSharp.Rect? previousMatch = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // O monitor permanece ativo com a janela reduzida; suas regiões são relativas à resolução atual.
            var frame = await _windowCaptureService.CaptureAsync(target, cancellationToken, allowNon720p: true);
            var result = await _templateSearchService.FindAsync(frame, templatePath, region, 0.78,
                cancellationToken);
            UpdateDetectionPreview(result);
            var stablePosition = previousMatch is { } previous &&
                Math.Abs(previous.X + previous.Width / 2d - result.Bounds.X - result.Bounds.Width / 2d) <= Math.Max(4, result.Bounds.Width * 0.2) &&
                Math.Abs(previous.Y + previous.Height / 2d - result.Bounds.Y - result.Bounds.Height / 2d) <= Math.Max(4, result.Bounds.Height * 0.2);
            consecutiveMatches = result.Found ? (stablePosition ? consecutiveMatches + 1 : 1) : 0;
            previousMatch = result.Found ? result.Bounds : null;

            if (consecutiveMatches >= 3)
            {
                var clickX = MouseVariation.TargetCoordinate(result.Bounds.X, result.Bounds.Width);
                var clickY = MouseVariation.TargetCoordinate(result.Bounds.Y, result.Bounds.Height);
                AppendLog($"{routineName}: OK confirmado ({result.Confidence:0.00}); movendo o mouse e clicando.");
                var click = await _windowClickService.ClickRelativeAsync(target, clickX / frame.PixelWidth,
                    clickY / frame.PixelHeight, cancellationToken);
                AppendLog($"{routineName}: clique em OK enviado em X={click.X}, Y={click.Y}; Windows aceitou a entrada.");
                return;
            }

            if (watch.Elapsed - lastReport >= TimeSpan.FromMinutes(1))
            {
                AppendLog($"{routineName}: confirmação ainda pendente ({watch.Elapsed.TotalMinutes:0} min).");
                lastReport = watch.Elapsed;
            }
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task<bool> FindWhileScrollingAsync(PreviewWindow target, string templatePath,
        RelativeSearchRegion region, string targetName, double threshold, CancellationToken cancellationToken,
        Int32Rect? templateCrop = null, double? clickRightMargin = null)
    {
        var templateWidth = clickRightMargin.HasValue
            ? BitmapFrame.Create(new Uri(Path.GetFullPath(templatePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).PixelWidth
            : 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var consecutiveMatches = 0;
        var scrollCount = 0;
        var bestConfidence = 0d;
        OpenCvSharp.Rect? previousMatch = null;
        var scrollX = region.X + region.Width / 2;
        var scrollY = region.Y + region.Height / 2;

        while (timer.Elapsed < TimeSpan.FromSeconds(35) && scrollCount <= 50)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await _windowCaptureService.CaptureAsync(target, cancellationToken);
            var result = await _templateSearchService.FindAsync(frame, templatePath, region, threshold,
                cancellationToken, templateCrop);
            UpdateDetectionPreview(result);
            bestConfidence = Math.Max(bestConfidence, result.Confidence);
            var stablePosition = previousMatch is { } previous &&
                Math.Abs(previous.X + previous.Width / 2d - result.Bounds.X - result.Bounds.Width / 2d) <= Math.Max(4, result.Bounds.Width * 0.2) &&
                Math.Abs(previous.Y + previous.Height / 2d - result.Bounds.Y - result.Bounds.Height / 2d) <= Math.Max(4, result.Bounds.Height * 0.2);
            consecutiveMatches = result.Found ? (stablePosition ? consecutiveMatches + 1 : 1) : 0;
            previousMatch = result.Found ? result.Bounds : null;
            if (consecutiveMatches >= 3)
            {
                var clickX = result.Bounds.X + result.Bounds.Width / 2d;
                var clickY = result.Bounds.Y + result.Bounds.Height / 2d;
                if (clickRightMargin is { } margin)
                {
                    // A detecção usa só o miolo: recupera a largura completa antes de deslocar o clique.
                    var scale = result.Bounds.Width / (double)(templateCrop?.Width ?? templateWidth);
                    var iconLeft = result.Bounds.X - (templateCrop?.X ?? 0) * scale;
                    clickX = iconLeft + templateWidth * scale * (1 + margin);
                }
                if (clickX < 0 || clickX >= frame.PixelWidth)
                {
                    AppendLog($"{targetName}: ponto à direita do ícone fora da janela. Nenhum clique enviado.");
                    return false;
                }
                AppendLog($"{targetName} encontrado após {scrollCount} rolagens; clique em X={clickX:0}, Y={clickY:0}" +
                    (clickRightMargin.HasValue ? " à direita do ícone." : " no ícone."));
                var click = await _windowClickService.ClickRelativeAsync(target, clickX / frame.PixelWidth,
                    clickY / frame.PixelHeight, cancellationToken);
                AppendLog($"Windows aceitou o clique do item em X={click.X}, Y={click.Y}.");
                return true;
            }

            if (!result.Found)
            {
                var nearTarget = result.Confidence >= threshold - 0.18;
                var notches = nearTarget ? 1 : 2;
                await _windowClickService.ScrollDownAtRelativeAsync(target, scrollX, scrollY,
                    cancellationToken, notches: notches);
                scrollCount++;
            }
            var nearTargetNow = result.Confidence >= threshold - 0.18;
            await Task.Delay(result.Found ? 100 : nearTargetNow ? 90 : 36, cancellationToken);
        }

        AppendLog($"DailyScroll: item não encontrado na região após {scrollCount} rolagens. Melhor confiança: {bestConfidence:0.00}; mínimo: {threshold:0.00}. Nenhum clique foi enviado.");
        return false;
    }

    private async Task<bool> FindAndClickTemplateAsync(PreviewWindow target, string templatePath,
        RelativeSearchRegion region, Int32Rect? templateCrop, string targetName, double threshold,
        CancellationToken cancellationToken, double? clickAnchorX = null, double? clickAnchorY = null,
        TimeSpan? timeoutOverride = null, int clickCount = 1, bool logFailure = true, bool matchColor = false)
    {
        var searchTime = System.Diagnostics.Stopwatch.StartNew();
        var consecutiveMatches = 0;
        var bestConfidence = 0d;
        OpenCvSharp.Rect? previousMatch = null;

        var timeout = timeoutOverride ?? (targetName == "Cobre" ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(12));
        while (searchTime.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await _windowCaptureService.CaptureAsync(target, cancellationToken);
            var result = await _templateSearchService.FindAsync(frame, templatePath, region, threshold,
                cancellationToken, templateCrop, matchColor);
            UpdateDetectionPreview(result);
            bestConfidence = Math.Max(bestConfidence, result.Confidence);
            var stablePosition = previousMatch is { } previous &&
                Math.Abs(previous.X + previous.Width / 2d - result.Bounds.X - result.Bounds.Width / 2d) <= Math.Max(4, result.Bounds.Width * 0.2) &&
                Math.Abs(previous.Y + previous.Height / 2d - result.Bounds.Y - result.Bounds.Height / 2d) <= Math.Max(4, result.Bounds.Height * 0.2);
            consecutiveMatches = result.Found ? (stablePosition ? consecutiveMatches + 1 : 1) : 0;
            previousMatch = result.Found ? result.Bounds : null;
            if (consecutiveMatches >= 3)
            {
                var clickX = result.Bounds.X + result.Bounds.Width * (clickAnchorX ?? 0.5);
                var clickY = result.Bounds.Y + result.Bounds.Height * (clickAnchorY ?? 0.5);
                AppendLog($"{targetName} confirmado ({result.Confidence:0.00}). Preparando {clickCount} clique(s) em X={clickX:0}, Y={clickY:0} da captura.");
                (int X, int Y) click = default;
                for (var press = 1; press <= clickCount; press++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    clickX = MouseVariation.TargetCoordinate(result.Bounds.X, result.Bounds.Width, clickAnchorX ?? 0.5);
                    clickY = MouseVariation.TargetCoordinate(result.Bounds.Y, result.Bounds.Height, clickAnchorY ?? 0.5);
                    click = await _windowClickService.ClickRelativeAsync(target, clickX / frame.PixelWidth,
                        clickY / frame.PixelHeight, cancellationToken);
                    AppendLog($"{target.ProcessName} (PID {target.ProcessId}): Windows aceitou clique {press}/{clickCount} em X={click.X}, Y={click.Y} da tela.");
                    if (press < clickCount) await Task.Delay(Random.Shared.Next(150, 241), cancellationToken);
                }
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        if (logFailure)
            AppendLog($"{targetName} não encontrado após {searchTime.Elapsed.TotalSeconds:0.0}s (melhor confiança {bestConfidence:0.00}; mínimo {threshold:0.00}). Nenhum clique enviado.");
        return false;
    }

    private void UpdateDetectionPreview(TemplateSearchResult result)
    {
        ScreenshotImage.Source = result.AnnotatedFrame;
        DetectionStatusText.Text = $"{(result.Found ? "ENCONTRADO" : "BUSCANDO")}  /  Confiança: {result.Confidence:P0}  /  X={result.Bounds.X}, Y={result.Bounds.Y}";
    }

    private void AppendLog(string message)
    {
        if (ExecutionLog is null) return;
        if (ExecutionLog.LineCount > 100) ExecutionLog.Clear();
        ExecutionLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        ExecutionLog.ScrollToEnd();
    }

    private async Task PrepareRoutineWindowAsync(PreviewWindow target, CancellationToken token, bool dismissOk = true)
    {
        await _windowClickService.Prepare720pAsync(target, token);
        await DismissStartupScreensAsync(target, token, dismissOk);
    }

    private async Task DismissStartupScreensAsync(PreviewWindow target, CancellationToken token, bool dismissOk = true)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates");
        var powerTemplate = Path.Combine(directory, "routine-start-energy.png");
        var okTemplate = Path.Combine(directory, "routine-start-ok.png");
        var powerRegion = new RelativeSearchRegion(0.25, 0.60, 0.50, 0.15);
        var okRegion = new RelativeSearchRegion(0.3936, 0.7814, 0.2252, 0.1695);
        // Texto fixo da instrução: exclui horário, atividade e contadores variáveis.
        var powerCrop = new Int32Rect(419, 487, 455, 27);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var frame = await _windowCaptureService.CaptureAsync(target, token);
            var power = await _templateSearchService.FindAsync(frame, powerTemplate, powerRegion, 0.80, token, powerCrop);
            var ok = await _templateSearchService.FindAsync(frame, okTemplate, okRegion, 0.85, token);
            if (!power.Found && (!dismissOk || !ok.Found)) return;
            if (attempt == 3) throw new InvalidOperationException("A tela inicial de poupança/OK permaneceu aberta; rotina interrompida.");
            await Task.Delay(150, token);
            frame = await _windowCaptureService.CaptureAsync(target, token);
            if (power.Found)
            {
                var confirmed = await _templateSearchService.FindAsync(frame, powerTemplate, powerRegion, 0.80, token, powerCrop);
                if (!confirmed.Found) continue;
                AppendLog($"PID {target.ProcessId}: poupança de energia detectada; deslizando da esquerda para a direita.");
                await _windowClickService.SwipeRightAsync(target, token);
            }
            else
            {
                var confirmed = await _templateSearchService.FindAsync(frame, okTemplate, okRegion, 0.85, token);
                if (!confirmed.Found) continue;
                AppendLog($"PID {target.ProcessId}: OK detectado antes da rotina.");
                await _windowClickService.ClickRelativeAsync(target,
                    (confirmed.Bounds.X + confirmed.Bounds.Width / 2d) / frame.PixelWidth,
                    (confirmed.Bounds.Y + confirmed.Bounds.Height / 2d) / frame.PixelHeight, token, AppendLog);
            }
            await Task.Delay(700, token);
        }
    }

}
