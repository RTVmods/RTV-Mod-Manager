// Mod-loadout profile: a named snapshot of which mods are enabled,
// at what priority, and which version was installed when the profile
// was saved. Profiles are persisted as individual JSON files under
//   %APPDATA%\VostokModManager\profiles\<sanitised-name>.json
// so they can be shared (export the file, send it, import it).
//
// The import flow checks the receiving machine's installed mods and
// offers to download missing / outdated ones via ModWorkshop.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

/// <summary>One mod entry stored inside a ModProfile. Only the fields
/// that identify the mod and specify the desired state are persisted —
/// full manifest data comes from the installed .vmz, not the profile.
/// `DisplayName` is stored for human-readable diffs; it is NOT used as
/// a matching key (only ModId is).</summary>
public class ProfileMod
{
    public string ModId       { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version     { get; set; } = "";
    public bool   IsEnabled   { get; set; }
    public int    Priority    { get; set; }
    /// <summary>ModWorkshop numeric ID (0 = unknown). Used to
    /// auto-download a missing or outdated mod during import.</summary>
    public int    ModWorkshopId { get; set; }
}

public class ModProfile
{
    public string  Name        { get; set; } = "";
    public string  Description { get; set; } = "";
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public List<ProfileMod> Mods { get; set; } = new();

    // ── Persistence ──────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Folder where all profile JSON files live.</summary>
    public static string ProfilesDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VostokModManager", "profiles");

    /// <summary>Sanitises a profile name into a safe filesystem
    /// filename (no special chars, ≤60 chars). The .json extension
    /// is NOT appended here.</summary>
    public static string SafeFileName(string profileName)
    {
        var s = Regex.Replace(profileName.Trim(), @"[^\w\s\-\.]", "_");
        s = Regex.Replace(s, @"\s+", " ").Trim('_', ' ');
        if (s.Length > 60) s = s[..60];
        return string.IsNullOrEmpty(s) ? "profile" : s;
    }

    /// <summary>The on-disk path for this profile's JSON file.</summary>
    public string FilePath =>
        System.IO.Path.Combine(ProfilesDir, SafeFileName(Name) + ".json");

    /// <summary>Loads all profiles from ProfilesDir. Returns an empty
    /// list when the directory doesn't exist or is empty.</summary>
    public static List<ModProfile> LoadAll()
    {
        var dir = ProfilesDir;
        if (!Directory.Exists(dir)) return new();
        var result = new List<ModProfile>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var txt = File.ReadAllText(f);
                var p = JsonSerializer.Deserialize<ModProfile>(txt, _opts);
                if (p != null) result.Add(p);
            }
            catch { /* skip corrupted file */ }
        }
        return result
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Loads a single profile from an arbitrary file path.
    /// Returns null on parse failure.</summary>
    public static ModProfile? LoadFromFile(string path)
    {
        try
        {
            var txt = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModProfile>(txt, _opts);
        }
        catch { return null; }
    }

    /// <summary>Writes this profile to ProfilesDir using the sanitised
    /// name as the filename. Creates the directory if needed.</summary>
    public void Save()
    {
        Directory.CreateDirectory(ProfilesDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _opts));
    }

    /// <summary>Saves to an arbitrary path (for Export-to-file).</summary>
    public void SaveTo(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, _opts));
    }

    /// <summary>Deletes this profile's JSON file from disk. Safe to
    /// call when the file doesn't exist (no-op).</summary>
    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    // ── Factory ───────────────────────────────────────────────────────

    /// <summary>Builds a profile from the currently-installed and
    /// -scanned mods. Entries without a mod_id are skipped because
    /// there's nothing to match against on import.</summary>
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
