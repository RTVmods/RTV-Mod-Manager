using VostokModManager.Domain;
using VostokModManager.Services;
using static VostokModManager.Tests.UpdatesTests;

namespace VostokModManager.Tests;

public class ConflictViewTests
{
    private const string AiGd = "res://Scripts/AI.gd";

    /// <summary>Alpha and Beta both claim the replace hook on AI.Loot (a
    /// real collision); they also share an -post hook, which is not.</summary>
    private static ManagerSession HookPair(Sandbox sb)
    {
        sb.AddMod("alpha", "1.0", name: "Alpha", priority: 1,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"Loot, Death\"",
            files: new Dictionary<string, string>
            {
                ["mods/Alpha/Main.gd"] = "extends Node\nfunc _ready():\n\tlib.hook(\"ai-loot\", _loot)\n\tlib.hook(\"ai-death-post\", _dead)\n",
            });
        sb.AddMod("beta", "1.0", name: "Beta", priority: 9,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"Loot\"",
            files: new Dictionary<string, string>
            {
                ["mods/Beta/Main.gd"] = "extends Node\nfunc _ready():\n\tlib.hook_many({\"ai-loot\": _loot, \"ai-death-post\": _dead})\n",
            });
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
        Assert.Equal($"Two mods replace `{AiGd}::Loot`", hook.Title);
        Assert.Equal("replace-hook collision · only the first to register runs", hook.Subtitle);
        Assert.Equal(new[] { "Alpha", "Beta" }, hook.ModNames);   // earliest-loading first
        // The loader keeps the FIRST registration of a replace hook.
        Assert.Equal(("Alpha", WinnerTone.Winner), (hook.Winner.Text, hook.Winner.Tone));
        Assert.Equal($"[hook_collision]  key: {AiGd}::Loot", hook.RawTooltip);
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
    public void Hook_detail_explains_the_replace_slot_and_who_keeps_it()
    {
        using var sb = new Sandbox();
        var s = HookPair(sb);
        var hook = s.LastConflicts.Single(c => c.Type == ConflictDetector.TYPE_HOOK_COLLISION);

        var d = s.GetConflictDetail(hook);

        Assert.StartsWith("A replace hook swaps out a game method", d.Explanation);
        Assert.Equal(new[]
            {
                ("Alpha", "replace hook \"ai-loot\": registers first, runs"),
                ("Beta", "replace hook \"ai-loot\": rejected, never runs"),
            },
            d.Values.Select(v => (v.ModName, v.Value)));
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

        var d = s.GetConflictDetail(c);

        Assert.StartsWith("Both mods register an autoload", d.Explanation);
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
