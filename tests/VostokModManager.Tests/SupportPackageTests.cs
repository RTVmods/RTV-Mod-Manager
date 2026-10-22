using System.IO.Compression;
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
