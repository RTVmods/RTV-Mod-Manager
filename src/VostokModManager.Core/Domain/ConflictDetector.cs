// Finds conflicts among enabled mods.
//
// Sharing a game script is NOT a conflict with Mod Loader 3.4: a wrapped
// method runs every mod's -pre hook, then the first-registered replace
// hook (or the game's code), then every -post hook; [script_extend] and
// take-over patches chain, each extending the previous occupant of the
// path. Only the cases below make one mod's change disappear.
//
// Manifest-only signals (cheap; no archive content reads):
//   FileOverlap            same file path written by 2+ mods
//   AutoloadCollision      same autoload name registered by 2+ mods
//
// Script-level signals (reads .gd contents from archives):
//   ClassNameCollision     same class_name declared by 2+ mods
//   HookCollision          2+ mods claim the same REPLACE hook slot
//                          ("ai-loot", no -pre/-post/-callback): the
//                          loader keeps the first and rejects the rest
//   OverrideCollision      2+ overrides installed over one game script
//                          ([script_extend] / [script_overrides] / a
//                          take_over_path patch that extends it) replace
//                          the same method without calling super, or a
//                          chaining override is applied before one that
//                          doesn't: the earlier override never runs
//   TakeOverCollision      a mod take_over_path()s a game script with a
//                          script that does not extend it, while other
//                          mods hook or extend that script: their changes
//                          are thrown away
//   SuperChainConstraint   not a conflict — an ordering requirement: one
//                          override skips super and another chains, and
//                          their order is only known at runtime; the one
//                          that skips super must be applied first
//
// Scripts that merely extend a game script (a mod's own Pickup subclass
// for its items) are not overrides and are never compared.

namespace VostokModManager.Domain;

public static class ConflictDetector
{
    public const string TYPE_FILE_OVERLAP = "file_overlap";
    public const string TYPE_AUTOLOAD_COLLISION = "autoload_collision";
    public const string TYPE_HOOK_COLLISION = "hook_collision";
    public const string TYPE_OVERRIDE_COLLISION = "override_collision";
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
        // [hooks] and [script_extend] overlaps are not listed here: the
        // loader runs shared hooks side by side and chains extends. The
        // script pass finds the cases that really collide.
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
        var scripts = GameScripts(live, analyses);
        result.AddRange(ReplaceHookCollisions(live, analyses, scripts));
        var overrides = CollectOverrides(live, analyses, out var fullTakeOvers);
        result.AddRange(FullTakeOverCollisions(live, analyses, scripts, overrides, fullTakeOvers));
        result.AddRange(OverrideConflicts(live, overrides));
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

    // --- shared game scripts ------------------------------------------

    /// <summary>A game script the enabled mods touch, with its res://
    /// path and method names as a mod spelled them ("" / lower case when
    /// no mod spelled them out).</summary>
    private sealed class GameScript
    {
        public string Path = "";
        public Dictionary<string, string> Methods = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One mod script installed over a game script: listed in
    /// [script_extend] / [script_overrides] (applied by the loader in
    /// priority order) or a take_over_path patch that extends the target
    /// (applied at runtime, order unknown).</summary>
    private sealed record Override(string ModId, string File, GDScriptAnalyzer.Analysis Info, bool AtRuntime);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string s) => s.Trim().Trim('"', '\'').Trim();

    private static string StemOf(string resPath)
        => System.IO.Path.GetFileNameWithoutExtension(resPath).ToLowerInvariant();

    private static bool IsScriptPath(string path)
        => path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase);

    /// <summary>The game scripts the enabled mods touch, by lower-case
    /// stem: every [hooks] / [script_extend] / [script_overrides] target,
    /// every take_over_path target, and the stem of every -pre / -post /
    /// -callback hook name a mod uses. A bare literal such as
    /// "loader-version" only counts as a replace hook when its stem is
    /// one of these, which keeps ordinary strings out.</summary>
    private static Dictionary<string, GameScript> GameScripts(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses)
    {
        var map = new Dictionary<string, GameScript>(StringComparer.OrdinalIgnoreCase);
        GameScript Get(string stem)
        {
            if (!map.TryGetValue(stem, out var s)) map[stem] = s = new GameScript();
            return s;
        }
        GameScript? AddPath(string path)
        {
            if (!IsScriptPath(path)) return null;
            var s = Get(StemOf(path));
            if (s.Path.Length == 0) s.Path = path;
            return s;
        }
        foreach (var e in entries)
        {
            if (e.Manifest.TryGetValue("hooks", out var hooks))
            {
                foreach (var (path, methods) in hooks)
                {
                    var s = AddPath(Unquote(path));
                    if (s == null) continue;
                    foreach (var m in Unquote(methods).Split(','))
                    {
                        var name = m.Trim();
                        if (name.Length > 0 && name != "*") s.Methods.TryAdd(name, name);
                    }
                }
            }
            foreach (var section in new[] { "script_extend", "script_overrides" })
                if (e.Manifest.TryGetValue(section, out var ext))
                    foreach (var path in ext.Keys) AddPath(Unquote(path));
            if (!analyses.TryGetValue(e.ModId, out var mod)) continue;
            foreach (var info in mod.Values)
            {
                foreach (var t in info.TakeOverPaths) AddPath(t);
                foreach (var h in info.HookNames)
                    if (!GDScriptAnalyzer.IsReplaceHook(h)) Get(GDScriptAnalyzer.HookStem(h));
            }
        }
        return map;
    }

    private static string ScriptLabel(Dictionary<string, GameScript> scripts, string stem)
        => scripts.TryGetValue(stem, out var s) && s.Path.Length > 0 ? s.Path : stem + ".gd";

    private static string MethodLabel(Dictionary<string, GameScript> scripts, string stem, string method)
        => scripts.TryGetValue(stem, out var s) && s.Methods.TryGetValue(method, out var m) ? m : method;

    /// <summary>The ids in load order: priority, then registry order.</summary>
    private static List<string> InLoadOrder(List<ModEntry> entries, IEnumerable<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        return entries
            .Select((e, i) => (e, i))
            .Where(t => wanted.Contains(t.e.ModId))
            .OrderBy(t => t.e.Priority)
            .ThenBy(t => t.i)
            .Select(t => t.e.ModId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>2+ mods claiming the same replace hook slot. The loader
    /// keeps the first registration (in practice the mod that loads
    /// first) and rejects the rest, so their replacement never runs.
    /// Shared -pre / -post / -callback hooks all run and are not listed.</summary>
    private static List<Conflict> ReplaceHookCollisions(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses,
        Dictionary<string, GameScript> scripts)
    {
        var byHook = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!analyses.TryGetValue(e.ModId, out var mod)) continue;
            foreach (var info in mod.Values)
            {
                foreach (var h in info.HookNames)
                {
                    if (!GDScriptAnalyzer.IsReplaceHook(h)) continue;
                    if (!scripts.ContainsKey(GDScriptAnalyzer.HookStem(h))) continue;
                    if (!byHook.TryGetValue(h, out var mods)) byHook[h] = mods = new();
                    if (!mods.Any(m => Same(m, e.ModId))) mods.Add(e.ModId);
                }
            }
        }
        var result = new List<Conflict>();
        foreach (var (hook, mods) in byHook)
        {
            if (mods.Count < 2) continue;
            var stem = GDScriptAnalyzer.HookStem(hook);
            var method = GDScriptAnalyzer.HookMethod(hook);
            var ordered = InLoadOrder(entries, mods);
            result.Add(new Conflict
            {
                Type = TYPE_HOOK_COLLISION,
                Key = $"{ScriptLabel(scripts, stem)}::{MethodLabel(scripts, stem, method)}",
                ModIds = ordered,
                Details = new Dictionary<string, object>
                {
                    ["hook"] = hook,
                    ["values"] = ordered
                        .Select((_, i) => i == 0
                            ? $"replace hook \"{hook}\": registers first, runs"
                            : $"replace hook \"{hook}\": rejected, never runs")
                        .ToList(),
                },
            });
        }
        return result;
    }

    /// <summary>Every mod script installed over a game script, by target
    /// path. A take_over_path target with no script in the mod extending
    /// it is a full replacement, returned in `fullTakeOvers`.</summary>
    private static Dictionary<string, List<Override>> CollectOverrides(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses,
        out List<(string ModId, string Target)> fullTakeOvers)
    {
        var byPath = new Dictionary<string, List<Override>>(StringComparer.OrdinalIgnoreCase);
        var full = new List<(string ModId, string Target)>();
        void Add(string path, Override o)
        {
            if (!byPath.TryGetValue(path, out var list)) byPath[path] = list = new();
            if (!list.Any(x => Same(x.ModId, o.ModId) && Same(x.File, o.File))) list.Add(o);
        }
        foreach (var e in entries)
        {
            var mod = analyses.TryGetValue(e.ModId, out var m) ? m : new();
            foreach (var section in new[] { "script_extend", "script_overrides" })
            {
                if (!e.Manifest.TryGetValue(section, out var ext)) continue;
                foreach (var (target, script) in ext)
                {
                    var file = Unquote(script);
                    if (file.StartsWith("res://", StringComparison.OrdinalIgnoreCase)) file = file.Substring(6);
                    var hit = mod.FirstOrDefault(kv => Same(kv.Key.Replace('\\', '/'), file));
                    if (hit.Value != null) Add(Unquote(target), new Override(e.ModId, hit.Key, hit.Value, false));
                }
            }
            var targets = mod.Values
                .SelectMany(a => a.TakeOverPaths)
                .Where(IsScriptPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var target in targets)
            {
                var patches = mod.Where(kv => Same(kv.Value.ExtendsPath, target)).ToList();
                if (patches.Count == 0)
                {
                    full.Add((e.ModId, target));
                    continue;
                }
                foreach (var (file, info) in patches) Add(target, new Override(e.ModId, file, info, true));
            }
        }
        fullTakeOvers = full;
        return byPath;
    }

    /// <summary>A take_over_path with a script that does not extend the
    /// game script throws away everything chained or hooked onto it.
    /// Listed only when another enabled mod hooks or extends that script.</summary>
    private static List<Conflict> FullTakeOverCollisions(
        List<ModEntry> entries,
        Dictionary<string, Dictionary<string, GDScriptAnalyzer.Analysis>> analyses,
        Dictionary<string, GameScript> scripts,
        Dictionary<string, List<Override>> overrides,
        List<(string ModId, string Target)> fullTakeOvers)
    {
        var result = new List<Conflict>();
        foreach (var (replacer, target) in fullTakeOvers.Distinct())
        {
            var stem = StemOf(target);
            var others = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, t) in fullTakeOvers)
                if (!Same(id, replacer) && Same(t, target)) others[id] = "replaces it outright too";
            if (overrides.TryGetValue(target, out var list))
                foreach (var o in list)
                    if (!Same(o.ModId, replacer)) others.TryAdd(o.ModId, "extends it: its changes are lost");
            foreach (var e in entries)
            {
                if (Same(e.ModId, replacer) || others.ContainsKey(e.ModId)) continue;
                var declares = e.Manifest.TryGetValue("hooks", out var hooks)
                    && hooks.Keys.Any(k => Same(Unquote(k), target));
                var registers = analyses.TryGetValue(e.ModId, out var mod)
                    && mod.Values.Any(a => a.HookNames.Any(h => Same(GDScriptAnalyzer.HookStem(h), stem)));
                if (declares || registers) others[e.ModId] = "hooks it: its hooks stop firing";
            }
            if (others.Count == 0) continue;
            var ids = new List<string> { replacer };
            ids.AddRange(InLoadOrder(entries, others.Keys));
            result.Add(new Conflict
            {
                Type = TYPE_TAKE_OVER_COLLISION,
                Key = scripts.TryGetValue(stem, out var s) && s.Path.Length > 0 ? s.Path : target,
                ModIds = ids,
                Details = new Dictionary<string, object>
                {
                    ["replacer"] = replacer,
                    ["values"] = ids
                        .Select(id => Same(id, replacer)
                            ? "take_over_path with a script that does not extend it"
                            : others[id])
                        .ToList(),
                },
            });
        }
        return result;
    }

    /// <summary>Overrides of one game script that cut each other off.
    /// Per method overridden by 2+ mods:
    ///   - everyone calls super → they chain, nothing to report;
    ///   - 2+ skip super → only the last applied runs (override_collision);
    ///   - one skips super, the others chain → fine when it is applied
    ///     first. With [script_extend] on both sides the order is known
    ///     from priority: the wrong order is an override_collision. With
    ///     a runtime take-over involved the order is unknown, so it is a
    ///     super_chain_constraint (an ordering note).</summary>
    private static List<Conflict> OverrideConflicts(
        List<ModEntry> entries,
        Dictionary<string, List<Override>> overrides)
    {
        var prio = entries
            .GroupBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Priority, StringComparer.OrdinalIgnoreCase);
        var result = new List<Conflict>();
        foreach (var (target, list) in overrides)
        {
            if (list.Select(o => o.ModId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;

            // mod → method → (calls super, applied at runtime)
            var perMod = new Dictionary<string, Dictionary<string, (bool Super, bool AtRuntime)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in list)
            {
                if (!perMod.TryGetValue(o.ModId, out var fns))
                    perMod[o.ModId] = fns = new Dictionary<string, (bool, bool)>(StringComparer.Ordinal);
                foreach (var (fn, info) in o.Info.Functions)
                    fns[fn] = fns.TryGetValue(fn, out var prev)
                        ? (prev.Super && info.CallsSuper, prev.AtRuntime || o.AtRuntime)
                        : (info.CallsSuper, o.AtRuntime);
            }

            var methods = perMod.Values.SelectMany(f => f.Keys).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal);
            foreach (var fn in methods)
            {
                var touching = perMod
                    .Where(kv => kv.Value.ContainsKey(fn))
                    .Select(kv => (Mod: kv.Key, Info: kv.Value[fn]))
                    .ToList();
                if (touching.Count < 2) continue;
                var replacers = touching.Where(t => !t.Info.Super).ToList();
                var chainers = touching.Where(t => t.Info.Super).ToList();
                if (replacers.Count == 0) continue;

                var key = $"{target}::{fn}";
                Conflict Collision(IEnumerable<string> ids)
                {
                    var ordered = InLoadOrder(entries, ids);
                    return new Conflict
                    {
                        Type = TYPE_OVERRIDE_COLLISION,
                        Key = key,
                        ModIds = ordered,
                        Details = new Dictionary<string, object>
                        {
                            ["target_script"] = target,
                            ["function"] = fn,
                            ["values"] = ordered
                                .Select(id => touching.First(t => Same(t.Mod, id)).Info.Super
                                    ? $"overrides {fn}() and calls super"
                                    : $"overrides {fn}() without calling super")
                                .ToList(),
                        },
                    };
                }

                if (replacers.Count >= 2)
                {
                    result.Add(Collision(touching.Select(t => t.Mod)));
                    continue;
                }
                var replacer = replacers[0];
                foreach (var chainer in chainers)
                {
                    var known = !replacer.Info.AtRuntime && !chainer.Info.AtRuntime
                        && prio.TryGetValue(replacer.Mod, out var rp)
                        && prio.TryGetValue(chainer.Mod, out var cp)
                        && rp != cp;
                    if (known)
                    {
                        // Applied lowest priority first; the last applied runs first.
                        if (prio[replacer.Mod] > prio[chainer.Mod])
                            result.Add(Collision(new[] { replacer.Mod, chainer.Mod }));
                        continue;
                    }
                    result.Add(new Conflict
                    {
                        Type = TYPE_SUPER_CHAIN_CONSTRAINT,
                        Key = key,
                        ModIds = new List<string> { replacer.Mod, chainer.Mod },
                        Details = new Dictionary<string, object>
                        {
                            ["target_script"] = target,
                            ["function"] = fn,
                            ["before"] = replacer.Mod,
                            ["after"] = chainer.Mod,
                            ["values"] = new List<string>
                            {
                                $"overrides {fn}() without calling super: must be applied first",
                                $"overrides {fn}() and calls super: must be applied after",
                            },
                        },
                    });
                }
            }
        }
        return result;
    }
}
