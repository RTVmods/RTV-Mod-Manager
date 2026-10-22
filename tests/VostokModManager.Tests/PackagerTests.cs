using System.IO.Compression;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class PackagerTests
{
    /// <summary>A session whose mods folder holds `beta` (linked to
    /// VostokMods) and `gamma` (unlinked).</summary>
    private static ManagerSession OpenWithDeps(Sandbox sb, ScriptedPrompt prompt)
    {
        sb.AddMod("beta", "2.0", name: "Beta Lib", extraManifest: "[updates]\nsource=\"vostokmods:beta-slug\"");
        sb.AddMod("gamma", "1.0", name: "Gamma");
        var session = RefreshTests.OpenWithProfile(sb, prompt);
        session.Refresh();
        return session;
    }

    /// <summary>The source mod, outside the mods folder.</summary>
    private static string SourceMod(Sandbox sb, string extraManifest = "")
        => sb.AddMod("main", "1.0", name: "Main Mod", priority: 5,
            extraManifest: "author=\"Joni\"\n" + extraManifest,
            files: new Dictionary<string, string> { ["Scripts/Main.gd"] = "extends Node\n" },
            dir: Path.Combine(sb.Root, "work"));

    private static Dictionary<string, Dictionary<string, string>> ReadManifest(string vmz)
    {
        using var zip = ZipFile.OpenRead(vmz);
        using var r = new StreamReader(zip.GetEntry("mod.txt")!.Open());
        return ModArchive.ParseConfigFile(r.ReadToEnd());
    }

    [Fact]
    public void Opening_a_source_fills_the_manifest_and_ticks_installed_deps()
    {
        using var sb = new Sandbox();
        var packager = OpenWithDeps(sb, new ScriptedPrompt()).OpenPackager();
        var src = SourceMod(sb, "\n[updates]\nsource=\"vostokmods:main-mod\"\n\n"
            + "[dependencies]\nrequired=[\"beta-slug\", \"delta\"]\noptional=[\"gamma\"]");

        packager.OpenSource(src);

        var s = packager.State;
        Assert.Equal(("main", "Main Mod", "1.0", 5, "main-mod"), (s.ModId, s.Name, s.Version, s.Priority, s.SourceText));
        Assert.Equal("Joni", s.Author);
        Assert.Equal(PackagerSourceKind.Archive, s.SourceKind);
        // beta is matched by its VostokMods slug; delta isn't installed.
        Assert.Equal(new[] { "beta" }, s.CheckedRequired);
        Assert.Equal(new[] { "gamma" }, s.CheckedOptional);
        Assert.Equal("delta", s.CustomRequired);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(src)!, "main-v1.0.vmz"), s.OutputPath);
        Assert.Contains(packager.LogLines, l => l.StartsWith("✓ Loaded manifest"));
    }

    [Fact]
    public void Validate_reports_what_the_form_logged()
    {
        using var sb = new Sandbox();
        var packager = OpenWithDeps(sb, new ScriptedPrompt()).OpenPackager();

        Assert.Equal(new[] { "✗ Pick a source first." }, packager.Validate());
        packager.State.SourcePath = SourceMod(sb);
        Assert.Equal(new[] { "✗ Pick an output .vmz path first." }, packager.Validate());
        packager.State.OutputPath = Path.Combine(sb.Root, "out", "x.vmz");
        packager.State.SourceText = "12345";
        Assert.StartsWith("✗ VostokMods: '12345'", Assert.Single(packager.Validate()));
        packager.State.SourceText = "https://vostokmods.net/mod/main-mod";
        Assert.Empty(packager.Validate());

        // Exports need something in the set; a modpack needs a source.
        packager.State.SourcePath = "";
        packager.State.SourceText = "";
        Assert.StartsWith("✗ Nothing to export", Assert.Single(packager.Validate(PackagerOutput.PackJson)));
        packager.SetRequired("gamma", true);
        Assert.Empty(packager.Validate(PackagerOutput.PackJson));
        Assert.StartsWith("✗ No mod in this set", Assert.Single(packager.Validate(PackagerOutput.Modpack)));
        Assert.Null(packager.Build());
    }

    [Fact]
    public void Build_writes_the_edits_dependencies_and_their_sources()
    {
        using var sb = new Sandbox();
        var packager = OpenWithDeps(sb, new ScriptedPrompt()).OpenPackager();
        packager.OpenSource(SourceMod(sb));
        var s = packager.State;
        s.Name = "Main Mod Renamed";
        s.Version = "1.1";
        s.Priority = 99999;  // clamped to the form's range
        s.SourceText = "main-mod";
        s.OutputPath = Path.Combine(sb.Root, "out", "main-packed");  // ".vmz" gets appended
        packager.SetRequired("beta", true);
        packager.SetOptional("gamma", true);
        s.CustomRequired = "https://vostokmods.net/mod/delta-slug\n12345";
        s.CustomOptional = "beta, epsilon";

        var result = packager.Build();

        Assert.NotNull(result);
        Assert.True(result!.Success, string.Join("\n", result.Errors));
        var outPath = Path.Combine(sb.Root, "out", "main-packed.vmz");
        Assert.Equal(outPath, result.OutputPath);
        var m = ReadManifest(outPath);
        Assert.Equal("Main Mod Renamed", m["mod"]["name"]);
        Assert.Equal("1.1", m["mod"]["version"]);
        Assert.Equal("10000", m["mod"]["priority"]);
        Assert.Equal("vostokmods:main-mod", m["updates"]["source"]);
        // An uninstalled URL dep is written as its slug; the bare number
        // is skipped; beta stays required only.
        Assert.Equal(new[] { "beta", "delta-slug" }, ModEntry.ParseCsvOrArray(m["dependencies"]["required"]));
        Assert.Equal(new[] { "gamma", "epsilon" }, ModEntry.ParseCsvOrArray(m["dependencies"]["optional"]));
        Assert.Equal("vostokmods:beta-slug", m["dependency_sources"]["beta"]);
        Assert.Equal("vostokmods:delta-slug", m["dependency_sources"]["delta-slug"]);
        Assert.Contains(packager.LogLines, l => l.Contains("'12345'") && l.Contains("skipped"));
        Assert.Contains(packager.LogLines, l => l.StartsWith("✓ Done"));
    }

    [Fact]
    public void Ticks_are_exclusive_and_survive_the_filter()
    {
        using var sb = new Sandbox();
        var packager = OpenWithDeps(sb, new ScriptedPrompt()).OpenPackager();

        Assert.Equal(new[] { "beta", "gamma" }, packager.DependencyRows.Select(r => r.ModId));
        packager.SetRequired("beta", true);
        packager.SetOptional("beta", true);
        Assert.Empty(packager.State.CheckedRequired);
        Assert.Equal(new[] { "beta" }, packager.State.CheckedOptional);

        packager.State.DependencyFilter = "gam";
        var visible = Assert.Single(packager.VisibleDependencyRows());
        Assert.Equal("gamma", visible.Row.ModId);
        packager.State.DependencyFilter = "beta-slug";
        Assert.True(Assert.Single(packager.VisibleDependencyRows()).Optional);
    }

    [Fact]
    public void Export_json_and_modpack_carry_the_set()
    {
        using var sb = new Sandbox();
        var packager = OpenWithDeps(sb, new ScriptedPrompt()).OpenPackager();
        packager.OpenSource(SourceMod(sb));
        packager.State.SourceText = "main-mod";
        packager.SetRequired("beta", true);
        packager.SetOptional("gamma", true);
        packager.State.CustomOptional = "https://vostokmods.net/mod/beta-slug";  // dup of beta, dropped

        var json = Path.Combine(sb.Root, packager.SuggestedExportFileName());
        Assert.True(packager.ExportPackJson(json));
        var import = ModListImport.LoadFromFile(json);
        Assert.Equal("Main Mod", import.Name);
        Assert.Equal(new[] { "main", "beta", "gamma" }, import.Mods.Select(e => e.ModId));
        Assert.Equal("1.0", import.Mods[0].Version);
        Assert.Null(import.Mods[1].IsOptional);
        Assert.True(import.Mods[2].IsOptional);

        var zip = Path.Combine(sb.Root, packager.SuggestedModpackFileName());
        Assert.True(packager.BuildModpack(zip));
        Assert.True(ModpackFile.TryRead(zip, out var pack, out _));
        // gamma has no source, so the modpack leaves it out.
        Assert.Equal(new[] { "vostokmods:main-mod", "vostokmods:beta-slug" }, pack.Mods.Select(e => e.Source));
        Assert.Equal(new[] { "1.0", "2.0" }, pack.Mods.Select(e => e.Version));
        Assert.Contains(packager.LogLines, l => l.Contains("Gamma: no VostokMods source"));
    }

    [Fact]
    public void Opening_a_pack_json_asks_before_replacing_and_keeps_sources()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var packager = OpenWithDeps(sb, prompt).OpenPackager();
        var json = Path.Combine(sb.Root, "pack.json");
        File.WriteAllText(json, """
            {
              "name": "My Pack",
              "description": "things",
              "mods": [
                { "display_name": "My Pack" },
                { "mod_id": "zeta", "source": "vostokmods:zeta-slug" },
                { "source": "https://vostokmods.net/mod/eta", "is_optional": true }
              ]
            }
            """);
        packager.SetRequired("beta", true);

        prompt.ConfirmAnswer = false;
        Assert.False(packager.LoadPackJson(json));
        Assert.Equal(new[] { "beta" }, packager.State.CheckedRequired);

        prompt.ConfirmAnswer = true;
        Assert.True(packager.LoadPackJson(json));
        Assert.Contains("confirm:Replace dependencies?", prompt.Asked);
        Assert.Empty(packager.State.CheckedRequired);
        Assert.Equal(("My Pack", "things"), (packager.State.Name, packager.State.Description));
        Assert.Equal("zeta", packager.State.CustomRequired);
        Assert.Equal("https://vostokmods.net/mod/eta", packager.State.CustomOptional);

        // The JSON's source for zeta is remembered, even after a rebuild
        // of the index, so a re-export carries it.
        packager.RefreshDependencyRows();
        Assert.Equal(ModSource.ForSlug("zeta-slug"), packager.ResolveCustomDepEntry("zeta").Source);
    }

    [Fact]
    public void Dropped_mods_become_custom_dependencies_once()
    {
        using var sb = new Sandbox();
        var packager = OpenWithDeps(sb, new ScriptedPrompt()).OpenPackager();
        var dep = sb.AddMod("theta", "1.0", extraManifest: "[updates]\nsource=\"vostokmods:theta-slug\"",
            dir: Path.Combine(sb.Root, "drops"));
        var notAMod = Path.Combine(sb.Root, "drops", "readme.txt");
        File.WriteAllText(notAMod, "hi");

        var picked = PackagerService.PickModDropPaths(new[] { dep, notAMod });
        Assert.Equal(new[] { dep }, picked);
        Assert.Equal(new[] { "theta" }, packager.AddDroppedDependencies(picked, optional: false));
        Assert.Empty(packager.AddDroppedDependencies(picked, optional: false));
        Assert.Equal("theta", packager.State.CustomRequired);
        Assert.Equal(ModSource.ForSlug("theta-slug"), packager.KnownSourceFor("theta"));

        // A dropped .json is opened as a pack, anything else as the source.
        Assert.Equal(dep, PackagerService.PickDropPath(new[] { notAMod, dep }));
        packager.Drop(dep);
        Assert.Equal(("theta", PackagerSourceKind.Archive), (packager.State.ModId, packager.State.SourceKind));
    }

    [Fact]
    public void Dependency_picker_keeps_missing_deps_and_manual_ids()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: "[dependencies]\nrequired=[\"beta\", \"missing\"]");
        var session = OpenWithDeps(sb, new ScriptedPrompt());
        var alpha = session.Registry.FindById("alpha")!;

        var picker = session.OpenDependencyPicker(alpha);

        Assert.Equal(new[] { "missing", "beta", "gamma" }, picker.AllModIds);
        Assert.Equal(new[] { true, true, false }, picker.Visible().Select(i => i.Checked));
        picker.Filter = "GAM";
        Assert.Equal("gamma", Assert.Single(picker.Visible()).ModId);
        picker.SetChecked("beta", false);
        Assert.Equal("new-dep", picker.AddManual("  new-dep "));
        Assert.Equal("", picker.AddManual("   "));
        Assert.Equal(new[] { "missing", "new-dep" }, picker.Result.OrderBy(x => x));
        Assert.Equal("new-dep", picker.AllModIds[0]);
    }
}
