using VostokModManager.Domain;
using static VostokModManager.Tests.UpdatesTests;

namespace VostokModManager.Tests;

/// <summary>What counts as a conflict with Mod Loader 3.4: sharing a game
/// script is fine (hooks run side by side, extends chain); only a claimed
/// replace slot, an override that cuts another off, or a full take-over
/// makes one mod's change disappear.</summary>
public class ConflictDetectorTests
{
    private const string AiGd = "res://Scripts/AI.gd";
    private const string InterfaceGd = "res://Scripts/Interface.gd";

    private static List<ConflictDetector.Conflict> Detect(Sandbox sb)
    {
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        return s.LastConflicts;
    }

    private static Dictionary<string, string> Gd(string path, string source) => new() { [path] = source };

    private static string Hooks(params string[] names)
        => "extends Node\nfunc _ready():\n" + string.Concat(names.Select(n => $"\tlib.hook(\"{n}\", _cb)\n"));

    [Fact]
    public void Mods_sharing_a_script_through_pre_and_post_hooks_do_not_conflict()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", priority: 1,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"WeaponDamage, Death, Loot\"",
            files: Gd("mods/A/Main.gd", Hooks("ai-weapondamage-pre", "ai-death-post", "ai-loot")));
        sb.AddMod("beta", "1.0", priority: 2,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"WeaponDamage, Death, ExplosionDamage\"",
            files: Gd("mods/B/Main.gd", Hooks("ai-weapondamage-pre", "ai-death-pre", "ai-explosiondamage")));

        Assert.Empty(Detect(sb));
    }

    [Fact]
    public void Two_mods_claiming_one_replace_slot_conflict_and_the_first_to_load_keeps_it()
    {
        using var sb = new Sandbox();
        sb.AddMod("late", "1.0", priority: 5,
            files: Gd("mods/L/Main.gd", Hooks("controller-crouch", "controller-crouch-post")));
        sb.AddMod("early", "1.0", priority: 1,
            extraManifest: "[hooks]\n\"res://Scripts/Controller.gd\"=\"Crouch\"",
            files: Gd("mods/E/Main.gd",
                "extends Node\nconst HOOKS := {\"controller-crouch\": \"_c\"}\n"));

        var c = Assert.Single(Detect(sb));

        Assert.Equal(ConflictDetector.TYPE_HOOK_COLLISION, c.Type);
        Assert.Equal("res://Scripts/Controller.gd::Crouch", c.Key);
        Assert.Equal(new[] { "early", "late" }, c.ModIds);
    }

    [Fact]
    public void A_hook_shaped_string_on_a_script_no_mod_touches_is_ignored()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", files: Gd("mods/A/Main.gd", "extends Node\nvar dir = \"left-right\"\n"));
        sb.AddMod("beta", "1.0", files: Gd("mods/B/Main.gd", "extends Node\nvar dir = \"left-right\"\n"));

        Assert.Empty(Detect(sb));
    }

    // ── overrides installed over a game script ────────────────────────

    private static string Patch(string target, params (string Fn, bool Super)[] fns)
        => $"extends \"{target}\"\n" + string.Concat(fns.Select(f =>
            $"func {f.Fn}():\n" + (f.Super ? $"\tsuper.{f.Fn}()\n" : "\tpass\n")));

    private static void ExtendMod(Sandbox sb, string id, int priority, string target, params (string, bool)[] fns)
        => sb.AddMod(id, "1.0", priority: priority,
            extraManifest: $"[script_extend]\n\"{target}\"=\"res://mods/{id}/Interface.gd\"",
            files: Gd($"mods/{id}/Interface.gd", Patch(target, fns)));

    private static void TakeOverMod(Sandbox sb, string id, int priority, string target, params (string, bool)[] fns)
        => sb.AddMod(id, "1.0", priority: priority, files: new Dictionary<string, string>
        {
            [$"mods/{id}/Main.gd"] = $"extends Node\nfunc _ready():\n\tload(\"res://mods/{id}/Patch.gd\").take_over_path(\"{target}\")\n",
            [$"mods/{id}/Patch.gd"] = Patch(target, fns),
        });

    [Fact]
    public void Extends_and_take_overs_that_call_super_chain_without_conflict()
    {
        using var sb = new Sandbox();
        ExtendMod(sb, "tabs", 1, InterfaceGd, ("Open", true), ("UpdateStats", true));
        ExtendMod(sb, "cash", 2, InterfaceGd, ("Open", true), ("CalculateDeal", true));
        TakeOverMod(sb, "fire", 3, InterfaceGd, ("ContextPlace", true), ("Sleep", false));
        TakeOverMod(sb, "games", 4, InterfaceGd, ("ContextPlace", true));

        Assert.Empty(Detect(sb));
    }

    [Fact]
    public void Two_overrides_that_both_skip_super_collide()
    {
        using var sb = new Sandbox();
        ExtendMod(sb, "alpha", 1, InterfaceGd, ("Open", false));
        ExtendMod(sb, "beta", 2, InterfaceGd, ("Open", false));

        var c = Assert.Single(Detect(sb));

        Assert.Equal(ConflictDetector.TYPE_OVERRIDE_COLLISION, c.Type);
        Assert.Equal($"{InterfaceGd}::Open", c.Key);
        Assert.Equal(new[] { "alpha", "beta" }, c.ModIds);
    }

    [Theory]
    [InlineData(1, 2, false)]  // the one skipping super is applied first: both run
    [InlineData(2, 1, true)]   // it is applied last: the chaining override never runs
    public void An_extend_that_skips_super_must_be_applied_before_one_that_chains(
        int replacerPriority, int chainerPriority, bool collides)
    {
        using var sb = new Sandbox();
        ExtendMod(sb, "replacer", replacerPriority, InterfaceGd, ("Open", false));
        ExtendMod(sb, "chainer", chainerPriority, InterfaceGd, ("Open", true));

        var conflicts = Detect(sb);

        if (!collides) { Assert.Empty(conflicts); return; }
        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictDetector.TYPE_OVERRIDE_COLLISION, c.Type);
    }

    [Fact]
    public void A_runtime_take_over_mixed_with_a_chain_is_an_ordering_note()
    {
        using var sb = new Sandbox();
        TakeOverMod(sb, "replacer", 1, InterfaceGd, ("ContextPlace", false));
        TakeOverMod(sb, "chainer", 2, InterfaceGd, ("ContextPlace", true));

        var c = Assert.Single(Detect(sb));

        Assert.Equal(ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT, c.Type);
        Assert.Equal("replacer", c.Details["before"]);
        Assert.Equal("chainer", c.Details["after"]);
    }

    [Fact]
    public void A_take_over_with_a_script_that_does_not_extend_the_game_script_wipes_other_mods_out()
    {
        using var sb = new Sandbox();
        sb.AddMod("wiper", "1.0", priority: 1, files: new Dictionary<string, string>
        {
            ["mods/W/Main.gd"] = $"extends Node\nfunc _ready():\n\tload(\"res://mods/W/AI.gd\").take_over_path(\"{AiGd}\")\n",
            ["mods/W/AI.gd"] = "extends CharacterBody3D\nfunc Loot():\n\tpass\n",
        });
        sb.AddMod("hooker", "1.0", priority: 2,
            extraManifest: $"[hooks]\n\"{AiGd}\"=\"Death\"",
            files: Gd("mods/H/Main.gd", Hooks("ai-death-post")));

        var c = Assert.Single(Detect(sb));

        Assert.Equal(ConflictDetector.TYPE_TAKE_OVER_COLLISION, c.Type);
        Assert.Equal(AiGd, c.Key);
        Assert.Equal(new[] { "wiper", "hooker" }, c.ModIds);
    }

    [Fact]
    public void A_full_take_over_nobody_else_touches_is_not_listed()
    {
        using var sb = new Sandbox();
        sb.AddMod("wiper", "1.0", files: new Dictionary<string, string>
        {
            ["mods/W/Main.gd"] = $"extends Node\nfunc _ready():\n\tload(\"res://mods/W/AI.gd\").take_over_path(\"{AiGd}\")\n",
            ["mods/W/AI.gd"] = "extends CharacterBody3D\n",
        });
        sb.AddMod("other", "1.0", files: Gd("mods/O/Main.gd", Hooks("controller-crouch-post")));

        Assert.Empty(Detect(sb));
    }

    [Fact]
    public void Subclasses_of_a_game_script_for_a_mods_own_items_are_not_overrides()
    {
        // Cat Bed and the board games each extend Pickup.gd for their own
        // item scripts; nothing is installed over Pickup.gd itself.
        using var sb = new Sandbox();
        sb.AddMod("catbed", "1.0", files: Gd("mods/C/CatKitPickup.gd",
            Patch("res://Scripts/Pickup.gd", ("UpdateTooltip", false))));
        sb.AddMod("games", "1.0", files: Gd("mods/G/ChessBoardPickup.gd",
            Patch("res://Scripts/Pickup.gd", ("UpdateTooltip", true))));

        Assert.Empty(Detect(sb));
    }
}
