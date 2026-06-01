// Lightweight "add these mods to my active profile" import format.
//
// Distinct from ModProfile JSON:
//   • ModProfile JSON = a full profile snapshot — replaces / activates
//     a whole loadout.
//   • ModListImport JSON = a batch add — merges entries into the
//     CURRENT active profile, downloading anything not already on
//     disk via the listed ModWorkshop ids.
//
// On-disk shape (the field names are JSON-snake-case to mirror the
// rest of the JSON we accept; .NET maps via JsonPropertyName):
//
//   {
//     "name":        "optional batch label",
//     "description": "optional note",
//     "mods": [
//       {
//         "mod_id":          "weapon-rig-api",
//         "display_name":    "Weapon Rig API",
//         "mod_workshop_id": 56123,
//         "version":         "1.2.0",        // informational
//         "is_enabled":      true,           // default true
//         "priority":        0,              // default 0
//         "dependencies":  [
//           { "mod_id": "weapon-rigger", "mod_workshop_id": 56124 }
//         ]
//       }
//     ]
//   }
//
// Either mod_id OR mod_workshop_id MUST be present per entry — without
// at least one we can't dedupe or download. `dependencies` may be
// nested arbitrarily deep; Flatten() walks the tree and returns a
// deduped flat list keyed first by mod_id (case-insensitive), then
// by mod_workshop_id when an entry has no id.
//
// Multiple deps per mod — fully supported via the array form:
//
//   {
//     "mods": [
//       {
//         "mod_id": "mega-mod",
//         "mod_workshop_id": 56999,
//         "dependencies": [
//           { "mod_id": "core-lib",   "mod_workshop_id": 56100 },
//           { "mod_id": "ui-toolkit", "mod_workshop_id": 56101 },
//           { "mod_id": "audio-pack", "mod_workshop_id": 56102 }
//         ]
//       }
//     ]
//   }
//
// Diamond inheritance (A→C, B→C) — Flatten() dedupes C by mod_id so
// it lands in the output once, regardless of how many parents listed
// it. ParentChain prefers the SHORTEST path (so a mod ALSO listed at
// top-level wins over only being reached through a chain of parents).
//
// Cycles (A→B→A) — Flatten() tracks the recursion path and refuses
// to re-enter a key already on the stack. The cyclic node is still
// recorded once; its inner walk just doesn't loop.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VostokModManager.Domain;

public class ImportEntry
{
    [JsonPropertyName("mod_id")]
    public string ModId { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("mod_workshop_id")]
    public int ModWorkshopId { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("is_enabled")]
    public bool? IsEnabled { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    /// <summary>Required (false, default) vs optional ("nice to have",
    /// true). The pack author marks mods optional in the Mod Packager
    /// → on import, optional rows default to UNticked and are visually
    /// distinct from required ones. Missing on the wire is treated as
    /// required so older pack JSONs (pre-0.5.66) keep importing the
    /// same way they always did.</summary>
    [JsonPropertyName("is_optional")]
    public bool? IsOptional { get; set; }

    [JsonPropertyName("dependencies")]
    public List<ImportEntry> Dependencies { get; set; } = new();
}

/// <summary>One row produced by ModListImport.Flatten — the dedup
/// payload plus the lineage that brought it in. ParentChain is
/// empty for top-level entries; otherwise it lists ancestor display
/// labels in order (eldest → nearest parent).</summary>
public class FlattenedEntry
{
    public ImportEntry Entry       { get; set; } = new();
    public List<string> ParentChain { get; set; } = new();
    public bool IsTopLevel => ParentChain.Count == 0;
}

public class ModListImport
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("mods")]
    public List<ImportEntry> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling          = JsonCommentHandling.Skip,
        AllowTrailingCommas          = true,
        DefaultIgnoreCondition       = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses a .json file as a ModListImport. Throws
    /// JsonException / IOException on parse / IO failures — caller
    /// wraps for user-facing reporting.</summary>
    public static ModListImport LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<ModListImport>(json, _opts)
                     ?? throw new InvalidDataException(
                         "JSON parsed to null — file is probably empty or "
                         + "malformed.");
        return parsed;
    }

    /// <summary>Walks the tree (mods[] and each entry's nested
    /// dependencies[]) producing a deduped flat list with lineage.
    ///
    /// Dedup key: mod_id case-insensitive when present, else
    /// `mw#<id>`. Entries with NEITHER a mod_id NOR a mod_workshop_id
    /// are silently dropped — there's no way to download or dedupe
    /// them.
    ///
    /// Lineage: each FlattenedEntry carries ParentChain — the display
    /// labels of every ancestor that brought it in, eldest first.
    /// Top-level entries have an empty chain. When the same key
    /// shows up via multiple paths we prefer the SHORTEST chain (so
    /// "mod also listed at top-level" beats "mod only reached as a
    /// dep-of-dep").
    ///
    /// Cycle safety: a recursion-path set tracks every key currently
    /// on the call stack; if we re-enter the same key we abort the
    /// inner walk. Prevents A→B→A from infinite-looping while still
    /// letting unrelated subtrees be walked fully.
    ///
    /// Field merging: the FIRST occurrence's fields are kept; later
    /// occurrences fill in missing values (so a parent that names a
    /// child by mod_id only, plus a sibling re-listing the same
    /// mod_id with a mod_workshop_id, still gets the MW id).</summary>
    public List<FlattenedEntry> Flatten()
    {
        var byKey   = new Dictionary<string, FlattenedEntry>(
            StringComparer.OrdinalIgnoreCase);
        var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineage = new List<string>();

        static string KeyFor(ImportEntry e)
            => !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : (e.ModWorkshopId > 0 ? $"mw#{e.ModWorkshopId}" : "");

        static string LabelFor(ImportEntry e)
            => !string.IsNullOrEmpty(e.DisplayName)
                ? e.DisplayName
                : (!string.IsNullOrEmpty(e.ModId)
                    ? e.ModId
                    : (e.ModWorkshopId > 0
                        ? $"MW {e.ModWorkshopId}"
                        : "(unknown)"));

        void Walk(ImportEntry e)
        {
            var key = KeyFor(e);
            // No-key entries can still wrap children with valid keys.
            // Recurse but skip the dedup/payload step.
            if (string.IsNullOrEmpty(key))
            {
                foreach (var d in e.Dependencies ?? new()) Walk(d);
                return;
            }

            // Cycle check — refuse to re-enter a key already on the
            // recursion path. We don't `return` outright though: we
            // still want to dedup-record THIS occurrence (it might
            // have fresher fields). Just skip the recurse step.
            var onCycle = !pathSet.Add(key);

            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = new FlattenedEntry
                {
                    Entry = new ImportEntry
                    {
                        ModId         = e.ModId,
                        DisplayName   = e.DisplayName,
                        ModWorkshopId = e.ModWorkshopId,
                        Version       = e.Version,
                        IsEnabled     = e.IsEnabled,
                        Priority      = e.Priority,
                        IsOptional    = e.IsOptional,
                    },
                    ParentChain = new List<string>(lineage),
                };
            }
            else
            {
                var first = existing.Entry;
                // Backfill missing fields without overwriting.
                if (string.IsNullOrEmpty(first.DisplayName)
                    && !string.IsNullOrEmpty(e.DisplayName))
                    first.DisplayName = e.DisplayName;
                if (first.ModWorkshopId == 0 && e.ModWorkshopId > 0)
                    first.ModWorkshopId = e.ModWorkshopId;
                if (string.IsNullOrEmpty(first.Version)
                    && !string.IsNullOrEmpty(e.Version))
                    first.Version = e.Version;
                if (first.IsEnabled == null && e.IsEnabled != null)
                    first.IsEnabled = e.IsEnabled;
                if (first.Priority == null && e.Priority != null)
                    first.Priority = e.Priority;
                // Required wins on conflict — if any path lists this
                // mod as required (or implicitly via missing flag),
                // the merged entry is required. An entry that's
                // explicitly required (false) downgrades a later
                // optional (true) sighting.
                if (e.IsOptional == false)
                    first.IsOptional = false;
                else if (first.IsOptional == null && e.IsOptional != null)
                    first.IsOptional = e.IsOptional;
                // Prefer the shorter (closer-to-top-level) lineage so
                // a mod listed both at top-level AND under another's
                // deps shows as "top-level", not "dep of …".
                if (lineage.Count < existing.ParentChain.Count)
                    existing.ParentChain = new List<string>(lineage);
            }

            if (onCycle)
            {
                // Already on stack — recursing into our own deps
                // again would loop. The outer walks will cover any
                // siblings this entry has.
                return;
            }

            lineage.Add(LabelFor(e));
            foreach (var d in e.Dependencies ?? new()) Walk(d);
            lineage.RemoveAt(lineage.Count - 1);
            pathSet.Remove(key);
        }

        foreach (var top in Mods) Walk(top);
        return byKey.Values.ToList();
    }
}
