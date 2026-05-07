// One installed mod, either as a .vmz archive or an unpacked directory.
// Constructed by ModRegistry; consumed by ConflictDetector and the UI.
//
// Mirrors the GDScript ModEntry from v0.2-godot-standalone, ported to
// C# with property accessors instead of method calls (mod_id() →
// ModId, etc.) — closer to idiomatic C# while preserving the shape.

namespace VostokModManager.Domain;

public class ModEntry
{
    /// <summary>Absolute path to the .vmz file or the directory.</summary>
    public string Path { get; set; } = "";

    /// <summary>True if .vmz, false if a directory mod.</summary>
    public bool IsArchive { get; set; }

    /// <summary>False if located under &lt;mods&gt;/Disabled/.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Parsed mod.txt sections.
    ///   "mod" → {id, name, version, priority?, description?, ...}
    ///   "autoload" → {Name → res:// path}
    ///   "hooks" → {res:// target → method}
    ///   "script_extend" → {res:// target → res:// override}
    ///   "updates" → {modworkshop → int}</summary>
    public Dictionary<string, Dictionary<string, string>> Manifest { get; set; } = new();

    /// <summary>Archive-relative file paths (no leading slash).</summary>
    public List<string> Files { get; set; } = new();

    public string ModId => GetSection("mod", "id");
    public string DisplayName => GetSection("mod", "name");
    public string Version => GetSection("mod", "version");
    public int Priority
        => int.TryParse(GetSection("mod", "priority"), out var p) ? p : 0;
    public string Description => GetSection("mod", "description");

    public int ModWorkshopId
        => int.TryParse(GetSection("updates", "modworkshop"), out var i) ? i : 0;

    public Dictionary<string, string> Autoloads => GetSectionDict("autoload");
    public Dictionary<string, string> Hooks => GetSectionDict("hooks");
    public Dictionary<string, string> ScriptExtends => GetSectionDict("script_extend");

    /// <summary>Returns the text content of `filePath` inside this mod,
    /// or "" if absent. Re-opens the archive each call; small mods make
    /// this cheap, but tight loops should batch reads via a freshly
    /// opened ModArchive.</summary>
    public string ReadFileText(string filePath)
    {
        if (IsArchive)
        {
            using var arch = new ModArchive();
            if (!arch.Open(Path)) return "";
            return arch.ReadText(filePath);
        }
        var full = System.IO.Path.Combine(Path, filePath);
        if (!File.Exists(full)) return "";
        try
        {
            return File.ReadAllText(full);
        }
        catch
        {
            return "";
        }
    }

    private string GetSection(string section, string key)
    {
        if (Manifest.TryGetValue(section, out var sec)
            && sec.TryGetValue(key, out var v))
            return v ?? "";
        return "";
    }

    private Dictionary<string, string> GetSectionDict(string section)
    {
        return Manifest.TryGetValue(section, out var sec)
            ? sec
            : new Dictionary<string, string>();
    }
}
