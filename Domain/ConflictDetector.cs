// Finds conflicts among enabled mods.
//
// Manifest-only signals (cheap; no archive content reads):
//   FileOverlap            same file path written by 2+ mods
//   AutoloadCollision      same autoload name registered by 2+ mods
//   HookCollision          same (script, method) hooked by 2+ mods
//   ScriptExtendCollision  same script extended by 2+ mods
//
// Script-level signals (reads .gd contents from archives):
//   ClassNameCollision     same class_name declared by 2+ mods
//   TakeOverCollision      same take_over_path target used by 2+ mods
//   SuperChainConstraint   not a hard conflict — an inferred load-order
//                          constraint: replacer (no super) must load
//                          before chainer (calls super)

namespace VostokModManager.Domain;

public static class ConflictDetector
{
    public const string TYPE_FILE_OVERLAP = "file_overlap";
    public const string TYPE_AUTOLOAD_COLLISION = "autoload_collision";
    public const string TYPE_HOOK_COLLISION = "hook_collision";
    public const string TYPE_SCRIPT_EXTEND_COLLISION = "script_extend_collision";
    public const string TYPE_CLASS_NAME_COLLISION = "class_name_collision";
    public const string TYPE_TAKE_OVER_COLLISION = "take_over_collision";
    public const string TYPE_SUPER_CHAIN_CONSTRAINT = "super_chain_constraint";

    public class Conflict
    {
        public string Type { get; set; } = "";
        public string Key { get; set; } = "";
        public List<string> ModIds { get; set; } = new();
        public Dictionary<string, object> Details { get; set; } = new();
    }

    /// <summary>Manifest + script passes combined.</summary>
    public static List<Conflict> DetectAll(IEnumerable<ModEntry> entries)
    {
        var result = new List<Conflict>();
        result.AddRange(DetectManifestConflicts(entries));
        result.AddRange(DetectScriptConflicts(entries));
        return result;
    }

    /// <summary>Cheap pass — no archive opens beyond what ModRegistry
    /// already did during scan().</summary>
    public static List<Conflict> DetectManifestConflicts(IEnumerable<ModEntry> entries)
    {
        var live = entries.Where(e => e.IsEnabled).ToList();
        var result = new List<Conflict>();
        result.AddRange(OverlapByFiles(live));
        result.AddRange(OverlapBySection(live, "autoload", TYPE_AUTOLOAD_COLLISION));
        result.AddRange(OverlapBySection(live, "hooks", TYPE_HOOK_COLLISION));
        result.AddRange(OverlapBySection(live, "script_extend", TYPE_SCRIPT_EXTEND_COLLISION));
        return result;
    }

    /// <summary>Deep pass — opens each enabled archive once and reads
    /// every .gd file. ~1 archive open + N file reads per mod. For 50
    /// mods with ~10 .gd files each, expect a few hundred ms.</summary>
    public static List<Conflict> DetectScriptConflicts(IEnumerable<ModEntry> entries)
    {
        var live = entries.Where(e => e.IsEnabled).ToList();
        var analyses = new Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>>();
        foreach (var e in live)
            analyses[e.ModId] = AnalyzeModScripts(e);

        var result = new List<Conflict>();
        result.AddRange(ClassNameCollisions(live, analyses));
        result.AddRange(TakeOverCollisions(live, analyses));
        result.AddRange(SuperChainConstraints(live, analyses));
        return result;
    }

    // --- internals ----------------------------------------------------

    /// <summary>Filename stems (case-insensitive) that almost
    /// universally indicate per-mod human documentation rather than
    /// game content. Covers any extension — README, README.md,
    /// README.txt, etc. — by stripping the extension before matching.</summary>
    private static readonly HashSet<string> _docStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "READ_ME",
        "INSTRUCTIONS",
        "LICENSE", "LICENCE", "COPYING",
        "CHANGELOG", "CHANGES",
        "AUTHORS", "CONTRIBUTORS", "CREDITS",
        "NOTICE", "TODO",
    };

    /// <summary>Exact-match dotfile names that don't carry extensions
    /// the same way (".gitignore" has empty stem so the stem set
    /// can't catch it).</summary>
    private static readonly HashSet<string> _docFullNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".gitattributes", ".editorconfig",
    };

    private static bool IsDocumentationFile(string path)
    {
        var name = Path.GetFileName(path);
        if (_docFullNames.Contains(name)) return true;
        var stem = Path.GetFileNameWithoutExtension(name);
        return _docStems.Contains(stem);
    }

    private static List<Conflict> OverlapByFiles(List<ModEntry> entries)
    {
        var byPath = new Dictionary<string, List<ModEntry>>();
        foreach (var e in entries)
        {
            foreach (var f in e.Files)
            {
                // Skip the manifest, dir markers, and Godot-imported
                // sidecar files — those only matter when their source
                // file collides too.
                if (f == "mod.txt") continue;
                if (f.EndsWith("/")) continue;
                if (f.StartsWith(".godot/")) continue;
                // Skip per-mod documentation files that share filenames
                // across the ecosystem (every mod has a README.md). The
                // game doesn't load these — colliding READMEs/LICENSEs
                // are a packaging-hygiene issue, not a mod conflict —
                // so they'd just be expensive noise in the resolver.
                if (IsDocumentationFile(f)) continue;
                if (!byPath.TryGetValue(f, out var list))
                {
                    list = new List<ModEntry>();
                    byPath[f] = list;
                }
                list.Add(e);
            }
        }
        var result = new List<Conflict>();
        foreach (var (path, owners) in byPath)
        {
            if (owners.Count <= 1) continue;
            result.Add(new Conflict
            {
                Type = TYPE_FILE_OVERLAP,
                Key = path,
                ModIds = owners.Select(e => e.ModId).ToList(),
                Details = new Dictionary<string, object>
                {
                    ["is_gdscript"] = path.EndsWith(".gd"),
                },
            });
        }
        return result;
    }

    private static List<Conflict> OverlapBySection(
        List<ModEntry> entries,
        string section,
        string conflictType)
    {
        var byKey = new Dictionary<string, List<(ModEntry mod, string value)>>();
        foreach (var e in entries)
        {
            if (!e.Manifest.TryGetValue(section, out var data)) continue;
            foreach (var (key, value) in data)
            {
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new();
                    byKey[key] = list;
                }
                list.Add((e, value));
            }
        }
        var result = new List<Conflict>();
        foreach (var (key, owners) in byKey)
        {
            if (owners.Count <= 1) continue;
            result.Add(new Conflict
            {
                Type = conflictType,
                Key = key,
                ModIds = owners.Select(t => t.mod.ModId).ToList(),
                Details = new Dictionary<string, object>
                {
                    ["values"] = owners.Select(t => t.value).ToList(),
                },
            });
        }
        return result;
    }

    private static Dictionary<string, GDScriptAnalyzer.Analysis> AnalyzeModScripts(ModEntry entry)
    {
        var result = new Dictionary<string, GDScriptAnalyzer.Analysis>();
        if (entry.IsArchive)
        {
            using var arch = new ModArchive();
            if (!arch.Open(entry.Path)) return result;
            foreach (var f in entry.Files)
            {
                if (!f.EndsWith(".gd")) continue;
                var src = arch.ReadText(f);
                if (!string.IsNullOrEmpty(src))
                    result[f] = GDScriptAnalyzer.Analyze(src);
            }
        }
        else
        {
            foreach (var f in entry.Files)
            {
                if (!f.EndsWith(".gd")) continue;
                var src = entry.ReadFileText(f);
                if (!string.IsNullOrEmpty(src))
                    result[f] = GDScriptAnalyzer.Analyze(src);
            }
        }
        return result;
    }

    private static List<Conflict> ClassNameCollisions(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses)
    {
        var byClass = new Dictionary<string, List<string>>();
        foreach (var e in entries)
        {
            if (!analyses.TryGetValue(e.ModId, out var modAnalyses)) continue;
            foreach (var (_, info) in modAnalyses)
            {
                var cls = info.ClassName;
                if (string.IsNullOrEmpty(cls)) continue;
                if (!byClass.TryGetValue(cls, out var list))
                {
                    list = new();
                    byClass[cls] = list;
                }
                list.Add(e.ModId);
            }
        }
        var result = new List<Conflict>();
        foreach (var (cls, owners) in byClass)
        {
            // Dedupe — a single mod with two scripts declaring the same
            // class_name is its own bug, not a cross-mod conflict.
            var unique = owners.Distinct().ToList();
            if (unique.Count <= 1) continue;
            result.Add(new Conflict
            {
                Type = TYPE_CLASS_NAME_COLLISION,
                Key = cls,
                ModIds = unique,
            });
        }
        return result;
    }

    private static List<Conflict> TakeOverCollisions(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses)
    {
        var byTarget = new Dictionary<string, List<string>>();
        foreach (var e in entries)
        {
            if (!analyses.TryGetValue(e.ModId, out var modAnalyses)) continue;
            foreach (var (_, info) in modAnalyses)
            {
                foreach (var target in info.TakeOverPaths)
                {
                    if (!byTarget.TryGetValue(target, out var list))
                    {
                        list = new();
                        byTarget[target] = list;
                    }
                    if (!list.Contains(e.ModId))
                        list.Add(e.ModId);
                }
            }
        }
        var result = new List<Conflict>();
        foreach (var (target, owners) in byTarget)
        {
            if (owners.Count <= 1) continue;
            result.Add(new Conflict
            {
                Type = TYPE_TAKE_OVER_COLLISION,
                Key = target,
                ModIds = owners,
            });
        }
        return result;
    }

    /// <summary>For each game script that 2+ mods extend, for each
    /// function any extending mod overrides: if mod A's override calls
    /// super() and mod B's doesn't, A must load AFTER B (B replaces, A
    /// chains). Emits one constraint per (chainer, replacer) pair.
    /// Details carry the implied load order in `before`/`after`.</summary>
    private static List<Conflict> SuperChainConstraints(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses)
    {
        // extended_path → { mod_id → { func_name → FunctionInfo } }
        var byTarget = new Dictionary<string, Dictionary<string, Dictionary<string, GDScriptAnalyzer.FunctionInfo>>>();
        foreach (var e in entries)
        {
            if (!analyses.TryGetValue(e.ModId, out var modAnalyses)) continue;
            foreach (var (_, info) in modAnalyses)
            {
                var extPath = info.ExtendsPath;
                if (string.IsNullOrEmpty(extPath)) continue;
                if (info.Functions.Count == 0) continue;
                if (!byTarget.TryGetValue(extPath, out var perMod))
                {
                    perMod = new();
                    byTarget[extPath] = perMod;
                }
                perMod[e.ModId] = info.Functions;
            }
        }

        var result = new List<Conflict>();
        foreach (var (target, perMod) in byTarget)
        {
            if (perMod.Count < 2) continue;
            var fnSet = new HashSet<string>();
            foreach (var (_, funcs) in perMod)
                foreach (var fn in funcs.Keys)
                    fnSet.Add(fn);
            foreach (var fn in fnSet)
            {
                var chainers = new List<string>();
                var replacers = new List<string>();
                foreach (var (modId, funcs) in perMod)
                {
                    if (!funcs.TryGetValue(fn, out var info)) continue;
                    if (info.CallsSuper) chainers.Add(modId);
                    else replacers.Add(modId);
                }
                foreach (var chainer in chainers)
                {
                    foreach (var replacer in replacers)
                    {
                        result.Add(new Conflict
                        {
                            Type = TYPE_SUPER_CHAIN_CONSTRAINT,
                            Key = $"{target}::{fn}",
                            ModIds = new List<string> { replacer, chainer },
                            Details = new Dictionary<string, object>
                            {
                                ["target_script"] = target,
                                ["function"] = fn,
                                ["before"] = replacer,
                                ["after"] = chainer,
                            },
                        });
                    }
                }
            }
        }
        return result;
    }
}
