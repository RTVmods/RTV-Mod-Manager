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
    public string Description => GetSection("mod", "description");

    /// <summary>The mod.txt-declared priority. Used as the fallback
    /// when mod_config.cfg has no override for this mod. Stays
    /// constant for the lifetime of the entry — the *effective*
    /// priority that consumers actually want is `Priority`.</summary>
    public int DeclaredPriority
        => int.TryParse(GetSection("mod", "priority"), out var p) ? p : 0;

    /// <summary>The effective load-order priority. Defaults to
    /// DeclaredPriority but is overwritten by ModRegistry.Scan when
    /// mod_config.cfg has an explicit value for this mod's
    /// `mod-id@version` key — which it usually does, because the
    /// in-game loader uses cfg as the source of truth.
    /// Settable so ModRegistry / cfg edits can update it without
    /// reaching into the manifest dictionary.</summary>
    public int Priority { get; set; }

    public int ModWorkshopId
        => int.TryParse(GetSection("updates", "modworkshop"), out var i) ? i : 0;

    public Dictionary<string, string> Autoloads => GetSectionDict("autoload");
    public Dictionary<string, string> Hooks => GetSectionDict("hooks");
    public Dictionary<string, string> ScriptExtends => GetSectionDict("script_extend");

    /// <summary>Mod IDs this mod declares as required dependencies in
    /// `[dependencies] required = ...`. Accepts either CSV form
    /// (a, b, c) or Godot-style array form (["a", "b", "c"]). Empty
    /// when the section is absent.</summary>
    public List<string> RequiredDependencies => ParseDepList("required");

    /// <summary>Mod IDs this mod declares as optional / soft
    /// dependencies in `[dependencies] optional = ...`. Same parsing
    /// rules as RequiredDependencies.</summary>
    public List<string> OptionalDependencies => ParseDepList("optional");

    private List<string> ParseDepList(string key)
        => ParseCsvOrArray(GetSection("dependencies", key));

    /// <summary>Optional sidecar mapping `dep_id → ModWorkshop
    /// numeric id`, written by the Mod Packager into a
    /// `[dependency_sources]` section in mod.txt. The in-game
    /// loader doesn't read this section (Godot's ConfigFile parser
    /// silently ignores unknown sections), but the manager uses it
    /// to auto-resolve missing dependencies to a downloadable URL
    /// without round-tripping to the user for input.
    ///
    /// Why this exists: the standard `[dependencies] required`
    /// list stores manifest mod_id SLUGS only — there's no public
    /// MW lookup for "find the mod whose manifest id is X", so
    /// downloading a missing dep blind isn't possible. By
    /// recording the (slug, mw_id) pair at pack time, the dep is
    /// resolvable end-to-end on first install.
    ///
    /// Format:
    ///   [dependency_sources]
    ///   mcm = 56781
    ///   weapon-rig-api = 56123
    ///
    /// Empty when the section is absent or every value parses as
    /// non-positive.</summary>
    public Dictionary<string, int> DependencySources
    {
        get
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in GetSectionDict("dependency_sources"))
            {
                if (int.TryParse(kvp.Value, out var n) && n > 0)
                    result[kvp.Key] = n;
            }
            return result;
        }
    }

    /// <summary>Parses a value like `a, b, c` or `["a", "b", "c"]`
    /// into a list of trimmed, dequoted, non-empty mod IDs. The
    /// surrounding-quote stripping in ModArchive's parser already
    /// handles `"a, b, c"` (whole value quoted) before we see it,
    /// so we only need to handle the array brackets here.</summary>
    internal static List<string> ParseCsvOrArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        raw = raw.Trim();
        if (raw.StartsWith("[") && raw.EndsWith("]") && raw.Length >= 2)
            raw = raw.Substring(1, raw.Length - 2);
        return raw.Split(',')
            .Select(s => s.Trim().Trim('"').Trim('\''))
            .Where(s => s.Length > 0)
            .ToList();
    }

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
