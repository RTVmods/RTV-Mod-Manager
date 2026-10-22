using VostokModManager.Domain;

namespace VostokModManager.Tests;

public class SandboxTests
{
    [Fact]
    public void Every_appdata_path_lands_inside_the_sandbox()
    {
        using var sb = new Sandbox();
        Assert.True(AppPaths.IsSandboxed);
        foreach (var p in new[]
                 {
                     Settings.Path, ModConfig.DefaultPath, ModProfile.ProfilesDir,
                     CrashCheckpoint.DirFor(CheckpointSlot.LastLaunch), SupportPackage.GameUserDir,
                     AppPaths.BackupsDir,
                 })
            Assert.StartsWith(sb.Root, p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_reads_sandbox_mods_and_overlays_cfg()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", "Alpha", priority: 5);
        sb.AddMod("beta", "2.1", "Beta");
        sb.AddMod("gamma", "0.3", inDisabledFolder: true);
        sb.WriteCfg("""
            [settings]

            active_profile="Default"

            [profile.Default.enabled]

            alpha@1.0=true
            beta@2.1=false

            [profile.Default.priority]

            alpha@1.0=-10
            """);

        var reg = new ModRegistry();
        Assert.True(reg.Scan(sb.ModsDir, sb.LoadCfg()));

        Assert.Equal(3, reg.Entries.Count);
        var alpha = reg.FindById("alpha")!;
        Assert.True(alpha.IsEnabled);
        Assert.Equal(-10, alpha.Priority);
        Assert.False(reg.FindById("beta")!.IsEnabled);
        Assert.False(reg.FindById("gamma")!.IsEnabled);
    }

    [Fact]
    public void Unedited_cfg_round_trips_byte_for_byte()
    {
        using var sb = new Sandbox();
        const string text = "[settings]\n\nactive_profile=\"Default\"\n\n[mod_sources]\n\nalpha@1.0=\"{\\\"id\\\":\\\"alpha\\\",\\\"provider\\\":\\\"vostokmods\\\"}\"\n";
        sb.WriteCfg(text);
        sb.LoadCfg().Save();
        Assert.Equal(text, File.ReadAllText(sb.CfgPath));
    }
}
