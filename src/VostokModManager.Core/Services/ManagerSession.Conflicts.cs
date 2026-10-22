// How conflicts are presented: their severity tier, a plain-English
// description, which mod currently wins and why, the conflicts a given
// mod is part of, and the detail view (per-method hook breakdown with
// the last play session's runtime evidence).

using System.Collections;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    // ── tiers ─────────────────────────────────────────────────────────

    public static ConflictSeverity SeverityOf(string type) => type switch
    {
        ConflictDetector.TYPE_CLASS_NAME_COLLISION => ConflictSeverity.Blocking,
        ConflictDetector.TYPE_MISSING_DEPENDENCY => ConflictSeverity.Blocking,
        ConflictDetector.TYPE_DEPENDENCY_ORDER => ConflictSeverity.Info,
        ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT => ConflictSeverity.Info,
        _ => ConflictSeverity.Behavior,
    };

    /// <summary>The tier banner's label, e.g. "●  LOAD-BLOCKING (2)".</summary>
    public static string TierLabel(ConflictSeverity s, int count) => s switch
    {
        ConflictSeverity.Blocking => $"●  LOAD-BLOCKING ({count})",
        ConflictSeverity.Behavior => $"●  BEHAVIOR-AFFECTING ({count})",
        ConflictSeverity.Info => $"●  INFO ({count})",
        _ => "",
    };

    /// <summary>What each tier means in practice (the banner's hover tip).</summary>
    public static string TierTooltip(ConflictSeverity s) => s switch
    {
        ConflictSeverity.Blocking => "Load-blocking — game refuses to start until you fix it.",
        ConflictSeverity.Behavior =>
            "Behavior-affecting — game runs, but one mod overrides another in a way you might not notice.",
        ConflictSeverity.Info => "Info — advisory only; ordering hint, not really wrong.",
        _ => "",
    };

    /// <summary>Plain-English title (line 1) and the technical type +
    /// resolution hint (line 2) of a conflict.</summary>
    public static (string Title, string Subtitle) DescribeConflict(ConflictDetector.Conflict c)
    {
        var key = c.Key;
        return c.Type switch
        {
            ConflictDetector.TYPE_CLASS_NAME_COLLISION
                => ($"Two mods declare class `{key}`", "class_name collision · project will not load"),
            ConflictDetector.TYPE_FILE_OVERLAP
                => ($"Both modify `{key}`", "file overlap · mergeable"),
            ConflictDetector.TYPE_AUTOLOAD_COLLISION
                => ($"Both register autoload `{key}`", "autoload collision · last to load wins"),
            ConflictDetector.TYPE_HOOK_COLLISION
                => ($"Two mods hook `{key}`", "hook collision · last to load wins the chain"),
            ConflictDetector.TYPE_SCRIPT_EXTEND_COLLISION
                => ($"Both extend `{key}`", "script_extend collision · last applies"),
            ConflictDetector.TYPE_TAKE_OVER_COLLISION
                => ($"Two mods take_over `{key}`", "take-over collision · only one applies"),
            ConflictDetector.TYPE_DUPLICATE_MOD_ID
                => ($"Two .vmz files share mod_id `{key}`",
                    "duplicate mod_id · loader picks one, ignores the rest"),
            ConflictDetector.TYPE_MISSING_DEPENDENCY
                => (c.Details.TryGetValue("required", out var dep)
                        ? $"Missing dependency: `{dep}`"
                        : "Missing dependency",
                    "missing dependency · dependent mod won't function"),
            ConflictDetector.TYPE_DEPENDENCY_ORDER
                => ($"Load-order constraint on `{key}`",
                    "dependency order · raise dependent's priority above its dependency"),
            ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT
                => ($"Super-chain constraint on `{key}`",
                    "super_chain · replacer must load before chainer"),
            _ => (c.Type, c.Key),
        };
    }

    /// <summary>The Conflicts status line.</summary>
    public string ConflictsStatusText()
    {
        var conflicts = LastConflicts;
        if (conflicts.Count == 0) return "Conflicts: none detected ✓";
        var byTier = conflicts.GroupBy(c => SeverityOf(c.Type)).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        if (byTier.TryGetValue(ConflictSeverity.Blocking, out var b) && b > 0) parts.Add($"{b} load-blocking");
        if (byTier.TryGetValue(ConflictSeverity.Behavior, out var be) && be > 0) parts.Add($"{be} behavior");
        if (byTier.TryGetValue(ConflictSeverity.Info, out var i) && i > 0) parts.Add($"{i} info");
        return $"Conflicts: {conflicts.Count} ({string.Join(" · ", parts)})";
    }

    // ── winner ────────────────────────────────────────────────────────

    /// <summary>Which mod currently "wins" a conflict given the live
    /// load order. Per type:
    ///   - file_overlap / autoload / hook / script_extend / take_over →
    ///     the highest priority (loads last → final write wins);
    ///   - class_name → no winner, the project refuses to load;
    ///   - super_chain_constraint / dependency_order → the late-loading
    ///     side, whose behaviour the player ends up seeing;
    ///   - missing_dependency → "(needs &lt;dep&gt;)";
    ///   - duplicate_mod_id → the loader picks the alphabetically-first
    ///     file name it finds.</summary>
    public ConflictWinner DetermineWinner(ConflictDetector.Conflict c)
    {
        if (c.ModIds.Count == 0)
            return new("—", WinnerTone.Muted, "No involved mods recorded.");

        switch (c.Type)
        {
            case ConflictDetector.TYPE_CLASS_NAME_COLLISION:
                return new("(hard fail — game won't load)", WinnerTone.HardFail,
                    "Two mods declare the same class_name. Godot refuses to load the project. "
                    + "Disable one of the involved mods.");

            case ConflictDetector.TYPE_MISSING_DEPENDENCY:
                var required = c.Details.TryGetValue("required", out var r) ? r?.ToString() ?? "?" : "?";
                return new($"(needs `{required}`)", WinnerTone.HardFail,
                    "Dependent mod won't function until the required mod is installed and enabled.");

            case ConflictDetector.TYPE_DUPLICATE_MOD_ID:
                // ModIds are FILE NAMES for this type.
                var picked = c.ModIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).First();
                return new($"{picked} (loader picks first)", WinnerTone.Muted,
                    "Two .vmz files share the same mod_id; the in-game loader picks the "
                    + "alphabetically-first filename and silently ignores the rest.");

            case ConflictDetector.TYPE_DEPENDENCY_ORDER:
            case ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT:
                // Ordering constraints: the mod that loads LAST is the
                // one whose behaviour the player sees.
                var lateMod = HighestPriorityMod(c.ModIds);
                return new(lateMod == null ? "—" : EntryLabel(lateMod), WinnerTone.Default,
                    "Late-loading side of the order constraint — its overrides land last in the chain.");

            default:
                // Last write wins.
                var winner = HighestPriorityMod(c.ModIds);
                if (winner == null)
                    return new("—", WinnerTone.Muted, "Couldn't resolve any involved mod against the registry.");
                return new(EntryLabel(winner), WinnerTone.Winner,
                    "Highest priority value (loads last) → its version is what the game actually sees.");
        }
    }

    /// <summary>Among the ids, the entry with the highest Priority
    /// (loads last); ties go to the alphabetically-first id. Null when
    /// none resolve.</summary>
    private ModEntry? HighestPriorityMod(IEnumerable<string> modIds)
        => modIds
            .Select(id => Registry.FindById(id))
            .Where(e => e != null)
            .Select(e => e!)
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>DisplayName, else mod id, else the file name.</summary>
    private static string EntryLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId) ? e.ModId
            : Path.GetFileName(e.Path);

    /// <summary>Conflict ids as display names, earliest-loading first,
    /// so the last name is the "last write wins" winner. Ids that don't
    /// resolve (missing mods, duplicate_mod_id file names) come last,
    /// alphabetical.</summary>
    private List<string> OrderedModNames(IEnumerable<string> ids)
    {
        var resolved = new List<(string label, int priority)>();
        var unresolved = new List<string>();
        foreach (var id in ids)
        {
            var entry = Registry.FindById(id);
            if (entry != null) resolved.Add((EntryLabel(entry), entry.Priority));
            else unresolved.Add(id);
        }
        return resolved
            .OrderBy(t => t.priority)
            .ThenBy(t => t.label, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.label)
            .Concat(unresolved.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    // ── resolve button ────────────────────────────────────────────────

    /// <summary>True for every file_overlap in the AI edition (whether
    /// or not Claude is available — the click re-checks); never in the
    /// Integrated edition.</summary>
    public static bool HasResolveButton(ConflictDetector.Conflict c)
#if AI_RESOLVER
        => c.Type == ConflictDetector.TYPE_FILE_OVERLAP;
#else
        => false;
#endif

    /// <summary>True when the conflict can be sent to Claude now: v1
    /// covers every file_overlap (Claude can merge any text file) and
    /// needs Claude Code installed. Always false in the Integrated
    /// edition.</summary>
    public bool IsResolvable(ConflictDetector.Conflict c)
    {
#if AI_RESOLVER
        if (c.Type != ConflictDetector.TYPE_FILE_OVERLAP) return false;
        return Claude.IsAvailable;
#else
        return false;
#endif
    }

    /// <summary>The Resolve cell's hover text: how to fix this kind of
    /// conflict, or why AI resolve isn't offered.</summary>
    public string ResolveHint(ConflictDetector.Conflict c)
    {
        if (c.Type == ConflictDetector.TYPE_DUPLICATE_MOD_ID)
            return "Two installed mods declare the same mod_id. The Mods column lists the colliding "
                + "files — delete or move one out of the mods folder (and its Disabled subfolder) so "
                + "only one remains. Often this is a leftover .vmz from a previous version sitting "
                + "alongside the current copy.";
        if (c.Type == ConflictDetector.TYPE_MISSING_DEPENDENCY)
            return "An enabled mod requires another mod that's either disabled or not installed. The "
                + "Mods column lists the dependent and the missing one — enable / install the required "
                + "mod, or disable the dependent.";
        if (c.Type == ConflictDetector.TYPE_DEPENDENCY_ORDER)
        {
            var min = c.Details.TryGetValue("suggested_dependent_min", out var v) ? v?.ToString() ?? "?" : "?";
            return $"A mod loads before its declared dependency. Raise the dependent's priority to at "
                + $"least {min} (right-click → Set priority…) so it loads after the dependency.";
        }
        if (c.Type != ConflictDetector.TYPE_FILE_OVERLAP)
            return "AI resolve only handles file_overlap conflicts in v1.";
#if AI_RESOLVER
        if (!Claude.IsAvailable)
            return "Claude Code not detected — install it to enable AI resolve.";
        return "Send this conflict to Claude Code for analysis.";
#else
        return "AI conflict resolution is not available in the Integrated edition.";
#endif
    }

    // ── views ─────────────────────────────────────────────────────────

    /// <summary>One conflict as the list shows it.</summary>
    public ConflictView ToConflictView(ConflictDetector.Conflict c)
    {
        var (title, subtitle) = DescribeConflict(c);
        return new ConflictView(
            c, SeverityOf(c.Type), title, subtitle,
            $"[{c.Type}]  key: {c.Key}",
            OrderedModNames(c.ModIds),
            DetermineWinner(c),
            HasResolveButton(c),
            ResolveHint(c));
    }

    /// <summary>The last detected conflicts (or `conflicts`) in list
    /// order: by tier (Blocking → Behavior → Info), then the load-order
    /// priority of the earliest-loading mod involved, then type and key.
    /// Ids that don't resolve sort to the end of their tier.</summary>
    public List<ConflictView> GetConflictViews(IEnumerable<ConflictDetector.Conflict>? conflicts = null)
    {
        int PrioOf(string modIdOrFile)
            => Registry.Entries.FirstOrDefault(m =>
                string.Equals(m.ModId, modIdOrFile, StringComparison.OrdinalIgnoreCase))?.Priority
               ?? int.MaxValue;
        int EarliestPrio(ConflictDetector.Conflict c)
            => c.ModIds.Count == 0 ? int.MaxValue : c.ModIds.Min(PrioOf);

        return (conflicts ?? LastConflicts)
            .OrderBy(c => (int)SeverityOf(c.Type))
            .ThenBy(EarliestPrio)
            .ThenBy(c => c.Type, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(ToConflictView)
            .ToList();
    }

    /// <summary>The last detected conflicts a mod takes part in, in
    /// list order. A duplicate_mod_id conflict names files, so it
    /// matches on the mod id it is keyed by or the entry's file name.</summary>
    public List<ConflictView> ConflictsInvolving(string modId, string? fileName = null)
    {
        if (string.IsNullOrEmpty(modId) && string.IsNullOrEmpty(fileName)) return new();
        bool Involves(ConflictDetector.Conflict c)
        {
            if (!string.IsNullOrEmpty(modId)
                && c.ModIds.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (c.Type != ConflictDetector.TYPE_DUPLICATE_MOD_ID) return false;
            return (!string.IsNullOrEmpty(modId) && string.Equals(c.Key, modId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(fileName)
                    && c.ModIds.Any(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)));
        }
        return GetConflictViews(LastConflicts.Where(Involves));
    }

    /// <summary>The last detected conflicts `e` takes part in.</summary>
    public List<ConflictView> ConflictsInvolving(ModEntry e)
        => ConflictsInvolving(e.ModId, Path.GetFileName(e.Path));

    // ── detail ────────────────────────────────────────────────────────

    /// <summary>The conflict detail view. The list flags a hook
    /// collision at the FILE level, but the loader chains hooks per
    /// METHOD: two mods hooking different methods of one script coexist,
    /// only the same path::method clashes. This derives the per-method
    /// truth from Details["values"] (each mod's method list, parallel
    /// to ModIds) and, when a godot.log exists, adds the hook clashes
    /// the last play session confirmed. Other types list each mod's
    /// conflicting value.</summary>
    public ConflictDetail GetConflictDetail(ConflictDetector.Conflict c)
        => GetConflictDetail(c, GodotLogAnalyzer.LatestLoaderLog);

    internal ConflictDetail GetConflictDetail(ConflictDetector.Conflict c, Func<string> findLog)
    {
        var (title, subtitle) = DescribeConflict(c);
        string Name(string id)
        {
            var e = Registry.FindById(id);
            return e != null && !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : id;
        }
        var modNames = c.ModIds.Select(Name).ToList();
        subtitle = subtitle + "   ·   mods: " + string.Join(", ", modNames);
        var values = ConflictValues(c);

        if (c.Type != ConflictDetector.TYPE_HOOK_COLLISION)
        {
            var rows = new List<ConflictValueRow>();
            for (int i = 0; i < c.ModIds.Count; i++)
            {
                var val = i < values.Count && !string.IsNullOrEmpty(values[i]) ? values[i] : c.Key;
                rows.Add(new ConflictValueRow(Name(c.ModIds[i]), val));
            }
            if (c.ModIds.Count == 0) rows.Add(new ConflictValueRow(c.Key, ""));
            return new ConflictDetail(title, subtitle, modNames, false, "",
                Array.Empty<HookMethodRow>(), "", Array.Empty<RuntimeHookRow>(), rows);
        }

        // method → mods hooking it, from the manifest method lists.
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 0; i < c.ModIds.Count; i++)
        {
            var modId = c.ModIds[i];
            // Prefer the captured value; fall back to the live manifest.
            var raw = i < values.Count ? values[i] : "";
            if (string.IsNullOrEmpty(raw))
            {
                var e = Registry.FindById(modId);
                if (e != null && e.Hooks.TryGetValue(c.Key, out var hv)) raw = hv;
            }
            foreach (var m in raw.Split(','))
            {
                var method = m.Trim();
                if (method.Length == 0) continue;
                if (!map.TryGetValue(method, out var mods)) map[method] = mods = new();
                if (!mods.Contains(modId)) mods.Add(modId);
            }
        }
        var methods = map
            .OrderByDescending(k => k.Value.Count)
            .ThenBy(k => k.Key, StringComparer.Ordinal)
            .Select(kv => new HookMethodRow(kv.Key, kv.Value.Select(Name).ToList()))
            .ToList();
        var realCollisions = methods.Count(m => m.IsCollision);
        var legend = realCollisions > 0
            ? $"{realCollisions} method(s) are hooked by more than one mod — those are the real "
              + "collisions (the rest only overlap at the file level and coexist via MML's hook chain)."
            : "These mods hook the same script but on DIFFERENT methods — they coexist via MML's hook "
              + "chain. No method is hooked by more than one mod.";

        // Runtime correlation (best-effort; only when a log exists).
        var note = "Runtime (last play session)";
        var runtime = new List<RuntimeHookRow>();
        try
        {
            var logPath = findLog();
            if (string.IsNullOrEmpty(logPath))
            {
                note = "Runtime (last play session) — no godot.log found";
                runtime.Add(new("(no log)", "Launch the game once to capture runtime hook data."));
            }
            else
            {
                var a = GodotLogAnalyzer.Analyze(logPath);
                var clashes = a.HookClashes
                    .Where(h => string.Equals(h.VanillaPath, c.Key, StringComparison.Ordinal))
                    .ToList();
                if (a.HooksNotLogged)
                {
                    note = "Runtime (last play session) — hooks were not logged";
                    runtime.Add(new("(not logged)", GodotLogAnalyzer.HooksNotLoggedHint));
                }
                else if (clashes.Count == 0)
                {
                    note = "Runtime (last play session) — no multi-mod hook on this script in the last log";
                    runtime.Add(new("(none)", "No method on this script was hooked by 2+ mods at runtime."));
                }
                else
                {
                    foreach (var h in clashes)
                        runtime.Add(new(h.Method, string.Join("  →  ", h.Mods)));
                }
            }
        }
        catch
        {
            note = "Runtime (last play session) — could not read the log";
        }
        return new ConflictDetail(title, subtitle, modNames, true, legend, methods, note, runtime,
            Array.Empty<ConflictValueRow>());
    }

    /// <summary>Per-mod manifest values parallel to ModIds, from
    /// Details["values"]; empty when absent.</summary>
    private static List<string> ConflictValues(ConflictDetector.Conflict c)
    {
        var list = new List<string>();
        if (c.Details.TryGetValue("values", out var v) && v is IEnumerable seq && v is not string)
            foreach (var item in seq) list.Add(item?.ToString() ?? "");
        return list;
    }
}
