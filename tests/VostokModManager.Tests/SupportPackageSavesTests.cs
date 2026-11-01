using VostokModManager.Domain;
using Xunit;

namespace VostokModManager.Tests;

public class SupportPackageSavesTests
{
    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "vmm_saves_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void ListSaves_lists_root_saves_and_slot_folder_without_logs()
    {
        var user = Temp();
        try
        {
            File.WriteAllText(Path.Combine(user, "Character.tres"), "c");
            File.WriteAllText(Path.Combine(user, "godot.log"), "x");
            var slot1 = Path.Combine(user, SaveSlots.FolderName, "slot1");
            Directory.CreateDirectory(slot1);
            File.WriteAllText(Path.Combine(slot1, "Character.tres"), "cc");
            var backup = Path.Combine(user, SaveSlots.FolderName, "_backups", "20261101-220000-01_slot1");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "World.tres"), "w");
            File.WriteAllText(Path.Combine(backup, "_backup.cfg"), "[backup]");

            var text = SupportPackage.ListSaves(user, out var count);

            Assert.Equal(4, count);
            Assert.Contains("Character.tres", text);
            Assert.Contains("MultiSaveSlots/slot1/Character.tres", text);
            Assert.Contains("MultiSaveSlots/_backups/20261101-220000-01_slot1/World.tres", text);
            Assert.DoesNotContain("godot.log", text);
        }
        finally { Directory.Delete(user, true); }
    }

    [Fact]
    public void SaveStateFiles_returns_only_state_meta_and_backup_notes()
    {
        var user = Temp();
        try
        {
            var slots = Path.Combine(user, SaveSlots.FolderName);
            var slot2 = Path.Combine(slots, "slot2");
            var backup = Path.Combine(slots, "_backups", "20261101-220000-01_user");
            Directory.CreateDirectory(slot2);
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(slots, "state.cfg"), "[state]");
            File.WriteAllText(Path.Combine(slot2, "_meta.cfg"), "[meta]");
            File.WriteAllText(Path.Combine(slot2, "Character.tres"), "c");
            File.WriteAllText(Path.Combine(backup, "_backup.cfg"), "[backup]");
            File.WriteAllText(Path.Combine(backup, "Character.tres"), "c");

            var files = SupportPackage.SaveStateFiles(user).Select(Path.GetFileName).ToList();

            Assert.Equal(new[] { "state.cfg", "_meta.cfg", "_backup.cfg" }, files);
        }
        finally { Directory.Delete(user, true); }
    }

    [Fact]
    public void ListSaves_without_user_folder_says_so()
    {
        var text = SupportPackage.ListSaves(Path.Combine(Path.GetTempPath(), "vmm_none_" + Guid.NewGuid().ToString("N")), out var count);
        Assert.Equal(0, count);
        Assert.Contains("not found", text);
    }
}
