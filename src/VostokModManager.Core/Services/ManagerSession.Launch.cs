// Launching the game: the pre-launch checkpoint, the crash watch, the
// Steam launch with the mod loader's skip-launcher marker, and the
// pre-flight summary the launch panel shows.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>What the launch panel shows before the player presses Play.
/// Built only from what the session already holds (no disk scan).</summary>
/// <param name="ModsFolderFound">The mods folder the session scans exists.</param>
/// <param name="ConfigReadable">mod_config.cfg was read (false when the game
/// held it locked: the manager then refuses to write it).</param>
/// <param name="LoadOrderValid">The loader can apply the load order as shown:
/// the cfg is readable and no two enabled entries share a mod id.</param>
/// <param name="DuplicateModIds">Mod ids enabled more than once.</param>
/// <param name="EnabledCount">Enabled mods.</param>
/// <param name="MissingDependencyIds">Required dependencies of enabled mods
/// that are not enabled themselves.</param>
/// <param name="OutdatedCount">Mods older than the version VostokMods lists
/// (as of the last update check).</param>
/// <param name="ConflictCount">Conflicts in the active profile's scope.</param>
/// <param name="SaveSlotsWarning">Why the built-in save slots would not work
/// with the live mod set, or null.</param>
/// <param name="SkipLoaderUi">The launch asks the loader to skip its launcher.</param>
public sealed record LaunchReadiness(
    bool ModsFolderFound,
    bool ConfigReadable,
    bool LoadOrderValid,
    IReadOnlyList<string> DuplicateModIds,
    int EnabledCount,
    IReadOnlyList<string> MissingDependencyIds,
    int OutdatedCount,
    int ConflictCount,
    string? SaveSlotsWarning,
    bool SkipLoaderUi)
{
    public int MissingDependencyCount => MissingDependencyIds.Count;

    /// <summary>Nothing worth a second look before launching.</summary>
    public bool AllClear => ModsFolderFound && LoadOrderValid
        && MissingDependencyIds.Count == 0 && ConflictCount == 0 && SaveSlotsWarning == null;
}

public sealed partial class ManagerSession
{
    /// <summary>Starts the game. Steam by default; tests put a recorder here.</summary>
    public IGameLauncher Launcher { get; set; } = new SteamGameLauncher();

    /// <summary>Watches each launched session for a crash. Replaceable
    /// for tests (a fake process source).</summary>
    public GameSessionMonitor GameMonitor { get; set; } = new();

    private CancellationTokenSource? _gameWatch;
    private Task<GameExit>? _gameWatchTask;
    private string _lastLaunchStatus = "";

    /// <summary>The watch started by the last launch, for callers (and
    /// tests) that want to await its outcome. Null before any launch.</summary>
    public Task<GameExit>? GameWatchTask => _gameWatchTask;

    /// <summary>Launches Road to Vostok via Steam. The app id is
    /// auto-detected from the steamapps appmanifest whose installdir
    /// matches the parent of ModsDir; Steam integration matters for
    /// achievements, playtime and the overlay, so the .exe is never run
    /// directly. Tells the user and returns false when detection or the
    /// launch fails.</summary>
    public bool LaunchGame()
    {
        // Pre-launch checkpoint: snapshot mod_config.cfg + the active
        // profile's profile.json so "I launched the game and it broke"
        // has a one-click rollback. Non-fatal if the snapshot fails —
        // better to let the user launch than block on a backup write.
        var captured = CrashCheckpoint.Capture(
            ModConfig.DefaultPath,
            ActiveProfile?.Name ?? "",
            ActiveProfile?.FolderPath ?? "");
        if (captured) RaiseChanged();  // the checkpoint row appears / ages

        // Watch for a crash by polling for the game's process. Steam
        // launches are fire-and-forget, so this is best-effort.
        StartGameWatch();

        var appId = Launcher.FindAppId(ModsDir);
        if (appId <= 0)
        {
            Prompt.Inform("Can't launch via Steam",
                "Couldn't find Road to Vostok's Steam app id.\n\n"
                + "Expected an appmanifest_*.acf in:\n  "
                + $"{Path.GetDirectoryName(Path.GetDirectoryName(ModsDir)) ?? "(unknown)"}\n"
                + "with installdir matching the game folder.\n\n"
                + "Either the Mods folder isn't under a Steam library, "
                + "or you've moved the game outside of Steam. Launch "
                + "from your Steam library directly.",
                PromptKind.Info);
            return false;
        }
        // The mods are already set up here, so ask the loader to go
        // straight into the game instead of showing its launcher. The
        // marker is written just before the launch and the loader deletes
        // it on that launch; a loader that wouldn't understand it never
        // gets one.
        var skipUi = Settings.SkipLoaderUi && MmlInstall.WriteSkipUiOnce(ModsDir);

        if (!Launcher.LaunchAppId(appId))
        {
            if (skipUi) RemoveSkipUiMarker();
            Prompt.Inform("Launch failed",
                $"Steam protocol launch failed for app id {appId}. "
                + "Is Steam installed and running?",
                PromptKind.Warning);
            return false;
        }
        _lastLaunchStatus = $"Launching Vostok via Steam (app id {appId})…"
            + (skipUi ? "  (mod loader launcher skipped)" : "");
        Say(StatusArea.Launch, _lastLaunchStatus);
        return true;
    }

    /// <summary>Takes back the skip-launcher marker when the launch never
    /// happened, so a later launch from Steam itself still shows the
    /// loader's launcher.</summary>
    internal void RemoveSkipUiMarker()
    {
        var gameDir = Path.GetDirectoryName(ModsDir);
        if (string.IsNullOrEmpty(gameDir)) return;
        try { File.Delete(Path.Combine(gameDir, MmlInstall.SkipUiOnceFile)); } catch { }
    }

    /// <summary>Stops the crash watch (the app is closing, or a new
    /// launch replaces it). Its outcome is then ignored.</summary>
    public void StopWatchingGame()
    {
        try { _gameWatch?.Cancel(); } catch { /* already disposed */ }
        _gameWatch = null;
    }

    private void StartGameWatch()
    {
        StopWatchingGame();
        var cts = new CancellationTokenSource();
        _gameWatch = cts;
        _gameWatchTask = WatchGameSessionAsync(cts);
    }

    /// <summary>Awaits the monitor and acts on the outcome. The await
    /// resumes on the caller's context, so the restore offer runs on the
    /// UI thread like every other session call.</summary>
    private async Task<GameExit> WatchGameSessionAsync(CancellationTokenSource cts)
    {
        var exit = await GameMonitor.WatchAsync(cts.Token);
        var cancelled = cts.IsCancellationRequested;
        if (ReferenceEquals(_gameWatch, cts)) _gameWatch = null;
        cts.Dispose();
        if (cancelled) return exit;  // the app closed / a newer launch took over
        HandleGameExit(exit);
        return exit;
    }

    /// <summary>A clean session promotes the last-launch checkpoint to
    /// last-known-good (the cfg state was validated by a real play); a
    /// likely crash offers to restore a checkpoint.</summary>
    internal void HandleGameExit(GameExit exit)
    {
        switch (exit.Kind)
        {
            case GameExitKind.CleanExit:
                CrashCheckpoint.PromoteToKnownGood();
                RaiseChanged();
                break;
            case GameExitKind.LikelyCrash:
                OfferCrashRestore(exit);
                break;
        }
    }

    /// <summary>Tells the user the game looks to have crashed and, on
    /// Yes, runs the checkpoint restore (with its picker when both slots
    /// exist). Returns true when a checkpoint was restored.</summary>
    public bool OfferCrashRestore(GameExit exit)
    {
        var hasGood = CrashCheckpoint.Exists(CheckpointSlot.LastKnownGood);
        var msg =
            $"Road to Vostok exited after {(int)exit.SessionSeconds}s "
            + $"(exit code {exit.ExitCode}).\n\n"
            + "This looks like a crash. Restore a checkpoint?\n\n"
            + "• Last-launch — undo just the change you made "
            + "right before this launch.\n"
            + (hasGood
                ? "• Last-known-good — roll back to the state "
                  + "from the most recent session that ended "
                  + "cleanly.\n\n"
                : "(no last-known-good available yet)\n\n")
            + "Yes opens the restore picker; No leaves things "
            + "as-is.";
        if (!Prompt.Confirm("Possible crash detected", msg, PromptKind.Warning)) return false;
        return RestoreCheckpoint();
    }

    // ── pre-flight ────────────────────────────────────────────────────

    /// <summary>The launch panel's pre-flight summary, from the session's
    /// current state.</summary>
    public LaunchReadiness GetLaunchReadiness()
    {
        var enabled = Registry.Enabled();
        var duplicates = enabled
            .Where(e => !string.IsNullOrEmpty(e.ModId))
            .GroupBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        var enabledIds = new HashSet<string>(
            enabled.Select(e => e.ModId).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);
        var missingDeps = enabled
            .SelectMany(e => e.RequiredDependencies)
            .Where(dep => !string.IsNullOrEmpty(dep) && !enabledIds.Contains(dep))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outdated = Registry.Entries.Count(IsOutdated);

        return new LaunchReadiness(
            ModsFolderFound: Directory.Exists(ModsDir),
            ConfigReadable: !Config.LoadFailed,
            LoadOrderValid: !Config.LoadFailed && duplicates.Count == 0,
            DuplicateModIds: duplicates,
            EnabledCount: enabled.Count,
            MissingDependencyIds: missingDeps,
            OutdatedCount: outdated,
            ConflictCount: LastConflicts.Count,
            SaveSlotsWarning: SaveSlotsWarning(),
            SkipLoaderUi: Settings.SkipLoaderUi);
    }
}
