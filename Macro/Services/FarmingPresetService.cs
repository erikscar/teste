using System.IO;
using System.Text.Json;
using Macro.Models;

namespace Macro.Services;

public sealed class FarmingPreset : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private string _orderStatus = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string OrderStatus
    {
        get => _orderStatus;
        set { _orderStatus = value; PropertyChanged?.Invoke(this, new(nameof(OrderStatus))); }
    }
    public string Name { get; set; } = "";
    public FarmingConfiguration Configuration { get; set; } = new();

    private static string CompletionCheckDetails(FarmingConfiguration configuration) =>
        string.Join(" · ", new[]
        {
            (Name: "Launcher 1", Check: configuration.DailyCompletionLauncher1),
            (Name: "Launcher 2", Check: configuration.DailyCompletionLauncher2),
            (Name: "Steam", Check: configuration.DailyCompletionSteam)
        }.Where(item => item.Check is { Enabled: true })
         .Select(item => $"{item.Name}: meta {item.Check.Limit}/30 · conferir a cada 40 minutos"));

    [System.Text.Json.Serialization.JsonIgnore]
    public string ScheduleLabel => string.IsNullOrWhiteSpace(Configuration.StartTime)
        ? "Início imediato" : $"Agendado · {Configuration.StartTime}";

    [System.Text.Json.Serialization.JsonIgnore]
    public string StepCountLabel => Steps.Count == 0 ? "Nenhuma rotina ativada" : $"{Steps.Count} etapas";

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<PresetStep> Steps
    {
        get
        {
            var c = Configuration;
            var steps = new List<PresetStep>();
            void Add(string title, string details, string repeat = "1 vez") =>
                steps.Add(new PresetStep((steps.Count + 1).ToString("00"), title, details, repeat));
            string Launchers(List<string> names) => names.Count == 0 ? "Nenhum launcher selecionado" : string.Join(" · ", names);
            string RaidDetails(LauncherGroup group)
            {
                var guests = new[] { group.Guest1, group.Guest2 }
                    .Where(name => !string.IsNullOrWhiteSpace(name) && name != "Não utilizar");
                var guestText = string.Join(" · ", guests);
                return $"Starter: {group.Starter}" + (guestText.Length == 0 ? "" : $"\nConvidados: {guestText}");
            }
            if (c.DailyDonation) Add("Doação diária", Launchers(c.DonationLaunchers));
            if (c.DailyScroll) Add("Pergaminho diário", Launchers(c.DailyScrollLaunchers ?? []));
            if (c.DailyTenOfTenEnabled) Add("Diárias 10/10", "Criar material raro e encantar · " + Launchers(c.DailyTenOfTenLaunchers));
            if (c.DarkSteelMiningEnabled) Add("Minerar Dark Steel", Launchers(c.DarkSteelMiningLaunchers ?? []));
            if (c.Normal.IsEnabled) Add("Raids normais", RaidDetails(c.NormalLaunchers), $"{c.Normal.RepeatCountText}×");
            if (c.Boss.IsEnabled) Add("Boss", RaidDetails(c.BossLaunchers), $"{c.Boss.RepeatCountText}×");
            if (c.DailyMissionLaunchersToRun.Any())
            {
                var favoriteDetails = string.Join(" · ", c.DailyMissionLaunchersToRun.Select(launcher =>
                    $"{launcher}: {(c.DailyLauncherMaps.TryGetValue(launcher, out var map) ? map : "Campo")}"));
                var completionDetails = CompletionCheckDetails(c);
                if (completionDetails.Length > 0) favoriteDetails += "\nConferir se finalizou todas — " + completionDetails;
                Add("Missões favoritas", favoriteDetails);
            }
            if (c.ArenaEnabled) Add("Arena", $"Starter: {c.ArenaStarter}\nConvidado: {c.ArenaInviter}" +
                (string.IsNullOrWhiteSpace(c.ArenaStartTime) ? "" : $"\nHorário: {c.ArenaStartTime}"), $"{c.ArenaRepeatCountText}×");
            return c.OrderedActions.Select(title => steps.FirstOrDefault(step => step.Title == title))
                .OfType<PresetStep>()
                .Concat(steps.Where(step => step.Title == "Conferir se finalizou todas" && !c.OrderedActions.Contains(step.Title)))
                .Select((step, index) => step with { Number = (index + 1).ToString("00") }).ToList();
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary
    {
        get
        {
            var c = Configuration;
            var steps = new List<string>();
            if (c.DailyDonation) steps.Add("Doação diária");
            if (c.DailyScroll) steps.Add("Pergaminho diário");
            if (c.DailyTenOfTenEnabled) steps.Add("Diárias 10/10 — criar material raro e encantar: " + string.Join(", ", c.DailyTenOfTenLaunchers));
            if (c.DarkSteelMiningEnabled) steps.Add("Minerar Dark Steel — " + string.Join(", ", c.DarkSteelMiningLaunchers ?? []));
            if (c.Normal.IsEnabled) steps.Add($"Raids normais ({c.Normal.RepeatCountText}x) — {c.NormalLaunchers.Starter}; convidados: {c.NormalLaunchers.Guest1}, {c.NormalLaunchers.Guest2}");
            if (c.Boss.IsEnabled) steps.Add($"Boss ({c.Boss.RepeatCountText}x) — {c.BossLaunchers.Starter}; convidados: {c.BossLaunchers.Guest1}, {c.BossLaunchers.Guest2}");
            if (c.DailyMissionLaunchersToRun.Any())
                steps.Add("Missões favoritas — " + string.Join(", ", c.DailyMissionLaunchersToRun));
            var completionDetails = CompletionCheckDetails(c);
            if (completionDetails.Length > 0) steps.Add("Conferir se finalizou todas — " + completionDetails);
            if (steps.Count == 0) steps.Add("Nenhuma rotina ativada");
            if (c.DailyDonation) steps.Add("Launchers da doação: " + string.Join(", ", c.DonationLaunchers));
            if (c.DailyScroll) steps.Add("Launchers do DailyScroll: " + string.Join(", ", c.DailyScrollLaunchers ?? []));
            if (c.ArenaEnabled && !string.IsNullOrWhiteSpace(c.ArenaStartTime)) steps.Add("Horário da Arena: " + c.ArenaStartTime);
            steps.Add(string.IsNullOrWhiteSpace(c.StartTime) ? "Início imediato" : $"Horário: {c.StartTime}");
            return string.Join(Environment.NewLine, steps);
        }
    }
}

public sealed record PresetStep(string Number, string Title, string Details, string Repeat);

public sealed class FarmingPresetService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MacroMir4", "farming-presets.json");

    public List<FarmingPreset> Load() => File.Exists(_path)
        ? JsonSerializer.Deserialize<List<FarmingPreset>>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Arquivo de presets inválido.")
        : [];

    public void Save(List<FarmingPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }

    public static FarmingConfiguration Copy(FarmingConfiguration configuration) =>
        JsonSerializer.Deserialize<FarmingConfiguration>(JsonSerializer.Serialize(configuration))!;
}
