// Scans the game's mods folder and builds a list of installed ModEntry.
// Recognizes:
//   <mods>/*.vmz                    → enabled archive mod
//   <mods>/<DirName>/mod.txt        → enabled directory mod
//   <mods>/Disabled/*.vmz           → disabled archive mod
//   <mods>/Disabled/<DirName>/...   → disabled directory mod
// Other folders without a mod.txt (e.g. config-only folders like
// MoreJobs/) are silently ignored.

namespace VostokModManager.Domain;

public class ModRegistry
{
    public string ModsDir { get; private set; } = "";
    public List<ModEntry> Entries { get; } = new();

    /// <summary>Scans `modsDir` and `modsDir/Disabled/`. Returns true if
    /// the top-level folder existed and was readable.</summary>
    public bool Scan(string modsDir)
    {
        ModsDir = modsDir;
        Entries.Clear();
        if (!Directory.Exists(modsDir)) return false;
        ScanDir(modsDir, isEnabled: true);
        var disabled = Path.Combine(modsDir, "Disabled");
        if (Directory.Exists(disabled))
            ScanDir(disabled, isEnabled: false);
        return true;
    }

    public ModEntry? FindById(string modId)
        => Entries.FirstOrDefault(e => e.ModId == modId);

    public List<ModEntry> Enabled()
        => Entries.Where(e => e.IsEnabled).ToList();

    private void ScanDir(string dirPath, bool isEnabled)
    {
        DirectoryInfo dir;
        try { dir = new DirectoryInfo(dirPath); }
        catch { return; }

        // .vmz files at this level.
        IEnumerable<FileInfo> archives;
        try { archives = dir.EnumerateFiles("*.vmz"); }
        catch { return; }
        foreach (var file in archives)
        {
            var entry = LoadArchiveEntry(file.FullName, isEnabled);
            if (entry != null) Entries.Add(entry);
        }

        // Subdirectories that contain a mod.txt.
        IEnumerable<DirectoryInfo> subdirs;
        try { subdirs = dir.EnumerateDirectories(); }
        catch { return; }
        foreach (var sub in subdirs)
        {
            if (sub.Name.StartsWith(".")) continue;
            // Skip Disabled when scanning the top-level — caller handles
            // it as a separate pass with isEnabled=false.
            if (isEnabled && sub.Name == "Disabled") continue;
            var manifestPath = Path.Combine(sub.FullName, "mod.txt");
            if (!File.Exists(manifestPath)) continue;
            var entry = LoadDirEntry(sub.FullName, isEnabled);
            if (entry != null) Entries.Add(entry);
        }
    }

    private static ModEntry? LoadArchiveEntry(string path, bool isEnabled)
    {
        using var arch = new ModArchive();
        if (!arch.Open(path)) return null;
        return new ModEntry
        {
            Path = path,
            IsArchive = true,
            IsEnabled = isEnabled,
            Manifest = arch.GetManifest(),
            Files = arch.FileList().ToList(),
        };
    }

    private static ModEntry? LoadDirEntry(string path, bool isEnabled)
    {
        var manifestPath = Path.Combine(path, "mod.txt");
        string text;
        try { text = File.ReadAllText(manifestPath); }
        catch { return null; }

        var entry = new ModEntry
        {
            Path = path,
            IsArchive = false,
            IsEnabled = isEnabled,
            Manifest = ModArchive.ParseConfigFile(text),
            Files = new List<string>(),
        };
        ListFilesRecursive(path, "", entry.Files);
        return entry;
    }

    private static void ListFilesRecursive(string root, string rel, List<string> output)
    {
        var fullDir = string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel);
        IEnumerable<string> files;
        IEnumerable<string> subdirs;
        try
        {
            files = Directory.EnumerateFiles(fullDir);
            subdirs = Directory.EnumerateDirectories(fullDir);
        }
        catch
        {
            return;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(".")) continue;
            var relChild = string.IsNullOrEmpty(rel) ? name : $"{rel}/{name}";
            output.Add(relChild);
        }
        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith(".")) continue;
            var relChild = string.IsNullOrEmpty(rel) ? name : $"{rel}/{name}";
            ListFilesRecursive(root, relChild, output);
        }
    }
}
