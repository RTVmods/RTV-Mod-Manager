using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

/// <summary>Records launches instead of starting Steam.</summary>
internal sealed class RecordingLauncher : IGameLauncher
{
    public int AppId { get; set; } = 1963610;
    public bool LaunchSucceeds { get; set; } = true;
    public bool Running { get; set; }
    public List<int> Launched { get; } = new();

    public int FindAppId(string modsDir) => AppId;

    public bool LaunchAppId(int appId)
    {
        if (!LaunchSucceeds) return false;
        Launched.Add(appId);
        return true;
    }

    public bool IsGameRunning() => Running;
}

internal sealed class FakeGameProcess : IGameProcess
{
    public TaskCompletionSource Exit { get; } = new();
    public int ExitCode { get; set; }
    public bool Disposed { get; private set; }
    public Task WaitForExitAsync(CancellationToken ct) => Exit.Task.WaitAsync(ct);
    public void Dispose() => Disposed = true;
}

internal sealed class FakeProcessSource : IGameProcessSource
{
    public IGameProcess? Game { get; set; }
    public int Polls { get; private set; }
    public IGameProcess? FindGame() { Polls++; return Game; }
}

internal static class LaunchKit
{
    /// <summary>A session that never touches Steam or the process table:
    /// launches are recorded and the crash watch finds no game (unless
    /// the test hands it one).</summary>
    internal static ManagerSession Open(
        Sandbox sb, ScriptedPrompt prompt, RecordingLauncher launcher,
        bool saveSlots = false, string? activeProfile = "Default", IGameProcessSource? processes = null,
        bool isFirstRun = false, params ModProfile[] profiles)
    {
        var settings = new Settings
        {
            ModsDir = sb.ModsDir,
            SaveSlotsBuiltIn = saveSlots,
            ActiveProfileName = activeProfile ?? "",
            GameSourcePath = sb.Root,  // keeps the Decomp autodetect off the real Desktop
        };
        if (profiles.Length == 0 && activeProfile != null)
            profiles = new[] { new ModProfile { Name = activeProfile, CreatedAt = DateTime.UtcNow } };
        foreach (var p in profiles) p.SaveMetadataOnly();
        var session = new ManagerSession(settings, prompt, isFirstRun)
        {
            Launcher = launcher,
            GameMonitor = new GameSessionMonitor(processes ?? new FakeProcessSource(),
                delay: (_, _) => Task.CompletedTask),
            DecompDetector = () => "",
        };
#if AI_RESOLVER
        session.ClaudeDetector = () => { };
#endif
        session.ReloadProfiles();
        return session;
    }

    /// <summary>A modloader.gd beside the game that knows the skip-launcher marker.</summary>
    internal static void InstallLoader(Sandbox sb, bool skipUi = true)
        => File.WriteAllText(Path.Combine(sb.GameDir, "modloader.gd"),
            "const MODLOADER_VERSION := \"3.4.2\"\n"
            + (skipUi ? $"var marker = \"{MmlInstall.SkipUiOnceFile}\"\n" : ""));
}

public class LaunchTests
{
    [Fact]
    public void Launch_snapshots_a_checkpoint_writes_the_skip_marker_and_launches_the_app_id()
    {
        using var sb = new Sandbox();
        LaunchKit.InstallLoader(sb);
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n");
        var launcher = new RecordingLauncher { AppId = 777 };
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), launcher);
        var said = new List<(StatusArea, string)>();
        session.Status += (a, m) => said.Add((a, m));

        Assert.True(session.LaunchGame());

        Assert.Equal(new[] { 777 }, launcher.Launched);
        Assert.True(File.Exists(Path.Combine(sb.GameDir, MmlInstall.SkipUiOnceFile)));
        Assert.True(CrashCheckpoint.Exists(CheckpointSlot.LastLaunch));
        Assert.Contains(said, s => s.Item1 == StatusArea.Launch && s.Item2.Contains("app id 777")
            && s.Item2.Contains("launcher skipped"));
    }

    [Fact]
    public void Launch_leaves_the_marker_out_when_the_loader_cannot_skip_its_launcher()
    {
        using var sb = new Sandbox();
        LaunchKit.InstallLoader(sb, skipUi: false);
        var launcher = new RecordingLauncher();
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), launcher);

        Assert.True(session.LaunchGame());

        Assert.Single(launcher.Launched);
        Assert.False(File.Exists(Path.Combine(sb.GameDir, MmlInstall.SkipUiOnceFile)));
    }

    [Fact]
    public void Launch_without_a_steam_app_id_tells_the_user_and_launches_nothing()
    {
        using var sb = new Sandbox();
        LaunchKit.InstallLoader(sb);
        var prompt = new ScriptedPrompt();
        var launcher = new RecordingLauncher { AppId = 0 };
        var session = LaunchKit.Open(sb, prompt, launcher);

        Assert.False(session.LaunchGame());

        Assert.Empty(launcher.Launched);
        Assert.Contains("inform:Can't launch via Steam", prompt.Asked);
        Assert.False(File.Exists(Path.Combine(sb.GameDir, MmlInstall.SkipUiOnceFile)));
    }

    [Fact]
    public void A_failed_steam_launch_takes_the_skip_marker_back()
    {
        using var sb = new Sandbox();
        LaunchKit.InstallLoader(sb);
        var prompt = new ScriptedPrompt();
        var launcher = new RecordingLauncher { LaunchSucceeds = false };
        var session = LaunchKit.Open(sb, prompt, launcher);

        Assert.False(session.LaunchGame());

        Assert.Contains("inform:Launch failed", prompt.Asked);
        Assert.False(File.Exists(Path.Combine(sb.GameDir, MmlInstall.SkipUiOnceFile)));
    }

    [Fact]
    public void Steam_app_id_is_read_from_the_appmanifest_whose_installdir_matches()
    {
        using var sb = new Sandbox();
        var steamapps = Path.Combine(sb.Root, "steamapps");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_111.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"Other Game\"\n}");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1963610.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"Road to Vostok\"\n}");

        Assert.Equal(1963610, new SteamGameLauncher().FindAppId(sb.ModsDir));
    }

    [Fact]
    public void Readiness_counts_missing_dependencies_and_flags_save_slots_off()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: "[dependencies]\nrequired=\"beta, gamma\"");
        sb.AddMod("gamma", "1.0");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());
        session.Refresh();

        var r = session.GetLaunchReadiness();

        Assert.True(r.ModsFolderFound);
        Assert.True(r.LoadOrderValid);
        Assert.Equal(2, r.EnabledCount);
        Assert.Equal(new[] { "beta" }, r.MissingDependencyIds);
        Assert.Contains("Built-in save slots are off", r.SaveSlotsWarning);
        Assert.False(r.AllClear);
    }

    // ── the crash watch ───────────────────────────────────────────────

    private static (GameSessionMonitor Monitor, FakeGameProcess Game, Action<double> Advance) Monitor(int exitCode)
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var game = new FakeGameProcess { ExitCode = exitCode };
        var monitor = new GameSessionMonitor(new FakeProcessSource { Game = game },
            () => now, (_, _) => Task.CompletedTask);
        return (monitor, game, s => now = t0.AddSeconds(s));
    }

    [Theory]
    [InlineData(0, 120, GameExitKind.CleanExit)]
    [InlineData(0, 20, GameExitKind.LikelyCrash)]   // a quick quit-out isn't a real session
    [InlineData(3, 600, GameExitKind.LikelyCrash)]
    public async Task Monitor_classifies_how_the_session_ended(int exitCode, double seconds, GameExitKind expected)
    {
        var (monitor, game, advance) = Monitor(exitCode);
        GameExit? crash = null, clean = null;
        monitor.CrashDetected += e => crash = e;
        monitor.CleanExit += e => clean = e;

        var watch = monitor.WatchAsync();
        advance(seconds);
        game.Exit.SetResult();
        var exit = await watch;

        Assert.Equal(expected, exit.Kind);
        Assert.Equal(exitCode, exit.ExitCode);
        Assert.Equal(expected == GameExitKind.CleanExit ? exit : null, clean);
        Assert.Equal(expected == GameExitKind.LikelyCrash ? exit : null, crash);
        Assert.True(game.Disposed);
    }

    [Fact]
    public async Task Monitor_gives_up_quietly_when_no_game_process_appears()
    {
        var source = new FakeProcessSource();
        var monitor = new GameSessionMonitor(source, delay: (_, _) => Task.CompletedTask);
        var fired = false;
        monitor.CrashDetected += _ => fired = true;

        var exit = await monitor.WatchAsync();

        Assert.Equal(GameExitKind.NotFound, exit.Kind);
        Assert.Equal(60, source.Polls);  // once a second for a minute
        Assert.False(fired);
    }

    [Fact]
    public async Task A_crash_after_launch_offers_the_restore_and_rolls_the_cfg_back()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=true\n");
        sb.AddMod("alpha", "1.0");
        var (monitor, game, advance) = Monitor(exitCode: 1);
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher(),
            profiles: new ModProfile
            {
                Name = "Default", CreatedAt = DateTime.UtcNow,
                Mods = { new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = true } },
            });
        session.GameMonitor = monitor;
        session.Refresh();

        Assert.True(session.LaunchGame());
        // Something changes the cfg while the game runs, then it crashes.
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=false\n");
        advance(15);
        game.Exit.SetResult();
        var exit = await session.GameWatchTask!;

        Assert.Equal(GameExitKind.LikelyCrash, exit.Kind);
        Assert.Contains("confirm:Possible crash detected", prompt.Asked);
        Assert.Contains("confirm:Restore last-launch checkpoint", prompt.Asked);
        Assert.True(sb.LoadCfg().IsEnabled("alpha", "1.0"));
        Assert.False(CrashCheckpoint.Exists(CheckpointSlot.LastLaunch));  // the restored slot is cleared
    }

    [Fact]
    public async Task A_clean_session_promotes_the_launch_checkpoint_to_last_known_good()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n");
        var (monitor, game, advance) = Monitor(exitCode: 0);
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());
        session.GameMonitor = monitor;

        session.LaunchGame();
        advance(3600);
        game.Exit.SetResult();
        await session.GameWatchTask!;

        Assert.True(CrashCheckpoint.Exists(CheckpointSlot.LastKnownGood));
        Assert.DoesNotContain(prompt.Asked, a => a.Contains("crash"));
    }

    [Fact]
    public async Task Stopping_the_watch_ignores_the_session()
    {
        using var sb = new Sandbox();
        var (monitor, game, _) = Monitor(exitCode: 1);
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());
        session.GameMonitor = monitor;

        session.LaunchGame();
        session.StopWatchingGame();
        var exit = await session.GameWatchTask!;

        Assert.Equal(GameExitKind.Lost, exit.Kind);
        Assert.Empty(prompt.Asked);
    }
}
