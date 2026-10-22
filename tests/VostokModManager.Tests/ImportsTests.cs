using System.Security.Cryptography;
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class ImportsTests
{
    private static ModListImport List(string name, params ImportEntry[] mods)
        => new() { Name = name, Mods = mods.ToList() };

    private static ImportEntry Entry(string modId = "", string source = "", string name = "", bool? optional = null,
        string version = "")
        => new() { ModId = modId, Source = source, DisplayName = name, IsOptional = optional, Version = version };

    // ── where it goes ─────────────────────────────────────────────────

    [Fact]
    public void Target_options_suggest_a_unique_new_profile_name()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        ProfileKit.SaveProfile("Pack");
        s.ReloadProfiles();

        var opts = s.GetImportTargetOptions("Pack");

        Assert.NotNull(opts);
        Assert.Equal("Default", opts!.Active.Name);
        Assert.Equal("Pack", Assert.Single(opts.Others).Name);
        Assert.Equal("Pack (2)", opts.SuggestedNewName);
    }

    [Fact]
    public void Without_an_active_profile_there_is_nowhere_to_import()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = new ManagerSession(new Settings { ModsDir = sb.ModsDir, SaveSlotsBuiltIn = false }, prompt);
        s.ReloadProfiles();

        Assert.Null(s.GetImportTargetOptions("x"));
        Assert.Contains("inform:No active profile", prompt.Asked);
    }

    [Fact]
    public void A_new_target_profile_is_created_and_switched_to()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        var import = new ModListImport { Name = "Pack", Description = "A pack" };

        Assert.True(s.SelectImportTarget(new ImportTargetChoice(ImportTarget.NewProfile, "Pack"), import));

        Assert.Equal("Pack", s.ActiveProfile!.Name);
        Assert.Equal("A pack", s.ActiveProfile.Description);
        Assert.Equal("Pack", Settings.Load().ActiveProfileName);
    }

    [Fact]
    public void A_taken_name_is_refused()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);

        Assert.False(s.SelectImportTarget(new ImportTargetChoice(ImportTarget.NewProfile, "default"), new ModListImport()));
        Assert.Contains("inform:Name taken", prompt.Asked);
    }

    // ── the plan ──────────────────────────────────────────────────────

    [Fact]
    public void The_plan_classifies_each_entry()
    {
        using var sb = new Sandbox();
        sb.AddMod("inprofile", "1.0");
        sb.AddMod("handmade", "1.0", name: "Hand Made");
        ProfileKit.AddToLibrary(sb, "stored", "1.0");
        var companion = Path.Combine(sb.Root, "drop");
        sb.AddMod("companion", "1.0", extraManifest: ProfileKit.Source("companion-slug"), dir: companion);
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt(), ProfileKit.Pm("inprofile", "1.0"));
        s.Refresh();
        s.ActiveProfile!.Mods.RemoveAll(m => m.ModId == "handmade");   // adopted by Refresh; make it live-only

        var plan = s.BuildImportPlan(List("Pack",
            Entry("inprofile"),
            Entry(source: "vostokmods:hand-made", name: "Hand Made"),
            Entry("stored"),
            Entry("companion"),
            Entry("fresh", "vostokmods:fresh", optional: true),
            Entry("lost")), companion)!;

        ImportPlanRow Row(string label) => plan.Rows.Single(r => r.Label == label);
        Assert.Equal(ImportRowKind.AlreadyInProfile, Row("inprofile").Kind);
        Assert.Equal(ImportRowKind.LivePresent, Row("Hand Made").Kind);
        Assert.Equal(ImportRowKind.FromLibrary, Row("stored").Kind);
        Assert.Equal(ImportRowKind.Download, Row("companion").Kind);
        Assert.Equal("vostokmods:companion-slug", Row("companion").Entry.Source);
        Assert.Equal(ImportRowKind.Download, Row("fresh").Kind);
        Assert.False(Row("fresh").Apply);   // optional → unticked
        Assert.Equal(ImportRowKind.Unresolvable, Row("lost").Kind);
        Assert.Contains("1 already in profile", plan.Summary);

        plan.SelectAll();
        Assert.True(Row("fresh").Apply);
        plan.SelectRequiredOnly();
        Assert.False(Row("fresh").Apply);
        Assert.True(Row("stored").Apply);
    }

    [Fact]
    public async Task Executing_the_plan_installs_and_adds_the_ticked_mods_to_the_profile()
    {
        using var sb = new Sandbox();
        sb.AddMod("live", "1.0");
        ProfileKit.AddToLibrary(sb, "stored", "1.0", priority: 6);
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.Refresh();
        s.ActiveProfile!.Mods.Clear();
        var calls = new List<(string, string?)>();
        // The site's slug differs from the manifest id; the profile
        // gets the manifest id.
        s.ProfilesDownloadOverride = ProfileApplyTests.FakeDownloads(calls, idFor: slug => slug + "-mod");
        var plan = s.BuildImportPlan(List("Pack",
            Entry("live"),
            Entry("stored"),
            Entry(source: "vostokmods:remote", name: "Remote"),
            Entry("skipped", "vostokmods:skipped", optional: true)))!;

        var result = await s.ExecuteImportAsync(plan);

        Assert.True(result.Applied);
        Assert.Equal(new[] { ("remote", (string?)null) }, calls);
        Assert.Equal(new[] { "live", "stored", "remote-mod" }.OrderBy(x => x), result.InstalledModIds.OrderBy(x => x));
        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "stored.vmz")));
        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "remote.vmz")));
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "remote-mod", "9.0"));
        var mods = ProfileKit.OnDisk("Default").Mods;
        Assert.All(mods, m => Assert.Equal("Pack", m.PackName));
        Assert.Equal(6, mods.Single(m => m.ModId == "stored").Priority);
        Assert.Equal("vostokmods:remote", mods.Single(m => m.ModId == "remote-mod").Source);
        Assert.DoesNotContain(mods, m => m.ModId == "skipped");
    }

    [Fact]
    public async Task A_download_that_fails_the_checksum_is_discarded()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.ProfilesDownloadOverride = ProfileApplyTests.FakeDownloads(new());
        var import = List("Pack", Entry("pinned", "vostokmods:pinned", version: "1.0"));
        import.PinVersions = true;
        import.Checksums["vostokmods:pinned"] = new string('0', 64);
        var plan = s.BuildImportPlan(import)!;

        var result = await s.ExecuteImportAsync(plan);

        Assert.False(result.Applied);
        Assert.Contains(result.Log, l => l.Contains("does not match the modpack's checksum"));
        Assert.Empty(Directory.GetFiles(sb.ModsDir, "*.vmz"));
    }

    [Fact]
    public async Task A_matching_checksum_is_accepted()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.ProfilesDownloadOverride = ProfileApplyTests.FakeDownloads(new());
        var expected = Convert.ToHexString(SHA256.HashData(
            Sandbox.BuildVmz("pinned", "1.0", extraManifest: ProfileKit.Source("pinned")))).ToLowerInvariant();
        var import = List("Pack", Entry("pinned", "vostokmods:pinned", version: "1.0"));
        import.PinVersions = true;
        import.Checksums["vostokmods:pinned"] = expected;

        var result = await s.ExecuteImportAsync(s.BuildImportPlan(import)!);

        Assert.True(result.Applied);
        Assert.Equal("1.0", ProfileKit.OnDisk("Default").Mods.Single().Version);
    }

    // ── getting an import ─────────────────────────────────────────────

    [Fact]
    public async Task A_hosted_modpack_becomes_a_pinned_import_without_the_loader_or_unavailable_mods()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        s.ProfilesManifestOverride = (slug, _) => Task.FromResult(new VmModpackManifest
        {
            Name = "Big Pack",
            Summary = "All of it",
            Mods = new[]
            {
                new VmModpackMod { Slug = "b", Name = "B", LoadOrder = 2, Version = "2.0", Sha256 = "abc" },
                new VmModpackMod { Slug = "a", Name = "A", LoadOrder = 1, Version = "1.0" },
                new VmModpackMod { Slug = "metro-mod-loader", Name = "MML", LoadOrder = 0 },
                new VmModpackMod { Slug = "gone", Name = "Gone", LoadOrder = 3, Available = false, Reason = "removed" },
            },
        });

        var import = await s.LoadModpackAsync("big-pack");

        Assert.NotNull(import);
        Assert.True(import!.PinVersions);
        Assert.Equal(("Big Pack", "All of it"), (import.Name, import.Description));
        Assert.Equal(new[] { "vostokmods:a", "vostokmods:b" }, import.Mods.Select(m => m.Source));
        Assert.Equal("abc", import.Checksums["vostokmods:b"]);
        Assert.Contains(prompt.Messages, m => m.Title == "Import modpack" && m.Message.Contains("Gone: not available"));
    }

    [Fact]
    public async Task An_unknown_modpack_is_reported()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        s.ProfilesManifestOverride = (_, _) => throw new VostokModsException(VostokModsError.NotFound, "404");

        Assert.Null(await s.LoadModpackAsync("nope"));
        Assert.Contains(prompt.Messages, m => m.Title == "Import failed" && m.Message.Contains("no modpack at `nope`"));
    }

    [Fact]
    public async Task The_modpack_prompt_parses_urls_and_slugs()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt { TextAnswer = "https://vostokmods.net/modpack/big-pack" };
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        s.ProfilesModpackListOverride = _ => Task.FromResult(new VmPage<VmModpackSummary>
        {
            Items = new[] { new VmModpackSummary { Slug = "big-pack", Name = "Big Pack" } },
        });
        string? asked = null;
        s.ProfilesManifestOverride = (slug, _) =>
        {
            asked = slug;
            return Task.FromResult(new VmModpackManifest { Name = "Big", Mods = new[] { new VmModpackMod { Slug = "a" } } });
        };

        Assert.NotNull(await s.AskForModpackAsync());
        Assert.Equal("big-pack", asked);

        Assert.True(ManagerSession.TryParseModpackInput("big-pack", out var slug));
        Assert.Equal("big-pack", slug);
        Assert.False(ManagerSession.TryParseModpackInput("https://vostokmods.net/mod/some-mod", out _));
    }

    [Fact]
    public async Task The_modpack_list_is_filtered_and_failures_come_back_as_an_error()
    {
        using var sb = new Sandbox();
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.ProfilesModpackListOverride = _ => Task.FromResult(new VmPage<VmModpackSummary>
        {
            Items = new[]
            {
                new VmModpackSummary { Slug = "a", Name = "Alpha", Author = "Joni" },
                new VmModpackSummary { Slug = "b", Name = "Beta", Summary = "survival" },
            },
        });

        var listing = await s.ListModpacksAsync();
        Assert.Null(listing.Error);
        Assert.StartsWith("2 modpacks on VostokMods", listing.Header);
        Assert.Equal("b", Assert.Single(ManagerSession.FilterModpacks(listing.Packs, "SURV")).Slug);

        s.ProfilesModpackListOverride = _ => throw new VostokModsException(VostokModsError.Offline, "offline");
        var failed = await s.ListModpacksAsync();
        Assert.Empty(failed.Packs);
        Assert.Equal("offline", failed.Error);
    }

    [Fact]
    public void A_modpack_zip_is_read_and_sourceless_mods_are_named()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        var zip = Path.Combine(sb.Root, "pack.zip");
        new ModProfile
        {
            Name = "Zip Pack",
            Mods =
            {
                ProfileKit.Pm("a", "1.0", source: "vostokmods:a"),
                ProfileKit.Pm("local", "1.0", name: "Local Only"),
            },
        }.ExportAsModpackZip(zip);

        var import = s.LoadModpackFile(zip);

        Assert.NotNull(import);
        Assert.Equal("vostokmods:a", Assert.Single(import!.Mods).Source);
        Assert.Contains(prompt.Messages, m => m.Title == "Import modpack" && m.Message.Contains("Local Only"));

        File.WriteAllBytes(zip, Sandbox.BuildVmz("x", "1.0"));
        Assert.Null(s.LoadModpackFile(zip));
        Assert.Contains("inform:Not a modpack", prompt.Asked);
    }

    [Fact]
    public void Mod_list_files_that_dont_parse_or_are_empty_are_refused()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        var bad = Path.Combine(sb.Root, "bad.json");
        File.WriteAllText(bad, "{ nope");
        var empty = Path.Combine(sb.Root, "empty.json");
        File.WriteAllText(empty, "{ \"mods\": [] }");

        Assert.Null(s.LoadModListFile(bad));
        Assert.Null(s.LoadModListFile(empty));
        Assert.Contains("inform:Import failed", prompt.Asked);
        Assert.Contains("inform:Empty mod list", prompt.Asked);
    }

    [Fact]
    public void The_saved_template_is_a_mod_list_the_importer_reads()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = RefreshTests.OpenWithProfile(sb, prompt);
        var path = Path.Combine(sb.Root, "docs", ManagerSession.ModListTemplateFileName);

        Assert.True(s.SaveModListTemplate(path));

        Assert.Contains("inform:Template saved", prompt.Asked);
        var import = s.LoadModListFile(path);
        Assert.Equal(4, import!.Mods.Count);
        Assert.Equal("vostokmods:my-first-mod", import.Mods[0].Source);
    }
}
