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
    public const string TYPE_MISSING_DEPENDENCY = "missing_dependency";
    public const string TYPE_DUPLICATE_MOD_ID = "duplicate_mod_id";
    public const string TYPE_DEPENDENCY_ORDER = "dependency_order";

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
        var all = entries.ToList();
        var live = all.Where(e => e.IsEnabled).ToList();
        var result = new List<Conflict>();
        result.AddRange(OverlapByFiles(live));
        result.AddRange(OverlapBySection(live, "autoload", TYPE_AUTOLOAD_COLLISION));
        result.AddRange(OverlapBySection(live, "hooks", TYPE_HOOK_COLLISION));
        result.AddRange(OverlapBySection(live, "script_extend", TYPE_SCRIPT_EXTEND_COLLISION));
        // Dependency check needs the FULL registry (not just enabled
        // mods) so we can distinguish "installed but disabled" from
        // "not installed at all" in the conflict detail.
        result.AddRange(MissingDependencies(live, all));
        // Duplicate-mod-id is a registry-wide check (count includes
        // disabled copies) — leftover .vmz files in mods/Disabled/
        // alongside the live copy are the most common cause and the
        // user definitely wants to know about them.
        result.AddRange(DuplicateModIds(all));
        // Dependency order: dependent must load AFTER its
        // dependency, i.e. its priority must be strictly higher.
        // Only meaningful between enabled mods (a disabled dep
        // already shows up as missing_dependency).
        result.AddRange(DependencyOrderViolations(live));
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

    /// <summary>One conflict per mod_id that appears more than once
    /// in the registry. ModIds carries the file basenames of each
    /// duplicate so the user can identify which .vmz files to clean
    /// up; Details["paths"] has the full paths.</summary>
    private static List<Conflict> DuplicateModIds(List<ModEntry> all)
    {
        var byId = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (!byId.TryGetValue(e.ModId, out var list))
            {
                list = new List<ModEntry>();
                byId[e.ModId] = list;
            }
            list.Add(e);
        }
        var result = new List<Conflict>();
        foreach (var (modId, instances) in byId)
        {
            if (instances.Count <= 1) continue;
            result.Add(new Conflict
            {
                Type = TYPE_DUPLICATE_MOD_ID,
                Key = modId,
                // Use basenames here rather than the (identical) mod_id
                // so the Mods column actually distinguishes the
                // colliding files. Full paths in Details for rich UI.
                ModIds = instances
                    .Select(e => Path.GetFileName(e.Path))
                    .ToList(),
                Details = new Dictionary<string, object>
                {
                    ["mod_id"] = modId,
                    ["paths"] = instances.Select(e => e.Path).ToList(),
                    ["enabled_count"] = instances.Count(e => e.IsEnabled),
                },
            });
        }
        return result;
    }

    /// <summary>One conflict per (dependent, dependency) pair where
    /// the dependent's load-order priority isn't strictly greater
    /// than the dependency's. We require strictly-greater (not &gt;=)
    /// because equal-priority load order is implementation-defined
    /// (currently broken by filename), so a tie would still be a
    /// risk. Fix is "raise dependent.priority above dependency.priority"
    /// — the Details carry the suggested minimum so the UI can show
    /// it.</summary>
    private static List<Conflict> DependencyOrderViolations(List<ModEntry> live)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in live)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            byId.TryAdd(e.ModId, e);
        }
        var result = new List<Conflict>();
        foreach (var dependent in live)
        {
            if (string.IsNullOrEmpty(dependent.ModId)) continue;
            foreach (var depId in dependent.RequiredDependencies)
            {
                if (string.IsNullOrEmpty(depId)) continue;
                if (!byId.TryGetValue(depId, out var dep)) continue;
                if (dependent.Priority > dep.Priority) continue;
                // dependent.Priority <= dep.Priority — order violation.
                result.Add(new Conflict
                {
                    Type = TYPE_DEPENDENCY_ORDER,
                    Key = $"{dependent.ModId} after {dep.ModId}",
                    ModIds = new List<string> { dependent.ModId, dep.ModId },
                    Details = new Dictionary<string, object>
                    {
                        ["dependent"] = dependent.ModId,
                        ["dependency"] = dep.ModId,
                        ["dependent_priority"] = dependent.Priority,
                        ["dependency_priority"] = dep.Priority,
                        ["suggested_dependent_min"] = dep.Priority + 1,
                    },
                });
            }
        }
        return result;
    }

    /// <summary>One conflict per (enabled mod, missing required dep)
    /// pair. The detail differentiates "installed but disabled" from
    /// "not installed at all" so the UI can suggest the right fix —
    /// the former is a one-click toggle; the latter needs a download
    /// or a manual install.</summary>
    private static List<Conflict> MissingDependencies(
        List<ModEntry> live,
        List<ModEntry> all)
    {
        // First-wins on duplicate mod_ids — TryAdd avoids the
        // ArgumentException ToDictionary throws when two installed
        // mods declare the same id (legitimate-but-ugly state, e.g.
        // a leftover .vmz from a prior version still in mods/Disabled
        // alongside the current copy in mods/). Duplicates are their
        // own bug worth surfacing separately, but it shouldn't crash
        // dependency detection.
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            byId.TryAdd(e.ModId, e);
        }
        var result = new List<Conflict>();
        foreach (var e in live)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            foreach (var depId in e.RequiredDependencies)
            {
                if (string.IsNullOrEmpty(depId)) continue;
                var status = "not_installed";
                if (byId.TryGetValue(depId, out var dep))
                    status = dep.IsEnabled ? "satisfied" : "disabled";
                if (status == "satisfied") continue;
                result.Add(new Conflict
                {
                    Type = TYPE_MISSING_DEPENDENCY,
                    Key = $"{e.ModId} → {depId}",
                    ModIds = new List<string> { e.ModId, depId },
                    Details = new Dictionary<string, object>
                    {
                        ["dependent"] = e.ModId,
                        ["required"] = depId,
                        ["status"] = status,
                    },
                });
            }
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
