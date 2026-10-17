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
        { "Doação diária", "Pergaminho diário", "Diárias 10/10", "Raids normais", "Boss", "Arena", "Missões favoritas" }).Distinct();
    public List<string> DonationLaunchers { get; set; } = [];
    public List<string>? DailyScrollLaunchers { get; set; }
    public List<string> DailyTenOfTenLaunchers { get; set; } = [];
    public List<string> DailyLaunchers { get; set; } = [];
    public List<string>? DailyFavoriteLaunchers { get; set; }
    public Dictionary<string, string> DailyLauncherMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DailyFavoriteRetryLaunchers { get; set; } = [];
    public Dictionary<string, string> DailyFavoriteRetryMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
