using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class LogAnalysisTests
{
    private static string LogsDir => SupportPackage.GodotLogsDir;

    private static string WriteLog(string name, string text, DateTime written)
    {
        Directory.CreateDirectory(LogsDir);
        var path = Path.Combine(LogsDir, name);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private const string LoaderLog = """
        Godot Engine v4.6.3.stable.official - https://godotengine.org
        [ModLoader][Info] Metro Mod Loader v3.2.0
        [ModLoader][Info] === Load Order ===
        [ModLoader][Info] [1] Alpha | alpha.vmz [priority=0]
        [ModLoader][Info] [2] Beta | beta.vmz [priority=5]
        [Overrides] Applied: res://Scripts/AI.gd -> res://mods/alpha/AI.gd [Alpha]
        [Overrides] Applied: res://Scripts/AI.gd -> res://mods/beta/AI.gd [Beta]
        Hook declared: res://Scripts/Camera.gd :: _ready [Alpha]
        Hook declared: res://Scripts/Camera.gd :: _ready [Beta]
        Hook declared: res://Scripts/Camera.gd :: _ready [Gamma]
        SCRIPT ERROR: Invalid call in res://mods/alpha/Main.gd
        WARNING: something odd
        """;

    [Fact]
    public void Picker_starts_on_the_newest_log_the_loader_ran_in()
    {
        using var sb = new Sandbox();
        var session = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        Assert.Equal(-1, ManagerSession.PreferredLogIndex(session.AvailableLogFiles()));
        var report0 = session.AnalyzeLog();
        Assert.False(report0.LogFound);
        Assert.StartsWith("No Godot log found", report0.HeaderText);

        var older = WriteLog("godot_old.log", LoaderLog, DateTime.UtcNow.AddHours(-1));
        WriteLog("godot.log", "Godot Engine v4.6.3.stable\n", DateTime.UtcNow);

        var logs = session.AvailableLogFiles();
        Assert.Equal(new[] { "godot.log", "godot_old.log" }, logs.Select(l => Path.GetFileName(l.Path)));
        Assert.StartsWith("godot.log   (", logs[0].Label);
        Assert.Equal(1, ManagerSession.PreferredLogIndex(logs));
        Assert.Equal(older, session.AnalyzeLog().LogPath);
    }

    [Fact]
    public void Analysis_has_the_overwrite_map_issues_and_header()
    {
        using var sb = new Sandbox();
        var session = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        var path = WriteLog("godot.log", LoaderLog, DateTime.UtcNow);

        var r = session.AnalyzeLog(path);

        Assert.True(r.LogFound);
        Assert.Contains("Mod loader v3.2.0", r.HeaderText);
        Assert.Contains("2 mods loaded", r.HeaderText);
        Assert.Contains("1 hook clash(es)", r.HeaderText);
        var hook = Assert.Single(r.HookClashes);
        Assert.Equal(("res://Scripts/Camera.gd :: _ready", 3, true), (hook.Target, hook.ModCount, hook.IsCrowded));
        Assert.Equal("Alpha  →  Beta  →  Gamma", hook.Mods);
        Assert.Equal("", r.HookNote);
        var clash = Assert.Single(r.OverrideClashes);
        Assert.Equal(("res://Scripts/AI.gd", "Beta"), (clash.VanillaPath, clash.Winner));
        Assert.Equal(new[] { LogIssueLevel.Error, LogIssueLevel.Warning }, r.Issues.Select(i => i.Level));
        Assert.Equal("alpha", r.Issues[0].Mod);
        Assert.Equal("", r.IssueNote);

        // A 3.4+ loader that logged no hooks: the note says why.
        var quiet = WriteLog("quiet.log", "[ModLoader][Info] Metro Mod Loader v3.4.1\n", DateTime.UtcNow);
        var q = session.AnalyzeLog(quiet);
        Assert.Contains("hooks not logged", q.HeaderText);
        Assert.Equal(GodotLogAnalyzer.HooksNotLoggedHint, q.HookNote);
        Assert.NotEqual("", q.OverrideNote);
        Assert.NotEqual("", q.IssueNote);
    }

    [Fact]
    public void Applying_a_proposed_order_writes_priorities_after_asking()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", priority: 1);
        sb.AddMod("beta", "1.0", priority: 2);
        var prompt = new ScriptedPrompt();
        var session = RefreshTests.OpenWithProfile(sb, prompt);
        session.Refresh();
        var order = new[] { new ProposedPriorityChange("alpha", 10), new ProposedPriorityChange("beta", 3), new ProposedPriorityChange("ghost", 1) };

        prompt.ConfirmAnswer = false;
        Assert.Equal(0, session.ApplyProposedOrder(order));
        Assert.Equal(1, sb.LoadCfg().Priority("alpha", "1.0"));

        prompt.ConfirmAnswer = true;
        Assert.Equal(2, session.ApplyProposedOrder(order));
        var cfg = sb.LoadCfg();
        Assert.Equal(10, cfg.Priority("alpha", "1.0"));
        Assert.Equal(3, cfg.Priority("beta", "1.0"));
        Assert.Equal(10, session.Registry.FindById("alpha")!.Priority);
        Assert.Contains("inform:Load order applied", prompt.Asked);

        // Nothing installed: told, nothing asked.
        prompt.Asked.Clear();
        Assert.Equal(0, session.ApplyProposedOrder(new[] { new ProposedPriorityChange("ghost", 1) }));
        Assert.Equal(new[] { "inform:Nothing to apply" }, prompt.Asked);
    }
}
