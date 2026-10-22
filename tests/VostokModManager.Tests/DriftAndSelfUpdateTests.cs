using System.IO.Compression;
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;
using static VostokModManager.Tests.UpdatesTests;

namespace VostokModManager.Tests;

public class DriftAndSelfUpdateTests
{
    // ── drift ─────────────────────────────────────────────────────────

    private static ManagerSession Drifted(Sandbox sb, ScriptedPrompt prompt)
    {
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=true\n\n"
            + "[profile.Default.priority]\n\nalpha@1.0=5\n");
        sb.AddMod("alpha", "1.0", name: "Alpha");
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt,
            new ProfileMod { ModId = "alpha", DisplayName = "Alpha", Version = "1.0", IsEnabled = true, Priority = 3 },
            new ProfileMod { ModId = "ghost", DisplayName = "Ghost", Version = "1.0", IsEnabled = true }));
        s.Refresh();
        return s;
    }

    [Fact]
    public void Drift_lists_each_difference_and_previews_two()
    {
        using var sb = new Sandbox();
        var s = Drifted(sb, new ScriptedPrompt());

        var drift = s.ComputeDrift();

        Assert.Equal(new[]
            {
                ("Alpha", "priority: profile 3 → live 5"),
                ("Ghost", "in profile but not currently live (Apply will re-fetch)"),
            },
            drift.Select(d => (d.DisplayName, d.Description)));
        var status = s.GetDriftStatus()!;
        Assert.Equal(2, status.Count);
        Assert.Equal("⚠ Drift (2) vs 'Default': Alpha (priority: profile 3 → live 5); "
            + "Ghost (in profile but not currently live (Apply will re-fetch))  —  click for details.", status.Text);
    }

    [Fact]
    public void Sync_writes_live_state_into_the_profile_after_review()
    {
        using var sb = new Sandbox();
        var s = Drifted(sb, new ScriptedPrompt());

        Assert.Equal(0, s.SyncLiveStateIntoActiveProfile(_ => false));
        Assert.Equal(3, ModProfile.LoadAll().Single().Mods.First(m => m.ModId == "alpha").Priority);

        IReadOnlyList<DriftItem>? reviewed = null;
        Assert.Equal(1, s.SyncLiveStateIntoActiveProfile(d => { reviewed = d; return true; }));

        Assert.Equal(2, reviewed!.Count);
        Assert.Equal(5, ModProfile.LoadAll().Single().Mods.First(m => m.ModId == "alpha").Priority);
        // A profile mod that isn't live stays listed: Apply re-fetches it.
        Assert.Equal("ghost", Assert.Single(s.ComputeDrift()).ModId);
    }

    // ── mod loader ────────────────────────────────────────────────────

    [Fact]
    public void Loader_and_manager_lines()
    {
        Assert.Equal(VersionState.Outdated, ManagerSession.DescribeMml("3.4.1", "v3.4.2", false).State);
        Assert.Equal("MML: installed v3.4.2, latest v3.4.2 ✓ up to date (cached) — click to open releases page.",
            ManagerSession.DescribeMml("3.4.2", "v3.4.2", true).Text);
        Assert.Equal(VersionState.Unknown, ManagerSession.DescribeMml("", "", false).State);
        Assert.Equal("⬆ UPDATE AVAILABLE — Mod Manager v0.6.1 → v0.7.0  ·  click to auto-update",
            ManagerSession.DescribeManager("0.6.1", "0.7.0", false, false).Text);
        Assert.Equal(VersionState.Ahead, ManagerSession.DescribeManager("0.8.0", "0.7.0", false, false).State);
        Assert.Contains("no release published yet", ManagerSession.DescribeManager("0.6.1", "", false, true).Text);
    }

    private static string FakeModloader(string version)
        => $"extends Node\nconst MODLOADER_VERSION := \"{version}\"\n" + new string('#', 1200);

    [Fact]
    public async Task Mml_update_backs_up_and_replaces_the_loader_files()
    {
        using var sb = new Sandbox();
        File.WriteAllText(Path.Combine(sb.GameDir, "modloader.gd"), FakeModloader("3.4.1"));
        File.WriteAllText(Path.Combine(sb.GameDir, "override.cfg"), "old");
        var prompt = new ScriptedPrompt();
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt));
        s.UpdateNet.DownloadMmlAsset = (tag, asset, path) =>
        {
            Assert.Equal("v3.4.2", tag);
            File.WriteAllText(path, asset == "modloader.gd" ? FakeModloader("3.4.2") : "new");
            return Task.CompletedTask;
        };
        s.UpdateNet.FetchMmlRelease = () => Task.FromResult<MmlRelease?>(new MmlRelease { TagName = "v3.4.2", HtmlUrl = "u" });
        s.Settings.MmlLatestTag = "v3.4.2";
        s.Settings.MmlCheckedAt = DateTime.UtcNow.ToString("o");

        Assert.True(await s.UpdateMmlAsync("v3.4.2"));

        Assert.Equal("3.4.2", MmlInstall.DetectInstalledVersion(sb.ModsDir));
        Assert.Equal("new", File.ReadAllText(Path.Combine(sb.GameDir, "override.cfg")));
        Assert.Equal(2, Directory.GetFiles(sb.GameDir, "*.bak").Length);
        Assert.Empty(Directory.GetFiles(sb.GameDir, "*.download"));
        Assert.Contains("inform:MML updated", prompt.Asked);
        Assert.Equal(VersionState.UpToDate, (await s.CheckMmlVersionAsync()).State);
    }

    [Fact]
    public async Task A_bogus_mml_download_changes_nothing()
    {
        using var sb = new Sandbox();
        File.WriteAllText(Path.Combine(sb.GameDir, "modloader.gd"), FakeModloader("3.4.1"));
        var prompt = new ScriptedPrompt();
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt));
        s.UpdateNet.DownloadMmlAsset = (_, _, path) => { File.WriteAllText(path, new string('x', 5000)); return Task.CompletedTask; };
        s.UpdateNet.FetchMmlRelease = () => Task.FromResult<MmlRelease?>(null);

        Assert.False(await s.UpdateMmlAsync("v3.4.2"));

        Assert.Equal("3.4.1", MmlInstall.DetectInstalledVersion(sb.ModsDir));
        Assert.Empty(Directory.GetFiles(sb.GameDir, "*.bak"));
        Assert.Empty(Directory.GetFiles(sb.GameDir, "*.download"));
        Assert.Contains("error:MML update failed", prompt.Asked);
    }

    // ── manager ───────────────────────────────────────────────────────

    [Fact]
    public async Task Manager_check_is_cached_for_a_day_and_forgets_withdrawn_releases()
    {
        using var sb = new Sandbox();
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.UpdateNet.FetchManagerRelease = () => Task.FromResult<(ManagerReleaseStatus, ManagerRelease?)>(
            (ManagerReleaseStatus.Found, new ManagerRelease { Version = "99.0.0" }));

        var first = await s.CheckManagerUpdateAsync();
        Assert.Equal((VersionState.Outdated, false), (first.State, first.FromCache));
        Assert.Equal("99.0.0", Settings.Load().ManagerReleaseVersion);

        s.UpdateNet.FetchManagerRelease = () => throw new InvalidOperationException("cached; no request");
        Assert.True((await s.CheckManagerUpdateAsync()).FromCache);

        s.UpdateNet.FetchManagerRelease = () => Task.FromResult<(ManagerReleaseStatus, ManagerRelease?)>(
            (ManagerReleaseStatus.NoRelease, null));
        var none = await s.CheckManagerUpdateAsync(forceFresh: true);
        Assert.True(none.NoRelease);
        Assert.Equal("", s.Settings.ManagerReleaseVersion);
    }

    [Fact]
    public async Task Auto_update_writes_the_swap_script_and_asks_the_app_to_exit()
    {
        using var sb = new Sandbox();
        var exeDir = Path.Combine(sb.Root, "app");
        Directory.CreateDirectory(exeDir);
        var currentExe = Path.Combine(exeDir, "VostokModManagerAI.exe");
        File.WriteAllText(currentExe, "MZ old");
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.UpdateNet.CurrentExe = () => currentExe;
        s.UpdateNet.TempRoot = Path.Combine(sb.Root, "tmp");
        var asset = new ManagerReleaseAsset { Name = "VostokModManagerAI.zip", DownloadUrl = "x" };
        s.UpdateNet.FetchManagerRelease = () => Task.FromResult<(ManagerReleaseStatus, ManagerRelease?)>(
            (ManagerReleaseStatus.Found, new ManagerRelease { Version = "99.0.0", Assets = new[] { asset } }));
        s.UpdateNet.DownloadManagerAsset = (_, path) =>
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var name in new[] { "tools/other.exe", "bin/VostokModManagerAI.exe" })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write("MZ new");
            return Task.CompletedTask;
        };
        string? started = null;
        s.UpdateNet.StartSwapScript = bat => started = bat;

        var r = await s.AutoUpdateManagerAsync();

        Assert.True(r.MustExit);
        Assert.Equal(r.ScriptPath, started);
        var newExe = Path.Combine(Path.GetDirectoryName(r.ScriptPath)!, "extracted", "bin", "VostokModManagerAI.exe");
        Assert.Equal(ManagerSession.BuildSwapScript(newExe, currentExe), File.ReadAllText(r.ScriptPath));
        Assert.Contains($"copy /y \"{newExe}\" \"{currentExe}\" >nul\r\nif errorlevel 1 goto wait", File.ReadAllText(r.ScriptPath));
        Assert.Equal("MZ old", File.ReadAllText(currentExe));  // the script does the swap, not us
    }

    [Fact]
    public async Task Auto_update_without_a_build_for_this_edition_aborts()
    {
        using var sb = new Sandbox();
        var currentExe = Path.Combine(sb.Root, "VostokModManagerIntegrated.exe");
        File.WriteAllText(currentExe, "MZ");
        var prompt = new ScriptedPrompt();
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt));
        s.UpdateNet.CurrentExe = () => currentExe;
        s.UpdateNet.FetchManagerRelease = () => Task.FromResult<(ManagerReleaseStatus, ManagerRelease?)>(
            (ManagerReleaseStatus.Found, new ManagerRelease
            {
                Version = "99.0.0",
                Assets = new[] { new ManagerReleaseAsset { Name = "VostokModManagerAI.zip", DownloadUrl = "x" } },
            }));

        var r = await s.AutoUpdateManagerAsync();

        Assert.False(r.MustExit);
        Assert.Contains("inform:Auto-update aborted", prompt.Asked);
    }

    [Fact]
    public void A_bare_exe_payload_is_used_under_our_name()
    {
        using var sb = new Sandbox();
        var download = Path.Combine(sb.Root, "download.bin");
        File.WriteAllText(download, "MZ binary");
        var found = ManagerSession.FindUpdateExe(download, sb.Root, @"C:\x\VostokModManagerAI.exe");
        Assert.Equal(Path.Combine(sb.Root, "VostokModManagerAI.exe"), found);
        Assert.True(ManagerSession.LooksLikeExe(found));

        File.WriteAllText(download, "neither an exe nor a zip");
        Assert.Equal("", ManagerSession.FindUpdateExe(download, Path.Combine(sb.Root, "w"), "a.exe"));
    }

    // ── description + changelog ───────────────────────────────────────

    [Fact]
    public async Task Description_is_fetched_and_cached()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: Src("alpha"));
        sb.AddMod("beta", "1.0");
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        s.UpdateNet.GetMod = slug => Task.FromResult(new VmModDetail { Slug = slug, Description = " ## Hi\n", Author = "Joni" });

        var r = await s.FetchDescriptionAsync(s.Registry.FindById("alpha")!);

        Assert.Equal((true, "## Hi", "Last fetched just now · author: Joni"), (r.Ok, r.Text, r.Status));
        Assert.Equal("## Hi", s.GetCachedDescription(s.Registry.FindById("alpha")!));
        Assert.Equal("## Hi", Settings.Load().CachedDescriptions["vostokmods:alpha"]);
        Assert.False((await s.FetchDescriptionAsync(s.Registry.FindById("beta")!)).Ok);
    }

    [Fact]
    public void Changelog_is_read_from_the_archive()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", name: "Alpha", files: new Dictionary<string, string>
        {
            ["readme.md"] = "readme",
            ["Changelog.md"] = "## 1.0\n- first",
        });
        sb.AddMod("beta", "1.0", name: "Beta");
        var prompt = new ScriptedPrompt();
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt));
        s.Refresh();

        var alpha = s.Registry.FindById("alpha")!;
        Assert.True(ManagerSession.HasInternalChangelog(alpha));
        var log = s.ReadChangelog(alpha)!;
        Assert.Equal(("Changelog.md", "## 1.0\n- first"), (log.SourceFile, log.Text));

        Assert.Null(s.ReadChangelog(s.Registry.FindById("beta")!));
        Assert.Equal(new[] { "inform:No changelog" }, prompt.Asked);
    }
}
