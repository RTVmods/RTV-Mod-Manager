using System.IO.Compression;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class SaveSlotsTests
{
    private static string Root => SaveSlots.RootDir(SaveSlots.DefaultUserDir);

    /// <summary>Writes slot n: occupied (Character.tres) unless told
    /// otherwise, with the given _meta.cfg keys.</summary>
    private static void Slot(int n, bool occupied = true, string name = "", string profile = "", string map = "")
    {
        var dir = SaveSlots.SlotDir(Root, n);
        Directory.CreateDirectory(dir);
        if (occupied) File.WriteAllText(Path.Combine(dir, SaveSlots.OccupancyMarker), "save");
        File.WriteAllText(Path.Combine(dir, SaveSlots.MetaFile),
            $"[meta]\n\ndisplay_name=\"{name}\"\nlast_map=\"{map}\"\nprofile=\"{profile}\"\n"
            + "last_play_iso=\"2026-10-01T18:30:00\"\n");
    }

    private static void ActiveSlot(int n)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, SaveSlots.StateFile), $"[state]\n\nactive_slot={n}\n");
    }

    private static ModProfile Profile(string name, params string[] modIds) => new()
    {
        Name = name,
        CreatedAt = DateTime.UtcNow,
        Mods = modIds.Select(id => new ProfileMod { ModId = id, DisplayName = id, Version = "1.0", IsEnabled = true }).ToList(),
    };

    private static Dictionary<string, string> Pending()
        => SaveSlots.ReadSection(Path.Combine(Root, SaveSlots.PendingFile), "pending");

    [Fact]
    public void Slots_are_listed_as_cards_with_their_profile_and_missing_mods()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        Slot(1, name: "Hard run", profile: "Beta", map: "Village");
        Slot(3, profile: "Gone");
        ActiveSlot(1);
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(), activeProfile: "Alpha",
            profiles: new[] { Profile("Alpha", "alpha"), Profile("Beta", "alpha", "gamma") });
        session.Refresh();

        var view = session.GetSavesView();

        Assert.Equal("Alpha", view.LiveProfile);
        Assert.Equal(new[] { "Alpha", "Beta" }, view.ProfileNames);
        Assert.Equal(5, view.Slots.Count);
        var s1 = view.Slots[0];
        Assert.Equal(("Hard run", "Village", "Beta", true, true), (s1.Title, s1.LastMap, s1.RunsUnder, s1.IsActiveSlot, s1.CanLaunch));
        Assert.False(s1.ModsPresent);
        Assert.Equal(new[] { "gamma v1.0" }, s1.MissingMods);
        var s2 = view.Slots[1];
        Assert.Equal(("Slot 2", false, "Alpha", true), (s2.Title, s2.Occupied, s2.RunsUnder, s2.ModsPresent));
        Assert.False(s2.CanDelete);
        var s3 = view.Slots[2];
        Assert.True(s3.ProfileMissing);
        Assert.False(s3.ModsPresent);
    }

    [Fact]
    public void A_library_copy_counts_as_present()
    {
        using var sb = new Sandbox();
        var lib = ModLibrary.LibraryDir(sb.ModsDir);
        sb.AddMod("gamma", "0.9", dir: lib, fileName: "gamma__v0.9.vmz");
        Slot(1, profile: "Beta");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(), activeProfile: "Alpha",
            profiles: new[] { Profile("Alpha"), Profile("Beta", "gamma") });
        session.Refresh();

        Assert.True(session.GetSaveSlots()[0].ModsPresent);
    }

    [Fact]
    public void Launching_a_slot_switches_to_its_profile_and_tells_the_mod_which_slot()
    {
        using var sb = new Sandbox();
        sb.AddMod("alpha", "1.0");
        Slot(1, name: "Run", profile: "Beta");
        var launcher = new RecordingLauncher();
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), launcher, saveSlots: true, activeProfile: "Alpha",
            profiles: new[] { Profile("Alpha", "alpha"), Profile("Beta", "alpha") });
        session.Refresh();  // installs the built-in Save Slots

        Assert.True(session.LaunchSlot(1));

        Assert.Equal("Beta", session.ActiveProfile!.Name);
        Assert.Single(launcher.Launched);
        var pending = Pending();
        Assert.Equal(("load", "1", "Beta"), (pending["kind"], pending["slot"], pending["profile"]));
    }

    [Fact]
    public void A_failed_launch_takes_the_pending_slot_back()
    {
        using var sb = new Sandbox();
        Slot(1);
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher { AppId = 0 }, saveSlots: true);
        session.Refresh();

        Assert.False(session.LaunchSlot(1));

        Assert.Contains("inform:Can't launch via Steam", prompt.Asked);
        Assert.False(File.Exists(Path.Combine(Root, SaveSlots.PendingFile)));
    }

    [Fact]
    public void Nothing_launches_while_the_game_runs_or_into_a_dead_slot()
    {
        using var sb = new Sandbox();
        Slot(1);
        Slot(2);
        SaveSlots.SetMetaValue(Root, 2, "dead", "true");
        var prompt = new ScriptedPrompt();
        var launcher = new RecordingLauncher { Running = true };
        var session = LaunchKit.Open(sb, prompt, launcher, saveSlots: true);
        session.Refresh();

        Assert.False(session.LaunchSlot(1));
        launcher.Running = false;
        Assert.False(session.LaunchSlot(2));

        Assert.Equal(new[] { "inform:Save slots", "inform:Slot is dead" }, prompt.Asked);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void Built_in_save_slots_off_blocks_a_slot_launch()
    {
        using var sb = new Sandbox();
        Slot(1);
        var prompt = new ScriptedPrompt();
        var launcher = new RecordingLauncher();
        var session = LaunchKit.Open(sb, prompt, launcher, saveSlots: false);

        Assert.False(session.LaunchSlot(1));

        Assert.Contains("inform:Save slots", prompt.Asked);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void A_new_game_backs_up_the_occupied_slot_and_ties_it_to_the_live_profile()
    {
        using var sb = new Sandbox();
        Slot(2, name: "Old run");
        var prompt = new ScriptedPrompt { TextAnswer = "  Fresh  " };
        var launcher = new RecordingLauncher();
        var session = LaunchKit.Open(sb, prompt, launcher, saveSlots: true, activeProfile: "Alpha",
            profiles: new[] { Profile("Alpha") });
        session.Refresh();

        Assert.True(session.StartNewGameInSlot(2));

        Assert.Contains("confirm:Replace this save?", prompt.Asked);
        Assert.Single(Directory.GetFiles(AppPaths.BackupsDir, "saves-before-new-slot2-*.zip"));
        var pending = Pending();
        Assert.Equal(("new", "2", "Alpha", "Fresh"), (pending["kind"], pending["slot"], pending["profile"], pending["name"]));
        Assert.Equal("Alpha", SaveSlots.Read(Root, 2).Profile);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public void Deleting_the_slot_in_play_zips_it_and_clears_the_game_folder_copy_too()
    {
        using var sb = new Sandbox();
        Slot(1, name: "Run");
        ActiveSlot(1);
        File.WriteAllText(Path.Combine(SaveSlots.DefaultUserDir, "Character.tres"), "live save");
        sb.WriteCfg("[settings]\n");
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());

        Assert.True(session.DeleteSlot(1));

        Assert.Contains("confirm:Delete save", prompt.Asked);
        var zip = Assert.Single(Directory.GetFiles(AppPaths.BackupsDir, "saves-deleted-slot1-*.zip"));
        using (var z = ZipFile.OpenRead(zip))
            Assert.Contains(z.Entries, e => e.FullName == "game-folder/Character.tres");
        Assert.False(SaveSlots.Read(Root, 1).Occupied);
        Assert.False(File.Exists(Path.Combine(SaveSlots.DefaultUserDir, "Character.tres")));
        Assert.True(File.Exists(ModConfig.DefaultPath));  // never part of a save
    }

    [Fact]
    public void Rename_profile_link_and_backup_write_the_slot_folder()
    {
        using var sb = new Sandbox();
        Slot(1, name: "Run");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());

        Assert.True(session.RenameSlot(1, " Second run "));
        Assert.True(session.SetSlotProfile(1, "Beta"));
        Assert.False(session.RenameSlot(4, "empty slots can't be renamed"));
        var zip = session.BackUpSlots();

        var s = SaveSlots.Read(Root, 1);
        Assert.Equal(("Second run", "Beta"), (s.DisplayName, s.Profile));
        Assert.NotNull(zip);
        Assert.StartsWith(AppPaths.BackupsDir, zip);
        using var z = ZipFile.OpenRead(zip!);
        Assert.Contains(z.Entries, e => e.FullName == "MultiSaveSlots/slot1/_meta.cfg");

        Assert.True(session.SetSlotProfile(1, null));
        Assert.Equal("", SaveSlots.Read(Root, 1).Profile);
    }

    [Fact]
    public void Built_in_save_slots_retire_the_save_slot_mods_they_replace_everywhere()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("[settings]\n\nactive_profile=\"Alpha\"\n\n"
            + "[profile.Alpha.enabled]\n\nmulti-save-slots@1.1.0=false\nkeep@1.0=true\n\n"
            + "[profile.Beta.enabled]\n\nmulti-save-slots@1.1.16=true\n\n"
            + "[profile.Beta.priority]\n\nmulti-save-slots@1.1.16=6\n");
        // Two different mods sharing one id: Reborn 1.1.0 live, the original 1.4 in the Library.
        sb.AddMod("multi-save-slots", "1.1.0", "Multi Save Slots Reborn");
        sb.AddMod("multi-save-slots", "1.1.0", "Multi Save Slots Reborn", inDisabledFolder: true, fileName: "old.vmz");
        sb.AddMod("multi-save-slots", "1.4", "Multi Save Slots", dir: ModLibrary.LibraryDir(sb.ModsDir),
            fileName: "multi-save-slots__v1.4.vmz");
        sb.AddMod("keep", "1.0");
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher(), saveSlots: true, activeProfile: "Alpha",
            profiles: new[] { Profile("Alpha", "multi-save-slots", "keep"), Profile("Beta", "multi-save-slots") });
        var recycled = new List<string>();
        session.RecycleOverride = p => { recycled.Add(p); File.Delete(p); };

        session.Refresh();               // a scan retires the live archives and the cfg rows
        session.SetUpBuiltInSaveSlots(); // startup also clears the Library and every saved profile

        Assert.Empty(Directory.GetFiles(sb.ModsDir, "*.vmz", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).StartsWith("multi-save-slots", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f) == "old.vmz"));
        Assert.Equal(3, recycled.Count);
        Assert.DoesNotContain(session.Registry.Entries, e => e.ModId == "multi-save-slots");
        var cfg = sb.LoadCfg();
        Assert.Empty(cfg.ProfileEntries("Beta"));
        Assert.True(cfg.HasEntry("keep", "1.0"));
        Assert.All(ModProfile.LoadAll(), p => Assert.DoesNotContain(p.Mods, m => m.ModId == "multi-save-slots"));
        Assert.Contains(ModProfile.LoadAll().Single(p => p.Name == "Alpha").Mods, m => m.ModId == "keep");
        Assert.DoesNotContain("confirm:Save slots", prompt.Asked);  // no question: they're replaced
    }

    [Fact]
    public void Turning_built_in_save_slots_off_removes_the_mod_and_its_cfg_row()
    {
        using var sb = new Sandbox();
        sb.WriteCfg("[settings]\n\nactive_profile=\"Default\"\n");
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher(), saveSlots: true);
        session.Refresh();
        Assert.NotEmpty(Directory.GetFiles(sb.ModsDir, "save-slots__v*.vmz"));

        Assert.True(session.ApplySaveSlotsStep(false));

        Assert.Empty(Directory.GetFiles(sb.ModsDir, "save-slots__v*.vmz"));
        Assert.Empty(Directory.GetFiles(ModLibrary.LibraryDir(sb.ModsDir), "save-slots__v*.vmz"));
        Assert.False(sb.LoadCfg().HasEntry(BuiltInMods.SaveSlotsId, BuiltInMods.SaveSlotsVersion));
        Assert.Contains("Built-in save slots are off", session.SaveSlotsWarning());
    }
}
