using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class ModsTests
{
    private static ManagerSession Open(Sandbox sb, ScriptedPrompt prompt, params ProfileMod[] mods)
    {
        var s = RefreshTests.OpenWithProfile(sb, prompt, mods);
        // Never touch the real Recycle Bin from a test.
        s.RecycleOverride = p =>
        {
            if (File.Exists(p)) File.Delete(p);
            else if (Directory.Exists(p)) Directory.Delete(p, true);
        };
        s.Refresh();
        return s;
    }

    private static ModEntry Entry(ManagerSession s, string id) => s.Registry.FindById(id)!;

    private static ProfileMod Pm(ManagerSession s, string id)
        => s.ActiveProfile!.Mods.Single(m => m.ModId == id);

    // ── enable / disable ──

    [Fact]
    public void Enabling_a_parked_mod_moves_it_back_and_turns_on_its_required_deps_except_locked()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", inDisabledFolder: true,
            extraManifest: "[dependencies]\nrequired=[\"beta\", \"gamma\"]");
        sb.AddMod("beta", "1.0", extraManifest: "[dependencies]\nrequired=[\"delta\"]");
        sb.AddMod("gamma", "1.0");
        sb.AddMod("delta", "1.0");
        sb.WriteCfg("""
            [settings]

            active_profile="Default"

            [profile.Default.enabled]

            beta@1.0=true
            gamma@1.0=false
            delta@1.0=false
            """);
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);
        s.Settings.LockedMods.Add("gamma");
        var statuses = new List<string>();
        s.Status += (_, m) => statuses.Add(m);

        Assert.True(s.ToggleMod(Entry(s, "alpha")));

        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "alpha-1.0.vmz")));
        Assert.False(File.Exists(Path.Combine(sb.ModsDir, "Disabled", "alpha-1.0.vmz")));
        var cfg = sb.LoadCfg();
        Assert.True(cfg.IsEnabled("alpha", "1.0", fallback: false));
        Assert.True(cfg.IsEnabled("delta", "1.0", fallback: false));   // dep of an already-on dep
        Assert.False(cfg.IsEnabled("gamma", "1.0", fallback: true));   // locked: left alone
        Assert.True(Entry(s, "alpha").IsEnabled);
        Assert.True(Pm(s, "alpha").IsEnabled);
        Assert.Contains(statuses, m => m.Contains("1 required dependency"));
    }

    [Fact]
    public void Disabling_writes_cfg_false_and_leaves_the_file_in_place()
    {
        using var sb = new Sandbox();
        var path = sb.AddMod("alpha", "1.0");
        var s = Open(sb, new ScriptedPrompt());

        Assert.True(s.SetModEnabled(Entry(s, "alpha"), false));

        Assert.True(File.Exists(path));
        Assert.False(sb.LoadCfg().IsEnabled("alpha", "1.0"));
        Assert.False(Pm(s, "alpha").IsEnabled);
        Assert.Equal(new ModCounts(1, 0), s.CountMods());
    }

    [Fact]
    public void A_built_in_mod_stays_on()
    {
        using var sb = new Sandbox();
        sb.AddMod(BuiltInMods.SaveSlotsId, "1.0");
        var s = Open(sb, new ScriptedPrompt());
        s.Settings.SaveSlotsBuiltIn = true;
        var changed = 0;
        s.Changed += () => changed++;

        Assert.False(s.SetModEnabled(Entry(s, BuiltInMods.SaveSlotsId), false));
        Assert.Equal(1, changed);  // repaint undoes the click
        Assert.True(sb.LoadCfg().IsEnabled(BuiltInMods.SaveSlotsId, "1.0"));
    }

    [Fact]
    public void Bulk_toggle_asks_first_and_skips_locked_mods()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        sb.AddMod("beta", "1.0");
        var prompt = new ScriptedPrompt { ConfirmAnswer = false };
        var s = Open(sb, prompt);
        s.Settings.LockedMods.Add("beta");

        s.BulkToggle(enable: false);
        Assert.Contains("confirm:Disable all — confirm", prompt.Asked);
        Assert.True(Entry(s, "alpha").IsEnabled);

        prompt.ConfirmAnswer = true;
        s.BulkToggle(enable: false);
        Assert.False(Entry(s, "alpha").IsEnabled);
        Assert.True(Entry(s, "beta").IsEnabled);

        s.BulkToggleSelected(new[] { Entry(s, "alpha"), Entry(s, "beta") }, enable: true);
        Assert.True(Entry(s, "alpha").IsEnabled);
    }

    // ── load order ──

    [Fact]
    public void Priority_text_is_validated_then_written_to_cfg_and_profile()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", priority: 2);
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);

        Assert.False(s.SetModPriorityFromText(Entry(s, "alpha"), "abc"));
        Assert.Contains("inform:Invalid priority", prompt.Asked);
        Assert.Equal(2, sb.LoadCfg().Priority("alpha", "1.0"));

        Assert.True(s.SetModPriorityFromText(Entry(s, "alpha"), " -50 "));
        Assert.Equal(-50, sb.LoadCfg().Priority("alpha", "1.0"));
        Assert.Equal(-50, Entry(s, "alpha").Priority);
        Assert.Equal(-50, Pm(s, "alpha").Priority);
    }

    [Fact]
    public void Bulk_set_priority_applies_one_value_to_every_mod()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", priority: 1);
        sb.AddMod("beta", "1.0", priority: 2);
        var prompt = new ScriptedPrompt { TextAnswer = "7" };
        var s = Open(sb, prompt);

        Assert.True(s.PromptBulkSetPriority(new[] { Entry(s, "alpha"), Entry(s, "beta") }));

        var cfg = sb.LoadCfg();
        Assert.Equal(7, cfg.Priority("alpha", "1.0"));
        Assert.Equal(7, cfg.Priority("beta", "1.0"));
        Assert.All(s.ActiveProfile!.Mods, m => Assert.Equal(7, m.Priority));
    }

    private static List<ModEntry> FakeEntries(params (string Id, int Priority)[] mods)
        => mods.Select(m => new ModEntry
        {
            Path = m.Id + ".vmz",
            Manifest = { ["mod"] = new() { ["id"] = m.Id, ["version"] = "1.0", ["name"] = m.Id } },
            Priority = m.Priority,
        }).ToList();

    [Fact]
    public void Drag_plan_fits_the_moved_mod_between_neighbours_and_bumps_only_the_cluster()
    {
        var d = FakeEntries(("a", 0), ("b", 1), ("c", 2), ("d", 3), ("e", 10), ("f", -100));
        d = d.OrderBy(e => e.Priority).ToList();  // f, a, b, c, d, e

        // a (index 1) dropped on c (index 3): lands after c → c=2, so a=3; d bumps to 4; e stays.
        var changes = ManagerSession.PlanDragReorder(d, new[] { d[1] }, 3);
        Assert.Equal(new[] { ("a", 0, 3), ("d", 3, 4) },
            changes.Select(c => (c.Entry.ModId, c.OldPriority, c.NewPriority)));

        // e dropped at the top: above nothing, below f=-100 → -101.
        changes = ManagerSession.PlanDragReorder(d, new[] { d[5] }, 0);
        Assert.Equal(new[] { ("e", 10, -101) }, changes.Select(c => (c.Entry.ModId, c.OldPriority, c.NewPriority)));

        // Dropping a row on itself changes nothing.
        Assert.Empty(ManagerSession.PlanDragReorder(d, new[] { d[2] }, 2));

        // Two moved mods land as a block, in their displayed order:
        // b, a dropped on c → f, c, a, b, d, e.
        changes = ManagerSession.PlanDragReorder(d, new[] { d[2], d[1] }, 3);
        Assert.Equal(new[] { ("a", 0, 3), ("b", 1, 4), ("d", 3, 5) },
            changes.Select(c => (c.Entry.ModId, c.OldPriority, c.NewPriority)).OrderBy(x => x.ModId));

        // Dragged up, the block lands before the target: e onto b → f, a, e, b, ...
        changes = ManagerSession.PlanDragReorder(d, new[] { d[5] }, 2);
        Assert.Equal(new[] { ("e", 10, 1), ("b", 1, 2), ("c", 2, 3), ("d", 3, 4) },
            changes.Select(c => (c.Entry.ModId, c.OldPriority, c.NewPriority)));
    }

    [Fact]
    public void Drag_reorder_writes_cfg_and_the_profile()
    {
        using var sb = new Sandbox();
        sb.AddMod("a", "1.0", priority: 0);
        sb.AddMod("b", "1.0", priority: 1);
        sb.AddMod("c", "1.0", priority: 2);
        var s = Open(sb, new ScriptedPrompt());
        var view = s.BuildModRows(new ModListQuery());
        Assert.Equal(new[] { "a", "b", "c" }, view.Displayed.Select(e => e.ModId));

        Assert.True(s.DragReorder(view.Displayed, new[] { view.Displayed[2] }, 0));

        var cfg = sb.LoadCfg();
        Assert.Equal(-1, cfg.Priority("c", "1.0"));
        Assert.Equal(-1, Pm(s, "c").Priority);
        Assert.Equal(new[] { "c", "a", "b" }, s.BuildModRows(new ModListQuery()).Displayed.Select(e => e.ModId));
    }

    // ── lock / testing / notes ──

    [Fact]
    public void Lock_testing_and_notes_persist_in_settings()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var s = Open(sb, new ScriptedPrompt { TextAnswer = "  remember me  " });
        var alpha = Entry(s, "alpha");

        Assert.True(s.ToggleLock(alpha));
        Assert.True(s.ToggleTesting(alpha));
        Assert.True(s.EditModNote(alpha));

        var saved = Settings.Load();
        Assert.Contains("alpha", saved.LockedMods);
        Assert.Contains("alpha", saved.TestingMods);
        Assert.Equal("remember me", saved.ModNotes["alpha"]);

        s.ToggleLock(alpha);
        s.SetModNote(alpha, "   ");
        saved = Settings.Load();
        Assert.DoesNotContain("alpha", saved.LockedMods);
        Assert.False(saved.ModNotes.ContainsKey("alpha"));

        s.BulkSetLocked(new[] { alpha }, true);
        Assert.Contains("alpha", Settings.Load().LockedMods);
    }

    [Fact]
    public void A_mod_without_an_id_cannot_be_locked()
    {
        using var sb = new Sandbox();
        var s = Open(sb, new ScriptedPrompt());
        var prompt = (ScriptedPrompt)s.Prompt;
        Assert.False(s.ToggleLock(new ModEntry { Path = "x.vmz" }));
        Assert.Contains("inform:Can't lock", prompt.Asked);
    }

    // ── delete ──

    [Fact]
    public void Delete_removes_file_cfg_lock_and_profile_entry_then_asks_about_the_library()
    {
        using var sb = new Sandbox();
        var path = sb.AddMod("alpha", "1.0");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);
        ModLibrary.Add(sb.ModsDir, path);
        s.Settings.LockedMods.Add("alpha");
        // Yes to stage 1, then No to the Library question.
        var recycle = s.RecycleOverride!;
        s.RecycleOverride = p => { recycle(p); prompt.ConfirmAnswer = false; };

        Assert.True(s.DeleteMod(Entry(s, "alpha")));

        Assert.False(File.Exists(path));
        Assert.False(sb.LoadCfg().HasEntry("alpha", "1.0"));
        Assert.DoesNotContain("alpha", s.Settings.LockedMods);
        Assert.DoesNotContain(s.ActiveProfile!.Mods, m => m.ModId == "alpha");
        Assert.Contains("confirm:Delete from Library?", prompt.Asked);
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "alpha", "1.0"));
        Assert.Null(s.Registry.FindById("alpha"));
    }

    [Fact]
    public void Delete_with_yes_twice_also_recycles_the_library_copy()
    {
        using var sb = new Sandbox();
        var path = sb.AddMod("alpha", "1.0");
        var s = Open(sb, new ScriptedPrompt());
        ModLibrary.Add(sb.ModsDir, path);

        Assert.True(s.DeleteMod(Entry(s, "alpha")));
        Assert.Empty(ModLibrary.Find(sb.ModsDir, "alpha", "1.0"));
    }

    [Fact]
    public void Batch_delete_asks_once_and_keeps_library_copies()
    {
        using var sb = new Sandbox();
        var a = sb.AddMod("alpha", "1.0");
        var b = sb.AddMod("beta", "1.0");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);
        ModLibrary.Add(sb.ModsDir, a);

        s.DeleteMods(new[] { Entry(s, "alpha"), Entry(s, "beta") });

        Assert.Single(prompt.Asked, q => q.StartsWith("confirm:"));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Empty(s.ActiveProfile!.Mods);
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "alpha", "1.0"));
    }

    [Fact]
    public void Declining_the_delete_changes_nothing()
    {
        using var sb = new Sandbox();
        var path = sb.AddMod("alpha", "1.0");
        var s = Open(sb, new ScriptedPrompt { ConfirmAnswer = false });
        Assert.False(s.DeleteMod(Entry(s, "alpha")));
        Assert.True(File.Exists(path));
        Assert.Single(s.ActiveProfile!.Mods);
    }

    // ── install ──

    private static string Outside(Sandbox sb, string id, string version, string? extra = null)
    {
        var dir = Path.Combine(sb.Root, "downloads");
        return sb.AddMod(id, version, dir: dir, extraManifest: extra, priority: 4);
    }

    [Fact]
    public async Task Install_copies_into_mods_and_records_library_profile_cfg_and_session()
    {
        using var sb = new Sandbox();
        var src = Outside(sb, "alpha", "1.0");
        var bad = Path.Combine(sb.Root, "junk.vmz");
        File.WriteAllText(bad, "not a zip");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);

        var result = await s.InstallModFilesAsync(new[] { src, bad });

        Assert.Equal(new[] { "alpha-1.0.vmz" }, result.Installed);
        Assert.Single(result.Skipped);
        Assert.Equal(new[] { "alpha" }, result.InstalledModIds);
        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "alpha-1.0.vmz")));
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "alpha", "1.0"));
        var cfg = sb.LoadCfg();
        Assert.True(cfg.IsEnabled("alpha", "1.0", fallback: false));
        Assert.Equal(4, cfg.Priority("alpha", "1.0"));
        Assert.Equal(4, Pm(s, "alpha").Priority);
        Assert.Contains("alpha", s.SessionInstalled);
        Assert.Contains("inform:Mod installed", prompt.Asked);
        Assert.True(s.BuildModRows(new ModListQuery()).Rows.Single().InstalledThisSession);
    }

    [Fact]
    public async Task Install_asks_before_overwriting_and_skips_on_no()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var src = sb.AddMod("alpha", "1.1", dir: Path.Combine(sb.Root, "dl"), fileName: "alpha-1.0.vmz");
        var prompt = new ScriptedPrompt { ConfirmAnswer = false };
        var s = Open(sb, prompt);

        var result = await s.InstallModFilesAsync(new[] { src });

        Assert.Empty(result.Installed);
        Assert.Contains("confirm:File exists", prompt.Asked);
        Assert.Contains("inform:Install report", prompt.Asked);
        Assert.Equal("1.0", Entry(s, "alpha").Version);
    }

    [Fact]
    public async Task A_drop_installs_vmz_and_hands_back_modpacks_and_lists()
    {
        using var sb = new Sandbox();
        var src = Outside(sb, "alpha", "1.0");
        var s = Open(sb, new ScriptedPrompt());
        var drop = new[] { src, @"C:\x\pack.zip", @"C:\x\list.json", @"C:\x\readme.txt" };

        Assert.True(ManagerSession.AcceptsFileDrop(drop));
        Assert.False(ManagerSession.IsAcceptedDropPath(@"C:\x\readme.txt"));
        var result = await s.HandleFileDropAsync(drop);

        Assert.Equal(new[] { "alpha" }, result.Install!.InstalledModIds);
        Assert.Equal(new[] { @"C:\x\pack.zip" }, result.ModpackFiles);
        Assert.Equal(new[] { @"C:\x\list.json" }, result.ModListFiles);
    }

    [Fact]
    public async Task A_download_replaces_the_live_file_by_name_and_remembers_its_source()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", fileName: "Alpha Mod.vmz");
        var dl = sb.AddMod("alpha", "1.1", dir: Path.Combine(sb.Root, "tmp"), fileName: "tmp123.vmz");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);

        await s.InstallDownloadedModAsync(dl, ModSource.ForSlug("alpha-mod"));

        Assert.Equal(new[] { "Alpha Mod.vmz" }, Directory.GetFiles(sb.ModsDir, "*.vmz").Select(Path.GetFileName));
        Assert.Equal("1.1", Entry(s, "alpha").Version);
        Assert.Equal(ModSource.ForSlug("alpha-mod"), sb.LoadCfg().GetModSource("alpha", "1.1"));
        Assert.Equal(ModSource.ForSlug("alpha-mod"), Entry(s, "alpha").Source);
    }

    [Fact]
    public async Task Install_from_url_parses_the_slug_and_downloads_offline_through_the_seam()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt { TextAnswer = "not a url at all" };
        var s = Open(sb, prompt);
        string? asked = null;
        s.ModDownloadOverride = (slug, save) =>
        {
            asked = slug;
            File.WriteAllBytes(save, Sandbox.BuildVmz("event-mod", "2.0"));
            return Task.CompletedTask;
        };

        Assert.Null(await s.InstallFromVostokModsUrlAsync());
        Assert.Contains("inform:Not a mod URL", prompt.Asked);

        prompt.TextAnswer = "https://vostokmods.net/mod/event-mod";
        var result = await s.InstallFromVostokModsUrlAsync();

        Assert.Equal("event-mod", asked);
        Assert.Equal(new[] { "event-mod" }, result!.InstalledModIds);
        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "event-mod.vmz")));
        Assert.Equal(ModSource.ForSlug("event-mod"), Entry(s, "event-mod").Source);
    }

    [Fact]
    public void Browser_ownership_checks_live_and_library_mods_by_canonical_source()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: "[updates]\nsource=\"vostokmods:1111-uuid\"");
        var libOnly = sb.AddMod("beta", "1.0", dir: Path.Combine(sb.Root, "x"),
            extraManifest: "[updates]\nsource=\"vostokmods:beta\"");
        var s = Open(sb, new ScriptedPrompt());
        ModLibrary.Add(sb.ModsDir, libOnly);

        var hooks = s.PrepareModBrowser(loadSlugMap: false);
        Assert.True(hooks.OwnsSource(ModSource.ForSlug("beta")));
        Assert.False(hooks.OwnsSource(ModSource.ForSlug("alpha-slug")));

        s.ApplySlugListing(new[] { new VmModSummary { Id = "1111-uuid", Slug = "alpha-slug" } });
        Assert.True(hooks.OwnsSource(ModSource.ForSlug("alpha-slug")));
        Assert.Equal(ModSource.ForSlug("alpha-slug"), s.CanonicalSource(new ModSource("vostokmods", "1111-uuid")));
    }

    // ── the mods list ──

    [Fact]
    public void Rows_are_profile_scoped_with_locked_bypass_packs_and_load_positions()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", priority: 5);
        sb.AddMod("beta", "1.0", priority: 1);
        sb.AddMod("gamma", "1.0", priority: 3);
        sb.AddMod("outsider", "1.0", priority: 0);
        sb.AddMod("locked", "1.0", priority: 9);
        var s = Open(sb, new ScriptedPrompt(),
            new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = true, Priority = 5 },
            new ProfileMod { ModId = "beta", Version = "1.0", IsEnabled = true, Priority = 1, PackName = "Pack" },
            new ProfileMod { ModId = "gamma", Version = "1.0", IsEnabled = false, Priority = 3, PackName = "Pack" });
        s.SetModEnabled(Entry(s, "gamma"), false);
        // Refresh adopted the others; take them out of the profile again
        // (in memory) so the scope rules show.
        s.ActiveProfile!.Mods.RemoveAll(m => m.ModId is "outsider" or "locked");
        s.Settings.LockedMods.Add("locked");
        s.Settings.ModNotes["alpha"] = "note";

        var rows = s.BuildModRows(new ModListQuery()).Rows;

        Assert.Equal(new[] { "Pack  (2 mods)", "beta", "gamma", "alpha", "locked" }, rows.Select(r => r.DisplayName));
        Assert.True(rows[0].IsPackHeader);
        Assert.True(rows[1].InPack);
        Assert.Equal("Pack", rows[1].PackName);
        Assert.Equal(new int?[] { null, 1, null, 2, 3 }, rows.Select(r => r.Position));
        Assert.True(rows[4].Locked);
        Assert.Equal("note", rows[3].Note);

        s.TogglePackCollapse("pack");
        rows = s.BuildModRows(new ModListQuery()).Rows;
        Assert.Equal(new[] { "Pack  (2 mods)", "alpha", "locked" }, rows.Select(r => r.DisplayName));
        Assert.True(rows[0].PackCollapsed);

        rows = s.BuildModRows(new ModListQuery(Filter: "ALP")).Rows;
        Assert.Equal("alpha", Assert.Single(rows).DisplayName);
    }

    [Fact]
    public void Update_state_and_conflicts_are_reported_per_row()
    {
        using var sb = new Sandbox();
        const string hook = "[hooks]\n\"res://Scripts/AI.gd\"=\"_physics_process\"";
        sb.AddMod("old", "1.0", extraManifest: "[updates]\nsource=\"vostokmods:old\"\n" + hook);
        sb.AddMod("same", "2.0", extraManifest: "[updates]\nsource=\"vostokmods:same\"\n" + hook);
        sb.AddMod("dev", "3.1", extraManifest: "[updates]\nsource=\"vostokmods:dev\"");
        sb.AddMod("odd", "0.0.420", extraManifest: "[updates]\nsource=\"vostokmods:odd\"");
        sb.AddMod("nolatest", "1.0", extraManifest: "[updates]\nsource=\"vostokmods:nolatest\"");
        sb.AddMod("plain", "1.0");
        var s = Open(sb, new ScriptedPrompt());
        s.LatestVersions["vostokmods:old"] = "1.2";
        s.LatestVersions["vostokmods:same"] = "2.0";
        s.LatestVersions["vostokmods:dev"] = "3.0";
        s.LatestVersions["vostokmods:odd"] = "0.4.20_R";

        var byId = s.BuildModRows(new ModListQuery()).Rows.ToDictionary(r => r.ModId);

        Assert.Equal(ModUpdateState.Outdated, byId["old"].Update!.State);
        Assert.Equal("1.2", byId["old"].Update!.Latest);
        Assert.Equal(ModUpdateState.UpToDate, byId["same"].Update!.State);
        Assert.Equal(ModUpdateState.NewerLocal, byId["dev"].Update!.State);
        Assert.Equal(ModUpdateState.Ambiguous, byId["odd"].Update!.State);
        Assert.Equal(ModUpdateState.Unknown, byId["nolatest"].Update!.State);
        Assert.Equal(ModUpdateState.Unlinked, byId["plain"].Update!.State);
        Assert.True(byId["old"].InConflict);
        Assert.True(byId["same"].InConflict);
        Assert.False(byId["plain"].InConflict);
    }
}
