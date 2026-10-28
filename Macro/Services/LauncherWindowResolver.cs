using System.Text.RegularExpressions;

namespace Macro.Services;

public static class LauncherWindowResolver
{
    public static Dictionary<string, PreviewWindow> Resolve(IEnumerable<PreviewWindow> windows)
    {
        var targets = new Dictionary<string, PreviewWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            string? launcher = null;
            if (window.ProcessName.StartsWith("Mir4S", StringComparison.OrdinalIgnoreCase))
                launcher = "MIR4 Steam";
            else if (window.ProcessName.StartsWith("Mir4G", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(window.WindowTitle, @"\bMir4G\s*\[\s*([12])\s*\]", RegexOptions.IgnoreCase);
                if (match.Success) launcher = $"MIR4 Launcher {match.Groups[1].Value}";
            }
            // Nunca atribui um launcher pela ordem de abertura ou pela quantidade de processos.
            if (launcher is null) continue;
            if (!targets.TryAdd(launcher, window))
                throw new InvalidOperationException($"Mais de uma janela identificada como {launcher}. Feche a duplicada antes de iniciar.");
        }
        return targets;
    }
}
