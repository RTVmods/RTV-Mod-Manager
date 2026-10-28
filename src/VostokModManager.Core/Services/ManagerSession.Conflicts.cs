// How conflicts are presented: their severity tier, a plain-English
// description, which mod currently wins and why, the conflicts a given
// mod is part of, and the detail view (what happens in game and each
// mod's part in it).

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
                => ($"Two mods replace `{key}`", "replace-hook collision · only the first to register runs"),
            ConflictDetector.TYPE_OVERRIDE_COLLISION
                => ($"Overrides of `{key}` cut each other off", "override collision · only the last applied runs"),
            ConflictDetector.TYPE_TAKE_OVER_COLLISION
                => ($"A mod replaces `{key}` outright", "full take-over · other mods' hooks and extends on it are lost"),
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
                => ($"Order matters for `{key}`",
                    "override order · the override that skips super must be applied first"),
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
    ///   - file_overlap / autoload / override_collision → the highest
    ///     priority (loads last → final write wins);
    ///   - hook_collision → the earliest-loading mod (the loader keeps the
    ///     first registration of a replace hook);
    ///   - take_over_collision → the mod that replaces the script;
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

            case ConflictDetector.TYPE_HOOK_COLLISION:
                var first = c.ModIds.Select(id => Registry.FindById(id)).FirstOrDefault(e => e != null);
                return first == null
                    ? new("—", WinnerTone.Muted, "Couldn't resolve any involved mod against the registry.")
                    : new(EntryLabel(first), WinnerTone.Winner,
                        "Loads first, so it registers the replace hook first. The loader rejects the "
                        + "other mods' registration, and their replacement never runs.");

            case ConflictDetector.TYPE_TAKE_OVER_COLLISION:
                var replacer = Registry.FindById(c.ModIds[0]);
                return new(replacer == null ? c.ModIds[0] : EntryLabel(replacer), WinnerTone.Winner,
                    "Its script replaces the game script without extending it, so what the other mods "
                    + "hooked or chained onto that script is gone.");

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

    /// <summary>The conflict detail view: what happens in game, then each
    /// mod's part in it (Details["values"], parallel to ModIds, or the
    /// conflict key).</summary>
    public ConflictDetail GetConflictDetail(ConflictDetector.Conflict c)
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
        var rows = new List<ConflictValueRow>();
        for (int i = 0; i < c.ModIds.Count; i++)
        {
            var val = i < values.Count && !string.IsNullOrEmpty(values[i]) ? values[i] : c.Key;
            rows.Add(new ConflictValueRow(Name(c.ModIds[i]), val));
        }
        if (c.ModIds.Count == 0) rows.Add(new ConflictValueRow(c.Key, ""));
        return new ConflictDetail(title, subtitle, modNames, ExplainConflict(c), rows);
    }

    /// <summary>What the conflict does in game, in plain words.</summary>
    public static string ExplainConflict(ConflictDetector.Conflict c) => c.Type switch
    {
        ConflictDetector.TYPE_HOOK_COLLISION =>
            "A replace hook swaps out a game method, and the Mod Loader allows one per method. "
            + "The first mod to register keeps it; the other registration is rejected, so that "
            + "mod's replacement never runs. Hooks that run before or after a method are not "
            + "affected: any number of mods can share those.",
        ConflictDetector.TYPE_OVERRIDE_COLLISION =>
            "Both mods install their own version of this method over the game script. An override "
            + "that doesn't call super ends the chain, so whatever was applied before it never runs. "
            + "Ask the mod authors to call super, or keep only one of the mods.",
        ConflictDetector.TYPE_TAKE_OVER_COLLISION =>
            "One mod swaps the game script for a script that doesn't extend it. Everything other "
            + "mods hooked or chained onto that script is thrown away for objects created after "
            + "the swap.",
        ConflictDetector.TYPE_SUPER_CHAIN_CONSTRAINT =>
            "Both mods override this method; one calls super and one doesn't. They work together "
            + "only when the one that skips super is applied first. Its patch is applied while the "
            + "game runs, so the order can't be checked here.",
        ConflictDetector.TYPE_FILE_OVERLAP =>
            "Both mods ship a file at the same path. The game sees only the copy from the mod "
            + "that loads last.",
        ConflictDetector.TYPE_AUTOLOAD_COLLISION =>
            "Both mods register an autoload with the same name. Only one of them is created.",
        ConflictDetector.TYPE_CLASS_NAME_COLLISION =>
            "Two scripts declare the same class_name. Godot refuses to load the project.",
        _ => "",
    };

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
