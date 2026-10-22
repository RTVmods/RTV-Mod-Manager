// The Mods area: enable / disable, load order, lock / testing / notes,
// delete, install, and the mods list rows. Ported from the WinForms
// MainForm (ToggleMod, BulkToggle, SetModPriorityAsync, BulkSetPriority,
// DragReorderPriority, ToggleLock, ToggleTesting, EditModNote,
// DeleteMods, InstallModFilesAsync, PopulateModsGrid, StyleUpdateCell).

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    // ── seams ─────────────────────────────────────────────────────────

    /// <summary>Replaces sending a file or folder to the Recycle Bin
    /// (tests delete instead, so the real Recycle Bin stays clean).</summary>
    internal Action<string>? RecycleOverride { get; set; }

    /// <summary>Replaces the VostokMods download (slug, save path) for
    /// the install-from-URL flow, so tests stay offline.</summary>
    internal Func<string, string, Task>? ModDownloadOverride { get; set; }

    /// <summary>Sends a mod's file or folder to the Recycle Bin (NOT a
    /// hard delete, so it is recoverable). Does nothing for a path that
    /// is already gone.</summary>
    private void RecycleModPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (RecycleOverride != null) { RecycleOverride(path); return; }
        // Microsoft.VisualBasic's FileSystem with SendToRecycleBin is the
        // simplest reliable way to recycle from .NET; avoids P/Invoke.
        if (File.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private void SaveSettingsForMods()
    {
        try { Settings.Save(); } catch { /* best-effort; in memory it holds */ }
    }

    private static string FileOrDisplayName(ModEntry e)
        => string.IsNullOrEmpty(e.DisplayName) ? Path.GetFileName(e.Path) : e.DisplayName;

    // ── the mods list ─────────────────────────────────────────────────

    /// <summary>The mods status line's numbers (every scanned mod, not
    /// just the active profile's).</summary>
    public ModCounts CountMods()
        => new(Registry.Entries.Count, Registry.Enabled().Count);

    /// <summary>The rows of the mods list. Sorted by load order
    /// (priority, then file name), the same whether a mod is enabled or
    /// not, so a toggle never shuffles a row. With an active profile only
    /// its mods show, plus locked ones (a lock means "don't change or
    /// hide this mod"). Named packs are contiguous blocks anchored at the
    /// lowest priority of their members: a header row, then (unless
    /// collapsed) the members.</summary>
    public ModListView BuildModRows(ModListQuery query)
    {
        var filter = (query.Filter ?? "").Trim().ToLowerInvariant();
        bool Matches(ModEntry e)
        {
            if (filter.Length == 0) return true;
            if (e.DisplayName.ToLowerInvariant().Contains(filter)) return true;
            if (e.ModId.ToLowerInvariant().Contains(filter)) return true;
            if (Path.GetFileName(e.Path).ToLowerInvariant().Contains(filter)) return true;
            return false;
        }

        // Profile scope: before the profile model is adopted every mod
        // shows. Locked mods bypass it so they can always be unlocked.
        var activeIds = ActiveProfile != null
            ? new HashSet<string>(ActiveProfile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase)
            : null;
        var lockedIds = new HashSet<string>(ProtectedModIds(), StringComparer.OrdinalIgnoreCase);
        bool InActiveProfile(ModEntry e)
        {
            if (activeIds == null) return true;
            if (string.IsNullOrEmpty(e.ModId)) return false;
            if (activeIds.Contains(e.ModId)) return true;
            return lockedIds.Contains(e.ModId);
        }

        // Dependency-rollup filter, with the same locked bypass.
        var depFilter = query.DependencyFilter;
        bool InDependencyScope(ModEntry e)
        {
            if (depFilter == null) return true;
            if (string.IsNullOrEmpty(e.ModId)) return false;
            if (depFilter.Contains(e.ModId)) return true;
            return lockedIds.Contains(e.ModId);
        }

        var displayed = Registry.Entries
            .Where(InActiveProfile)
            .Where(InDependencyScope)
            .Where(Matches)
            .OrderBy(e => e.Priority)
            .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var packsByMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ActiveProfile != null)
        {
            foreach (var pm in ActiveProfile.Mods)
            {
                if (string.IsNullOrEmpty(pm.ModId)) continue;
                if (!string.IsNullOrEmpty(pm.PackName))
                    packsByMod[pm.ModId] = pm.PackName;
            }
        }

        // Bucket each displayed mod by pack name (empty → a single).
        var packBuckets = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        var unpackedSingles = new List<ModEntry>();
        foreach (var e in displayed)
        {
            var pack = !string.IsNullOrEmpty(e.ModId)
                && packsByMod.TryGetValue(e.ModId, out var p)
                && !string.IsNullOrEmpty(p) ? p : "";
            if (string.IsNullOrEmpty(pack))
            {
                unpackedSingles.Add(e);
            }
            else
            {
                if (!packBuckets.TryGetValue(pack, out var list))
                    packBuckets[pack] = list = new List<ModEntry>();
                list.Add(e);
            }
        }

        // Each unit is a single mod (key = its priority) or a pack block
        // (key = its members' lowest priority), with a stable tiebreak.
        var units = new List<(int sortKey, string secondary, ModEntry? single, string? packName, List<ModEntry>? packMods)>();
        foreach (var e in unpackedSingles)
            units.Add((e.Priority, Path.GetFileName(e.Path), e, null, null));
        foreach (var kvp in packBuckets)
            units.Add((kvp.Value.Min(m => m.Priority), kvp.Key, null, kvp.Key, kvp.Value));
        units.Sort((a, b) =>
        {
            var k = a.sortKey.CompareTo(b.sortKey);
            if (k != 0) return k;
            return string.Compare(a.secondary, b.secondary, StringComparison.OrdinalIgnoreCase);
        });
        var collapsed = new HashSet<string>(Settings.CollapsedPacks, StringComparer.OrdinalIgnoreCase);

        var conflicted = new HashSet<string>(
            LastConflicts.SelectMany(c => c.ModIds), StringComparer.OrdinalIgnoreCase);

        var rows = new List<ModRow>();
        var pos = 0;
        ModRow ModRowFor(ModEntry e, bool inPack)
        {
            var note = !string.IsNullOrEmpty(e.ModId)
                && Settings.ModNotes.TryGetValue(e.ModId, out var raw)
                && !string.IsNullOrWhiteSpace(raw) ? raw : "";
            return new ModRow
            {
                Entry = e,
                DisplayName = FileOrDisplayName(e),
                ModId = e.ModId,
                Version = e.Version,
                Priority = e.Priority,
                Enabled = e.IsEnabled,
                Position = e.IsEnabled ? ++pos : null,
                Locked = IsLocked(e),
                BuiltIn = IsBuiltInActive(e.ModId),
                Testing = IsTesting(e),
                InstalledThisSession = IsInstalledThisSession(e),
                InPack = inPack,
                PackName = inPack && packsByMod.TryGetValue(e.ModId, out var pk) ? pk : "",
                Note = note,
                Update = UpdateInfoFor(e),
                InConflict = !string.IsNullOrEmpty(e.ModId) && conflicted.Contains(e.ModId),
            };
        }

        foreach (var unit in units)
        {
            if (unit.single != null)
            {
                rows.Add(ModRowFor(unit.single, inPack: false));
                continue;
            }
            var packName = unit.packName!;
            var mods = unit.packMods!;
            var isCollapsed = collapsed.Contains(packName);
            rows.Add(new ModRow
            {
                PackName = packName,
                PackCount = mods.Count,
                PackCollapsed = isCollapsed,
                DisplayName = $"{packName}  ({mods.Count} mod{(mods.Count == 1 ? "" : "s")})",
            });
            if (isCollapsed) continue;
            foreach (var e in mods) rows.Add(ModRowFor(e, inPack: true));
        }
        return new ModListView(rows, displayed);
    }

    /// <summary>The Update column's state for a mod.</summary>
    public ModUpdateInfo UpdateInfoFor(ModEntry e)
    {
        if (IsBuiltInActive(e.ModId))
            return new(ModUpdateState.BuiltIn, "", "Built into RTV Mod Manager; updated with the manager.");
        if (!e.Source.IsValid)
            return new(ModUpdateState.Unlinked, "",
                "Not linked to VostokMods — can't check for updates. Click to link it.");
        if (!TryGetLatest(e, out var latest))
            return new(ModUpdateState.Unknown, "", "Update status unknown (cache miss). Click Refresh to check.");

        var cmp = ModRegistry.CompareVersions(e.Version, latest);
        if (cmp == 0)
            return new(ModUpdateState.UpToDate, latest, $"Up to date (v{latest}).");
        if (VersionsAreAmbiguous(e.Version, latest))
            return new(ModUpdateState.Ambiguous, latest,
                $"Version mismatch: mod.txt v{e.Version}, VostokMods reports v{latest}.\n\n"
                + "The two strings format differently and can't be reliably ordered — likely "
                + "the same release written two ways. The local file matches what VostokMods "
                + "currently serves; the author just hasn't matched mod.txt to the listing.");
        if (cmp > 0)
            return new(ModUpdateState.NewerLocal, latest,
                $"Local v{e.Version} is newer than VostokMods' reported v{latest}. Likely a dev "
                + "build, a manual bump, or the API cache catching up.");
        return new(ModUpdateState.Outdated, latest,
            $"Update available: v{e.Version} → v{latest}. Click to install.");
    }

    /// <summary>Collapses or expands a pack group in the mods list and
    /// remembers it in settings.</summary>
    public void TogglePackCollapse(string packName)
    {
        if (string.IsNullOrEmpty(packName)) return;
        var existing = Settings.CollapsedPacks.FirstOrDefault(p =>
            string.Equals(p, packName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(existing))
            Settings.CollapsedPacks.Remove(existing);
        else
            Settings.CollapsedPacks.Add(packName);
        SaveSettingsForMods();
        RaiseChanged();
    }

    // ── enable / disable ──────────────────────────────────────────────

    /// <summary>The Enabled checkbox: turns a mod on or off. A built-in
    /// mod stays on (the list is repainted to undo the click). Returns
    /// true when the mod ends up in the wanted state.</summary>
    public bool SetModEnabled(ModEntry e, bool enabled)
    {
        if (IsBuiltInActive(e.ModId))
        {
            Say(StatusArea.Mods, "Save Slots is built into the manager and stays on; "
                + "turn it off in Settings → Built-in save slots.");
            RaiseChanged();
            return false;
        }
        if (enabled == e.IsEnabled) return true;
        return ToggleMod(e);
    }

    /// <summary>Flips a mod's enabled state. Enabling also turns on its
    /// installed-but-disabled required dependencies (transitively), so
    /// an enabled mod never has its prerequisites off.</summary>
    public bool ToggleMod(ModEntry e)
    {
        // Capture the direction BEFORE toggling: ToggleModFiles flips
        // based on the still-current e.IsEnabled.
        var enabling = !e.IsEnabled;
        if (!ToggleModFiles(e))
        {
            Prompt.Error("Toggle failed",
                new Exception($"Couldn't toggle `{e.DisplayName}` — "
                    + "the file is locked or the mod has no mod_id. "
                    + "Quit Road to Vostok and any antivirus scan, then try again."));
            return false;
        }
        var autoEnabled = new List<ModEntry>();
        if (enabling)
            autoEnabled = EnableRequiredDependencies(e);
        if (!SaveModConfigSafely()) return false;
        if (autoEnabled.Count > 0)
        {
            var names = string.Join(", ", autoEnabled.Select(d =>
                string.IsNullOrEmpty(d.DisplayName) ? d.ModId : d.DisplayName));
            Say(StatusArea.Mods,
                $"Enabled `{e.DisplayName}` + {autoEnabled.Count} "
                + $"required dependenc{(autoEnabled.Count == 1 ? "y" : "ies")}: {names}.");
        }
        Rescan();
        // Mirror the new state into the active profile so a later switch
        // away and back keeps it.
        SyncActiveProfileFromEntry(e.ModId);
        Requery();
        return true;
    }

    /// <summary>When a mod is being enabled, turns on its REQUIRED
    /// dependencies that are installed but off, transitively. Optional
    /// deps are left alone, missing ones are the install flow's job, and
    /// LOCKED deps keep their state (a lock outranks the convenience).
    /// Doesn't save the cfg: the caller batches that with the root
    /// toggle. Returns the entries that were turned on.</summary>
    internal List<ModEntry> EnableRequiredDependencies(ModEntry root)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Registry.Entries)
            if (!string.IsNullOrEmpty(m.ModId) && !byId.ContainsKey(m.ModId))
                byId[m.ModId] = m;

        var enabled = new List<ModEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(root.ModId)) visited.Add(root.ModId);

        var queue = new Queue<ModEntry>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var depId in cur.RequiredDependencies)
            {
                if (string.IsNullOrEmpty(depId)) continue;
                if (!visited.Add(depId)) continue;                    // already handled
                if (!byId.TryGetValue(depId, out var dep)) continue;  // not installed
                // Walk on regardless of state so A→B(on)→C(off) reaches C.
                queue.Enqueue(dep);
                if (dep.IsEnabled) continue;
                if (IsLocked(dep)) continue;
                if (ToggleModFiles(dep))
                    enabled.Add(dep);
            }
        }
        return enabled;
    }

    /// <summary>Toggles a mod's enabled state in mod_config.cfg (the
    /// in-game loader's source of truth); enabling a mod parked in
    /// &lt;mods&gt;/Disabled/ also moves it back to &lt;mods&gt;/ so the
    /// loader can see it. Disabling never moves files: cfg=false is
    /// enough and matches the loader's own UI. Doesn't save the cfg (the
    /// caller batches it). False when the move fails or there is no
    /// mod_id to key the cfg on.</summary>
    internal bool ToggleModFiles(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId)) return false;
        var enabling = !e.IsEnabled;
        if (enabling)
        {
            if (IsInModsDisabledFolder(e.Path))
            {
                var dst = Path.Combine(ModsDir, Path.GetFileName(e.Path));
                try
                {
                    if (e.IsArchive) File.Move(e.Path, dst);
                    else Directory.Move(e.Path, dst);
                    e.Path = dst;
                }
                catch
                {
                    return false;
                }
            }
            Config.SetEnabled(e.ModId, e.Version, true);
            // The declared priority becomes the cfg priority on first
            // enable, so later cfg-only flows see a stable value.
            if (!Config.HasEntry(e.ModId, e.Version))
                Config.SetPriority(e.ModId, e.Version, e.DeclaredPriority);
        }
        else
        {
            Config.SetEnabled(e.ModId, e.Version, false);
        }
        return true;
    }

    private bool IsInModsDisabledFolder(string entryPath)
    {
        var disabled = Path.Combine(ModsDir, "Disabled");
        try
        {
            var entryFull = Path.GetFullPath(entryPath);
            var disabledFull = Path.GetFullPath(disabled);
            return entryFull.StartsWith(disabledFull + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Enables or disables a chosen set of mods (the multi-
    /// selection action). Locked mods are skipped; one cfg save and one
    /// refresh for the batch.</summary>
    public void BulkToggleSelected(IReadOnlyList<ModEntry> entries, bool enable)
    {
        if (entries.Count == 0) return;
        var candidates = entries.Where(e => e.IsEnabled != enable).ToList();
        var targets = candidates.Where(e => !IsLocked(e)).ToList();
        var skipped = candidates.Where(e => IsLocked(e)).ToList();
        var lockedNote = skipped.Count > 0 ? $" ({skipped.Count} locked, skipped)." : ".";
        if (targets.Count == 0)
        {
            Say(StatusArea.Mods, (enable ? "Selection: all already enabled" : "Selection: all already disabled")
                + lockedNote);
            return;
        }
        var failed = 0;
        foreach (var e in targets)
            if (!ToggleModFiles(e)) failed++;
        SaveModConfigSafely();
        Refresh();

        var verb = enable ? "Enabled" : "Disabled";
        var skipNote = skipped.Count > 0 ? $" ({skipped.Count} locked, skipped)" : "";
        var failNote = failed > 0 ? $" ({failed} failed)" : "";
        Say(StatusArea.Mods, $"{verb} {targets.Count - failed} mod(s){skipNote}{failNote}.");
    }

    /// <summary>Enable all / Disable all, after a confirmation that
    /// previews the mods and names how many locked ones are skipped.</summary>
    public void BulkToggle(bool enable)
    {
        var candidates = Registry.Entries.Where(e => e.IsEnabled != enable).ToList();
        var targets = candidates.Where(e => !IsLocked(e)).ToList();
        var skipped = candidates.Where(e => IsLocked(e)).ToList();
        if (targets.Count == 0)
        {
            if (skipped.Count > 0)
                Say(StatusArea.Mods, enable
                    ? $"All non-locked mods are already enabled ({skipped.Count} locked, skipped)."
                    : $"All non-locked mods are already disabled ({skipped.Count} locked, skipped).");
            else
                Say(StatusArea.Mods, enable ? "All mods are already enabled." : "All mods are already disabled.");
            return;
        }
        var verb = enable ? "Enable" : "Disable";
        const int previewCount = 8;
        var preview = string.Join("\n", targets.Take(previewCount).Select(e => "  • " + FileOrDisplayName(e)));
        if (targets.Count > previewCount)
            preview += $"\n  …and {targets.Count - previewCount} more";
        var skippedNote = skipped.Count > 0
            ? $"\n\n🔒 Skipping {skipped.Count} locked mod" + (skipped.Count == 1 ? "" : "s")
              + " — they'll keep their current state."
            : "";
        if (!Prompt.Confirm($"{verb} all — confirm",
                $"{verb} all {targets.Count} {(enable ? "disabled" : "enabled")} mods?\n\n"
                + preview + skippedNote + "\n\n"
                + "Each mod's .vmz will be moved between <mods>/ and <mods>/Disabled/. "
                + "You can undo with the opposite bulk action.",
                PromptKind.Warning))
            return;

        var failed = 0;
        foreach (var e in targets)
            if (!ToggleModFiles(e)) failed++;
        // One cfg save for the batch: one .bak rotation even for 50 toggles.
        SaveModConfigSafely();
        Refresh();
        if (failed > 0)
            Prompt.Inform("Bulk toggle finished with errors",
                $"{failed} of {targets.Count} mods couldn't be toggled. "
                + "(Likely because the .vmz file in mods/Disabled/ is "
                + "locked, or a mod has no mod_id in its mod.txt.)",
                PromptKind.Warning);
    }

    // ── load order ────────────────────────────────────────────────────

    private string PriorityPromptTail()
        => "Lower numbers load earlier; default is 0. Negative values are fine "
           + "(e.g. -100 to pin above everything else).\n\n"
           + $"Writes to mod_config.cfg's [profile.{Config.ActiveProfile}.priority] block — "
           + "the same key the in-game loader UI edits.";

    /// <summary>Asks for a mod's new load-order priority and applies it
    /// (the "Set priority" menu action).</summary>
    public bool PromptModPriority(ModEntry e)
    {
        var input = Prompt.AskText("Set priority",
            $"Enter the load-order priority for `{e.DisplayName}`.\n\n" + PriorityPromptTail(),
            e.Priority.ToString());
        if (input == null) return false;
        if (!int.TryParse(input.Trim(), out var newPriority))
        {
            Prompt.Inform("Invalid input",
                $"Couldn't parse `{input}` as an integer. Priority must "
                + "be a whole number (positive, negative, or zero).",
                PromptKind.Warning);
            return false;
        }
        if (newPriority == e.Priority)
        {
            Say(StatusArea.Mods, $"`{e.DisplayName}` priority is already {newPriority}.");
            return false;
        }
        return SetModPriority(e, newPriority);
    }

    /// <summary>The Priority cell edited in place. Returns false when
    /// nothing was written, so the front end puts the old value back
    /// (invalid text, no mod_id, or the cfg couldn't be saved).</summary>
    public bool SetModPriorityFromText(ModEntry e, string raw)
    {
        raw = (raw ?? "").Trim();
        if (!int.TryParse(raw, out var newPriority))
        {
            Prompt.Inform("Invalid priority",
                $"`{raw}` isn't a valid priority. Must be a whole "
                + "integer (negative is fine). Reverting.",
                PromptKind.Warning);
            return false;
        }
        if (newPriority == e.Priority) return true;
        return SetModPriority(e, newPriority);
    }

    /// <summary>Writes a mod's load-order priority into mod_config.cfg's
    /// [profile.&lt;active&gt;.priority] section. NOT mod.txt: the in-game
    /// loader's cfg overrides any [mod] priority, so editing mod.txt would
    /// be a no-op against the game.</summary>
    public bool SetModPriority(ModEntry e, int newPriority)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            Prompt.Inform("Can't set priority",
                $"`{Path.GetFileName(e.Path)}` has no mod_id in its mod.txt — "
                + "can't write a priority entry without an ID to key off.",
                PromptKind.Warning);
            return false;
        }
        var oldPriority = e.Priority;
        Config.SetPriority(e.ModId, e.Version, newPriority);
        if (!SaveModConfigSafely()) return false;
        Say(StatusArea.Mods, $"`{e.DisplayName}` priority {oldPriority} → {newPriority}.");
        Rescan();
        // Captured in the profile before a later switch rewrites the cfg.
        SyncActiveProfileFromEntry(e.ModId);
        // Re-detect: a priority change can resolve dependency_order conflicts.
        Requery();
        return true;
    }

    /// <summary>Asks once for a priority and applies it to every given
    /// mod (the multi-selection action).</summary>
    public bool PromptBulkSetPriority(IReadOnlyList<ModEntry> entries)
    {
        if (entries.Count == 0) return false;
        // The first mod's priority is the default: handy when one already
        // has the value the others should inherit.
        var input = Prompt.AskText("Set priority for selected mods",
            $"Enter the load-order priority to apply to {entries.Count} selected mod(s).\n\n"
            + PriorityPromptTail(),
            entries[0].Priority.ToString());
        if (input == null) return false;
        if (!int.TryParse(input.Trim(), out var newPriority))
        {
            Prompt.Inform("Invalid input",
                $"Couldn't parse `{input}` as an integer. Priority must be a whole number.",
                PromptKind.Warning);
            return false;
        }
        return BulkSetPriority(entries, newPriority);
    }

    /// <summary>Sets one priority on every given mod via mod_config.cfg,
    /// with one save, mirrored into the active profile.</summary>
    public bool BulkSetPriority(IReadOnlyList<ModEntry> entries, int newPriority)
    {
        if (entries.Count == 0) return false;
        var changed = 0;
        var skipped = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) { skipped++; continue; }
            Config.SetPriority(e.ModId, e.Version, newPriority);
            changed++;
        }
        if (changed == 0)
        {
            Say(StatusArea.Mods, "No mods updated (selected entries have no mod_id).");
            return false;
        }
        if (!SaveModConfigSafely()) return false;

        if (ActiveProfile != null)
        {
            var dirty = false;
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.ModId)) continue;
                var pm = ActiveProfile.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModId, e.ModId, StringComparison.OrdinalIgnoreCase));
                if (pm == null) continue;
                if (pm.Priority != newPriority) { pm.Priority = newPriority; dirty = true; }
            }
            if (dirty)
            {
                ActiveProfile.UpdatedAt = DateTime.UtcNow;
                try { ActiveProfile.SaveMetadataOnly(); } catch { /* best-effort */ }
            }
        }

        Refresh();
        var skipNote = skipped > 0 ? $" ({skipped} skipped — no mod_id)" : "";
        Say(StatusArea.Mods, $"Set priority = {newPriority} on {changed} mod(s){skipNote}.");
        return true;
    }

    /// <summary>The priority changes a drag would make. `displayed` is the
    /// list in display order (ModListView.Displayed); `moved` are taken
    /// out and land as a block, in their displayed order, next to the row
    /// at `targetIndex` (the row they were dropped on; negative or too
    /// large means the last): after it when dragged down, before it when
    /// dragged up. The first moved mod gets a priority that fits after its
    /// new upper neighbour (or before the lower one at the top); each
    /// further moved mod the next number; then rows below are bumped by
    /// one only until there is room again, so unrelated manual priorities
    /// (a -100 pin, say) survive. Moved mods without a mod_id stay put.
    /// Pure: nothing is written.</summary>
    public static IReadOnlyList<PriorityChange> PlanDragReorder(
        IReadOnlyList<ModEntry> displayed, IReadOnlyList<ModEntry> moved, int targetIndex)
    {
        var none = Array.Empty<PriorityChange>();
        if (displayed.Count == 0) return none;
        var movedSet = new HashSet<ModEntry>(ReferenceEqualityComparer.Instance);
        foreach (var m in moved)
            if (!string.IsNullOrEmpty(m.ModId) && displayed.Contains(m)) movedSet.Add(m);
        if (movedSet.Count == 0) return none;
        // Keep the block in its displayed order.
        var block = displayed.Where(movedSet.Contains).ToList();

        if (targetIndex < 0 || targetIndex >= displayed.Count) targetIndex = displayed.Count - 1;
        var target = displayed[targetIndex];
        if (movedSet.Contains(target)) return none;  // dropped on itself
        var newOrder = displayed.Where(e => !movedSet.Contains(e)).ToList();
        // Dragged down the block lands after the target row, dragged up
        // before it: where a single row lands in the WinForms grid.
        var firstIdx = 0;
        while (!ReferenceEquals(displayed[firstIdx], block[0])) firstIdx++;
        var draggingDown = firstIdx < targetIndex;
        var insertAt = newOrder.IndexOf(target) + (draggingDown ? 1 : 0);
        newOrder.InsertRange(insertAt, block);
        if (newOrder.SequenceEqual(displayed, ReferenceEqualityComparer.Instance)) return none;

        var current = new Dictionary<ModEntry, int>(ReferenceEqualityComparer.Instance);
        foreach (var e in newOrder) current[e] = e.Priority;

        int? aboveP = insertAt > 0 ? newOrder[insertAt - 1].Priority : null;
        var belowIdx = insertAt + block.Count;
        int? belowP = belowIdx < newOrder.Count ? newOrder[belowIdx].Priority : null;
        int newP;
        if (aboveP == null && belowP == null) newP = 0;
        else if (aboveP == null) newP = belowP!.Value - block.Count;
        else newP = aboveP.Value + 1;

        var changes = new List<PriorityChange>();
        void Set(ModEntry e, int p)
        {
            if (current[e] != p) changes.Add(new PriorityChange(e, e.Priority, p));
            current[e] = p;
        }
        var wantP = newP;
        foreach (var e in block) Set(e, wantP++);
        // Bump the rows below until one already sits high enough.
        for (var i = belowIdx; i < newOrder.Count; i++)
        {
            var e = newOrder[i];
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (current[e] >= wantP) break;
            Set(e, wantP);
            wantP++;
        }
        return changes;
    }

    /// <summary>Applies a drag reorder (see PlanDragReorder) to
    /// mod_config.cfg and mirrors every displayed mod's priority into
    /// the active profile. `displayed` is ModListView.Displayed.</summary>
    public bool DragReorder(IReadOnlyList<ModEntry> displayed, IReadOnlyList<ModEntry> moved, int targetIndex)
    {
        if (moved.Count > 0 && moved.All(m => string.IsNullOrEmpty(m.ModId)))
        {
            Say(StatusArea.Mods, "Can't reorder — mod has no mod_id.");
            return false;
        }
        var changes = PlanDragReorder(displayed, moved, targetIndex);
        if (changes.Count == 0) return false;
        foreach (var c in changes)
            Config.SetPriority(c.Entry.ModId, c.Entry.Version, c.NewPriority);
        if (!SaveModConfigSafely()) return false;

        // Mirror into the active profile so cfg and profile.json agree.
        if (ActiveProfile != null)
        {
            var newP = new Dictionary<ModEntry, int>(ReferenceEqualityComparer.Instance);
            foreach (var c in changes) newP[c.Entry] = c.NewPriority;
            var dirty = false;
            foreach (var e in displayed)
            {
                if (string.IsNullOrEmpty(e.ModId)) continue;
                var p = newP.TryGetValue(e, out var np) ? np : e.Priority;
                var pm = ActiveProfile.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModId, e.ModId, StringComparison.OrdinalIgnoreCase));
                if (pm != null && pm.Priority != p)
                {
                    pm.Priority = p;
                    dirty = true;
                }
            }
            if (dirty)
            {
                ActiveProfile.UpdatedAt = DateTime.UtcNow;
                try { ActiveProfile.SaveMetadataOnly(); } catch { /* best-effort */ }
            }
        }

        var first = changes.FirstOrDefault(c => moved.Contains(c.Entry)) ?? changes[0];
        Refresh();
        Say(StatusArea.Mods, moved.Count == 1
            ? $"Reordered `{first.Entry.DisplayName}` to priority {first.NewPriority}."
            : $"Reordered {moved.Count} mods (from priority {first.NewPriority}).");
        return true;
    }

    // ── lock / testing / notes ────────────────────────────────────────

    /// <summary>Locks or unlocks a mod. Locked mods are skipped by Enable
    /// all / Disable all and stay through profile switches. A built-in
    /// mod is always kept, so there is nothing to toggle.</summary>
    public bool ToggleLock(ModEntry e)
    {
        if (IsBuiltInActive(e.ModId)) return false;
        if (string.IsNullOrEmpty(e.ModId))
        {
            Prompt.Inform("Can't lock",
                $"`{Path.GetFileName(e.Path)}` has no mod_id in its mod.txt — "
                + "can't lock without an ID to key off.");
            return false;
        }
        if (!Settings.LockedMods.Remove(e.ModId))
            Settings.LockedMods.Add(e.ModId);
        SaveSettingsForMods();
        RaiseChanged();
        return true;
    }

    /// <summary>Adds or removes every given mod's lock, saving once.</summary>
    public void BulkSetLocked(IReadOnlyList<ModEntry> entries, bool lockThem)
    {
        if (entries.Count == 0) return;
        var changed = 0;
        var skipped = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) { skipped++; continue; }
            var isLocked = Settings.LockedMods.Contains(e.ModId);
            if (lockThem)
            {
                if (isLocked) continue;
                Settings.LockedMods.Add(e.ModId);
                changed++;
            }
            else
            {
                if (!isLocked) continue;
                Settings.LockedMods.Remove(e.ModId);
                changed++;
            }
        }
        if (changed == 0)
        {
            Say(StatusArea.Mods, lockThem
                ? "All selected mods were already locked."
                : "No selected mods were locked.");
            return;
        }
        SaveSettingsForMods();
        RaiseChanged();
        var skipNote = skipped > 0 ? $" ({skipped} skipped — no mod_id)" : "";
        Say(StatusArea.Mods, lockThem
            ? $"Locked {changed} mod(s){skipNote}."
            : $"Unlocked {changed} mod(s){skipNote}.");
    }

    /// <summary>Flags or unflags a mod as a Testing Mod: a visual marker
    /// only (the yellow row).</summary>
    public bool ToggleTesting(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId))
        {
            Prompt.Inform("Can't flag as testing",
                $"`{Path.GetFileName(e.Path)}` has no mod_id in its mod.txt — "
                + "can't flag without an ID to key off.");
            return false;
        }
        if (!Settings.TestingMods.Remove(e.ModId))
            Settings.TestingMods.Add(e.ModId);
        SaveSettingsForMods();
        RaiseChanged();
        return true;
    }

    /// <summary>The user's personal note on a mod, or "".</summary>
    public string GetModNote(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId) && Settings.ModNotes.TryGetValue(e.ModId, out var n) ? n : "";

    /// <summary>Sets (or, with blank text, removes) a mod's personal
    /// note, kept in settings.json.</summary>
    public bool SetModNote(ModEntry e, string? text)
    {
        if (string.IsNullOrEmpty(e.ModId)) return false;
        var trimmed = (text ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            Settings.ModNotes.Remove(e.ModId);
        else
            Settings.ModNotes[e.ModId] = trimmed;
        SaveSettingsForMods();
        RaiseChanged();
        return true;
    }

    /// <summary>Asks for a mod's note (multi-line) and stores it.</summary>
    public bool EditModNote(ModEntry e)
    {
        if (string.IsNullOrEmpty(e.ModId)) return false;
        var input = Prompt.AskText("Edit mod note",
            $"Personal note for `{e.DisplayName}`. Stored locally in settings.json; "
            + "appears as a tooltip on the Mod column. Leave blank and click OK to "
            + "remove an existing note.",
            GetModNote(e), multiline: true);
        if (input == null) return false;
        return SetModNote(e, input);
    }

    // ── delete ────────────────────────────────────────────────────────

    /// <summary>Deletes several mods after ONE confirmation: each goes to
    /// the Recycle Bin, loses its cfg entry, its lock and its place in
    /// the active profile. Library copies are KEPT (asking per mod for
    /// that mid-batch would be obnoxious). One failure doesn't stop the
    /// rest; failures are reported together at the end. A single mod
    /// goes through DeleteMod's two-stage flow instead.</summary>
    public void DeleteMods(IReadOnlyList<ModEntry> entries)
    {
        if (entries.Any(e => IsBuiltInActive(e.ModId)))
        {
            entries = entries.Where(e => !IsBuiltInActive(e.ModId)).ToList();
            Say(StatusArea.Mods, "Save Slots is built into the manager and stays; "
                + "turn it off in Settings → Built-in save slots.");
        }
        if (entries.Count == 0) return;
        if (entries.Count == 1) { DeleteMod(entries[0]); return; }

        var profile = ActiveProfile;
        const int previewMax = 10;
        var preview = string.Join("\n", entries.Take(previewMax).Select(e => $"  • {e.DisplayName}"));
        if (entries.Count > previewMax)
            preview += $"\n  …and {entries.Count - previewMax} more";
        var lines = new List<string>
        {
            $"Send the following {entries.Count} mods to the Recycle Bin?",
            "",
            preview,
            "",
            profile != null
                ? $"For each: drops the profile '{profile.Name}' entry, recycles the file, "
                  + "clears its mod_config.cfg entry, removes its lock (if any). Library "
                  + "copies are KEPT — delete those individually if you want them gone."
                : "For each: recycles the file and clears its mod_config.cfg entry. "
                  + "Recoverable from the Recycle Bin.",
        };
        if (!Prompt.Confirm($"Delete {entries.Count} mods — confirm", string.Join("\n", lines), PromptKind.Warning))
            return;

        var failures = new List<(string Name, string Reason)>();
        var deleted = 0;
        var profileDirty = false;
        var cfgDirty = false;
        foreach (var e in entries)
        {
            try
            {
                RecycleModPath(e.Path);  // a stale row (already gone) is no error
                if (!string.IsNullOrEmpty(e.ModId))
                {
                    Config.RemoveEntry(e.ModId, e.Version);
                    cfgDirty = true;
                    if (Settings.LockedMods.Remove(e.ModId)) SaveSettingsForMods();
                    if (profile != null
                        && profile.Mods.RemoveAll(m =>
                            string.Equals(m.ModId, e.ModId, StringComparison.OrdinalIgnoreCase)) > 0)
                        profileDirty = true;
                }
                deleted++;
            }
            catch (Exception ex)
            {
                failures.Add((e.DisplayName, ex.Message));
            }
        }
        if (cfgDirty) SaveModConfigSafely();
        if (profileDirty && profile != null)
        {
            profile.UpdatedAt = DateTime.UtcNow;
            try { profile.SaveMetadataOnly(); } catch { /* best-effort */ }
        }

        var noun = $"mod{(deleted == 1 ? "" : "s")}";
        Refresh();
        Say(StatusArea.Mods, failures.Count == 0
            ? $"Deleted {deleted} {noun} (sent to Recycle Bin)."
            : $"Deleted {deleted} {noun}; {failures.Count} failed.");

        if (failures.Count > 0)
        {
            var failPreview = string.Join("\n", failures.Take(previewMax).Select(f => $"  • {f.Name}: {f.Reason}"));
            if (failures.Count > previewMax)
                failPreview += $"\n  …and {failures.Count - previewMax} more";
            Prompt.Inform("Batch delete report",
                $"{deleted} deleted; {failures.Count} failed.\n\n{failPreview}",
                deleted > 0 ? PromptKind.Warning : PromptKind.Error);
        }
    }

    /// <summary>Deletes one mod in two stages. Stage 1 (confirmed): drop
    /// it from the active profile, send its file / folder to the Recycle
    /// Bin, clear its cfg entry and its lock. Stage 2, only with an active
    /// profile and a Library copy of this version: ask whether to recycle
    /// that copy too (other profiles may still want it). Before the
    /// profile model is adopted it is a single "delete" step. Returns
    /// true when stage 1 ran.</summary>
    public bool DeleteMod(ModEntry e)
    {
        if (IsBuiltInActive(e.ModId))
        {
            Prompt.Inform("Built in",
                "Save Slots is built into the manager. Turn it off in Settings → "
                + "Built-in save slots to remove it.");
            return false;
        }
        var fileName = Path.GetFileName(e.Path);
        var sizeNote = "";
        try
        {
            if (e.IsArchive && File.Exists(e.Path))
                sizeNote = $" ({new FileInfo(e.Path).Length / 1024} KiB)";
        }
        catch { /* size is decorative */ }

        var profile = ActiveProfile;
        var stage1Lines = new List<string>();
        if (profile != null)
            stage1Lines.Add($"  • Drops the entry from profile '{profile.Name}'");
        stage1Lines.Add($"  • Sends `{fileName}` to the Recycle Bin");
        stage1Lines.Add("  • Clears its mod_config.cfg entry");
        if (IsLocked(e)) stage1Lines.Add("  • Removes its lock");
        var stage1Tail = profile != null
            ? "\nThe Library copy is KEPT (asked separately after) so "
              + "other profiles can still reference this version."
            : "\nRecoverable from the Recycle Bin if you change your mind. "
              + "The .bak files (if any) are NOT deleted.";
        var headline = profile != null
            ? $"Remove `{e.DisplayName}` from profile '{profile.Name}'?"
            : $"Delete `{e.DisplayName}`{sizeNote}?";
        if (!Prompt.Confirm(profile != null ? "Remove from active profile" : "Delete mod — confirm",
                headline + "\n\n" + string.Join("\n", stage1Lines) + stage1Tail,
                PromptKind.Warning))
            return false;

        try
        {
            if (File.Exists(e.Path) || Directory.Exists(e.Path))
                RecycleModPath(e.Path);
            else
                Prompt.Inform("File missing",
                    $"Couldn't find `{e.Path}` on disk — nothing to recycle. "
                    + "Refreshing the registry to clean up the stale entry.");
        }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't delete mod", ex);
            return false;
        }

        // Clear cfg and lock state so nothing orphaned is left behind.
        if (!string.IsNullOrEmpty(e.ModId))
        {
            Config.RemoveEntry(e.ModId, e.Version);
            SaveModConfigSafely();
            if (Settings.LockedMods.Remove(e.ModId)) SaveSettingsForMods();
        }

        // Drop from the active profile. The Library is untouched here.
        if (profile != null && !string.IsNullOrEmpty(e.ModId))
        {
            var removed = profile.Mods.RemoveAll(m =>
                string.Equals(m.ModId, e.ModId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                profile.UpdatedAt = DateTime.UtcNow;
                try { profile.SaveMetadataOnly(); } catch { /* best-effort */ }
            }
        }

        // ── Stage 2: the Library copy, when there is one.
        if (profile != null && !string.IsNullOrEmpty(e.ModId))
        {
            var libPath = ModLibrary.Find(ModsDir, e.ModId, e.Version);
            if (!string.IsNullOrEmpty(libPath)
                && Prompt.Confirm("Delete from Library?",
                    $"Also delete `{e.DisplayName}` v{e.Version} from the Library?\n\n"
                    + "  • Removes the canonical .vmz copy from "
                    + $"{Path.GetFileName(ModLibrary.LibraryDir(ModsDir))}/ (Recycle Bin)\n"
                    + "  • Other profiles referencing this version won't be able to add "
                    + "the mod back without re-downloading from VostokMods.\n\n"
                    + "Say NO if you might want this version back later."))
            {
                try { RecycleModPath(libPath); }
                catch (Exception ex) { Prompt.Error("Couldn't delete library copy", ex); }
            }
        }

        Refresh();
        Say(StatusArea.Mods, profile != null
            ? $"Removed `{e.DisplayName}` from profile '{profile.Name}'."
            : $"Deleted `{e.DisplayName}` (sent to Recycle Bin).");
        return true;
    }

    // ── install ───────────────────────────────────────────────────────

    /// <summary>True for a dropped path the manager acts on: .vmz
    /// (install), .zip (modpack) or .json (mod list).</summary>
    public static bool IsAcceptedDropPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a drag carries at least one acceptable path, so
    /// the drop cursor should light up.</summary>
    public static bool AcceptsFileDrop(IEnumerable<string> paths) => paths.Any(IsAcceptedDropPath);

    /// <summary>Handles a file drop: the .vmz files are installed here;
    /// the .zip (modpack) and .json (mod list) files come back in the
    /// result, in drop order, for the front end to import one at a time
    /// after this returns.</summary>
    public async Task<DropResult> HandleFileDropAsync(IEnumerable<string> paths, IProgress<InstallProgress>? progress = null)
    {
        var files = paths.ToList();
        var vmz = files.Where(f => f.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)).ToList();
        var jsons = files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        var zips = files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        InstallResult? install = null;
        if (vmz.Count > 0) install = await InstallModFilesAsync(vmz, progress);
        return new DropResult(install, zips, jsons);
    }

    /// <summary>Copies .vmz files into the mods folder, rejecting anything
    /// that isn't a real mod archive and asking before overwriting a file
    /// of the same name. Each mod is also captured in the Library, added
    /// to (or updated in) the active profile, and written to
    /// mod_config.cfg as enabled with its declared priority. One profile
    /// save, one cfg save and one refresh for the whole batch. The
    /// missing-dependency follow-up is the caller's: pass the result's
    /// InstalledModIds on.</summary>
    public async Task<InstallResult> InstallModFilesAsync(
        IEnumerable<string> sources, IProgress<InstallProgress>? progress = null)
    {
        var modsDir = ModsDir;
        if (!Directory.Exists(modsDir))
        {
            Prompt.Inform("Mods folder not found",
                $"Mods folder `{modsDir}` doesn't exist. Set a valid Mods folder above before installing.",
                PromptKind.Warning);
            return InstallResult.Empty;
        }

        var list = sources.ToList();
        var installed = new List<string>();
        var skipped = new List<(string Path, string Reason)>();
        var installedModIds = new List<string>();
        var profileDirty = false;
        var cfgDirty = false;
        var done = 0;

        foreach (var src in list)
        {
            progress?.Report(new InstallProgress(done, list.Count, Path.GetFileName(src)));
            try
            {
                if (!File.Exists(src))
                    throw new FileNotFoundException("File not found.", src);
                if (!src.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Not a .vmz file.");

                // Validate (a zip holding mod.txt) and capture the manifest
                // values the Library, profile and cfg steps need.
                string manifestModId, manifestVersion, manifestDisplayName;
                int manifestPriority;
                ModSource manifestSource;
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(src))
                        throw new InvalidDataException(
                            "Couldn't open as a zip archive — probably corrupt or wrong file type.");
                    if (!arch.HasFile("mod.txt"))
                        throw new InvalidDataException(
                            "No mod.txt inside the archive — doesn't look like a Vostok mod.");
                    manifestModId = arch.ModId;
                    manifestVersion = arch.ModVersion;
                    manifestDisplayName = string.IsNullOrEmpty(arch.ModName) ? arch.ModId : arch.ModName;
                    manifestPriority = arch.ModPriority;
                    manifestSource = arch.Source;
                }

                var dst = Path.Combine(modsDir, Path.GetFileName(src));
                var srcInsideMods = string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst),
                    StringComparison.OrdinalIgnoreCase);
                if (srcInsideMods)
                {
                    // Already in <mods>/: no copy, but it still joins the
                    // Library and the active profile.
                    installed.Add(Path.GetFileName(src));
                }
                else
                {
                    if (File.Exists(dst)
                        && !Prompt.Confirm("File exists",
                            $"`{Path.GetFileName(src)}` already exists in the mods folder. "
                            + "Overwrite the existing file?",
                            PromptKind.Warning))
                    {
                        skipped.Add((src, "exists, user declined overwrite"));
                        continue;
                    }
                    // Off the UI thread so a slow disk doesn't freeze it.
                    await Task.Run(() => File.Copy(src, dst, overwrite: true));
                    installed.Add(Path.GetFileName(src));
                }

                if (!string.IsNullOrEmpty(manifestModId))
                    installedModIds.Add(manifestModId);

                // Library capture, idempotent on (mod_id, version). A
                // failure doesn't block the install: the live file is in place.
                try { ModLibrary.Add(modsDir, dst); }
                catch { /* best-effort; non-fatal */ }

                // Active profile: a new mod lands enabled at its declared
                // priority; an existing entry follows the new version.
                if (ActiveProfile != null && !string.IsNullOrEmpty(manifestModId))
                {
                    var existingPm = ActiveProfile.Mods.FirstOrDefault(m =>
                        string.Equals(m.ModId, manifestModId, StringComparison.OrdinalIgnoreCase));
                    if (existingPm == null)
                    {
                        ActiveProfile.Mods.Add(new ProfileMod
                        {
                            ModId = manifestModId,
                            DisplayName = manifestDisplayName,
                            Version = manifestVersion,
                            IsEnabled = true,
                            Priority = manifestPriority,
                            Source = manifestSource.Key,
                        });
                    }
                    else
                    {
                        existingPm.Version = manifestVersion;
                        existingPm.DisplayName = manifestDisplayName;
                        if (manifestSource.IsValid) existingPm.Source = manifestSource.Key;
                    }
                    profileDirty = true;
                }

                // The cfg row must be explicit: the manager shows a mod
                // with no row as enabled (fallback=true), but the in-game
                // loader leaves it OFF without a real `= true` line.
                if (!string.IsNullOrEmpty(manifestModId))
                {
                    Config.SetEnabled(manifestModId, manifestVersion, true);
                    Config.SetPriority(manifestModId, manifestVersion, manifestPriority);
                    cfgDirty = true;
                }
            }
            catch (Exception ex)
            {
                skipped.Add((src, ex.Message));
            }
            finally
            {
                done++;
            }
        }
        progress?.Report(new InstallProgress(done, list.Count, ""));

        if (profileDirty && ActiveProfile != null)
        {
            ActiveProfile.UpdatedAt = DateTime.UtcNow;
            try { ActiveProfile.SaveMetadataOnly(); }
            catch { /* best-effort; in-memory state still reflects the change */ }
        }
        if (cfgDirty) SaveModConfigSafely();

        // The "just installed" highlight; in memory, clears on relaunch.
        foreach (var id in installedModIds)
            if (!string.IsNullOrEmpty(id)) SessionInstalled.Add(id);

        Refresh();

        if (installed.Count > 0)
            Say(StatusArea.Mods, $"Installed {installed.Count} mod" + (installed.Count == 1 ? "" : "s")
                + (skipped.Count > 0 ? $" ({skipped.Count} skipped)" : "") + ".");
        else if (skipped.Count > 0)
            Say(StatusArea.Mods, $"No mods installed ({skipped.Count} skipped).");

        if (installed.Count > 0)
        {
            // Friendly names from the rescanned registry, else the id.
            var names = new List<string>();
            foreach (var id in installedModIds)
            {
                var entry = Registry.FindById(id);
                names.Add(entry != null && !string.IsNullOrEmpty(entry.DisplayName) ? entry.DisplayName : id);
            }
            if (names.Count == 0) names.AddRange(installed);
            var nameList = string.Join("\n", names.Take(10).Select(n => $"   ✓  {n}"));
            if (names.Count > 10) nameList += $"\n   …and {names.Count - 10} more";
            var msg = $"✓  {installed.Count} mod" + (installed.Count == 1 ? "" : "s")
                + " installed and enabled — highlighted in green in the list.\n\n" + nameList;
            if (skipped.Count > 0)
            {
                var sk = string.Join("\n", skipped.Take(6).Select(s => $"   •  {Path.GetFileName(s.Path)}: {s.Reason}"));
                if (skipped.Count > 6) sk += $"\n   …and {skipped.Count - 6} more";
                msg += $"\n\n{skipped.Count} skipped:\n{sk}";
            }
            Prompt.Inform(installed.Count == 1 ? "Mod installed" : "Mods installed", msg);
        }
        else if (skipped.Count > 0)
        {
            var preview = string.Join("\n", skipped.Take(8).Select(s => $"   •  {Path.GetFileName(s.Path)}: {s.Reason}"));
            if (skipped.Count > 8) preview += $"\n   …and {skipped.Count - 8} more";
            Prompt.Inform("Install report", $"No mods installed; {skipped.Count} skipped.\n\n" + preview,
                PromptKind.Warning);
        }

        return new InstallResult(installed, skipped, installedModIds);
    }

    /// <summary>Installs a .vmz a dialog holds (the Library) through the
    /// full pipeline.</summary>
    public Task<InstallResult> InstallDownloadedModAsync(string path)
        => InstallModFilesAsync(new[] { path });

    /// <summary>Installs a file downloaded from `source`, remembering the
    /// source so the mod can be update-checked. A download arrives under a
    /// throwaway temp name, so it is installed under the file name of the
    /// live mod it replaces, or else under its mod id.</summary>
    public async Task<InstallResult> InstallDownloadedModAsync(string path, ModSource source)
    {
        DownloadedSources.Record(path, source);

        string modId;
        using (var arch = new ModArchive())
            modId = arch.Open(path) ? arch.ModId : "";
        if (string.IsNullOrEmpty(modId))
            return await InstallModFilesAsync(new[] { path });  // let the pipeline say why

        var live = Registry.Entries.FirstOrDefault(e =>
            e.IsArchive
            && string.Equals(e.ModId, modId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(e.Path)),
                Path.GetFullPath(ModsDir), StringComparison.OrdinalIgnoreCase));
        var name = live != null ? Path.GetFileName(live.Path) : ModProfile.SafeFileName(modId) + ".vmz";
        var stageDir = Path.Combine(Path.GetTempPath(), "vmm_in_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stageDir);
            var staged = Path.Combine(stageDir, name);
            File.Copy(path, staged);
            return await InstallModFilesAsync(new[] { staged });
        }
        finally
        {
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { /* temp */ }
        }
    }

    /// <summary>The no-browser fallback: asks for a VostokMods mod URL or
    /// slug, downloads the latest version and installs it. Null when the
    /// user cancelled or the input wasn't a mod.</summary>
    public async Task<InstallResult?> InstallFromVostokModsUrlAsync(IProgress<(long done, long total)>? progress = null)
    {
        var input = Prompt.AskText("Install from VostokMods",
            "Paste a VostokMods mod page URL (e.g. https://vostokmods.net/mod/eventmodifier) or its slug:");
        if (input == null) return null;
        if (!VostokModsUrl.TryParseSlug(input, out var slug))
        {
            Prompt.Inform("Not a mod URL",
                "That isn't a VostokMods mod. Expected a vostokmods.net/mod/<slug> URL or the slug on its own.",
                PromptKind.Warning);
            return null;
        }
        var temp = Path.Combine(Path.GetTempPath(), "vmm_dl_" + Guid.NewGuid().ToString("N") + ".vmz");
        try
        {
            if (ModDownloadOverride != null) await ModDownloadOverride(slug, temp);
            else await Vm.DownloadAsync(slug, temp, progress: progress);
            return await InstallDownloadedModAsync(temp, ModSource.ForSlug(slug));
        }
        catch (Exception ex)
        {
            Prompt.Inform("Install failed", $"Couldn't download or install `{slug}`:\n{ex.Message}", PromptKind.Error);
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp */ }
        }
    }

    // ── VostokMods browser wiring ─────────────────────────────────────

    /// <summary>Fills the UUID → slug map from the VostokMods listing.
    /// Best-effort: offline, the map stays as it was.</summary>
    public async Task LoadSlugMapAsync()
    {
        try { ApplySlugListing(await Vm.ListAllModsAsync()); }
        catch { /* offline — comparisons fall back to the raw ids */ }
    }

    internal void ApplySlugListing(IEnumerable<VmModSummary> mods)
    {
        foreach (var m in mods)
            if (m.Id.Length > 0) VmSlugById[m.Id] = m.Slug;
    }

    /// <summary>The slug form of a source that names its mod by UUID, when
    /// the listing is known; otherwise the source unchanged.</summary>
    public ModSource CanonicalSource(ModSource source)
        => source.IsValid && VmSlugById.TryGetValue(source.Id, out var slug)
            ? ModSource.ForSlug(slug)
            : source;

    /// <summary>The callbacks for the embedded VostokMods browser. The
    /// Library's sources are snapshotted once (listing it opens every
    /// .vmz, too heavy per navigation); live mods are checked per call,
    /// so installs made while browsing count. With `loadSlugMap` the
    /// UUID → slug listing is fetched in the background: until it
    /// arrives, mods whose mod.txt names a UUID just don't show as owned.</summary>
    public ModBrowserHooks PrepareModBrowser(bool loadSlugMap = true)
    {
        var libSources = new HashSet<ModSource>();
        try
        {
            foreach (var l in ModLibrary.List(ModsDir))
            {
                if (l.Source.IsValid) libSources.Add(l.Source);
                var recorded = Config.GetModSource(l.ModId, l.Version);
                if (recorded.IsValid) libSources.Add(recorded);
            }
        }
        catch { /* best-effort — no "already owned" hits */ }

        if (loadSlugMap) _ = LoadSlugMapAsync();

        bool OwnsSource(ModSource source)
        {
            if (!source.IsValid) return false;
            source = CanonicalSource(source);
            return libSources.Any(s => CanonicalSource(s) == source)
                || Registry.Entries.Any(e => CanonicalSource(e.Source) == source);
        }

        return new ModBrowserHooks((path, source) => InstallDownloadedModAsync(path, source), OwnsSource);
    }
}
