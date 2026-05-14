// Mod-loadout profile: a named snapshot of which mods were installed,
// at what versions, with what enabled/priority state — AND copies of
// the actual .vmz archives so the exact version can be restored later
// even after the user updates the live mods folder.
//
// On-disk layout under %APPDATA%\VostokModManager\profiles\:
//
//   <profile-name>/
//     profile.json          ← metadata
//     mods/
//       <orig-filename>.vmz ← byte-for-byte copies of every bundled mod
//
// Export wraps the whole folder into a single .vmprofile zip the user
// can hand around. Import accepts either a .vmprofile zip, a loose
// .json file (legacy / metadata-only), or a folder layout above.
//
// When a profile is applied, mods with a bundled archive are restored
// from the bundle (deterministic — exact version pinning). Mods without
// a bundle (legacy JSON profiles, or save-failed entries) fall back to
// downloading the latest from ModWorkshop.

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

/// <summary>One mod entry stored inside a ModProfile. Only the fields
/// that identify the mod and specify the desired state are persisted —
/// full manifest data comes from the bundled .vmz, not the profile JSON.
/// `ArchiveFileName` points at the bundled .vmz inside the profile's
/// mods/ subfolder; empty when the mod is metadata-only (legacy profile
/// or save-time copy failure).</summary>
public class ProfileMod
{
    public string ModId       { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version     { get; set; } = "";
    public bool   IsEnabled   { get; set; }
    public int    Priority    { get; set; }
    /// <summary>ModWorkshop numeric ID (0 = unknown). Used as the
    /// fallback download source when the profile has no bundled
    /// archive for this mod.</summary>
    public int    ModWorkshopId { get; set; }
    /// <summary>Filename (no path) of the bundled .vmz inside this
    /// profile's `mods/` subfolder. Empty for metadata-only entries —
    /// directory mods, save-failures, or legacy JSON-only profiles.</summary>
    public string ArchiveFileName { get; set; } = "";
}

public class ModProfile
{
    public string  Name        { get; set; } = "";
    public string  Description { get; set; } = "";
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public List<ProfileMod> Mods { get; set; } = new();

    /// <summary>Path to the folder this profile was loaded from
    /// (or where it lives once saved). Populated by Save/LoadAll/
    /// LoadFromFolder; empty for a freshly-built profile not yet
    /// persisted. Not serialised — it's a runtime breadcrumb so the
    /// apply dialog can resolve ArchiveFileName → absolute path
    /// without needing to know about the profiles directory.</summary>
    [JsonIgnore]
    public string FolderPath { get; set; } = "";

    // ── Persistence ──────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Root folder for all profile subfolders.</summary>
    public static string ProfilesDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VostokModManager", "profiles");

    /// <summary>Sanitises a profile name into a safe filesystem
    /// folder/filename (≤60 chars, ASCII-friendly). The .json or
    /// .vmprofile extension is NOT appended here.</summary>
    public static string SafeFileName(string profileName)
    {
        var s = Regex.Replace(profileName.Trim(), @"[^\w\s\-\.]", "_");
        s = Regex.Replace(s, @"\s+", " ").Trim('_', ' ');
        if (s.Length > 60) s = s[..60];
        return string.IsNullOrEmpty(s) ? "profile" : s;
    }

    /// <summary>The on-disk subfolder for this profile inside
    /// ProfilesDir, derived from the sanitised name.</summary>
    public string DefaultFolderPath =>
        System.IO.Path.Combine(ProfilesDir, SafeFileName(Name));

    /// <summary>The absolute path to the bundled .vmz for a mod, or
    /// empty when no archive is bundled / the file is missing on
    /// disk. FolderPath must be populated (true after Save or any
    /// of the Load* paths).</summary>
    public string ResolveBundledArchive(ProfileMod m)
    {
        if (string.IsNullOrEmpty(FolderPath) || string.IsNullOrEmpty(m.ArchiveFileName))
            return "";
        var path = System.IO.Path.Combine(FolderPath, "mods", m.ArchiveFileName);
        return File.Exists(path) ? path : "";
    }

    /// <summary>Total size in bytes of all bundled .vmz archives.
    /// Used by the manager dialog to show "profile is 12 MB".</summary>
    public long BundledArchivesSize()
    {
        if (string.IsNullOrEmpty(FolderPath)) return 0;
        long total = 0;
        foreach (var m in Mods)
        {
            var p = ResolveBundledArchive(m);
            if (!string.IsNullOrEmpty(p))
            {
                try { total += new FileInfo(p).Length; }
                catch { /* skip unreadable */ }
            }
        }
        return total;
    }

    /// <summary>Count of mods with a bundled archive on disk.</summary>
    public int BundledArchivesCount()
        => Mods.Count(m => !string.IsNullOrEmpty(ResolveBundledArchive(m)));

    /// <summary>Loads all profiles from ProfilesDir. Supports both
    /// the new folder-with-bundle layout and the legacy top-level
    /// JSON file layout (auto-migrated by re-saving on first edit).</summary>
    public static List<ModProfile> LoadAll()
    {
        var dir = ProfilesDir;
        if (!Directory.Exists(dir)) return new();
        var result = new List<ModProfile>();

        // New folder-based profiles
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var p = LoadFromFolder(sub);
            if (p != null) result.Add(p);
        }

        // Legacy single-JSON profiles at the top level
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var txt = File.ReadAllText(f);
                var p = JsonSerializer.Deserialize<ModProfile>(txt, _opts);
                if (p != null)
                {
                    // No FolderPath — bundles aren't available, the
                    // apply dialog will fall back to MW downloads.
                    result.Add(p);
                }
            }
            catch { /* skip corrupted */ }
        }

        return result
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Loads a profile from a folder containing profile.json
    /// (+ optional mods/ subfolder). Returns null if profile.json is
    /// missing or unparseable.</summary>
    public static ModProfile? LoadFromFolder(string folderPath)
    {
        var jsonPath = System.IO.Path.Combine(folderPath, "profile.json");
        if (!File.Exists(jsonPath)) return null;
        try
        {
            var txt = File.ReadAllText(jsonPath);
            var p = JsonSerializer.Deserialize<ModProfile>(txt, _opts);
            if (p == null) return null;
            p.FolderPath = folderPath;
            return p;
        }
        catch { return null; }
    }

    /// <summary>Loads a single profile from an arbitrary .json file
    /// (legacy / metadata-only). Returns null on parse failure.</summary>
    public static ModProfile? LoadFromJsonFile(string path)
    {
        try
        {
            var txt = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModProfile>(txt, _opts);
        }
        catch { return null; }
    }

    /// <summary>Loads a profile from a .vmprofile zip. Extracts it
    /// into ProfilesDir/(sanitised name)/ and returns the loaded
    /// profile. Existing folder with the same name is overwritten.</summary>
    public static ModProfile? LoadFromZip(string zipPath)
    {
        // Peek at profile.json first so we know the target folder name.
        ModProfile? meta;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var jsonEntry = zip.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, "profile.json",
                    StringComparison.OrdinalIgnoreCase));
            if (jsonEntry == null) return null;
            using var s = jsonEntry.Open();
            using var r = new StreamReader(s);
            meta = JsonSerializer.Deserialize<ModProfile>(r.ReadToEnd(), _opts);
        }
        catch { return null; }
        if (meta == null) return null;

        var target = System.IO.Path.Combine(ProfilesDir, SafeFileName(meta.Name));
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(zipPath, target);
        }
        catch { return null; }
        meta.FolderPath = target;
        return meta;
    }

    /// <summary>Writes the profile (with bundled archives) to its
    /// canonical folder under ProfilesDir. Copies each profile mod's
    /// .vmz into the profile's mods/ subfolder. Returns the list of
    /// mods whose bundle save failed (rare — usually means the mod's
    /// file was locked or it was a directory mod with no archive).</summary>
    public List<ProfileMod> SaveWithBundles(IEnumerable<ModEntry> liveEntries)
    {
        var failed = new List<ProfileMod>();
        var folder = DefaultFolderPath;
        var modsDir = System.IO.Path.Combine(folder, "mods");
        Directory.CreateDirectory(modsDir);

        // Wipe any leftover archives in the mods/ folder — bundles
        // are recomputed from scratch each save so a deleted mod
        // doesn't linger.
        try
        {
            foreach (var old in Directory.GetFiles(modsDir, "*.vmz"))
                File.Delete(old);
        }
        catch { /* best-effort cleanup */ }

        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in liveEntries)
            if (!string.IsNullOrEmpty(e.ModId)) byId[e.ModId] = e;

        // Track filename collisions across multiple profile mods so
        // each ends up with a unique bundled filename.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Mods)
        {
            m.ArchiveFileName = "";
            if (!byId.TryGetValue(m.ModId, out var entry)) { failed.Add(m); continue; }
            if (!entry.IsArchive || !File.Exists(entry.Path))
            {
                // Directory mod, or .vmz file vanished — can't bundle.
                failed.Add(m);
                continue;
            }
            try
            {
                var origName = System.IO.Path.GetFileName(entry.Path);
                var unique = MakeUniqueName(origName, used);
                used.Add(unique);
                File.Copy(entry.Path, System.IO.Path.Combine(modsDir, unique), overwrite: true);
                m.ArchiveFileName = unique;
            }
            catch
            {
                failed.Add(m);
            }
        }

        FolderPath = folder;
        File.WriteAllText(
            System.IO.Path.Combine(folder, "profile.json"),
            JsonSerializer.Serialize(this, _opts));
        return failed;
    }

    private static string MakeUniqueName(string name, HashSet<string> used)
    {
        if (!used.Contains(name)) return name;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}{ext}";
            if (!used.Contains(candidate)) return candidate;
        }
        // Last resort — Guid for uniqueness.
        return $"{baseName}_{Guid.NewGuid():N}{ext}";
    }

    /// <summary>Persists metadata only (no archive copy). Used by
    /// rename / description edits when bundles already exist and
    /// shouldn't be touched.</summary>
    public void SaveMetadataOnly()
    {
        var folder = string.IsNullOrEmpty(FolderPath) ? DefaultFolderPath : FolderPath;
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            System.IO.Path.Combine(folder, "profile.json"),
            JsonSerializer.Serialize(this, _opts));
        FolderPath = folder;
    }

    /// <summary>Zips this profile's folder (profile.json + mods/) into
    /// a single .vmprofile file at `path`. Used by Export-to-file.</summary>
    public void ExportToZip(string path)
    {
        if (string.IsNullOrEmpty(FolderPath) || !Directory.Exists(FolderPath))
            throw new InvalidOperationException(
                "Profile has no on-disk folder to export. Save it first.");
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);
        ZipFile.CreateFromDirectory(FolderPath, path, CompressionLevel.Optimal, false);
    }

    /// <summary>Deletes the entire profile folder (including bundled
    /// archives). Safe to call when the folder doesn't exist.</summary>
    public void Delete()
    {
        var folder = string.IsNullOrEmpty(FolderPath) ? DefaultFolderPath : FolderPath;
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
        // Also clean up a legacy <name>.json sitting at the top level.
        var legacy = System.IO.Path.Combine(ProfilesDir, SafeFileName(Name) + ".json");
        if (File.Exists(legacy))
        {
            try { File.Delete(legacy); }
            catch { /* best-effort */ }
        }
    }

    // ── Factory ───────────────────────────────────────────────────────

    /// <summary>Builds a profile from the currently-installed and
    /// -scanned mods. Entries without a mod_id are skipped because
    /// there's nothing to match against on import.
    /// NOTE: This builds the metadata only — callers must invoke
    /// `SaveWithBundles` afterwards to actually copy the .vmz files.</summary>
    public static ModProfile FromRegistry(
        string name,
        string description,
        IEnumerable<ModEntry> entries)
    {
        return new ModProfile
        {
            Name        = name,
            Description = description,
            CreatedAt   = DateTime.UtcNow,
            Mods        = entries
                .Where(e => !string.IsNullOrEmpty(e.ModId))
                .Select(e => new ProfileMod
                {
                    ModId         = e.ModId,
                    DisplayName   = string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName,
                    Version       = e.Version,
                    IsEnabled     = e.IsEnabled,
                    Priority      = e.Priority,
                    ModWorkshopId = e.ModWorkshopId,
                })
                .ToList(),
        };
    }
}
