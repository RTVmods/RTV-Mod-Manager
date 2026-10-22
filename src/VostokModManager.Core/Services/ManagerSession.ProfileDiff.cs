// Comparing profiles. Ported from Ui/ProfileDiffDialog (two profiles,
// "only in A / only in B / different" rows, copy selected rows across)
// and generalised to a matrix over any number of profiles: one row per
// mod present in any of them, one cell per profile.
//
// Copies transplant a profile entry's metadata (version, priority,
// enabled state, source) into another profile and save it. Bundled
// archives belong to the source profile's own folder, so a copied entry
// is metadata-only and resolves from the Library or VostokMods on apply.

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>The comparison matrix for `profiles` (in that column
    /// order). Mods are matched by mod_id, case-insensitively; entries
    /// without an id are left out. Rows sort the way the two-profile
    /// diff did: only-in-the-first, then different, then only-in-others,
    /// then identical, each by name.</summary>
    public ProfileComparison CompareProfiles(IReadOnlyList<ModProfile> profiles)
    {
        var maps = profiles.Select(p =>
        {
            var map = new Dictionary<string, ProfileMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in p.Mods)
                if (!string.IsNullOrEmpty(m.ModId)) map.TryAdd(m.ModId, m);
            return map;
        }).ToList();

        // Every id, in first-seen order.
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
            foreach (var m in p.Mods)
                if (!string.IsNullOrEmpty(m.ModId) && seen.Add(m.ModId)) ids.Add(m.ModId);

        var rows = new List<(int Order, ProfileMatrixRow Row)>();
        foreach (var id in ids)
        {
            var cells = maps.Select(map => new ProfileMatrixCell(map.GetValueOrDefault(id))).ToList();
            var first = cells.First(c => c.Present).Mod!;
            var name = string.IsNullOrEmpty(first.DisplayName) ? first.ModId : first.DisplayName;
            var (what, order) = DescribeDifference(profiles, cells);
            rows.Add((order, new ProfileMatrixRow(first.ModId, name, cells, what.Length > 0, what)));
        }
        var sorted = rows
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Row)
            .ToList();
        return new ProfileComparison(profiles, sorted);
    }

    /// <summary>What differs in one row, and its sort tier: 0 present in
    /// the first profile but not every one, 1 present everywhere but
    /// different, 2 missing from the first profile, 3 identical. For two
    /// profiles the wording is the old diff dialog's.</summary>
    private static (string What, int Order) DescribeDifference(
        IReadOnlyList<ModProfile> profiles, IReadOnlyList<ProfileMatrixCell> cells)
    {
        var present = cells.Where(c => c.Present).Select(c => c.Mod!).ToList();
        if (present.Count < cells.Count)
        {
            var order = cells[0].Present ? 0 : 2;
            if (cells.Count == 2)
                return (cells[0].Present ? "Only in left profile" : "Only in right profile", order);
            var absent = profiles.Where((_, i) => !cells[i].Present).Select(p => $"'{p.Name}'");
            return ("Not in " + string.Join(", ", absent), order);
        }

        var what = new List<string>();
        if (cells.Count == 2)
        {
            var (a, b) = (present[0], present[1]);
            if (!string.Equals(a.Version, b.Version, StringComparison.Ordinal))
                what.Add($"version {a.Version} → {b.Version}");
            if (a.Priority != b.Priority)
                what.Add($"priority {a.Priority} → {b.Priority}");
            if (a.IsEnabled != b.IsEnabled)
                what.Add(a.IsEnabled ? "enabled → disabled" : "disabled → enabled");
        }
        else
        {
            if (present.Select(m => m.Version).Distinct(StringComparer.Ordinal).Count() > 1)
                what.Add("versions " + string.Join(" / ", present.Select(m => m.Version)));
            if (present.Select(m => m.Priority).Distinct().Count() > 1)
                what.Add("priorities " + string.Join(" / ", present.Select(m => m.Priority)));
            if (present.Select(m => m.IsEnabled).Distinct().Count() > 1)
                what.Add("enabled " + string.Join(" / ", present.Select(m => m.IsEnabled ? "on" : "off")));
        }
        return what.Count == 0 ? ("", 3) : (string.Join(", ", what), 1);
    }

    /// <summary>Copies the entries for `modIds` from one profile to
    /// another: a mod `to` lacks is added (metadata-only), one it has is
    /// overwritten with `from`'s name, version, enabled state, priority
    /// and (when set) source. Ids `from` doesn't list are skipped. Saves
    /// `to` and tells the user the outcome. Returns how many were copied.</summary>
    public int CopyProfileEntries(ModProfile from, ModProfile to, IEnumerable<string> modIds)
    {
        var ids = modIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        if (ids.Count == 0)
        {
            Prompt.Inform("Nothing to copy", "Pick at least one mod first.");
            return 0;
        }
        var copied = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            var srcPm = FindProfileMod(from, id);
            if (srcPm == null) { skipped++; continue; }   // can't copy what isn't there
            var dstPm = FindProfileMod(to, id);
            if (dstPm == null)
            {
                to.Mods.Add(new ProfileMod
                {
                    ModId           = srcPm.ModId,
                    DisplayName     = srcPm.DisplayName,
                    Version         = srcPm.Version,
                    IsEnabled       = srcPm.IsEnabled,
                    Priority        = srcPm.Priority,
                    Source          = srcPm.Source,
                    ArchiveFileName = "",
                });
            }
            else
            {
                dstPm.DisplayName = srcPm.DisplayName;
                dstPm.Version     = srcPm.Version;
                dstPm.IsEnabled   = srcPm.IsEnabled;
                dstPm.Priority    = srcPm.Priority;
                if (srcPm.SourceRef.IsValid) dstPm.Source = srcPm.Source;
            }
            copied++;
        }
        if (copied == 0)
        {
            Prompt.Inform("Copy skipped",
                $"Nothing was copied. {skipped} row(s) skipped — the source side was absent "
                + "(can't copy what isn't there).");
            return 0;
        }
        SaveCopiedProfile(to);

        var skipNote = skipped > 0 ? $" ({skipped} skipped — source absent)" : "";
        Prompt.Inform("Copy done",
            $"Copied {copied} entr{(copied == 1 ? "y" : "ies")} '{from.Name}' → '{to.Name}'{skipNote}.");
        return copied;
    }

    /// <summary>Moves `to`'s entries for `modIds` to the version `from`
    /// pins, leaving their enabled state and priority alone. Ids missing
    /// on either side are skipped. Returns how many changed.</summary>
    public int CopyProfileVersions(ModProfile from, ModProfile to, IEnumerable<string> modIds)
    {
        var changed = 0;
        foreach (var id in modIds)
        {
            var srcPm = FindProfileMod(from, id);
            var dstPm = FindProfileMod(to, id);
            if (srcPm == null || dstPm == null) continue;
            if (string.Equals(srcPm.Version, dstPm.Version, StringComparison.Ordinal)) continue;
            dstPm.Version = srcPm.Version;
            if (!string.IsNullOrEmpty(srcPm.DisplayName)) dstPm.DisplayName = srcPm.DisplayName;
            if (!dstPm.SourceRef.IsValid && srcPm.SourceRef.IsValid) dstPm.Source = srcPm.Source;
            // The bundle it had is the old version.
            dstPm.ArchiveFileName = "";
            changed++;
        }
        if (changed == 0) return 0;
        SaveCopiedProfile(to);
        Say(StatusArea.Profiles, $"Moved {changed} mod(s) in '{to.Name}' to the versions '{from.Name}' uses.");
        return changed;
    }

    /// <summary>Removes the entries for `modIds` from a profile (after
    /// the usual confirmation). Returns false when nothing was removed.</summary>
    public bool RemoveProfileEntries(ModProfile profile, IEnumerable<string> modIds, bool confirm = true)
    {
        var mods = modIds
            .Select(id => FindProfileMod(profile, id))
            .Where(m => m != null)
            .Cast<ProfileMod>()
            .Distinct()
            .ToList();
        return RemoveModsFromProfile(profile, mods, confirm);
    }

    private static ProfileMod? FindProfileMod(ModProfile profile, string modId)
        => profile.Mods.FirstOrDefault(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));

    private void SaveCopiedProfile(ModProfile to)
    {
        to.UpdatedAt = DateTime.UtcNow;
        try { to.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            // In-memory state still reflects the change.
            Prompt.Inform("Save failed",
                $"Saved copies to memory but couldn't write profile.json:\n\n{ex.Message}", PromptKind.Warning);
        }
        Requery();
    }
}
