// Mods that ship inside the manager and are installed and updated by it.
//
// Save Slots is the in-game half of the Saves feature: the slot panel in the
// main menu, the save sync and the restart into a slot's profile run inside
// the game, where only the mod loader can put code, so it has to be a .vmz in
// the mods folder. It is not a mod the player picks: the manager writes it
// into the mods folder and the Library from the copy embedded in its own exe,
// keeps it on in every profile, never lists it in a profile, and leaves it out
// of exports and modpacks (another manager brings its own copy).
//
// The embedded archive comes from SaveSlots/_pack.py, which copies the packed
// .vmz to Resources/save-slots.vmz.

using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class BuiltInMods
{
    public const string SaveSlotsId = "save-slots";
    private const string ResourceName = "VostokModManager.BuiltIn.save-slots.vmz";

    private static byte[]? _bytes;
    private static string? _version;

    /// <summary>True for a mod id the manager owns.</summary>
    public static bool IsBuiltIn(string? modId)
        => !string.IsNullOrEmpty(modId)
            && string.Equals(modId, SaveSlotsId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The embedded Save Slots archive, or null when this build
    /// carries none.</summary>
    public static byte[]? SaveSlotsArchive
    {
        get
        {
            if (_bytes != null) return _bytes;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return _bytes = ms.ToArray();
        }
    }

    /// <summary>The version in the embedded archive's mod.txt, "" when
    /// there is no archive.</summary>
    public static string SaveSlotsVersion
    {
        get
        {
            if (_version != null) return _version;
            var bytes = SaveSlotsArchive;
            if (bytes == null) return _version = "";
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry("mod.txt");
            if (entry == null) return _version = "";
            using var r = new StreamReader(entry.Open());
            var m = Regex.Match(r.ReadToEnd(), "^\\s*version\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);
            return _version = m.Success ? m.Groups[1].Value : "";
        }
    }

    /// <summary>File name of the built-in archive, as the Library names it.</summary>
    public static string SaveSlotsFileName(string modsDir)
        => Path.GetFileName(ModLibrary.PathFor(modsDir, SaveSlotsId, SaveSlotsVersion));

    /// <summary>Writes the embedded Save Slots into the Library and the
    /// mods folder when either copy is missing or differs, and removes
    /// other versions of it from both. Returns true when anything on disk
    /// changed. Throws on IO errors.</summary>
    public static bool InstallSaveSlots(string modsDir)
    {
        var bytes = SaveSlotsArchive;
        if (bytes == null || !Directory.Exists(modsDir)) return false;
        var name = SaveSlotsFileName(modsDir);
        var lib = ModLibrary.LibraryDir(modsDir);
        Directory.CreateDirectory(lib);
        var changed = false;
        changed |= WriteIfDifferent(Path.Combine(lib, name), bytes);
        changed |= WriteIfDifferent(Path.Combine(modsDir, name), bytes);
        foreach (var dir in new[] { modsDir, lib })
        {
            foreach (var f in Directory.GetFiles(dir, SaveSlotsId + "__v*.vmz"))
            {
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(f);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Removes every Save Slots archive from the mods folder and
    /// the Library. Returns true when a file was removed.</summary>
    public static bool RemoveSaveSlots(string modsDir)
    {
        var changed = false;
        foreach (var dir in new[] { modsDir, ModLibrary.LibraryDir(modsDir) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, SaveSlotsId + "__v*.vmz"))
            {
                File.Delete(f);
                changed = true;
            }
        }
        return changed;
    }

    private static bool WriteIfDifferent(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                return false;
        }
        File.WriteAllBytes(path, bytes);
        return true;
    }
}
