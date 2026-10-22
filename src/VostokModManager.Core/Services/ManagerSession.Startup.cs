// Startup: what the app does once its window is up, minus the UI work.
// The first scan, profile resolution, orphan adoption, the built-in Save
// Slots upkeep and the first conflict pass happen here; the steps that
// need the user (the profile migration, the first-run backup) and the
// network update checks (owned by the updates area) come back as a list
// for the front end to run in order.

using System.IO.Compression;
using System.Text;
using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>What the front end does after StartupAsync, in this order.</summary>
public enum StartupStep
{
    /// <summary>Show the profile-model migration (GetMigrationOffer, then
    /// AdoptProfileModel or DeclineProfileMigration).</summary>
    OfferProfileMigration,
    /// <summary>CheckUpdatesAsync (mod updates on VostokMods).</summary>
    CheckUpdates,
    /// <summary>CheckMmlVersionAsync (the mod loader's latest release).</summary>
    CheckMmlVersion,
    /// <summary>CheckManagerUpdateAsync(forceFresh: true): on a cold start
    /// the status should reflect what is published now, not the 24 h cache.</summary>
    CheckManagerUpdate,
    /// <summary>First run: show the welcome (FirstRunBackupItems), run
    /// WriteFirstRunBackupAsync on yes, then FinishFirstRun either way.</summary>
    OfferFirstRunBackup,
}

/// <param name="ModsFolderReadable">False when the first scan could not
/// read the mods folder; startup stopped there and NextSteps is empty.</param>
public sealed record StartupResult(bool ModsFolderReadable, IReadOnlyList<StartupStep> NextSteps)
{
    public bool Has(StartupStep step) => NextSteps.Contains(step);
}

/// <summary>What the first-run backup would capture, for the welcome screen.</summary>
public sealed record FirstRunBackupItems(string ModsDir, string ModConfigPath, string McmDir, string BackupsDir);

/// <summary>A written first-run backup: the zip and one line per part
/// captured or skipped.</summary>
public sealed record FirstRunBackupResult(string ZipPath, string Summary);

public sealed partial class ManagerSession
{
#if AI_RESOLVER
    /// <summary>Test seam: replaces Claude.Detect() (which runs the CLI).</summary>
    internal Action? ClaudeDetector { get; set; }

    internal void DetectClaude() => (ClaudeDetector ?? Claude.Detect)();
#endif

    /// <summary>Test seam: replaces AutodetectDecomp() (which looks in
    /// the real user profile).</summary>
    internal Func<string>? DecompDetector { get; set; }

    /// <summary>Runs the startup sequence. Returns what the front end
    /// still has to do (in order), or ModsFolderReadable=false when the
    /// mods folder could not be read.</summary>
    public async Task<StartupResult> StartupAsync()
    {
#if AI_RESOLVER
        // First-run convenience: auto-detect the Decomp/ folder if none is
        // set, from known locations only, and only when the candidate looks
        // like a real Decomp. The path only gives the AI resolver the
        // original game script as context.
        if (string.IsNullOrEmpty(Settings.GameSourcePath))
        {
            var detected = (DecompDetector ?? AutodetectDecomp)();
            if (!string.IsNullOrEmpty(detected))
            {
                Settings.GameSourcePath = detected;
                try { Settings.Save(); } catch { /* best-effort */ }
                Resolver.GameSourcePath = detected;
            }
        }

        // Detect runs the CLI; keep it off the UI thread.
        await Task.Run(DetectClaude);
        Say(StatusArea.Claude, ClaudeStatusText());
#else
        await Task.CompletedTask;
#endif

        Say(StatusArea.Mods, $"Mods: scanning {ModsDir} ...");
        if (!Rescan())
        {
            Say(StatusArea.Mods, $"Mods: cannot read {ModsDir}. "
                + "Is the game installed at the default Steam path?");
            RaiseChanged();
            return new StartupResult(false, Array.Empty<StartupStep>());
        }
        // Resolve the active profile before the first paint, so the
        // grid's profile filter sees the right value straight away.
        ReloadProfiles();
        // The first Rescan ran with no active profile loaded, so orphan
        // adoption short-circuited; this catch-up pass picks up any .vmz
        // dropped in through Explorer between sessions.
        AutoAdoptOrphansIntoActiveProfile();
        SetUpBuiltInSaveSlots();

        Say(StatusArea.Conflicts, "Conflicts: detecting (deep analysis) ...");
        Requery();

        var steps = new List<StartupStep>();
        // The migration offer fires when the profile model hasn't been
        // adopted on this install, the user hasn't declined it, and there
        // are installed archive mods worth migrating.
        if (ShouldOfferMigration) steps.Add(StartupStep.OfferProfileMigration);
        steps.Add(StartupStep.CheckUpdates);
        steps.Add(StartupStep.CheckMmlVersion);
        steps.Add(StartupStep.CheckManagerUpdate);
        // The welcome comes after the scan and the update checks, so it
        // shows over a populated window and the backup reflects the disk
        // state just observed.
        if (IsFirstRun) steps.Add(StartupStep.OfferFirstRunBackup);
        return new StartupResult(true, steps);
    }

    // ── first run ─────────────────────────────────────────────────────

    /// <summary>The game's MCM folder (Mod Configuration Menu's per-mod
    /// settings, user://MCM/).</summary>
    public static string McmDir => Path.Combine(
        Path.GetDirectoryName(ModConfig.DefaultPath) ?? "", "MCM");

    public FirstRunBackupItems GetFirstRunBackupItems()
        => new(ModsDir, ModConfig.DefaultPath, McmDir, AppPaths.BackupsDir);

    /// <summary>The welcome's "Back up now": a zip of the mods folder,
    /// mod_config.cfg and the MCM settings in the backups folder. A
    /// missing part is noted in the summary rather than failing. Tells
    /// the user the outcome; returns null when the zip couldn't be
    /// written.</summary>
    public async Task<FirstRunBackupResult?> WriteFirstRunBackupAsync()
    {
        var backupsDir = AppPaths.BackupsDir;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(backupsDir, $"backup-{stamp}.zip");
        var summary = new StringBuilder();
        var modsDir = ModsDir;
        try
        {
            Directory.CreateDirectory(backupsDir);
            await Task.Run(() => WriteFirstRunBackup(zipPath, modsDir, summary));
            Prompt.Inform("Backup complete", $"Backup written to:\n\n{zipPath}\n\n{summary}");
            return new FirstRunBackupResult(zipPath, summary.ToString());
        }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't write backup", ex);
            return null;
        }
    }

    /// <summary>Saves settings.json so the next start is not a first run,
    /// whatever the user picked on the welcome.</summary>
    public void FinishFirstRun()
    {
        try { Settings.Save(); } catch { /* best-effort */ }
    }

    /// <summary>Writes the zip: each file at the top of mods/, the bare
    /// mod_config.cfg at the root, and the whole MCM/ tree. Forward
    /// slashes in entry paths so every extractor opens it cleanly.</summary>
    internal static void WriteFirstRunBackup(string zipPath, string modsDir, StringBuilder summary)
    {
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        if (Directory.Exists(modsDir))
        {
            var files = Directory.GetFiles(modsDir, "*", SearchOption.TopDirectoryOnly);
            foreach (var f in files)
                AddFile(zip, "mods/" + Path.GetFileName(f), f);
            summary.AppendLine($"• mods/ — {files.Length} file(s)");
        }
        else
        {
            summary.AppendLine($"• mods/ — skipped (folder not found at {modsDir})");
        }

        var cfg = ModConfig.DefaultPath;
        if (File.Exists(cfg))
        {
            AddFile(zip, "mod_config.cfg", cfg);
            summary.AppendLine("• mod_config.cfg");
        }
        else
        {
            summary.AppendLine("• mod_config.cfg — skipped (no file yet)");
        }

        // MCM writes to user://MCM/ (%APPDATA%\Road to Vostok\MCM\). Mirror
        // the whole tree so every mod's saved config is captured. Skipped
        // when MCM isn't installed.
        var mcmDir = McmDir;
        if (Directory.Exists(mcmDir))
        {
            var mcmFiles = Directory.GetFiles(mcmDir, "*", SearchOption.AllDirectories);
            foreach (var f in mcmFiles)
                AddFile(zip, "MCM/" + Path.GetRelativePath(mcmDir, f).Replace('\\', '/'), f);
            summary.AppendLine($"• MCM/ — {mcmFiles.Length} file(s)");
        }
        else
        {
            summary.AppendLine("• MCM/ — skipped (MCM not installed)");
        }
    }

    private static void AddFile(ZipArchive zip, string entryName, string path)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var rs = File.OpenRead(path);
        rs.CopyTo(es);
    }

    // ── decomp auto-detect ────────────────────────────────────────────

    /// <summary>A Decomp/ folder in one of the known places (the dev
    /// Desktop layout, Desktop, Documents), or "".</summary>
    public static string AutodetectDecomp()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(Path.Combine(home, "Desktop", "RoadToVostok Dev", "Decomp"));
            candidates.Add(Path.Combine(home, "Desktop", "Decomp"));
        }
        if (!string.IsNullOrEmpty(docs))
            candidates.Add(Path.Combine(docs, "RoadToVostok_Decomp"));
        foreach (var c in candidates)
            if (LooksLikeDecomp(c)) return c;
        return "";
    }

    /// <summary>True if `path` holds the landmark files of a real
    /// Decomp/: Scripts/Loader.gd and Scripts/Interface.gd, both shipped
    /// by Road to Vostok and nothing else.</summary>
    public static bool LooksLikeDecomp(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "Scripts", "Loader.gd"))
            && File.Exists(Path.Combine(path, "Scripts", "Interface.gd"));
    }
}
