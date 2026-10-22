using System.IO.Compression;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class SetupTests
{
    [Fact]
    public async Task First_run_startup_with_loose_mods_lists_the_migration_then_the_update_checks_then_the_backup()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(),
            activeProfile: null, isFirstRun: true);

        var r = await session.StartupAsync();

        Assert.True(r.ModsFolderReadable);
        Assert.Equal(new[]
        {
            StartupStep.OfferProfileMigration, StartupStep.CheckUpdates, StartupStep.CheckMmlVersion,
            StartupStep.CheckManagerUpdate, StartupStep.OfferFirstRunBackup,
        }, r.NextSteps);
        Assert.Single(session.Registry.Entries);
    }

    [Fact]
    public async Task Startup_adopts_dropped_in_mods_into_the_active_profile()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());

        var r = await session.StartupAsync();

        Assert.False(r.Has(StartupStep.OfferProfileMigration));
        Assert.False(r.Has(StartupStep.OfferFirstRunBackup));
        Assert.Contains(session.ActiveProfile!.Mods, m => m.ModId == "alpha");
    }

    [Fact]
    public async Task Startup_stops_when_the_mods_folder_cannot_be_read()
    {
        using var sb = new Sandbox();
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(), isFirstRun: true);
        session.Settings.ModsDir = Path.Combine(sb.Root, "missing", "mods");
        var said = new List<string>();
        session.Status += (_, m) => said.Add(m);

        var r = await session.StartupAsync();

        Assert.False(r.ModsFolderReadable);
        Assert.Empty(r.NextSteps);
        Assert.Contains(said, m => m.StartsWith("Mods: cannot read"));
    }

    [Fact]
    public async Task First_run_backup_zips_mods_cfg_and_mcm_into_the_backups_folder()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        sb.WriteCfg("[settings]\n");
        var mcm = Path.Combine(ManagerSession.McmDir, "Alpha");
        Directory.CreateDirectory(mcm);
        File.WriteAllText(Path.Combine(mcm, "config.ini"), "x=1");
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher(), isFirstRun: true);

        var result = await session.WriteFirstRunBackupAsync();
        session.FinishFirstRun();

        Assert.NotNull(result);
        Assert.StartsWith(AppPaths.BackupsDir, result!.ZipPath);
        using (var z = ZipFile.OpenRead(result.ZipPath))
        {
            var names = z.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("mods/alpha-1.0.vmz", names);
            Assert.Contains("mod_config.cfg", names);
            Assert.Contains("MCM/Alpha/config.ini", names);
        }
        Assert.Contains("inform:Backup complete", prompt.Asked);
        Assert.True(File.Exists(Settings.Path));
    }

    [Fact]
    public void Migration_builds_the_profile_fills_the_library_and_imports_loader_profiles()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("""
            [settings]

            active_profile="Default"

            [profile.Default.enabled]

            alpha@1.0=true
            beta@2.0=false

            [profile.Raid.enabled]

            beta@2.0=true
            """);
        sb.AddMod("alpha", "1.0");
        sb.AddMod("beta", "2.0");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(), activeProfile: null);
        session.Refresh();
        Assert.True(session.ShouldOfferMigration);

        var offer = session.GetMigrationOffer();
        Assert.Equal((2, "Default"), (offer.ModCount, offer.DefaultName));
        Assert.Equal(new[] { new LoaderProfileInfo("Raid", 1) }, offer.LoaderProfiles);

        var r = session.AdoptProfileModel("Main", new[] { "Raid" });

        Assert.NotNull(r);
        Assert.Equal(2, r!.LibraryAdded);
        Assert.Equal("Main", session.Settings.ActiveProfileName);
        Assert.Equal("Main", session.ActiveProfile!.Name);
        Assert.Equal("Main", sb.LoadCfg().ActiveProfile);
        Assert.Equal(2, ModLibrary.List(sb.ModsDir).Count);
        var names = ModProfile.LoadAll().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Main", "Raid" }, names);
        Assert.False(session.ShouldOfferMigration);
    }

    [Fact]
    public void An_empty_profile_name_is_refused_and_declining_is_remembered()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher(), activeProfile: null);
        session.Refresh();

        Assert.Null(session.AdoptProfileModel("  "));
        Assert.Contains("inform:Name required", prompt.Asked);

        session.DeclineProfileMigration();
        Assert.False(session.ShouldOfferMigration);
        Assert.True(Settings.Load().MigrationDeclined);
    }

    [Fact]
    public void Game_folder_check_accepts_the_install_or_its_mods_folder()
    {
        using var sb = new Sandbox();
        LaunchKit.InstallLoader(sb);

        var a = ManagerSession.CheckGameFolder(sb.GameDir);
        var b = ManagerSession.CheckGameFolder(sb.ModsDir + "\\");
        var c = ManagerSession.CheckGameFolder(sb.Root);

        Assert.True(a.Ok);
        Assert.True(a.HasModloader);
        Assert.Equal(sb.ModsDir, a.ModsDir);
        Assert.Equal((true, sb.ModsDir, sb.GameDir), (b.Ok, b.ModsDir, b.GameDir));
        Assert.False(c.Ok);
        Assert.True(c.GameDirExists);
    }

    [Fact]
    public void Applying_another_game_folder_rescans_it_and_asks_for_the_watcher()
    {
        using var sb = new Sandbox();
        var otherGame = Path.Combine(sb.Root, "Other Library", "Road to Vostok");
        sb.AddMod("zeta", "1.0", dir: Path.Combine(otherGame, "mods"));
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());
        session.Refresh();

        var r = session.ApplyGameFolderStep(otherGame);

        Assert.NotNull(r);
        Assert.True(r!.RearmFolderWatcher);
        Assert.Equal(Path.Combine(otherGame, "mods"), session.Settings.ModsDir);
        Assert.Equal("zeta", Assert.Single(session.Registry.Entries).ModId);
        Assert.Equal(session.Settings.ModsDir, Settings.Load().ModsDir);
    }

    [Fact]
    public void A_missing_mods_folder_in_settings_is_reverted()
    {
        using var sb = new Sandbox();
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());
        var before = SettingsValues.From(session.Settings);

        var r = session.ApplySettingsChange(before,
            before with { ModsDir = Path.Combine(sb.Root, "nope"), SkipLoaderUi = false });

        Assert.True(r.Saved);
        Assert.True(r.ModsDirReverted);
        Assert.False(r.RearmFolderWatcher);
        Assert.Contains("inform:Mods folder not found", prompt.Asked);
        var saved = Settings.Load();
        Assert.Equal((sb.ModsDir, false), (saved.ModsDir, saved.SkipLoaderUi));
    }

    [Fact]
    public void The_wizard_reports_five_steps()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        LaunchKit.InstallLoader(sb);
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(), activeProfile: null);
        session.Refresh();

        var steps = session.GetSetupSteps();

        Assert.Equal(new[] { SetupStepId.GameFolder, SetupStepId.ModLoader, SetupStepId.Profiles,
            SetupStepId.SaveSlots, SetupStepId.VostokMods }, steps.Select(s => s.Id));
        Assert.Equal(SetupState.Done, steps[0].State);
        Assert.Equal(SetupState.Done, steps[1].State);
        Assert.Equal(SetupState.NeedsAction, steps[2].State);
        Assert.Equal(SetupState.Optional, steps[4].State);  // alpha has no VostokMods source
        Assert.Equal("3.4.2", session.GetModLoaderStatus().InstalledVersion);
        Assert.Equal(1, session.GetVostokModsSetupStatus().Unlinked);

        Assert.True(session.ApplyModLoaderStep(false));
        Assert.False(Settings.Load().SkipLoaderUi);
    }
}
