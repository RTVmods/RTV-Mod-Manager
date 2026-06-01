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

    /// <summary>Scans `modsDir` and `modsDir/Disabled/`. Returns true
    /// if the top-level folder existed and was readable.
    /// When `cfg` is provided, each entry's IsEnabled and Priority
    /// are overridden from mod_config.cfg's active-profile sections —
    /// the in-game loader uses cfg as its source of truth, so a
    /// mod showing "enabled" in our grid only matches reality when
    /// we read cfg too. Files in `mods/Disabled/` stay forced-off
    /// regardless of cfg (the loader can't see them anyway).</summary>
    public bool Scan(string modsDir, ModConfig? cfg = null)
    {
        ModsDir = modsDir;
        Entries.Clear();
        if (!Directory.Exists(modsDir)) return false;
        ScanDir(modsDir, locationEnabled: true);
        var disabled = Path.Combine(modsDir, "Disabled");
        if (Directory.Exists(disabled))
            ScanDir(disabled, locationEnabled: false);
        ApplyCfg(cfg);
        return true;
    }

    /// <summary>Overlays mod_config.cfg's active-profile state onto
    /// the freshly-scanned entries: cfg-driven IsEnabled, cfg-driven
    /// Priority. Files in `mods/Disabled/` keep IsEnabled = false
    /// regardless — the loader doesn't see them, so their cfg state
    /// (if any) is irrelevant.</summary>
    private void ApplyCfg(ModConfig? cfg)
    {
        foreach (var e in Entries)
        {
            // Default Priority = DeclaredPriority (mod.txt) before
            // cfg overlay; cfg.Priority falls back to DeclaredPriority
            // when the cfg has no entry for this mod.
            e.Priority = cfg != null
                ? cfg.Priority(e.ModId, e.Version, e.DeclaredPriority)
                : e.DeclaredPriority;

            var inDisabled = IsUnderDisabled(e.Path);
            if (inDisabled)
            {
                e.IsEnabled = false;
            }
            else if (cfg != null)
            {
                // fallback=true so a freshly-installed mod the user
                // hasn't toggled yet defaults to enabled (matches
                // the in-game loader — newly-discovered mods are on
                // until explicitly disabled).
                e.IsEnabled = cfg.IsEnabled(e.ModId, e.Version, fallback: true);
            }
            // No cfg given: leave IsEnabled at the location-based
            // value the scan code already set (true for mods/, false
            // for mods/Disabled/).
        }
    }

    private bool IsUnderDisabled(string entryPath)
    {
        var disabled = Path.Combine(ModsDir, "Disabled");
        var entryFull = Path.GetFullPath(entryPath);
        var disabledFull = Path.GetFullPath(disabled);
        return entryFull.StartsWith(
            disabledFull + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public ModEntry? FindById(string modId)
        => Entries.FirstOrDefault(e => e.ModId == modId);

    /// <summary>Compares two version strings component by component,
    /// parsing each component as int when both are numeric (so "1.14"
    /// &gt; "1.13"), falling back to ordinal string compare otherwise.
    /// Missing components are treated as 0 ("1.2" == "1.2.0").
    /// Strips leading 'v'/'V' prefixes before parsing.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        a = (a ?? "").Trim().TrimStart('v', 'V');
        b = (b ?? "").Trim().TrimStart('v', 'V');
        if (a == b) return 0;
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var n = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < n; i++)
        {
            var ap = i < aParts.Length ? aParts[i] : "0";
            var bp = i < bParts.Length ? bParts[i] : "0";
            var aIsInt = int.TryParse(ap, out var ai);
            var bIsInt = int.TryParse(bp, out var bi);
            if (aIsInt && bIsInt)
            {
                var cmp = ai.CompareTo(bi);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.CompareOrdinal(ap, bp);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }

    public List<ModEntry> Enabled()
        => Entries.Where(e => e.IsEnabled).ToList();

    private void ScanDir(string dirPath, bool locationEnabled)
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
            var entry = LoadArchiveEntry(file.FullName, locationEnabled);
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
            // it as a separate pass with locationEnabled=false.
            // Skip Backups, Library, and Profiles unconditionally
            // (case-insensitive) — these are manager-owned subfolders:
            //   Backups/   Domain/ModBackup.cs   — per-mod rollback snapshots
            //   Library/   Domain/ModLibrary.cs  — canonical .vmz store backing the profile model
            //   Profiles/                        — per-profile metadata (profile.json)
            // None of them contain LIVE mods; treating their files as
            // live would feed phantom entries into the grid + the
            // in-game loader.
            if (locationEnabled
                && string.Equals(sub.Name, "Disabled",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(sub.Name, "Backups",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(sub.Name, "Library",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(sub.Name, "Profiles",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var manifestPath = Path.Combine(sub.FullName, "mod.txt");
            if (!File.Exists(manifestPath)) continue;
            var entry = LoadDirEntry(sub.FullName, locationEnabled);
            if (entry != null) Entries.Add(entry);
        }
    }

    private static ModEntry? LoadArchiveEntry(string path, bool locationEnabled)
    {
        using var arch = new ModArchive();
        if (!arch.Open(path)) return null;
        return new ModEntry
        {
            Path = path,
            IsArchive = true,
            IsEnabled = locationEnabled,
            Manifest = arch.GetManifest(),
            Files = arch.FileList().ToList(),
        };
    }

    private static ModEntry? LoadDirEntry(string path, bool locationEnabled)
    {
        var manifestPath = Path.Combine(path, "mod.txt");
        string text;
        try { text = File.ReadAllText(manifestPath); }
        catch { return null; }

        var entry = new ModEntry
        {
            Path = path,
            IsArchive = false,
            IsEnabled = locationEnabled,
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
