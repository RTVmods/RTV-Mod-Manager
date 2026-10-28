using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class SupportPackageTests
{
    /// <summary>Collects progress reports synchronously (Progress&lt;T&gt;
    /// would post them to the thread pool).</summary>
    private sealed class ListProgress : IProgress<(string Message, int Percent)>
    {
        public List<(string Message, int Percent)> Reports { get; } = new();
        public void Report((string Message, int Percent) value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public void Summary_names_the_profile_and_counts_logs()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var session = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        session.Refresh();

        var none = session.GetSupportPackageSummary();
        Assert.Equal(("Default", 1, 0), (none.ProfileName, none.ModCount, none.LogFileCount));
        Assert.Contains("⚠ No Godot logs found", none.Text);
        Assert.EndsWith(".vmzlog", none.SuggestedFileName);

        Directory.CreateDirectory(SupportPackage.GodotLogsDir);
        File.WriteAllText(Path.Combine(SupportPackage.GodotLogsDir, "godot.log"), "x");
        var one = session.GetSupportPackageSummary();
        Assert.Equal(1, one.LogFileCount);
        Assert.Contains("Active profile: \"Default\"  (1 mods)", one.Text);
    }

    [Fact]
    public async Task Build_writes_the_package_with_progress_and_asks_to_reveal()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        Directory.CreateDirectory(SupportPackage.GodotLogsDir);
        File.WriteAllText(Path.Combine(SupportPackage.GodotLogsDir, "godot.log"), "Godot Engine v4.6\n");
        var prompt = new ScriptedPrompt { ConfirmAnswer = false };
        var session = RefreshTests.OpenWithProfile(sb, prompt);
        session.Refresh();
        var progress = new ListProgress();
        var outPath = Path.Combine(sb.Root, "out", "support.vmzlog");

        var outcome = await session.BuildSupportPackageAsync(outPath, includeRootLogs: true, progress);

        Assert.NotNull(outcome);
        Assert.False(outcome!.RevealRequested);
        Assert.Contains("confirm:Support package created", prompt.Asked);
        Assert.True(File.Exists(outPath));
        Assert.Equal(1, outcome.Result.ModCount);
        Assert.Equal(1, outcome.Result.LogFileCount);
        Assert.Contains(outcome.ReportLines, l => l.StartsWith("Package written:"));
        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, r => Assert.InRange(r.Percent, 0, 100));
        using var zip = ZipFile.OpenRead(outPath);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("logs/"));
    }

    private static string Sha256Of(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static JsonElement ReadJson(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    [Fact]
    public async Task By_default_the_package_lists_every_mod_with_its_checksum_and_leaves_the_files_out()
    {
        using var sb = new Sandbox();
        var alpha = sb.AddMod("alpha", "1.0", name: "Alpha Mod");
        var beta = sb.AddMod("beta", "2.1", inDisabledFolder: true);
        var session = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt { ConfirmAnswer = false });
        session.Refresh();
        var outPath = Path.Combine(sb.Root, "out", "support.vmzlog");

        var outcome = await session.BuildSupportPackageAsync(outPath);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Result.ModFilesIncluded);
        Assert.Equal(2, outcome.Result.ListedModCount);
        using var zip = ZipFile.OpenRead(outPath);
        Assert.Null(zip.GetEntry("profile.vmprofile"));
        Assert.NotNull(zip.GetEntry("profile.json"));

        var rows = ReadJson(zip, "mods.json").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.GetProperty("id").GetString() == "alpha");
        Assert.Equal("Alpha Mod", a.GetProperty("name").GetString());
        Assert.Equal("1.0", a.GetProperty("version").GetString());
        Assert.Equal("alpha-1.0.vmz", a.GetProperty("file").GetString());
        Assert.True(a.GetProperty("enabled").GetBoolean());
        Assert.Equal(new FileInfo(alpha).Length, a.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Sha256Of(alpha), a.GetProperty("sha256").GetString());
        var b = rows.Single(r => r.GetProperty("id").GetString() == "beta");
        Assert.Equal("Disabled/beta-2.1.vmz", b.GetProperty("file").GetString());
        Assert.False(b.GetProperty("enabled").GetBoolean());
        Assert.Equal(Sha256Of(beta), b.GetProperty("sha256").GetString());
        Assert.Equal("alpha", rows[0].GetProperty("id").GetString());  // enabled mods first

        Assert.False(ReadJson(zip, "support-info.json").GetProperty("modFilesIncluded").GetBoolean());
        Assert.Contains(outcome.ReportLines, l => l.StartsWith("Mods listed: 2") && !l.Contains("Mod files bundled"));
    }

    [Fact]
    public async Task Ticking_the_mod_files_adds_the_importable_profile_bundle()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var session = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt { ConfirmAnswer = false });
        session.Refresh();
        var outPath = Path.Combine(sb.Root, "out", "support.vmzlog");

        var outcome = await session.BuildSupportPackageAsync(outPath, includeModFiles: true);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Result.ModFilesIncluded);
        using var zip = ZipFile.OpenRead(outPath);
        var bundle = zip.GetEntry("profile.vmprofile");
        Assert.NotNull(bundle);
        using (var inner = new ZipArchive(bundle!.Open()))
            Assert.Contains(inner.Entries, e => e.FullName.StartsWith("mods/") && e.FullName.EndsWith(".vmz"));
        Assert.NotNull(zip.GetEntry("mods.json"));
        Assert.True(ReadJson(zip, "support-info.json").GetProperty("modFilesIncluded").GetBoolean());
    }

    [Fact]
    public void The_summary_says_the_mod_files_are_opt_in()
    {
        using var sb = new Sandbox();
        var session = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        session.Refresh();

        var text = session.GetSupportPackageSummary().Text;

        Assert.Contains("mods.json", text);
        Assert.Contains("only when ticked below", text);
    }

    [Fact]
    public async Task A_failed_build_is_reported_and_returns_null()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var session = RefreshTests.OpenWithProfile(sb, prompt);
        session.Refresh();
        // The output path is an existing folder: the package can't be written.
        var outPath = Path.Combine(sb.Root, "taken");
        Directory.CreateDirectory(Path.Combine(outPath, "inner"));

        var outcome = await session.BuildSupportPackageAsync(outPath);

        Assert.Null(outcome);
        Assert.Contains(prompt.Messages, m => m.Title == "Support package error" && m.Kind == PromptKind.Error);
    }
}
