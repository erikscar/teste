namespace Macro.Models;

// Dados simples usados apenas para preencher os bindings da interface.
public sealed class FarmingConfiguration
{
    public LauncherGroup NormalLaunchers { get; set; } = new();
    public LauncherGroup BossLaunchers { get; set; } = new();
    public string ArenaStarter { get; set; } = "MIR4 Steam";
    public string ArenaInviter { get; set; } = "MIR4 Launcher 1";
    public string SecretPeakLauncher { get; set; } = "MIR4 Launcher 1";
    public string MagicSquareLauncher { get; set; } = "MIR4 Launcher 1";
    public string ArenaRepeatCountText { get; set; } = "1";
    public string ArenaStartTime { get; set; } = "";
    public bool ArenaEnabled { get; set; }
    public List<string> ActionOrder { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> OrderedActions => (ActionOrder ?? []).Concat(new[]
        { "Doação diária", "Pergaminho diário", "Diárias 10/10", "Minerar Dark Steel", "Raids normais", "Boss", "Arena", "Missões favoritas" }).Distinct();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DarkSteelMiningEnabled => DarkSteelMiningLaunchers is { Count: > 0 };
    public List<string> DarkSteelMiningLaunchers { get; set; } = [];
    public List<string> DonationLaunchers { get; set; } = [];
    public List<string>? DailyScrollLaunchers { get; set; }
    public bool DailyScrollCleanBackpackLauncher1 { get; set; }
    public bool DailyScrollCleanBackpackLauncher2 { get; set; }
    public bool DailyScrollCleanBackpackSteam { get; set; }
    public List<string> DailyTenOfTenLaunchers { get; set; } = [];
    public List<string> DailyLaunchers { get; set; } = [];
    public List<string>? DailyFavoriteLaunchers { get; set; }
    public Dictionary<string, string> DailyLauncherMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MissionCompletionCheck DailyCompletionLauncher1 { get; set; } = new();
    public MissionCompletionCheck DailyCompletionLauncher2 { get; set; } = new();
    public MissionCompletionCheck DailyCompletionSteam { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> DailyMissionLaunchersToRun =>
        new[] { "MIR4 Launcher 1", "MIR4 Launcher 2", "MIR4 Steam" }
            .Where(launcher => DailyFavoriteLaunchers?.Contains(launcher) == true || CompletionCheckFor(launcher).Enabled);
    public MissionCompletionCheck CompletionCheckFor(string launcher) => launcher switch
    {
        "MIR4 Launcher 1" => DailyCompletionLauncher1,
        "MIR4 Launcher 2" => DailyCompletionLauncher2,
        "MIR4 Steam" => DailyCompletionSteam,
        _ => throw new ArgumentException("Launcher desconhecido.", nameof(launcher))
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DailyDonation => DonationLaunchers is { Count: > 0 };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DailyScroll => DailyScrollLaunchers is { Count: > 0 };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DailyTenOfTenEnabled => DailyTenOfTenLaunchers is { Count: > 0 };
    public bool DailyFavorites { get; set; }
    public string StartTime { get; set; } = "";
    public RaidConfiguration Normal { get; set; } = new() { Name = "Raide 1" };
    public RaidConfiguration Boss { get; set; } = new() { Name = "Boss 1" };
    public List<MissionItemConfiguration> DailyItems { get; set; } = CreateDailyMissionItems();
    public List<MissionItemConfiguration> DominationItems { get; set; } = CreateMissionItems();

    public static List<MissionItemConfiguration> CreateDailyMissionItems() =>
        [new() { Name = "Platina", Grade = "Rara" }, new() { Name = "Aço", Grade = "Rara" }, new() { Name = "Óleo", Grade = "Rara" }];

    private static List<MissionItemConfiguration> CreateMissionItems() =>
        [new() { Name = "Platina", Grade = "Rara" }, new() { Name = "Aço", Grade = "Rara" }, new() { Name = "Óleo", Grade = "Rara" }];
}

public sealed class MissionCompletionCheck
{
    public bool Enabled { get; set; }
    private int _limit;
    public int Limit
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 0, 30);
    }
}

public sealed class RaidConfiguration
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsAvailable { get; set; } = true;
    public bool IsEnabled { get; set; }
    public string RepeatCountText { get; set; } = "1";
}

public sealed class MissionItemConfiguration
{
    public string Name { get; set; } = "";
    public string Grade { get; set; } = "Rara";
    public bool IsEnabled { get; set; }
    public List<string> SelectedMaps { get; set; } = [];
    public string Launcher { get; set; } = "MIR4 Launcher 2";

    [System.Text.Json.Serialization.JsonIgnore]
    public string[] AvailableGrades => Name == "Óleo" ? ["Rara"] : ["Rara", "Épica"];
}

public sealed class LauncherGroup
{
    public string Starter { get; set; } = "MIR4 Steam";
    public string Guest1 { get; set; } = "MIR4 Launcher 2";
    public string Guest2 { get; set; } = "Não utilizar";
}
