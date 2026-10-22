using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class ProfileApplyTests
{
    /// <summary>A download seam that writes a mod archive for `slug`
    /// at `version` (or `newest` when none is asked for), recording
    /// each call.</summary>
    internal static Func<string, string, string?, CancellationToken, Task<VmFile>> FakeDownloads(
        List<(string Slug, string? Version)> calls, string newest = "9.0",
        Func<string, string?, Exception?>? fail = null, Func<string, string>? idFor = null)
        => (slug, dest, version, _) =>
        {
            calls.Add((slug, version));
            var ex = fail?.Invoke(slug, version);
            if (ex != null) throw ex;
            var v = version ?? newest;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, Sandbox.BuildVmz(idFor?.Invoke(slug) ?? slug, v,
                extraManifest: ProfileKit.Source(slug)));
            return Task.FromResult(new VmFile { Version = v, FileName = Path.GetFileName(dest) });
        };

    [Fact]
    public void The_plan_picks_a_row_kind_for_every_case()
    {
        using var sb = new Sandbox();
        sb.AddMod("match", "1.0");
        sb.AddMod("swap", "1.0");
        sb.AddMod("locked", "1.0");
        ProfileKit.AddToLibrary(sb, "swap", "2.0");
        ProfileKit.AddToLibrary(sb, "fromlib", "1.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.Settings.LockedMods.Add("locked");
        s.Refresh();
        var target = ProfileKit.SaveProfile("Target",
            ProfileKit.Pm("match", "1.0"),
            ProfileKit.Pm("swap", "2.0"),
            ProfileKit.Pm("locked", "5.0"),
            ProfileKit.Pm("fromlib", "1.0"),
            ProfileKit.Pm("remote", "1.0", source: "vostokmods:remote"),
            ProfileKit.Pm("nowhere", "1.0"));

        var plan = s.BuildApplyPlan(target);

        ApplyPlanRow Row(string id) => plan.Rows.Single(r => r.ProfileMod.ModId == id);
        Assert.Equal(ApplyRowKind.Match, Row("match").Kind);
        Assert.Equal(ApplyRowKind.ReplaceFromLibrary, Row("swap").Kind);
        Assert.Equal(ApplyRowKind.LockedSkip, Row("locked").Kind);
        Assert.True(Row("locked").SourceIsFixed);
        Assert.Equal(ApplyRowKind.InstallFromLibrary, Row("fromlib").Kind);
        Assert.Equal(ApplyRowKind.MissingDownload, Row("remote").Kind);
        Assert.Equal(ApplyRowKind.NoSource, Row("nowhere").Kind);
        Assert.Equal(new[] { ApplySource.KeepCurrent, ApplySource.UseLibrary }, Row("swap").SourceChoices);
        Assert.True(Row("swap").Apply);
        Assert.False(Row("match").Apply);
        Assert.Contains("1 🔒 locked (skipped)", plan.Summary);
    }

    [Fact]
    public void Changing_a_rows_source_re_derives_its_kind_and_tick()
    {
        using var sb = new Sandbox();
        sb.AddMod("swap", "1.0");
        ProfileKit.AddToLibrary(sb, "swap", "2.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.Refresh();
        var row = s.BuildApplyPlan(ProfileKit.SaveProfile("T", ProfileKit.Pm("swap", "2.0"))).Rows.Single();

        row.Source = ApplySource.KeepCurrent;
        Assert.Equal((ApplyRowKind.Match, false, "Keep installed version"), (row.Kind, row.IsCheckable, row.StatusText));

        row.Source = ApplySource.UseLibrary;
        Assert.Equal((ApplyRowKind.ReplaceFromLibrary, true), (row.Kind, row.Apply));
        Assert.Throws<ArgumentException>(() => row.Source = ApplySource.UseBundle);
    }

    [Fact]
    public async Task Executing_a_plan_makes_the_profile_live_and_active()
    {
        using var sb = new Sandbox();
        var extra = sb.AddMod("extra", "1.0");
        var swapLive = sb.AddMod("swap", "1.0");
        var lockedLive = sb.AddMod("locked", "1.0");
        ProfileKit.AddToLibrary(sb, "swap", "2.0");
        ProfileKit.AddToLibrary(sb, "fromlib", "1.0");
        sb.WriteCfg("""
            [settings]

            active_profile="Default"

            [profile.Default.enabled]

            locked@1.0=false
            """);
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.Settings.LockedMods.Add("locked");
        s.Refresh();
        ProfileKit.SaveProfile("Target",
            ProfileKit.Pm("swap", "2.0", priority: 2),
            ProfileKit.Pm("fromlib", "1.0", enabled: false),
            // The spec version isn't on the host any more; the download
            // brings the newest and the profile follows it.
            ProfileKit.Pm("remote", "0.5", source: "vostokmods:remote"));
        s.ReloadProfiles();
        var calls = new List<(string, string?)>();
        s.ProfilesDownloadOverride = FakeDownloads(calls,
            fail: (_, v) => v == "0.5" ? new VostokModsException(VostokModsError.VersionNotFound, "gone") : null);
        var plan = s.BuildApplyPlan(ProfileKit.Named(s, "Target"));
        var lines = new List<string>();

        var result = await s.ExecuteApplyAsync(plan, new SyncProgress(lines));

        Assert.True(result.Applied);
        Assert.Equal(1, result.Wiped);
        Assert.False(File.Exists(extra));
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "extra", "1.0"));     // captured before the wipe
        Assert.True(File.Exists(lockedLive));
        Assert.True(File.Exists(swapLive + ".replaced-by-profile.bak"));
        Assert.Equal(new[] { ("remote", (string?)"0.5"), ("remote", null) }, calls);
        Assert.Contains(lines, l => l.Contains("isn't on VostokMods"));

        Assert.Equal("Target", s.ActiveProfile!.Name);
        Assert.Equal("Target", Settings.Load().ActiveProfileName);
        var cfg = sb.LoadCfg();
        Assert.Equal("Target", cfg.ActiveProfile);
        Assert.Equal(2, cfg.Priority("swap", "2.0"));
        Assert.False(cfg.IsEnabled("fromlib", "1.0"));
        Assert.False(cfg.IsEnabled("locked", "1.0"));                    // locked state carried over
        Assert.Equal("9.0", ProfileKit.OnDisk("Target").Mods.Single(m => m.ModId == "remote").Version);
        Assert.Contains(s.Registry.Entries, e => e.ModId == "remote" && e.Version == "9.0");
        Assert.NotEmpty(ModLibrary.Find(sb.ModsDir, "remote", "9.0"));
    }

    [Fact]
    public async Task An_unticked_row_is_left_alone()
    {
        using var sb = new Sandbox();
        ProfileKit.AddToLibrary(sb, "fromlib", "1.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        var plan = s.BuildApplyPlan(ProfileKit.SaveProfile("T", ProfileKit.Pm("fromlib", "1.0")));
        plan.Rows.Single().Apply = false;

        var result = await s.ExecuteApplyAsync(plan);

        Assert.True(result.Applied);
        Assert.Empty(Directory.GetFiles(sb.ModsDir, "*.vmz"));
    }

    [Fact]
    public async Task A_cancelled_apply_stops_before_touching_cfg()
    {
        using var sb = new Sandbox();
        sb.AddMod("extra", "1.0");
        var s = RefreshTests.OpenWithProfile(sb, new ScriptedPrompt());
        s.Refresh();
        var plan = s.BuildApplyPlan(ProfileKit.SaveProfile("T"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await s.ExecuteApplyAsync(plan, ct: cts.Token);

        Assert.False(result.Applied);
        Assert.NotEqual("T", sb.LoadCfg().ActiveProfile);
        Assert.Single(Directory.GetFiles(sb.ModsDir, "*.vmz"));
    }

}

/// <summary>An IProgress that records synchronously (Progress&lt;T&gt;
/// posts to the thread pool without a sync context).</summary>
internal sealed class SyncProgress : IProgress<string>
{
    private readonly List<string> _lines;
    public SyncProgress(List<string> lines) => _lines = lines;
    public void Report(string value) => _lines.Add(value);
}
