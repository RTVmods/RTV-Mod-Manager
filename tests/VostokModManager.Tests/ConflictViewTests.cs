using VostokModManager.Domain;
using VostokModManager.Services;
using static VostokModManager.Tests.UpdatesTests;

namespace VostokModManager.Tests;

public class ConflictViewTests
{
    private const string AiGd = "res://Scripts/AI.gd";

    private static ManagerSession HookPair(Sandbox sb)
    {
        sb.AddMod("alpha", "1.0", name: "Alpha", priority: 1,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"_ready,_process\"");
        sb.AddMod("beta", "1.0", name: "Beta", priority: 9,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"_process\"");
        sb.AddMod("gamma", "1.0", name: "Gamma", extraManifest: "[dependencies]\nrequired=\"ghost\"");
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        return s;
    }

    [Fact]
    public void Views_are_tiered_and_name_the_last_loading_mod_as_winner()
    {
        using var sb = new Sandbox();
        var s = HookPair(sb);

        var views = s.GetConflictViews();

        // Blocking (missing dependency) before Behavior (hook).
        Assert.Equal(new[] { ConflictSeverity.Blocking, ConflictSeverity.Behavior },
            views.Select(v => v.Severity));
        var missing = views[0];
        Assert.Equal("Missing dependency: `ghost`", missing.Title);
        Assert.Equal(("(needs `ghost`)", WinnerTone.HardFail), (missing.Winner.Text, missing.Winner.Tone));
        Assert.Equal(new[] { "Gamma", "ghost" }, missing.ModNames);

        var hook = views[1];
        Assert.Equal($"Two mods hook `{AiGd}`", hook.Title);
        Assert.Equal("hook collision · last to load wins the chain", hook.Subtitle);
        Assert.Equal(new[] { "Alpha", "Beta" }, hook.ModNames);   // earliest-loading first
        Assert.Equal(("Beta", WinnerTone.Winner), (hook.Winner.Text, hook.Winner.Tone));
        Assert.Equal($"[hook_collision]  key: {AiGd}", hook.RawTooltip);
        Assert.False(hook.HasResolveButton);
        Assert.Equal("AI resolve only handles file_overlap conflicts in v1.", hook.ResolveHint);

        Assert.Equal("Conflicts: 2 (1 load-blocking · 1 behavior)", s.ConflictsStatusText());
        Assert.Equal("●  LOAD-BLOCKING (3)", ManagerSession.TierLabel(ConflictSeverity.Blocking, 3));
    }

    [Fact]
    public void Conflicts_involving_a_mod()
    {
        using var sb = new Sandbox();
        var s = HookPair(sb);

        Assert.Single(s.ConflictsInvolving("ALPHA"));
        Assert.Single(s.ConflictsInvolving(s.Registry.FindById("gamma")!));
        Assert.Single(s.ConflictsInvolving("ghost"));
        Assert.Empty(s.ConflictsInvolving("nobody"));
    }

    [Fact]
    public void Duplicate_ids_match_on_the_id_and_on_the_file_name()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", fileName: "a1.vmz");
        sb.AddMod("alpha", "1.1", fileName: "a2.vmz", inDisabledFolder: true);
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();

        var dup = Assert.Single(s.ConflictsInvolving("alpha"),
            v => v.Type == ConflictDetector.TYPE_DUPLICATE_MOD_ID);
        Assert.Equal("a1.vmz (loader picks first)", dup.Winner.Text);
        Assert.Contains(s.ConflictsInvolving("", "a2.vmz"), v => v.Type == ConflictDetector.TYPE_DUPLICATE_MOD_ID);
    }

    [Fact]
    public void Hook_detail_splits_the_file_level_collision_by_method()
    {
        using var sb = new Sandbox();
        var s = HookPair(sb);
        var hook = s.LastConflicts.Single(c => c.Type == ConflictDetector.TYPE_HOOK_COLLISION);

        var d = s.GetConflictDetail(hook, () => "");

        Assert.True(d.IsHook);
        Assert.Equal(new[] { ("_process", true), ("_ready", false) },
            d.Methods.Select(m => (m.Method, m.IsCollision)));
        Assert.Equal(new[] { "Alpha", "Beta" }, d.Methods[0].ModNames);
        Assert.Equal("⚠ COLLISION (2 mods)", d.Methods[0].StatusText);
        Assert.StartsWith("1 method(s) are hooked by more than one mod", d.Legend);
        Assert.Equal("Runtime (last play session) — no godot.log found", d.RuntimeNote);
        Assert.Equal("(no log)", Assert.Single(d.RuntimeRows).Method);
        Assert.EndsWith("mods: Alpha, Beta", d.Subtitle);
    }

    [Fact]
    public void Other_conflicts_list_each_mods_value()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", name: "Alpha", extraManifest: "[autoload]\nBus=\"res://alpha/bus.gd\"");
        sb.AddMod("beta", "1.0", extraManifest: "[autoload]\nBus=\"res://beta/bus.gd\"");
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        var c = s.LastConflicts.Single(x => x.Type == ConflictDetector.TYPE_AUTOLOAD_COLLISION);

        var d = s.GetConflictDetail(c, () => throw new InvalidOperationException("not read for autoloads"));

        Assert.False(d.IsHook);
        Assert.Equal(new[] { ("Alpha", "res://alpha/bus.gd"), ("beta", "res://beta/bus.gd") },
            d.Values.Select(v => (v.ModName, v.Value)));
    }

    [Fact]
    public void Severity_tiers()
    {
        Assert.Equal(ConflictSeverity.Blocking, ManagerSession.SeverityOf(ConflictDetector.TYPE_CLASS_NAME_COLLISION));
        Assert.Equal(ConflictSeverity.Info, ManagerSession.SeverityOf(ConflictDetector.TYPE_DEPENDENCY_ORDER));
        Assert.Equal(ConflictSeverity.Behavior, ManagerSession.SeverityOf(ConflictDetector.TYPE_FILE_OVERLAP));
    }
}
