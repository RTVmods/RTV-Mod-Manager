using VostokModManager.Services;

namespace VostokModManager.Tests;

public class ProfileDiffTests
{
    private static ManagerSession OpenAB(Sandbox sb, ScriptedPrompt prompt)
    {
        var s = RefreshTests.OpenWithProfile(sb, prompt,
            ProfileKit.Pm("shared", "1.0"),
            ProfileKit.Pm("changed", "1.0", priority: 1),
            ProfileKit.Pm("only-a", "1.0"));
        ProfileKit.SaveProfile("B",
            ProfileKit.Pm("shared", "1.0"),
            ProfileKit.Pm("changed", "2.0", enabled: false, priority: 1),
            ProfileKit.Pm("only-b", "3.0", source: "vostokmods:only-b"));
        s.ReloadProfiles();
        return s;
    }

    [Fact]
    public void Two_profiles_compare_like_the_old_diff_dialog()
    {
        using var sb = new Sandbox();
        var s = OpenAB(sb, new ScriptedPrompt());

        var cmp = s.CompareProfiles(new[] { ProfileKit.Named(s, "Default"), ProfileKit.Named(s, "B") });

        Assert.Equal(new[] { "only-a", "changed", "only-b", "shared" }, cmp.Rows.Select(r => r.ModId));
        Assert.Equal("Only in left profile", cmp.Rows[0].What);
        Assert.Equal("version 1.0 → 2.0, enabled → disabled", cmp.Rows[1].What);
        Assert.Equal("Only in right profile", cmp.Rows[2].What);
        Assert.False(cmp.Rows[3].HasDifferences);
        Assert.Equal("(absent)", cmp.Rows[0].Cells[1].Text);
        Assert.Equal("v2.0  prio 1  off", cmp.Rows[1].Cells[1].Text);
        Assert.Equal(3, cmp.Filter(onlyDifferences: true).Count());
        Assert.Equal("changed", Assert.Single(cmp.Filter(false, "2.0"), r => r.ModId == "changed").ModId);
        Assert.Equal("1 only in 'Default'  ·  1 only in 'B'  ·  1 different — 3 differences total.", cmp.Summary);
    }

    [Fact]
    public void Three_profiles_form_a_matrix()
    {
        using var sb = new Sandbox();
        var s = OpenAB(sb, new ScriptedPrompt());
        ProfileKit.SaveProfile("C", ProfileKit.Pm("shared", "1.0"), ProfileKit.Pm("changed", "3.0", priority: 1));
        s.ReloadProfiles();

        var cmp = s.CompareProfiles(new[]
        {
            ProfileKit.Named(s, "Default"), ProfileKit.Named(s, "B"), ProfileKit.Named(s, "C"),
        });

        var changed = cmp.Rows.Single(r => r.ModId == "changed");
        Assert.Equal(3, changed.Cells.Count);
        Assert.Equal("versions 1.0 / 2.0 / 3.0, enabled on / off / on", changed.What);
        Assert.Equal("Not in 'B', 'C'", cmp.Rows.Single(r => r.ModId == "only-a").What);
        Assert.False(cmp.Rows.Single(r => r.ModId == "shared").HasDifferences);
    }

    [Fact]
    public void Copying_entries_adds_missing_ones_and_overwrites_different_ones()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = OpenAB(sb, prompt);
        var a = ProfileKit.Named(s, "Default");
        var b = ProfileKit.Named(s, "B");

        var copied = s.CopyProfileEntries(b, a, new[] { "changed", "only-b", "only-a" });

        Assert.Equal(2, copied);
        Assert.Contains("inform:Copy done", prompt.Asked);
        var saved = ProfileKit.OnDisk("Default");
        var changed = saved.Mods.Single(m => m.ModId == "changed");
        Assert.Equal(("2.0", false), (changed.Version, changed.IsEnabled));
        Assert.Equal("vostokmods:only-b", saved.Mods.Single(m => m.ModId == "only-b").Source);
        Assert.False(s.CompareProfiles(new[] { a, b }).Rows.Single(r => r.ModId == "changed").HasDifferences);
    }

    [Fact]
    public void Copying_only_absent_rows_copies_nothing()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = OpenAB(sb, prompt);

        Assert.Equal(0, s.CopyProfileEntries(ProfileKit.Named(s, "B"), ProfileKit.Named(s, "Default"), new[] { "only-a" }));
        Assert.Contains("inform:Copy skipped", prompt.Asked);
    }

    [Fact]
    public void Copying_versions_leaves_enabled_state_and_priority_alone()
    {
        using var sb = new Sandbox();
        var s = OpenAB(sb, new ScriptedPrompt());

        Assert.Equal(1, s.CopyProfileVersions(ProfileKit.Named(s, "B"), ProfileKit.Named(s, "Default"),
            new[] { "changed", "shared", "only-b" }));

        var changed = ProfileKit.OnDisk("Default").Mods.Single(m => m.ModId == "changed");
        Assert.Equal(("2.0", true), (changed.Version, changed.IsEnabled));
    }

    [Fact]
    public void Entries_can_be_removed_by_id()
    {
        using var sb = new Sandbox();
        var s = OpenAB(sb, new ScriptedPrompt());

        Assert.True(s.RemoveProfileEntries(ProfileKit.Named(s, "B"), new[] { "only-b", "nope" }, confirm: false));
        Assert.DoesNotContain(ProfileKit.OnDisk("B").Mods, m => m.ModId == "only-b");
    }
}
