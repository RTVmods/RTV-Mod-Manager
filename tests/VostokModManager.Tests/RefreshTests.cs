using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class RefreshTests
{
    /// <summary>A session over the sandbox with built-in save slots off
    /// (so the embedded Save Slots archive stays out of the folder) and
    /// an active profile named "Default" holding `mods`.</summary>
    internal static ManagerSession OpenWithProfile(Sandbox sb, ScriptedPrompt prompt, params ProfileMod[] mods)
    {
        var settings = new Settings
        {
            ModsDir = sb.ModsDir,
            SaveSlotsBuiltIn = false,
            ActiveProfileName = "Default",
        };
        new ModProfile { Name = "Default", CreatedAt = DateTime.UtcNow, Mods = mods.ToList() }.SaveMetadataOnly();
        var session = new ManagerSession(settings, prompt);
        session.ReloadProfiles();
        return session;
    }

    [Fact]
    public void Refresh_adopts_a_dropped_in_mod_into_the_active_profile_and_cfg()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0", priority: 3);
        var session = OpenWithProfile(sb, new ScriptedPrompt());
        var changed = 0;
        session.Changed += () => changed++;

        Assert.True(session.Refresh());

        Assert.Equal(1, changed);
        var pm = Assert.Single(session.ActiveProfile!.Mods);
        Assert.Equal(("alpha", "1.0", 3, true), (pm.ModId, pm.Version, pm.Priority, pm.IsEnabled));
        var cfg = sb.LoadCfg();
        Assert.True(cfg.HasEntry("alpha", "1.0"));
        Assert.Equal(3, cfg.Priority("alpha", "1.0"));
        // ...and it was written to profile.json, not just held in memory.
        var onDisk = ModProfile.LoadAll().Single(p => p.Name == "Default");
        Assert.Contains(onDisk.Mods, m => m.ModId == "alpha");
    }

    [Fact]
    public void Refresh_follows_a_version_changed_outside_the_manager()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("""
            [settings]

            active_profile="Default"

            [profile.Default.enabled]

            alpha@1.0=false

            [profile.Default.priority]

            alpha@1.0=7
            """);
        sb.AddMod("alpha", "1.1");
        var session = OpenWithProfile(sb, new ScriptedPrompt(),
            new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = false, Priority = 7 });

        session.Refresh();

        Assert.Equal("1.1", session.ActiveProfile!.Mods.Single().Version);
        var cfg = sb.LoadCfg();
        Assert.False(cfg.HasEntry("alpha", "1.0"));
        Assert.False(cfg.IsEnabled("alpha", "1.1"));
        Assert.Equal(7, cfg.Priority("alpha", "1.1"));
        // The live file is archived in the Library so a switch back can restore it.
        Assert.NotEmpty(ModLibrary.List(sb.ModsDir));
    }

    [Fact]
    public void Conflicts_are_scoped_to_the_active_profile_plus_locked_mods()
    {
        using var sb = new Sandbox();
        const string hook = "[hooks]\n\"res://Scripts/AI.gd\"=\"_physics_process\"";
        sb.AddMod("alpha", "1.0", extraManifest: hook);
        sb.AddMod("beta", "1.0", extraManifest: hook);
        var session = OpenWithProfile(sb, new ScriptedPrompt(),
            new ProfileMod { ModId = "alpha", Version = "1.0", IsEnabled = true });
        // beta is live but not in the profile; Refresh adopts it, so it
        // becomes part of the scope through the profile itself.
        session.Refresh();
        Assert.Contains(session.LastConflicts, c => c.ModIds.Contains("alpha") && c.ModIds.Contains("beta"));
    }

    [Fact]
    public void Versions_in_formats_that_cannot_be_ordered_are_ambiguous()
    {
        Assert.True(ManagerSession.VersionsAreAmbiguous("0.0.420", "0.4.20_R"));
        Assert.False(ManagerSession.VersionsAreAmbiguous("1.14", "1.13"));
        Assert.False(ManagerSession.VersionsAreAmbiguous("1.2", "1.2"));
    }
}
