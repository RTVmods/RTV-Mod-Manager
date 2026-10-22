using System.IO.Compression;
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;
using static VostokModManager.Tests.UpdatesTests;

namespace VostokModManager.Tests;

public class LinksAndDependenciesTests
{
    private static ManagerSession Open(Sandbox sb, ScriptedPrompt prompt)
    {
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt));
        s.UpdateNet.CheckVersions = (_, _, _) => Task.FromResult(new Dictionary<string, string>());
        s.Refresh();
        return s;
    }

    private static string ModTxtIn(string vmz)
    {
        using var zip = ZipFile.OpenRead(vmz);
        using var r = new StreamReader(zip.GetEntry("mod.txt")!.Open());
        return r.ReadToEnd();
    }

    // ── linking ───────────────────────────────────────────────────────

    [Fact]
    public async Task Linking_records_the_source_in_mod_config_and_leaves_the_archive_alone()
    {
        using var sb = new Sandbox();
        var file = sb.AddMod("alpha", "1.0", name: "Alpha");
        var s = Open(sb, new ScriptedPrompt());
        var before = File.ReadAllBytes(file);
        s.UpdateNet.GetMod = slug => Task.FromResult(new VmModDetail { Slug = slug, Name = "Alpha Mod" });

        Assert.True(await s.LinkModAsync(s.Registry.FindById("alpha")!, "https://vostokmods.net/mod/alpha-mod"));

        Assert.Equal(ModSource.ForSlug("alpha-mod"), sb.LoadCfg().GetModSource("alpha", "1.0"));
        Assert.Equal("vostokmods:alpha-mod", s.Registry.FindById("alpha")!.Source.Key);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(s.UnlinkedMods());
    }

    [Fact]
    public async Task A_mod_that_declares_its_source_has_mod_txt_rewritten_with_a_backup()
    {
        using var sb = new Sandbox();
        var file = sb.AddMod("alpha", "1.0", extraManifest: Src("old-alpha"));
        var s = Open(sb, new ScriptedPrompt());
        s.UpdateNet.GetMod = slug => Task.FromResult(new VmModDetail { Slug = slug, Name = "Alpha" });

        Assert.True(await s.LinkModAsync(s.Registry.FindById("alpha")!, "alpha"));

        Assert.Contains("source=\"vostokmods:alpha\"", ModTxtIn(file).Replace(" ", ""));
        Assert.Single(Directory.GetFiles(sb.ModsDir, "alpha-1.0.vmz.*.bak"));
        Assert.Equal("vostokmods:alpha", s.Registry.FindById("alpha")!.Source.Key);
    }

    [Fact]
    public async Task Bad_input_and_unknown_pages_link_nothing()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);
        s.UpdateNet.GetMod = slug => throw new VostokModsException(VostokModsError.NotFound, "404");

        Assert.False(await s.LinkModAsync(s.Registry.FindById("alpha")!, "https://example.com/mod/x"));
        Assert.False(await s.LinkModAsync(s.Registry.FindById("alpha")!, "nosuchmod"));

        Assert.Equal(new[] { "inform:Invalid input", "inform:Not linked" }, prompt.Asked);
        Assert.Contains("no mod at `nosuchmod`", prompt.Messages.Last().Message);
        Assert.False(sb.LoadCfg().GetModSource("alpha", "1.0").IsValid);
    }

    [Fact]
    public async Task Auto_link_links_exact_name_matches_after_confirming()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", name: "Alpha Mod");
        sb.AddMod("beta", "1.0", name: "Something Else");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);
        s.UpdateNet.ListAllMods = () => Task.FromResult(new List<VmModSummary>
        {
            new() { Id = "uuid-1", Slug = "alpha-mod", Name = "Alpha mod" },
            new() { Id = "uuid-2", Slug = "gamma", Name = "Gamma" },
        });

        Assert.Equal(1, await s.AutoLinkModsAsync());

        Assert.Equal(new[] { "confirm:Link to VostokMods" }, prompt.Asked);
        Assert.Contains("1 other mod has no exact name match", prompt.Messages.Single().Message);
        Assert.Equal("vostokmods:alpha-mod", s.Registry.FindById("alpha")!.Source.Key);
        Assert.False(s.Registry.FindById("beta")!.Source.IsValid);
        Assert.Equal("gamma", s.VmSlugById["uuid-2"]);
    }

    [Fact]
    public async Task Auto_link_declined_records_nothing()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", name: "Alpha");
        var s = Open(sb, new ScriptedPrompt { ConfirmAnswer = false });
        s.UpdateNet.ListAllMods = () => Task.FromResult(new List<VmModSummary> { new() { Slug = "alpha", Name = "Alpha" } });

        Assert.Equal(0, await s.AutoLinkModsAsync());
        Assert.False(sb.LoadCfg().GetModSource("alpha", "1.0").IsValid);
    }

    // ── declared dependencies ─────────────────────────────────────────

    [Fact]
    public void Dependencies_are_classified_by_live_state()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", name: "Alpha",
            extraManifest: "[dependencies]\nrequired=\"beta, gamma\"\noptional=\"delta\"");
        sb.AddMod("beta", "1.0");
        sb.AddMod("gamma", "1.0", inDisabledFolder: true);
        var s = Open(sb, new ScriptedPrompt());

        var report = s.GetDependencies(s.Registry.FindById("alpha")!);

        Assert.Equal("`Alpha` declares 2 required and 1 optional dependency entries:", report.Header);
        Assert.Equal(new[]
            {
                ("beta", "required", DependencyState.Enabled),
                ("gamma", "required", DependencyState.Disabled),
                ("delta", "optional", DependencyState.NotInstalled),
            },
            report.Rows.Select(r => (r.ModId, r.Kind, r.State)));
        Assert.Equal("Not installed (optional)", report.Rows[2].StatusText);
        Assert.Equal(new HashSet<string> { "alpha", "beta", "gamma", "delta" },
            s.BuildDependencyFilterSet());
    }

    [Fact]
    public void Dependency_choices_keep_declared_deps_that_are_not_installed()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: "[dependencies]\nrequired=\"ghost, beta\"");
        sb.AddMod("beta", "1.0");
        sb.AddMod("gamma", "1.0");
        var s = Open(sb, new ScriptedPrompt());

        var choices = s.GetDependencyChoices(s.Registry.FindById("alpha")!);

        Assert.Equal(new[] { ("ghost", true, false), ("beta", true, true), ("gamma", false, true) },
            choices.Select(c => (c.ModId, c.Checked, c.Installed)));
    }

    [Fact]
    public async Task Setting_dependencies_writes_mod_txt_and_offers_a_priority_bump()
    {
        using var sb = new Sandbox();
        var file = sb.AddMod("alpha", "1.0", name: "Alpha", priority: 0);
        sb.AddMod("beta", "1.0", priority: 5);
        var prompt = new ScriptedPrompt { ChooseAnswer = 0 };
        var s = Open(sb, prompt);

        Assert.True(await s.SetModDependenciesAsync(s.Registry.FindById("alpha")!, new[] { "beta" }));

        Assert.Equal(new[] { "choose:Bump priority?" }, prompt.Asked);
        Assert.Equal(new[] { "beta" }, s.Registry.FindById("alpha")!.RequiredDependencies);
        Assert.Equal(6, sb.LoadCfg().Priority("alpha", "1.0"));
        Assert.Equal(6, s.Registry.FindById("alpha")!.Priority);
        Assert.Contains("required", ModTxtIn(file));

        // Unchanged set (any order) → nothing written, nothing asked.
        prompt.Asked.Clear();
        Assert.False(await s.SetModDependenciesAsync(s.Registry.FindById("alpha")!, new[] { "BETA" }));
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task Cancelling_the_bump_question_writes_nothing()
    {
        using var sb = new Sandbox();
        var file = sb.AddMod("alpha", "1.0", priority: 0);
        sb.AddMod("beta", "1.0", priority: 5);
        var s = Open(sb, new ScriptedPrompt { ChooseAnswer = -1 });
        var before = File.ReadAllBytes(file);

        Assert.False(await s.SetModDependenciesAsync(s.Registry.FindById("alpha")!, new[] { "beta" }));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // ── missing dependencies ──────────────────────────────────────────

    [Fact]
    public async Task Missing_deps_come_from_the_library_first_then_declared_sources()
    {
        using var sb = new Sandbox();
        // beta is only in the library; gamma's source is declared by
        // alpha; delta is nowhere.
        var staging = Path.Combine(sb.Root, "staging");
        ModLibrary.Add(sb.ModsDir, sb.AddMod("beta", "1.0", dir: staging));
        sb.AddMod("alpha", "1.0", name: "Alpha", extraManifest:
            "[dependencies]\nrequired=\"beta, gamma\"\noptional=\"delta\"\n\n"
            + "[dependency_sources]\ngamma=\"vostokmods:gamma-mod\"");
        var prompt = new ScriptedPrompt();
        var s = Open(sb, prompt);
        var downloaded = new List<string>();
        s.UpdateNet.DownloadMod = (slug, path, _) =>
        {
            downloaded.Add(slug);
            File.WriteAllBytes(path, Sandbox.BuildVmz("gamma", "1.0"));
            return Task.CompletedTask;
        };

        await s.CheckAndPromptMissingDepsAsync(new[] { "alpha" });

        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "beta.vmz")));
        Assert.True(File.Exists(Path.Combine(sb.ModsDir, "gamma.vmz")));
        Assert.Equal(new[] { "gamma-mod" }, downloaded);
        Assert.Equal(new[] { "confirm:Missing dependencies" }, prompt.Asked);
        Assert.Contains("1 required + 1 optional", prompt.Messages[0].Message);
        Assert.Contains("1 other dep(s) auto-copied from your library", prompt.Messages[0].Message);
        Assert.NotNull(s.Registry.FindById("gamma"));
        // The download's source was recorded for the new file.
        Assert.Equal("vostokmods:gamma-mod", s.Registry.FindById("gamma")!.Source.Key);
        Assert.Contains(ModLibrary.List(sb.ModsDir), e => e.ModId == "gamma");
    }

    [Fact]
    public async Task The_resolver_downloads_rows_the_user_fills_in()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: "[dependencies]\nrequired=\"delta\"");
        File.WriteAllText(Path.Combine(sb.ModsDir, "delta.vmz"), "not a mod, in the way");
        var s = Open(sb, new ScriptedPrompt());
        s.UpdateNet.DownloadMod = (slug, path, _) =>
        {
            Assert.Equal("delta-mod", slug);
            File.WriteAllBytes(path, Sandbox.BuildVmz("delta", "2.0"));
            return Task.CompletedTask;
        };
        MissingDependencyResolver? seen = null;

        await s.CheckAndPromptMissingDepsAsync(new[] { "alpha" }, async r =>
        {
            seen = r;
            var row = Assert.Single(r.Rows);
            Assert.Equal(MissingDependencyStatus.Pending, row.Status);
            row.SourceInput = "https://nexusmods.com/mods/1";
            Assert.False(await r.DownloadAsync(row));
            Assert.Equal("(not a VostokMods URL)", row.StatusText);
            row.SourceInput = "https://vostokmods.net/mod/delta-mod";
            Assert.True(await r.DownloadAsync(row));
        });

        var installed = Assert.Single(seen!.Rows);
        Assert.Equal(MissingDependencyStatus.Installed, installed.Status);
        // A file already named delta.vmz is not clobbered.
        Assert.Equal("delta_2.vmz", Path.GetFileName(installed.InstalledPath));
        Assert.Equal("2.0", s.Registry.FindById("delta")!.Version);
        Assert.Contains(seen.Log, l => l.Contains("✓ → delta_2.vmz"));
    }

    [Fact]
    public async Task Declining_the_missing_deps_question_downloads_nothing()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest:
            "[dependencies]\nrequired=\"gamma\"\n\n[dependency_sources]\ngamma=\"vostokmods:gamma\"");
        var s = Open(sb, new ScriptedPrompt { ConfirmAnswer = false });

        await s.CheckAndPromptMissingDepsAsync(new[] { "alpha" });  // DownloadMod would throw

        Assert.Null(s.Registry.FindById("gamma"));
    }

    [Fact]
    public void Source_input_accepts_vostokmods_pages_and_slugs_only()
    {
        Assert.True(MissingDependencyResolver.TryParseSourceInput(" https://vostokmods.net/mod/Some-Mod ", out var a));
        Assert.Equal("vostokmods:some-mod", a.Key);
        Assert.True(MissingDependencyResolver.TryParseSourceInput("some-mod", out _));
        Assert.False(MissingDependencyResolver.TryParseSourceInput("12345", out _));
        Assert.False(MissingDependencyResolver.TryParseSourceInput("https://modworkshop.net/mod/1", out _));
        Assert.False(MissingDependencyResolver.TryParseSourceInput("", out _));
        Assert.Equal("https://vostokmods.net/explore/mods?q=my%20mod", MissingDependencyResolver.SearchUrl("my mod"));
    }
}
