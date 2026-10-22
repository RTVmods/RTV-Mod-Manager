// Log Analysis (ported from Ui/LogAnalysisDialog, the part that needs
// no AI).
//
// Parses a godot.log (via GodotLogAnalyzer) into what the view shows:
//   • the log picker (newest first, starting on the newest log the mod
//     loader actually ran in) and the header stats,
//   • the hook / override OVERWRITE MAP — which mods hooked the same
//     function, which mods replaced each vanilla script and who won,
//   • the issue list (errors / warnings, with mod hints).
// ApplyProposedOrder writes a proposed load order (from the Claude
// diagnosis, or anywhere else) to mod_config.cfg.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>A log the picker offers: "godot.log   (2026-10-22 18:04, 312 KB)".</summary>
public sealed record LogFileItem(string Path, string Label);

/// <summary>A function hooked by more than one mod. `IsCrowded` (3+
/// mods) is where ordering bugs cluster; the view flags it.</summary>
public sealed record LogHookClashRow(string Target, int ModCount, string Mods, bool IsCrowded);

/// <summary>A vanilla script replaced by 2+ mods; the last applied wins.</summary>
public sealed record LogOverrideClashRow(string VanillaPath, string Mods, string Winner);

public enum LogIssueLevel { Error, Warning, Other }

public sealed record LogIssueRow(string Severity, LogIssueLevel Level, string Mod, string Message);

/// <summary>Everything the Log Analysis view shows for one log. Each
/// `...Note` is the placeholder line for an empty section ("" when the
/// section has rows, or for HookNote when hooks were not logged it is
/// the hint to turn on Developer Mode).</summary>
public sealed record LogAnalysisReport(
    string LogPath,
    bool LogFound,
    GodotLogAnalyzer.LogAnalysis Analysis,
    string HeaderText,
    IReadOnlyList<LogHookClashRow> HookClashes,
    string HookNote,
    IReadOnlyList<LogOverrideClashRow> OverrideClashes,
    string OverrideNote,
    IReadOnlyList<LogIssueRow> Issues,
    string IssueNote);

/// <summary>A mod's proposed priority (lower loads earlier).</summary>
public sealed record ProposedPriorityChange(string ModId, int Priority);

public sealed partial class ManagerSession
{
    /// <summary>The logs the picker offers, newest first.</summary>
    public IReadOnlyList<LogFileItem> AvailableLogFiles()
    {
        var items = new List<LogFileItem>();
        foreach (var p in GodotLogAnalyzer.AvailableLogs())
        {
            string label;
            try
            {
                var fi = new FileInfo(p);
                label = $"{fi.Name}   ({fi.LastWriteTime:yyyy-MM-dd HH:mm}, {fi.Length / 1024} KB)";
            }
            catch { label = Path.GetFileName(p); }
            items.Add(new LogFileItem(p, label));
        }
        return items;
    }

    /// <summary>Where the picker starts: the newest log the mod loader
    /// actually ran in, as an index into `logs` (0 when not found, -1
    /// for an empty list).</summary>
    public static int PreferredLogIndex(IReadOnlyList<LogFileItem> logs)
    {
        if (logs.Count == 0) return -1;
        var preferred = GodotLogAnalyzer.LatestLoaderLog();
        for (var i = 0; i < logs.Count; i++)
            if (string.Equals(logs[i].Path, preferred, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Analyzes `logPath`, or the newest log the loader ran in
    /// when it is null or empty.</summary>
    public LogAnalysisReport AnalyzeLog(string? logPath = null)
    {
        var path = string.IsNullOrEmpty(logPath) ? GodotLogAnalyzer.LatestLoaderLog() : logPath;
        var a = GodotLogAnalyzer.Analyze(path);
        var found = !string.IsNullOrEmpty(path) && File.Exists(path);

        var header = !found
            ? $"No Godot log found in {SupportPackage.GodotLogsDir}."
            : $"Engine {a.EngineVersion}   ·   "
              + (a.LoaderVersion.Length > 0
                  ? $"Mod loader v{a.LoaderVersion}   ·   "
                  : "Mod loader did not run   ·   ")
              + $"{a.LoadOrder.Count} mods loaded   ·   "
              + (a.HooksNotLogged
                  ? "hooks not logged   ·   "
                  : $"{a.Hooks.Count} hooks   ·   "
                    + $"{a.HookClashes.Count} hook clash(es)   ·   ")
              + $"{a.Clashes.Count} script-override clash(es)   ·   "
              + $"{a.Issues.Count} issue(s)";

        var hooks = a.HookClashes
            .Select(c => new LogHookClashRow(c.Target, c.Mods.Count, string.Join("  →  ", c.Mods), c.Mods.Count >= 3))
            .ToList();
        var hookNote = a.HooksNotLogged
            ? GodotLogAnalyzer.HooksNotLoggedHint
            : a.HookClashes.Count == 0
                ? "No function was hooked by more than one mod in this session."
                : "";

        var clashes = a.Clashes
            .Select(c => new LogOverrideClashRow(c.VanillaPath, string.Join("  →  ", c.Mods), c.Winner))
            .ToList();
        var clashNote = a.Clashes.Count == 0
            ? "No whole vanilla script was replaced by more than one mod."
            : "";

        var issues = a.Issues
            .Select(i => new LogIssueRow(i.Severity, i.Severity switch
                {
                    "ERROR" or "SCRIPT_ERROR" => LogIssueLevel.Error,
                    "WARNING"                 => LogIssueLevel.Warning,
                    _                         => LogIssueLevel.Other,
                }, i.ModHint, i.Message))
            .ToList();
        var issueNote = a.Issues.Count == 0 ? "No errors or warnings found in this log." : "";

        return new LogAnalysisReport(path, found, a, header,
            hooks, hookNote, clashes, clashNote, issues, issueNote);
    }

    /// <summary>Writes a proposed load order to mod_config.cfg after
    /// listing the changes and asking. Mods not installed are skipped;
    /// when none is installed the user is told there is nothing to
    /// apply. On a save failure the priorities stay staged in memory and
    /// the user is told. Refreshes the session after a successful save.
    /// Returns the number of priorities written (0 when nothing was
    /// applied).</summary>
    public int ApplyProposedOrder(IReadOnlyList<ProposedPriorityChange> order)
    {
        if (order.Count == 0) return 0;

        var lines = new List<string>();
        foreach (var pp in order)
        {
            var entry = Registry.FindById(pp.ModId);
            if (entry == null) continue;
            lines.Add($"  {(string.IsNullOrEmpty(entry.DisplayName) ? entry.ModId : entry.DisplayName)}: "
                + $"{entry.Priority} → {pp.Priority}");
        }
        if (lines.Count == 0)
        {
            Prompt.Inform("Nothing to apply",
                "None of the proposed mods are currently installed, so there's "
                + "nothing to apply.");
            return 0;
        }

        if (!Prompt.Confirm("Apply proposed load order",
                "Apply these load-order (priority) changes to mod_config.cfg?\n\n"
                + string.Join("\n", lines)
                + "\n\nLower priority loads earlier. You can revert via the "
                + "last-launch checkpoint if needed.",
                PromptKind.Warning))
            return 0;

        var applied = 0;
        foreach (var pp in order)
        {
            var entry = Registry.FindById(pp.ModId);
            if (entry == null) continue;
            Config.SetPriority(entry.ModId, entry.Version, pp.Priority);
            applied++;
        }
        try { Config.Save(); }
        catch (Exception ex)
        {
            Prompt.Inform("Save failed",
                "Priorities staged but saving mod_config.cfg failed:\n" + ex.Message,
                PromptKind.Error);
            return 0;
        }

        Refresh();
        Prompt.Inform("Load order applied",
            $"Applied {applied} priority change(s). The mod list has been refreshed.");
        return applied;
    }
}
