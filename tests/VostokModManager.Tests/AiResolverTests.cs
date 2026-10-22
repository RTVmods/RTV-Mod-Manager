#if AI_RESOLVER
using System.IO.Compression;
using VostokModManager.Ai;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

/// <summary>The AI edition's session paths that don't need Claude
/// itself (it is never detected here, so nothing is run).</summary>
public class AiResolverTests
{
    [Fact]
    public void Applying_a_merge_backs_up_the_vmz_and_replaces_the_contested_entry()
    {
        using var sb = new Sandbox();
        var path = sb.AddMod("alpha", "1.0", files: new Dictionary<string, string> { ["Scripts/AI.gd"] = "old" });
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());
        var verdict = new ConflictResolver.Verdict { Ok = true, ConflictKey = "Scripts/AI.gd", MergedSource = "merged" };
        var mod = new ConflictResolver.ConflictMod { ModId = "alpha", DisplayName = "Alpha", ArchivePath = path, IsArchive = true };

        Assert.True(session.ApplyMergeToMod(verdict, mod));

        Assert.Contains("confirm:Apply to Alpha", prompt.Asked);
        Assert.Contains("inform:Apply succeeded", prompt.Asked);
        var bak = Assert.Single(Directory.GetFiles(sb.ModsDir, "alpha-1.0.vmz.*.bak"));
        using (var z = ZipFile.OpenRead(bak))
            Assert.Equal("old", new StreamReader(z.GetEntry("Scripts/AI.gd")!.Open()).ReadToEnd());
        using (var z = ZipFile.OpenRead(path))
            Assert.Equal("merged", new StreamReader(z.GetEntry("Scripts/AI.gd")!.Open()).ReadToEnd());
    }

    [Fact]
    public void A_directory_mod_or_an_incomplete_verdict_is_not_patched()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());

        Assert.False(session.ApplyMergeToMod(new ConflictResolver.Verdict { ConflictKey = "a", MergedSource = "b" },
            new ConflictResolver.ConflictMod { ModId = "dir", IsArchive = false }));
        Assert.False(session.ApplyMergeToMod(new ConflictResolver.Verdict(),
            new ConflictResolver.ConflictMod { ModId = "x", IsArchive = true, ArchivePath = "x.vmz" }));

        Assert.Equal(new[] { "inform:Not supported", "inform:Not enough data" }, prompt.Asked);
    }

    [Fact]
    public async Task Resolve_without_claude_says_why_and_runs_nothing()
    {
        using var sb = new Sandbox();
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());
        var said = new List<(StatusArea, string)>();
        session.Status += (a, m) => said.Add((a, m));

        var v = await session.ResolveAsync(new ConflictDetector.Conflict
        {
            Type = ConflictDetector.TYPE_FILE_OVERLAP, Key = "Scripts/AI.gd", ModIds = { "a", "b" },
        });

        Assert.Null(v);
        Assert.False(session.IsResolving);
        Assert.Contains(said, s => s.Item1 == StatusArea.Conflicts && s.Item2.StartsWith("Resolve needs Claude Code"));
    }

    [Fact]
    public async Task Log_diagnosis_without_claude_reports_the_failure()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());

        var v = await session.DiagnoseLogAsync(new GodotLogAnalyzer.LogAnalysis());

        Assert.False(v.Ok);
        Assert.Contains("inform:Analysis error", prompt.Asked);
    }

    [Fact]
    public void Proposed_order_rows_name_live_mods_and_flag_changes()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", "Alpha", priority: 2);
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());
        session.Refresh();
        var verdict = new LogDiagnoser.LogVerdict
        {
            Ok = true,
            ProposedOrder =
            {
                new LogDiagnoser.ProposedPriority { ModId = "alpha", Priority = 5 },
                new LogDiagnoser.ProposedPriority { ModId = "ghost", Priority = 1 },
            },
        };

        var rows = session.ProposedOrderRows(verdict);

        Assert.Equal(new ProposedOrderRow("alpha", "Alpha", 2, 5), rows[0]);
        Assert.True(rows[0].Changes);
        Assert.Equal(new ProposedOrderRow("ghost", "ghost", null, 1), rows[1]);
    }
}
#endif
