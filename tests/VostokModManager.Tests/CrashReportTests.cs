using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public sealed class CrashReportTests
{
    private const string CrashedLog =
        "Godot Engine v4.6.stable.official - https://godotengine.org\n"
        + "[ModLoader][Info] === Load Order ===\n"
        + "[ModLoader][Info] [1] Alpha | alpha-1.0.vmz [priority=0]\n"
        + "[ModLoader][Info] [2] Beta Mod | beta-1.0.vmz [priority=1]\n"
        + "SCRIPT ERROR: Invalid call. Nonexistent function 'foo' in base 'Node'.\n"
        + "   at: _ready (res://mods/beta/Main.gd:12)\n"
        + "[Alpha] saved state\n"
        + "[Beta Mod] spawning 40 crates\n"
        + "[ModLoader] frame\n"
        + "================================================================\n"
        + "CrashHandlerException: Program crashed with signal 11\n"
        + "Engine version: Godot Engine v4.6.stable.official\n"
        + "Dumping the backtrace. Please include this when reporting the bug to the project developer.\n"
        + "[1] Node::call\n"
        + "[2] Object::emit_signal\n"
        + "-- END OF BACKTRACE --\n"
        + "================================================================\n";

    private static string WriteLog(string text, string name = "godot.log")
    {
        Directory.CreateDirectory(SupportPackage.GodotLogsDir);
        var path = Path.Combine(SupportPackage.GodotLogsDir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Reads_the_crash_block_and_the_lines_before_it()
    {
        using var sb = new Sandbox();
        var info = CrashLog.Read(WriteLog(CrashedLog));

        Assert.True(info.HasCrashSignature);
        Assert.Equal("CrashHandlerException: Program crashed with signal 11", info.CrashLine);
        Assert.Contains("[1] Node::call", info.Backtrace);
        Assert.Equal("[ModLoader] frame", info.Tail[^1]);
        Assert.DoesNotContain(info.Tail, l => l.StartsWith("====="));
        // Most recent first, the loader's own tag left out.
        Assert.Equal(new[] { "Beta Mod", "Alpha" }, info.TailTags.Take(2));
        Assert.DoesNotContain("ModLoader", info.TailTags);
    }

    [Fact]
    public void A_log_without_the_crash_block_is_not_a_crash()
    {
        using var sb = new Sandbox();
        var info = CrashLog.Read(WriteLog("Godot Engine v4.6\n[Alpha] quit\n"));
        Assert.True(info.Found);
        Assert.False(info.HasCrashSignature);
        Assert.Equal(new[] { "Alpha" }, info.TailTags);
    }

    [Fact]
    public void Exit_codes_get_a_plain_meaning()
    {
        Assert.StartsWith("Access violation", CrashLog.DescribeExitCode(unchecked((int)0xC0000005)));
        Assert.StartsWith("Stack overflow", CrashLog.DescribeExitCode(unchecked((int)0xC00000FD)));
        Assert.Equal("", CrashLog.DescribeExitCode(42));
    }

    /// <summary>alpha was on at the last clean session; beta was turned
    /// on since. Both are in the Default profile now.</summary>
    private static ManagerSession AlphaGoodBetaNew(Sandbox sb, ScriptedPrompt prompt)
    {
        sb.AddMod("alpha", "1.0", name: "Alpha");
        sb.AddMod("beta", "1.0", name: "Beta Mod", files: new Dictionary<string, string> { ["mods/beta/Main.gd"] = "extends Node" });
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=true\nbeta@1.0=false\n");
        Assert.True(CrashCheckpoint.Capture(ModConfig.DefaultPath, "Default", ""));
        Assert.True(CrashCheckpoint.PromoteToKnownGood());
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=true\nbeta@1.0=true\n");
        var s = LaunchKit.Open(sb, prompt, new RecordingLauncher(),
            profiles: new ModProfile
            {
                Name = "Default", CreatedAt = DateTime.UtcNow,
                Mods =
                {
                    new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = true },
                    new ProfileMod { ModId = "beta", Version = "1.0", IsEnabled = true },
                },
            });
        s.Refresh();
        return s;
    }

    [Fact]
    public void The_report_points_at_the_mod_that_logged_last_errored_and_was_turned_on_since_the_clean_session()
    {
        using var sb = new Sandbox();
        var s = AlphaGoodBetaNew(sb, new ScriptedPrompt());
        WriteLog(CrashedLog);

        var report = s.BuildCrashReport(new GameExit(GameExitKind.LikelyCrash, unchecked((int)0xC0000005), 95));

        Assert.Equal("The game crashed", report.Headline);
        Assert.Contains("exit code", report.Summary);
        Assert.StartsWith("Access violation", report.ExitMeaning);
        var top = report.Suspects[0];
        Assert.Equal("beta", top.ModId);
        Assert.Contains("last mod to write to the log before the crash", top.Why);
        Assert.True(top.Likely);
        var alpha = report.Suspects.Single(x => x.ModId == "alpha");
        Assert.False(alpha.Likely);  // only logged a line before beta did
        Assert.Contains("error", top.Why);
        Assert.Contains("turned on since your last clean session", top.Why);
        Assert.True(top.CanTurnOff);
        Assert.Equal(new CrashChange("beta", "Beta Mod", "turned on"), Assert.Single(report.ChangesSinceGood));
        Assert.Equal(CrashAction.RestoreLastKnownGood, report.Steps[0].Action);
        Assert.Contains(report.Steps, st => st.Action == CrashAction.TurnOffSuspects);
        Assert.Contains(report.Steps, st => st.Action == CrashAction.OpenLogAnalysis);
        Assert.Equal(CrashAction.BuildSupportPackage, report.Steps[^1].Action);
    }

    [Fact]
    public void Turning_off_the_suspects_saves_the_cfg_and_the_profile()
    {
        using var sb = new Sandbox();
        var s = AlphaGoodBetaNew(sb, new ScriptedPrompt());

        Assert.Equal(1, s.TurnOffModsAfterCrash(new[] { "beta", "missing-mod" }));

        Assert.False(sb.LoadCfg().IsEnabled("beta", "1.0"));
        Assert.True(sb.LoadCfg().IsEnabled("alpha", "1.0"));
        Assert.False(ModProfile.LoadAll().Single().Mods.Single(m => m.ModId == "beta").IsEnabled);
        Assert.Empty(s.ChangesSinceLastKnownGood());
    }

    [Fact]
    public async Task A_clean_exit_whose_log_has_a_crash_block_is_reported_as_a_crash()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=true\n");
        sb.AddMod("alpha", "1.0", name: "Alpha");
        var game = new FakeGameProcess { ExitCode = 0 };
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());
        session.GameMonitor = new GameSessionMonitor(new FakeProcessSource { Game = game },
            () => now, (_, _) => Task.CompletedTask);
        session.Refresh();
        CrashReport? reported = null;
        session.GameCrashed += r => reported = r;

        Assert.True(session.LaunchGame());
        WriteLog(CrashedLog);
        now = now.AddMinutes(10);
        game.Exit.SetResult();
        var exit = await session.GameWatchTask!;

        Assert.Equal(GameExitKind.LikelyCrash, exit.Kind);
        Assert.NotNull(reported);
        Assert.Same(reported, session.LastCrash);
        Assert.Contains("its log shows a crash", reported!.Summary);
        Assert.False(CrashCheckpoint.Exists(CheckpointSlot.LastKnownGood));  // not promoted
        // Shown once: the startup check doesn't report it again.
        Assert.Null(session.FindUnseenCrash());
    }

    [Fact]
    public void Startup_finds_a_crash_from_a_session_it_did_not_watch_once()
    {
        using var sb = new Sandbox();
        var s = AlphaGoodBetaNew(sb, new ScriptedPrompt());
        WriteLog(CrashedLog);

        var report = s.FindUnseenCrash();

        Assert.NotNull(report);
        Assert.Equal("Your last game session crashed", report!.Headline);
        Assert.False(report.FromWatch);
        s.MarkCrashSeen(report);
        Assert.Null(s.FindUnseenCrash());

        WriteLog("Godot Engine v4.6\n[Alpha] quit\n");  // a later clean session
        Assert.Null(s.FindUnseenCrash());
    }
}
