using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

/// <summary>Collects every report, synchronously.</summary>
internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = new();
    public void Report(T value) => Reports.Add(value);
}

public class UpdatesTests
{
    internal static string Src(string slug) => $"[updates]\nsource=\"vostokmods:{slug}\"";

    /// <summary>A session whose network calls fail loudly unless a test
    /// replaces them, so nothing ever reaches VostokMods or GitHub.</summary>
    internal static ManagerSession Offline(ManagerSession s)
    {
        s.UpdateNet.CheckVersions = (_, _, _) => throw new InvalidOperationException("network: CheckVersions");
        s.UpdateNet.DownloadMod = (_, _, _) => throw new InvalidOperationException("network: DownloadMod");
        s.UpdateNet.GetMod = _ => throw new InvalidOperationException("network: GetMod");
        s.UpdateNet.ListAllMods = () => throw new InvalidOperationException("network: ListAllMods");
        s.UpdateNet.FetchManagerRelease = () => throw new InvalidOperationException("network: manager");
        s.UpdateNet.DownloadManagerAsset = (_, _) => throw new InvalidOperationException("network: manager asset");
        s.UpdateNet.FetchMmlRelease = () => throw new InvalidOperationException("network: mml");
        s.UpdateNet.DownloadMmlAsset = (_, _, _) => throw new InvalidOperationException("network: mml asset");
        s.UpdateNet.StartSwapScript = _ => throw new InvalidOperationException("never start swap.bat in tests");
        s.UpdateNet.CurrentExe = () => "";
        return s;
    }

    private static string Cfg(params (string Key, bool Enabled, int Priority)[] rows)
    {
        var text = "[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\n";
        foreach (var r in rows) text += $"{r.Key}={(r.Enabled ? "true" : "false")}\n";
        text += "\n[profile.Default.priority]\n\n";
        foreach (var r in rows) text += $"{r.Key}={r.Priority}\n";
        return text;
    }

    /// <summary>alpha 1.0, linked to vostokmods:alpha, priority 7 in
    /// cfg, in the Default profile, with VostokMods serving 1.1.</summary>
    private static ManagerSession AlphaOutdated(Sandbox sb, ScriptedPrompt prompt)
    {
        sb.WriteCfg(Cfg(("alpha@1.0", true, 7)));
        sb.AddMod("alpha", "1.0", name: "Alpha", extraManifest: Src("alpha"));
        var s = Offline(RefreshTests.OpenWithProfile(sb, prompt,
            new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = true, Priority = 7 }));
        s.Refresh();
        s.LatestVersions["vostokmods:alpha"] = "1.1";
        s.UpdateNet.DownloadMod = (slug, path, progress) =>
        {
            Assert.Equal("alpha", slug);
            progress?.Report((5, 10));
            File.WriteAllBytes(path, Sandbox.BuildVmz("alpha", "1.1", name: "Alpha", extraManifest: Src("alpha")));
            progress?.Report((10, 10));
            return Task.CompletedTask;
        };
        s.UpdateNet.CheckVersions = (_, _, _) =>
            Task.FromResult(new Dictionary<string, string> { ["alpha"] = "1.1" });
        return s;
    }

    [Fact]
    public async Task Fresh_cache_is_used_without_a_request()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: Src("alpha"));
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        s.Settings.CachedVersions = new() { ["vostokmods:alpha"] = "2.0" };
        s.Settings.CacheTimestamp = DateTime.UtcNow.AddMinutes(-10).ToString("o");
        var said = new List<string>();
        s.Status += (_, m) => said.Add(m);

        await s.CheckUpdatesAsync();

        Assert.Equal("2.0", s.LatestVersions["vostokmods:alpha"]);
        Assert.Contains("cached ~10 min ago", said.Last());
        Assert.Contains("1 outdated", said.Last());
    }

    [Fact]
    public async Task Forced_check_stores_versions_by_source_key_and_persists_them()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.2", extraManifest: Src("alpha"));
        sb.AddMod("beta", "1.0", extraManifest: Src("beta"));
        sb.AddMod("gamma", "1.0");  // unlinked
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        IReadOnlyList<string>? asked = null;
        s.UpdateNet.CheckVersions = (slugs, fresh, progress) =>
        {
            asked = slugs;
            Assert.True(fresh);
            progress?.Report((1, 2));
            return Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["alpha"] = "1.1", ["beta"] = "1.3" });
        };
        var said = new List<string>();
        s.Status += (_, m) => said.Add(m);

        await s.CheckUpdatesAsync(forceFresh: true);

        Assert.Equal(new[] { "alpha", "beta" }, asked!.OrderBy(x => x));
        Assert.Equal("1.3", s.LatestVersions["vostokmods:beta"]);
        Assert.Contains("1/2", said[1]);
        Assert.Equal("Updates: 1 outdated, 1 current, 0 unknown  ·  1 not linked to VostokMods — click to link", said.Last());
        Assert.Equal("1.3", Settings.Load().CachedVersions["vostokmods:beta"]);
        Assert.Equal(UpdateStatus.Ahead, s.GetUpdateState(s.Registry.FindById("alpha")!).Status);
        Assert.Equal(UpdateStatus.Outdated, s.GetUpdateState(s.Registry.FindById("beta")!).Status);
        Assert.Equal(UpdateStatus.Unlinked, s.GetUpdateState(s.Registry.FindById("gamma")!).Status);
    }

    [Fact]
    public async Task Failed_check_says_so_and_keeps_the_old_versions()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: Src("alpha"));
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        s.LatestVersions["vostokmods:alpha"] = "1.0";
        var said = new List<string>();
        s.Status += (_, m) => said.Add(m);

        await s.CheckUpdatesAsync(forceFresh: true);

        Assert.StartsWith("Updates: check failed — network: CheckVersions", said.Last());
        Assert.Equal("1.0", s.LatestVersions["vostokmods:alpha"]);
    }

    [Fact]
    public void Mismatched_version_formats_are_neither_outdated_nor_current()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "0.0.420", extraManifest: Src("alpha"));
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Refresh();
        s.LatestVersions["vostokmods:alpha"] = "0.4.20_R";
        var state = s.GetUpdateState(s.Registry.FindById("alpha")!);
        Assert.Equal((UpdateStatus.Mismatch, "≠"), (state.Status, state.Glyph));
        Assert.Equal(1, s.GetUpdateSummary().Mismatch);
        Assert.Empty(s.GetOutdatedMods());
    }

    [Fact]
    public async Task Update_replaces_the_file_and_carries_cfg_state_backup_library_and_profile()
    {
        using var sb = new Sandbox();
        var s = AlphaOutdated(sb, new ScriptedPrompt());
        var progress = new ListProgress<DownloadProgress>();
        var path = s.Registry.FindById("alpha")!.Path;

        var r = await s.UpdateModAsync(s.Registry.FindById("alpha")!, progress);

        Assert.NotNull(r);
        Assert.True(r!.Ok);
        Assert.Equal(("1.0", "1.1"), (r.OldVersion, r.NewVersion));
        Assert.Equal("Updated Alpha → v1.1 (cfg state carried over).", r.Message);
        var live = s.Registry.FindById("alpha")!;
        Assert.Equal(("1.1", path), (live.Version, live.Path));
        Assert.False(File.Exists(path + ".download"));
        var cfg = sb.LoadCfg();
        Assert.False(cfg.HasEntry("alpha", "1.0"));
        Assert.Equal(7, cfg.Priority("alpha", "1.1"));
        Assert.True(cfg.IsEnabled("alpha", "1.1"));
        Assert.Equal("1.0", Assert.Single(ModBackup.ListBackups(sb.ModsDir, "alpha")).Version);
        Assert.Contains(ModLibrary.List(sb.ModsDir), e => e.ModId == "alpha" && e.Version == "1.1");
        Assert.Equal("1.1", ModProfile.LoadAll().Single().Mods.Single().Version);
        Assert.Contains(progress.Reports, p => p.Percent == 50 && p.Path == path);
        Assert.Null(s.CurrentDownload);
        Assert.False(s.IsUpdating);
        Assert.Equal(UpdateStatus.Current, s.GetUpdateState(live).Status);
    }

    [Fact]
    public async Task Locked_mods_are_never_updated()
    {
        using var sb = new Sandbox();
        var s = AlphaOutdated(sb, new ScriptedPrompt());
        s.Settings.LockedMods.Add("alpha");
        var downloads = 0;
        s.UpdateNet.DownloadMod = (_, _, _) => { downloads++; return Task.CompletedTask; };

        var r = await s.UpdateModAsync(s.Registry.FindById("alpha")!);

        Assert.False(r!.Ok);
        Assert.StartsWith("🔒 Alpha is locked", r.Message);
        Assert.Equal(0, downloads);
        Assert.Equal("1.0", s.Registry.FindById("alpha")!.Version);
        Assert.Empty(s.GetOutdatedMods());
    }

    [Fact]
    public async Task A_failed_download_reports_the_error_and_leaves_the_mod_alone()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = AlphaOutdated(sb, prompt);
        var path = s.Registry.FindById("alpha")!.Path;
        s.UpdateNet.DownloadMod = (_, p, _) =>
        {
            File.WriteAllText(p, "partial");
            throw new VostokModsException(VostokModsError.Offline, "Could not reach VostokMods.");
        };

        var r = await s.UpdateModAsync(s.Registry.FindById("alpha")!);

        Assert.False(r!.Ok);
        Assert.Contains("error:Update failed for Alpha", prompt.Asked);
        Assert.False(File.Exists(path + ".download"));
        Assert.Equal("1.0", s.Registry.FindById("alpha")!.Version);
        Assert.Null(s.CurrentDownload);
    }

    [Fact]
    public async Task Update_all_updates_every_outdated_mod_and_rechecks_once()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", extraManifest: Src("alpha"));
        sb.AddMod("beta", "2.0", extraManifest: Src("beta"));
        sb.AddMod("gamma", "1.0", extraManifest: Src("gamma"));  // locked
        var s = Offline(RefreshTests.OpenWithProfile(sb, new ScriptedPrompt()));
        s.Settings.LockedMods.Add("gamma");
        s.Refresh();
        foreach (var (k, v) in new[] { ("alpha", "1.1"), ("beta", "2.1"), ("gamma", "1.5") })
            s.LatestVersions["vostokmods:" + k] = v;
        s.UpdateNet.DownloadMod = (slug, path, _) =>
        {
            File.WriteAllBytes(path, Sandbox.BuildVmz(slug, s.LatestVersions["vostokmods:" + slug],
                extraManifest: Src(slug)));
            return Task.CompletedTask;
        };
        var checks = 0;
        s.UpdateNet.CheckVersions = (_, _, _) =>
        {
            checks++;
            return Task.FromResult(new Dictionary<string, string> { ["alpha"] = "1.1", ["beta"] = "2.1", ["gamma"] = "1.5" });
        };

        var results = await s.UpdateAllAsync();

        Assert.Equal(new[] { "alpha", "beta" }, results.Select(r => r.ModId).OrderBy(x => x));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(1, checks);
        Assert.Equal("1.1", s.Registry.FindById("alpha")!.Version);
        Assert.Equal("2.1", s.Registry.FindById("beta")!.Version);
        Assert.Equal("1.0", s.Registry.FindById("gamma")!.Version);
    }

    [Fact]
    public async Task Revert_restores_a_backup_and_backs_up_the_current_version_first()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var s = AlphaOutdated(sb, prompt);
        await s.UpdateModAsync(s.Registry.FindById("alpha")!);
        var backup = Assert.Single(s.GetBackups(s.Registry.FindById("alpha")!));
        Assert.Equal("v1.0", backup.VersionLabel);

        var r = await s.RevertModFromBackupAsync(s.Registry.FindById("alpha")!, backup.Path);

        Assert.True(r!.Ok);
        Assert.Equal("Reverted Alpha: v1.1 → v1.0.", r.Message);
        Assert.Equal("1.0", s.Registry.FindById("alpha")!.Version);
        var cfg = sb.LoadCfg();
        Assert.Equal(7, cfg.Priority("alpha", "1.0"));
        Assert.False(cfg.HasEntry("alpha", "1.1"));
        Assert.Contains(s.GetBackups(s.Registry.FindById("alpha")!), b => b.Version == "1.1");
        Assert.Equal("1.0", ModProfile.LoadAll().Single().Mods.Single().Version);

        // Deleting a backup asks first.
        prompt.ConfirmAnswer = false;
        Assert.False(s.DeleteBackup(backup));
        Assert.True(File.Exists(backup.Path));
        prompt.ConfirmAnswer = true;
        Assert.True(s.DeleteBackup(backup));
        Assert.False(File.Exists(backup.Path));
    }

    [Fact]
    public void Download_progress_formats_like_the_status_line()
    {
        var p = new DownloadProgress("x", 512 * 1024, 2L * 1024 * 1024);
        Assert.Equal(25, p.Percent);
        Assert.Equal("25%", p.CellText);
        Assert.Equal("Downloading A … 25%  (512 KB / 2.0 MB)", p.StatusText("A"));
        Assert.Equal("…", new DownloadProgress("x", 10, 0).CellText);
        Assert.Equal("Downloading A … 10 B", new DownloadProgress("x", 10, 0).StatusText("A"));
    }
}
