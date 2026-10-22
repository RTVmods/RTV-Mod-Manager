using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class LibraryTests
{
    /// <summary>Archives a version straight into the Library (built in
    /// a staging folder outside mods\, so it never goes live).</summary>
    private static string Archive(Sandbox sb, string id, string version, string? extraManifest = null)
    {
        var staging = Path.Combine(sb.Root, "staging");
        var path = sb.AddMod(id, version, extraManifest: extraManifest, dir: staging);
        return ModLibrary.Add(sb.ModsDir, path)!.Path;
    }

    private static ManagerSession Open(Sandbox sb, ScriptedPrompt prompt)
    {
        var session = RefreshTests.OpenWithProfile(sb, prompt,
            new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = true });
        session.Refresh();
        return session;
    }

    [Fact]
    public void Rows_show_live_state_profiles_and_source()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        Archive(sb, "alpha", "1.0");
        Archive(sb, "alpha", "1.1");
        Archive(sb, "beta", "2.0", "[updates]\nsource=\"vostokmods:beta-slug\"");
        var session = Open(sb, new ScriptedPrompt());
        new ModProfile { Name = "Other", CreatedAt = DateTime.UtcNow,
            Mods = { new ProfileMod { ModId = "alpha", Version = "1.1" } } }.SaveMetadataOnly();

        var lib = session.OpenLibrary();
        var rows = lib.Rows;

        Assert.Equal(3, rows.Count);
        // Default sort: name, then newest version first.
        Assert.Equal(new[] { "alpha 1.1", "alpha 1.0", "beta 2.0" }, rows.Select(r => r.Name + " " + r.Version));
        Assert.Equal(LibraryLiveState.OtherVersionLive, rows[0].State);
        Assert.Equal("Other v1.0 live", rows[0].StateText);
        Assert.Equal(LibraryLiveState.LiveEnabled, rows[1].State);
        Assert.Equal(LibraryLiveState.NotInstalled, rows[2].State);
        Assert.Equal("Default, Other", rows[0].ProfilesText);
        Assert.Equal("—", rows[2].ProfilesText);
        Assert.Equal("beta-slug", rows[2].SourceText);
        Assert.Equal("—", rows[0].SourceText);
        Assert.StartsWith("3 archived version(s)", lib.HeaderText);
    }

    [Fact]
    public void Filter_matches_every_word_and_sort_toggles_direction()
    {
        using var sb = new Sandbox();
        Archive(sb, "alpha", "1.0");
        Archive(sb, "alpha", "1.1");
        Archive(sb, "beta", "2.0", "[updates]\nsource=\"vostokmods:beta-slug\"");
        var lib = Open(sb, new ScriptedPrompt()).OpenLibrary();

        lib.Filter = "alpha 1.1";
        Assert.Equal("1.1", Assert.Single(lib.Rows).Version);
        Assert.Contains("1 shown", lib.HeaderText);
        lib.Filter = "beta-slug";
        Assert.Equal("beta", Assert.Single(lib.Rows).Entry.ModId);

        lib.Filter = "";
        lib.SortBy(LibrarySortColumn.Version);
        Assert.Equal(new[] { "1.0", "1.1", "2.0" }, lib.Rows.Select(r => r.Version));
        lib.SortBy(LibrarySortColumn.Version);
        Assert.False(lib.SortAscending);
        Assert.Equal(new[] { "2.0", "1.1", "1.0" }, lib.Rows.Select(r => r.Version));
    }

    [Fact]
    public void Cleanup_keeps_newest_live_and_pinned_versions()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        Archive(sb, "alpha", "0.8");
        Archive(sb, "alpha", "0.9");
        Archive(sb, "alpha", "1.0");
        Archive(sb, "alpha", "1.1");
        Archive(sb, "beta", "1.0");
        Archive(sb, "beta", "2.0");
        var prompt = new ScriptedPrompt();
        var session = Open(sb, prompt);
        new ModProfile { Name = "Other", CreatedAt = DateTime.UtcNow,
            Mods = { new ProfileMod { ModId = "alpha", Version = "0.9" } } }.SaveMetadataOnly();
        var lib = session.OpenLibrary();

        var plan = lib.PlanCleanup();
        Assert.Equal(new[] { "alpha@0.8", "beta@1.0" },
            plan.Entries.Select(e => e.ModId + "@" + e.Version).OrderBy(s => s));

        prompt.ConfirmAnswer = false;
        Assert.Equal(0, lib.CleanUpOldVersions());
        Assert.Equal(6, ModLibrary.List(sb.ModsDir).Count);

        prompt.ConfirmAnswer = true;
        Assert.Equal(2, lib.CleanUpOldVersions());
        Assert.True(lib.Changed);
        Assert.Equal("", ModLibrary.Find(sb.ModsDir, "alpha", "0.8"));
        Assert.Equal("", ModLibrary.Find(sb.ModsDir, "beta", "1.0"));
        Assert.Equal(4, lib.Entries.Count);
        Assert.StartsWith("Cleaned up 2 version(s)", lib.HeaderText);

        // Nothing left to clean: the user is told so.
        Assert.Equal(0, lib.CleanUpOldVersions());
        Assert.Contains("inform:Clean up old versions", prompt.Asked);
    }

    [Fact]
    public void Delete_asks_then_removes_only_the_library_copy()
    {
        using var sb = new Sandbox();
        var live = sb.AddMod("alpha", "1.0");
        Archive(sb, "alpha", "1.0");
        var older = Archive(sb, "alpha", "0.9");
        var prompt = new ScriptedPrompt();
        var lib = Open(sb, prompt).OpenLibrary();

        var picked = lib.Rows.Where(r => r.Version == "0.9").Select(r => r.Entry).ToList();
        Assert.Equal(new[] { older }, lib.PathsToInstall(picked));
        Assert.Equal(1, lib.Delete(picked));

        Assert.Contains("confirm:Delete from library", prompt.Asked);
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(live));
        Assert.Single(lib.Entries);
        Assert.Empty(lib.PathsToInstall(picked));
    }
}
