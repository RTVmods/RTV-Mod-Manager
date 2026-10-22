// Collect Support Package (ported from Ui/SupportPackageDialog).
//
// One action: snapshot the active profile (as a directly-importable
// .vmprofile bundle + loose metadata), bundle the whole Godot logs
// directory and the loader's mod_config.cfg, and write it all to a
// .vmzlog the user saves wherever they like. The heavy lifting lives in
// Domain/SupportPackage; this is the summary shown before the build and
// the build itself with progress. Read-only with respect to the live
// mods folder (apart from indexing live archives into the Library), so
// nothing is rescanned.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>What the support package will hold, shown before building.
/// `ProfileName` is null when no profile is active (every installed mod
/// is snapshotted instead).</summary>
public sealed record SupportPackageSummary(
    string? ProfileName,
    int ModCount,
    int LogFileCount,
    string LogsDir,
    string SuggestedFileName,
    string Text);

/// <summary>A finished support package, the lines the dialog logged
/// about it, and whether the user asked to see it in Explorer.</summary>
public sealed record SupportPackageOutcome(
    SupportPackage.Result Result,
    IReadOnlyList<string> ReportLines,
    bool RevealRequested);

public sealed partial class ManagerSession
{
    /// <summary>The summary the Support Package view opens with.</summary>
    public SupportPackageSummary GetSupportPackageSummary()
    {
        var profileName = ActiveProfile?.Name;
        var modCount    = ActiveProfile?.Mods.Count ?? Registry.Entries.Count;
        var logs        = SupportPackage.CountLogFiles();
        var logsDir     = SupportPackage.GodotLogsDir;

        var profileLine = string.IsNullOrEmpty(profileName)
            ? $"No active profile — a snapshot of all {modCount} installed mods will be bundled."
            : $"Active profile: \"{profileName}\"  ({modCount} mods)";
        var logsLine = logs > 0
            ? $"Godot logs found: {logs} file(s) in {logsDir}"
            : $"⚠ No Godot logs found at {logsDir}";
        var nl = Environment.NewLine;
        var text = profileLine + nl
            + logsLine + nl + nl
            + "The package (.vmzlog) will contain:" + nl
            + "  • profile.vmprofile — load it straight into the Profiles Manager" + nl
            + "  • profile.json — quick-read mod list" + nl
            + "  • logs/ — the full Godot log directory" + nl
            + "  • mod_config.cfg — the in-game loader's state" + nl
            + "  • support-info.json — manager + MML versions and counts";

        var suggested = SupportPackage.SuggestedFileName(profileName ?? "Support Snapshot", DateTime.UtcNow);
        return new SupportPackageSummary(profileName, modCount, logs, logsDir, suggested, text);
    }

    /// <summary>Builds the support package at `outPath` (a path the
    /// caller picked, e.g. from a save dialog seeded with the summary's
    /// SuggestedFileName). `includeRootLogs` adds the other mod logs in
    /// the game's user folder (modloader_filescope.log, ...). Progress
    /// is reported as (message, percent 0-100) from a worker thread, so
    /// pass a Progress&lt;T&gt; to land it on the UI thread.
    ///
    /// On success the user is told where it went and asked whether to
    /// reveal it (the answer is in RevealRequested; revealing is the
    /// front end's job). On failure the error is shown and null
    /// returned.</summary>
    public async Task<SupportPackageOutcome?> BuildSupportPackageAsync(
        string outPath, bool includeRootLogs = true,
        IProgress<(string Message, int Percent)>? progress = null)
    {
        // Snapshot the live entries on this thread before going async —
        // ModEntry is a plain data object, safe to read from the task.
        var entries       = Registry.Entries.ToList();
        var activeProfile = ActiveProfile;
        var modsDir       = ModsDir;

        try
        {
            var result = await Task.Run(() =>
                SupportPackage.Build(
                    activeProfile, entries, modsDir, outPath, includeRootLogs,
                    (msg, pct) => progress?.Report((msg, Math.Clamp(pct, 0, 100)))));

            var sizeMb = result.BytesWritten / (1024.0 * 1024.0);
            var lines = new List<string>
            {
                "",
                $"Package written: {result.BytesWritten:N0} bytes ({sizeMb:F1} MB)",
                $"Mods bundled: {result.ModCount}   Log files: {result.LogFileCount}",
            };
            if (result.Warnings.Count > 0)
            {
                lines.Add("");
                lines.Add($"Warnings ({result.Warnings.Count}):");
                foreach (var w in result.Warnings) lines.Add("  • " + w);
            }

            var msg = $"Support package saved to:\n{outPath}\n\n"
                    + $"{sizeMb:F1} MB · {result.ModCount} mods · "
                    + $"{result.LogFileCount} log file(s)";
            if (result.Warnings.Count > 0)
                msg += $"\n\n{result.Warnings.Count} warning(s) — see the log above "
                     + "(the package was still created).";
            var reveal = Prompt.Confirm("Support package created",
                msg + "\n\nReveal it in Explorer?", PromptKind.Info);
            return new SupportPackageOutcome(result, lines, reveal);
        }
        catch (Exception ex)
        {
            Prompt.Inform("Support package error",
                "Failed to build the support package:\n" + ex.Message, PromptKind.Error);
            return null;
        }
    }
}
