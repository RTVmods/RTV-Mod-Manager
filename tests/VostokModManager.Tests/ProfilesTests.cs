using System.IO.Compression;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

/// <summary>Shared setup for the profile-area tests.</summary>
internal static class ProfileKit
{
    /// <summary>Puts an archive straight into mods\Library\ (built in a
    /// scratch folder first, as a download or an earlier profile would
    /// have left it) and returns its Library path.</summary>
    public static string AddToLibrary(Sandbox sb, string id, string version, string? name = null,
        int? priority = null, string? extraManifest = null)
    {
        var scratch = Path.Combine(sb.Root, "scratch", Guid.NewGuid().ToString("N")[..8]);
        var src = sb.AddMod(id, version, name, priority, extraManifest, dir: scratch);
        return ModLibrary.Add(sb.ModsDir, src)!.Path;
    }

    public static ModProfile SaveProfile(string name, params ProfileMod[] mods)
    {
        var p = new ModProfile { Name = name, CreatedAt = DateTime.UtcNow, Mods = mods.ToList() };
        p.SaveMetadataOnly();
        return p;
    }

    public static ProfileMod Pm(string id, string version, bool enabled = true, int priority = 0,
        string? name = null, string source = "")
        => new() { ModId = id, DisplayName = name ?? id, Version = version, IsEnabled = enabled, Priority = priority, Source = source };

    public static ModProfile Named(ManagerSession s, string name)
        => s.AllProfiles.Single(p => p.Name == name);

    public static ModProfile OnDisk(string name)
        => ModProfile.LoadAll().Single(p => p.Name == name);

    public static string Source(string slug) => $"[updates]\nsource=\"vostokmods:{slug}\"";
}

public class ProfilesTests
{
    // ── switching ─────────────────────────────────────────────────────

    [Fact]
    public void Switch_swaps_the_live_folder_from_the_library_and_records_the_new_active_profile()
    {
        using var sb = new Sandbox();
        var alphaLive = sb.AddMod("alpha", "1.0");
        ProfileKit.AddToLibrary(sb, "beta", "2.0");
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt, ProfileKit.Pm("alpha", "1.0"));
        ProfileKit.SaveProfile("Other", ProfileKit.Pm("beta", "2.0", enabled: false, priority: 4));
        s.ReloadProfiles();
        s.Refresh();
        var status = new List<(StatusArea, string)>();
        s.Status += (a, m) => status.Add((a, m));

        Assert.True(s.SwitchActiveProfile(ProfileKit.Named(s, "Other")));

        Assert.False(File.Exists(alphaLive));
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "alpha", "1.0"));
        Assert.True(File.Exists(Path.Combine(sb.ModsDir, Path.GetFileName(ModLibrary.PathFor(sb.ModsDir, "beta", "2.0")))));
        Assert.Equal("Other", s.ActiveProfile!.Name);
        Assert.Equal("Other", Settings.Load().ActiveProfileName);
        var cfg = sb.LoadCfg();
        Assert.Equal("Other", cfg.ActiveProfile);
        Assert.False(cfg.IsEnabled("beta", "2.0"));
        Assert.Equal(4, cfg.Priority("beta", "2.0"));
        Assert.Contains(status, x => x.Item1 == StatusArea.Profiles && x.Item2.StartsWith("Switched to 'Other'"));
    }

    [Fact]
    public void Switching_to_the_active_profile_is_a_no_op_that_reports_success()
    {
        using var sb = new Sandbox();
        var live = sb.AddMod("alpha", "1.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(), ProfileKit.Pm("alpha", "1.0"));

        Assert.True(s.SwitchActiveProfile(s.ActiveProfile!));
        Assert.True(File.Exists(live));
    }

    [Fact]
    public void Switch_offers_newer_library_versions_and_moves_the_ticked_ones()
    {
        using var sb = new Sandbox();
        ProfileKit.AddToLibrary(sb, "beta", "1.0", "Beta");
        ProfileKit.AddToLibrary(sb, "beta", "1.1", "Beta");
        ProfileKit.AddToLibrary(sb, "gamma", "1.0", "Gamma");
        ProfileKit.AddToLibrary(sb, "gamma", "2.0", "Gamma [DEV]");
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        ProfileKit.SaveProfile("Other", ProfileKit.Pm("beta", "1.0", name: "Beta"), ProfileKit.Pm("gamma", "1.0", name: "Gamma"));
        s.ReloadProfiles();

        Assert.True(s.SwitchActiveProfile(ProfileKit.Named(s, "Other")));

        Assert.Contains(prompt.Asked, a => a.StartsWith("pick:Newer versions"));
        var saved = ProfileKit.OnDisk("Other");
        Assert.Equal("1.1", saved.Mods.Single(m => m.ModId == "beta").Version);
        // The DEV build was offered unticked, so it stays put.
        Assert.Equal("1.0", saved.Mods.Single(m => m.ModId == "gamma").Version);
        Assert.Contains(s.Registry.Entries, e => e.ModId == "beta" && e.Version == "1.1");
    }

    [Fact]
    public void Cancelling_the_version_question_cancels_the_switch()
    {
        using var sb = new Sandbox();
        ProfileKit.AddToLibrary(sb, "beta", "1.0");
        ProfileKit.AddToLibrary(sb, "beta", "1.1");
        var prompt = new ScriptedPrompt { PickAnswer = _ => null };
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        ProfileKit.SaveProfile("Other", ProfileKit.Pm("beta", "1.0"));
        s.ReloadProfiles();

        Assert.False(s.SwitchActiveProfile(ProfileKit.Named(s, "Other")));

        Assert.Equal("Default", s.ActiveProfile!.Name);
        Assert.Equal("1.0", ProfileKit.OnDisk("Other").Mods.Single().Version);
        Assert.Empty(Directory.GetFiles(sb.ModsDir, "*.vmz"));
    }

    [Fact]
    public void A_live_file_held_open_aborts_the_switch_and_tells_the_user()
    {
        using var sb = new Sandbox();
        var live = sb.AddMod("alpha", "1.0");
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt, ProfileKit.Pm("alpha", "1.0"));
        ProfileKit.SaveProfile("Other");
        s.ReloadProfiles();
        s.Refresh();

        using (File.Open(live, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(s.SwitchActiveProfile(ProfileKit.Named(s, "Other")));

        Assert.Contains("error:Profile switch failed", prompt.Asked);
        Assert.True(File.Exists(live));
        Assert.Equal("Default", s.Settings.ActiveProfileName);
    }

    [Fact]
    public void Sync_after_profile_changes_follows_the_cfg_active_profile()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        ProfileKit.SaveProfile("Other");
        s.Config.ActiveProfile = "Other";
        s.Config.Save();

        s.SyncAfterProfileChanges(liveFolderChanged: false);

        Assert.Equal("Other", s.ActiveProfile!.Name);
        Assert.Equal("Other", Settings.Load().ActiveProfileName);
    }

    // ── cards ─────────────────────────────────────────────────────────

    [Fact]
    public void Summaries_count_mods_and_list_the_save_slots_tied_to_each_profile()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(),
            ProfileKit.Pm("alpha", "1.0"), ProfileKit.Pm("beta", "1.0", enabled: false));
        ProfileKit.SaveProfile("Other", ProfileKit.Pm("gamma", "1.0"));
        var slots = SaveSlots.RootDir(SaveSlots.DefaultUserDir);
        SaveSlots.SetProfile(slots, 2, "Other");
        SaveSlots.SetDisplayName(slots, 2, "Hardcore run");
        s.ReloadProfiles();

        var cards = s.GetProfileSummaries();

        var def = cards.Single(c => c.Name == "Default");
        Assert.True(def.IsActive);
        Assert.Equal((2, 1), (def.ModCount, def.EnabledCount));
        Assert.Empty(def.SaveSlots);
        var other = cards.Single(c => c.Name == "Other");
        Assert.False(other.IsActive);
        Assert.Equal("Hardcore run", Assert.Single(other.SaveSlots).Title);
    }

    [Fact]
    public void Profile_mods_are_listed_by_load_order_and_filtered()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(),
            ProfileKit.Pm("zulu", "1.0", priority: 1, source: "vostokmods:zulu"),
            ProfileKit.Pm("alpha", "2.0", priority: 5));

        var all = s.GetProfileMods(s.ActiveProfile!);
        Assert.Equal(new[] { "zulu", "alpha" }, all.Select(v => v.Mod.ModId));
        Assert.Contains("VostokMods (zulu)", all[0].Note);
        Assert.Equal("alpha", Assert.Single(s.GetProfileMods(s.ActiveProfile!, "2.0")).Mod.ModId);
    }

    // ── create / import / export / delete ─────────────────────────────

    [Fact]
    public void Clone_copies_the_mod_list_without_bundles_and_does_not_activate_it()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(),
            ProfileKit.Pm("alpha", "1.0", enabled: false, priority: 3));
        s.ActiveProfile!.Mods[0].ArchiveFileName = "alpha.vmz";

        var clone = s.CloneProfile(s.ActiveProfile!, "Copy");

        Assert.NotNull(clone);
        var saved = ProfileKit.OnDisk("Copy");
        var pm = Assert.Single(saved.Mods);
        Assert.Equal(("alpha", "1.0", false, 3, ""), (pm.ModId, pm.Version, pm.IsEnabled, pm.Priority, pm.ArchiveFileName));
        Assert.Equal("Default", s.ActiveProfile!.Name);
        Assert.Contains(s.AllProfiles, p => p.Name == "Copy");
    }

    [Fact]
    public void Clone_onto_an_existing_name_asks_first()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt { ConfirmAnswer = false };
        var s = RefreshTests.OpenWithProfile(sb, prompt, ProfileKit.Pm("alpha", "1.0"));
        ProfileKit.SaveProfile("Other", ProfileKit.Pm("beta", "1.0"));
        s.ReloadProfiles();

        Assert.Null(s.CloneProfile(s.ActiveProfile!, "other"));

        Assert.Contains("confirm:Overwrite?", prompt.Asked);
        Assert.Equal("beta", ProfileKit.OnDisk("Other").Mods.Single().ModId);
    }

    [Fact]
    public void New_modlist_starts_with_the_locked_mods_bundled()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        sb.AddMod("beta", "1.0");
        var prompt = new ScriptedPrompt { TextAnswer = "Fresh" };
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        s.Settings.LockedMods.Add("alpha");
        s.Refresh();

        var fresh = s.CreateNewModlistProfile();

        Assert.NotNull(fresh);
        Assert.Contains("text:New Modlist", prompt.Asked);
        var pm = Assert.Single(fresh!.Mods);
        Assert.Equal("alpha", pm.ModId);
        Assert.NotEmpty(fresh.ResolveBundledArchive(pm));
    }

    [Fact]
    public void A_vmprofile_round_trips_and_its_archives_land_in_the_library()
    {
        using var sb = new Sandbox();
        ProfileKit.AddToLibrary(sb, "alpha", "1.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        var shared = ProfileKit.SaveProfile("Shared", ProfileKit.Pm("alpha", "1.0"));
        s.ReloadProfiles();
        var file = Path.Combine(sb.Root, "out", "shared.vmprofile");
        Assert.True(s.ExportProfile(ProfileKit.Named(s, "Shared"), file));

        // A fresh machine: no profile, empty Library.
        ProfileKit.Named(s, "Shared").Delete();
        Directory.Delete(ModLibrary.LibraryDir(sb.ModsDir), true);
        var prompt = new ScriptedPrompt();
        var s2 = RefreshTests.OpenWithProfile(sb, prompt);

        var result = s2.ImportProfileFile(file);

        Assert.NotNull(result);
        Assert.True(result!.IsBundle);
        Assert.Equal(1, result.LibraryAdded);
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "alpha", "1.0"));
        Assert.Contains(s2.AllProfiles, p => p.Name == "Shared");
        Assert.Contains("inform:Imported", prompt.Asked);
    }

    [Fact]
    public void A_json_profile_import_counts_what_needs_downloading_and_offers_to_apply()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt { ConfirmAnswer = true };
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        var json = Path.Combine(sb.Root, "spec.json");
        File.WriteAllText(json, MetroProfile.Serialize(new ModProfile
        {
            Name = "Spec",
            Mods = { ProfileKit.Pm("alpha", "1.0", source: "vostokmods:alpha") },
        }));

        var result = s.ImportProfileFile(json);

        Assert.NotNull(result);
        Assert.False(result!.IsBundle);
        Assert.Equal(1, result.MissingFromLibrary);
        Assert.True(result.ApplyRequested);
        Assert.Equal("Spec", result.Profile.Name);
        Assert.True(File.Exists(Path.Combine(ModProfile.ProfilesDir, "Spec", "profile.json")));
    }

    [Fact]
    public void A_file_that_is_not_a_profile_is_refused()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        var junk = Path.Combine(sb.Root, "junk.json");
        File.WriteAllText(junk, "not json");

        Assert.Null(s.ImportProfileFile(junk));
        Assert.Contains("inform:Import failed", prompt.Asked);
    }

    [Fact]
    public void Export_picks_the_format_from_the_extension()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(),
            ProfileKit.Pm("alpha", "1.0", source: "vostokmods:alpha"));
        var dir = Path.Combine(sb.Root, "out");

        var pack = Path.Combine(dir, "pack.zip");
        Assert.True(s.ExportProfile(s.ActiveProfile!, pack));
        Assert.True(ModpackFile.IsModpackZip(pack));
        using (var zip = ZipFile.OpenRead(pack))
            Assert.Equal("profile.json", Assert.Single(zip.Entries).FullName);

        var json = Path.Combine(dir, "list.json");
        Assert.True(s.ExportProfile(s.ActiveProfile!, json));
        Assert.Equal("alpha", ModProfile.LoadFromJsonFile(json)!.Mods.Single().ModId);

        Assert.Equal(ProfileExportFormat.Bundle, ManagerSession.ExportFormatFor("x.vmprofile"));
        Assert.Equal("Default.vmprofile", ManagerSession.DefaultExportFileName(s.ActiveProfile!));
    }

    [Fact]
    public void Delete_removes_the_profile_folder_only_when_confirmed()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt { ConfirmAnswer = false };
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        ProfileKit.SaveProfile("Other");
        s.ReloadProfiles();
        var folder = ProfileKit.Named(s, "Other").FolderPath;

        Assert.False(s.DeleteProfile(ProfileKit.Named(s, "Other")));
        Assert.True(Directory.Exists(folder));

        prompt.ConfirmAnswer = true;
        Assert.True(s.DeleteProfile(ProfileKit.Named(s, "Other")));
        Assert.False(Directory.Exists(folder));
        Assert.DoesNotContain(s.AllProfiles, p => p.Name == "Other");
    }

    // ── rename / description ──────────────────────────────────────────

    [Fact]
    public void Renaming_the_active_profile_moves_its_folder_cfg_section_settings_and_slots()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        sb.WriteCfg("""
            [settings]

            active_profile="Default"

            [profile.Default.enabled]

            alpha@1.0=false
            """);
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(), ProfileKit.Pm("alpha", "1.0", enabled: false));
        s.Refresh();
        var slots = SaveSlots.RootDir(SaveSlots.DefaultUserDir);
        SaveSlots.SetProfile(slots, 1, "Default");
        var oldFolder = s.ActiveProfile!.FolderPath;

        Assert.True(s.RenameProfile(s.ActiveProfile!, "Main"));

        Assert.Equal("Main", s.ActiveProfile!.Name);
        Assert.False(Directory.Exists(oldFolder));
        Assert.True(File.Exists(Path.Combine(ModProfile.ProfilesDir, "Main", "profile.json")));
        Assert.Equal("Main", Settings.Load().ActiveProfileName);
        var cfg = sb.LoadCfg();
        Assert.Equal("Main", cfg.ActiveProfile);
        Assert.False(cfg.IsEnabled("alpha", "1.0"));
        Assert.Empty(cfg.ProfileEntries("Default"));
        Assert.Equal("Main", SaveSlots.Read(slots, 1).Profile);
    }

    [Fact]
    public void Rename_refuses_a_name_another_profile_has()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        ProfileKit.SaveProfile("Other");
        s.ReloadProfiles();

        Assert.False(s.RenameProfile(s.ActiveProfile!, "OTHER"));
        Assert.Contains("inform:Name taken", prompt.Asked);
        Assert.Equal("Default", s.ActiveProfile!.Name);
    }

    [Fact]
    public void Description_is_asked_for_and_saved()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt { TextAnswer = "  Night runs  " };
        var s = RefreshTests.OpenWithProfile(sb, prompt);

        Assert.True(s.SetProfileDescription(s.ActiveProfile!));
        Assert.Equal("Night runs", ProfileKit.OnDisk("Default").Description);
    }

    // ── inline edits ──────────────────────────────────────────────────

    [Fact]
    public void Inline_enable_and_priority_edits_are_saved_to_profile_json()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(), ProfileKit.Pm("alpha", "1.0"));
        var p = s.ActiveProfile!;

        Assert.True(s.SetProfileModEnabled(p, p.Mods[0], false));
        Assert.True(s.SetProfileModPriority(p, p.Mods[0], 9));
        Assert.False(s.SetProfileModPriority(p, p.Mods[0], 9));

        var pm = ProfileKit.OnDisk("Default").Mods.Single();
        Assert.Equal((false, 9), (pm.IsEnabled, pm.Priority));
    }

    [Fact]
    public void Removing_mods_from_a_profile_deletes_their_bundles_but_not_the_live_files()
    {
        using var sb = new Sandbox();
        var live = sb.AddMod("alpha", "1.0");
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        s.Refresh();
        var p = ModProfile.FromRegistry("Bundled", "", s.Registry.Entries);
        p.SaveWithBundles(s.Registry.Entries);
        s.ReloadProfiles();
        p = ProfileKit.Named(s, "Bundled");
        var bundle = p.ResolveBundledArchive(p.Mods[0]);
        Assert.True(File.Exists(bundle));

        Assert.True(s.RemoveModsFromProfile(p, p.Mods.ToList()));

        Assert.Contains("confirm:Remove from profile", prompt.Asked);
        Assert.False(File.Exists(bundle));
        Assert.True(File.Exists(live));
        Assert.Empty(ProfileKit.OnDisk("Bundled").Mods);
    }

    [Fact]
    public void Library_mods_can_be_added_to_a_profile()
    {
        using var sb = new Sandbox();
        ProfileKit.AddToLibrary(sb, "alpha", "1.0", "Alpha", priority: 7);
        ProfileKit.AddToLibrary(sb, "beta", "2.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(), ProfileKit.Pm("beta", "1.0"));

        var changed = s.AddLibraryModsToProfile(s.ActiveProfile!, ModLibrary.List(sb.ModsDir));

        Assert.Equal(2, changed);
        var mods = ProfileKit.OnDisk("Default").Mods;
        var alpha = mods.Single(m => m.ModId == "alpha");
        Assert.Equal(("Alpha", "1.0", true, 7), (alpha.DisplayName, alpha.Version, alpha.IsEnabled, alpha.Priority));
        Assert.Equal("2.0", mods.Single(m => m.ModId == "beta").Version);
    }

    [Fact]
    public void Dragging_mods_into_another_profile_copies_new_ones_and_skips_present_ones()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt,
            ProfileKit.Pm("alpha", "1.0", priority: 2), ProfileKit.Pm("beta", "1.0"));
        s.ActiveProfile!.Mods[0].ArchiveFileName = "alpha.vmz";
        ProfileKit.SaveProfile("Other", ProfileKit.Pm("beta", "0.9"));
        s.ReloadProfiles();

        var result = s.CopyModsToProfile(ProfileKit.Named(s, "Other"), s.ActiveProfile!.Mods, s.ActiveProfile);

        Assert.Equal(new ProfileCopyResult(1, 1), result);
        Assert.Contains("inform:Drag-and-drop copy", prompt.Asked);
        var other = ProfileKit.OnDisk("Other");
        var alpha = other.Mods.Single(m => m.ModId == "alpha");
        Assert.Equal((2, ""), (alpha.Priority, alpha.ArchiveFileName));
        Assert.Equal("0.9", other.Mods.Single(m => m.ModId == "beta").Version);
    }

    // ── dependencies ──────────────────────────────────────────────────

    [Fact]
    public void Dependency_rollup_classes_each_declared_dependency()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", "Alpha", extraManifest: "[dependencies]\nrequired=\"lib-a, lib-b\"\noptional=\"extra\"");
        sb.AddMod("lib-a", "1.0", inDisabledFolder: true);
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(), ProfileKit.Pm("alpha", "1.0"));
        s.Refresh();

        var report = s.GetProfileDependencies();

        var dep = Assert.Single(report.Dependents);
        Assert.Equal("Alpha", dep.Label);
        Assert.Equal(DependencyStatus.Disabled, dep.Dependencies.Single(d => d.DepId == "lib-a").Status);
        Assert.Equal(DependencyStatus.Missing, dep.Dependencies.Single(d => d.DepId == "lib-b").Status);
        Assert.False(dep.Dependencies.Single(d => d.DepId == "extra").Required);
        Assert.Equal((1, 1), (report.MissingRequired, report.DisabledRequired));
        Assert.Contains("1 required dep(s) MISSING", report.Summary);
    }
}
