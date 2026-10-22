// Drift: where the live mods and the active profile disagree, and the
// "save the live state into the profile" sync. (Applying a profile is
// the other direction.)

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>Every point where the active profile and the live
    /// registry disagree:
    ///   • toggle   — enabled/disabled flipped
    ///   • priority — load-order number changed
    ///   • version  — the file on disk is another version than recorded
    ///   • missing  — the profile lists a mod that isn't live
    ///   • adopted  — a live mod the profile doesn't list yet
    /// Sorted by name; empty when they agree (or there's no profile).</summary>
    public List<DriftItem> ComputeDrift()
    {
        var result = new List<DriftItem>();
        if (ActiveProfile == null) return result;

        var liveById = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) liveById[e.ModId] = e;

        foreach (var pm in ActiveProfile.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            var name = !string.IsNullOrEmpty(pm.DisplayName) ? pm.DisplayName : pm.ModId;
            if (!liveById.TryGetValue(pm.ModId, out var live))
            {
                result.Add(new DriftItem(pm.ModId, name,
                    "in profile but not currently live (Apply will re-fetch)"));
                continue;
            }
            if (pm.IsEnabled != live.IsEnabled)
                result.Add(new DriftItem(pm.ModId, name,
                    pm.IsEnabled ? "live: disabled (profile: enabled)" : "live: enabled (profile: disabled)"));
            if (pm.Priority != live.Priority)
                result.Add(new DriftItem(pm.ModId, name, $"priority: profile {pm.Priority} → live {live.Priority}"));
            if (!string.IsNullOrEmpty(pm.Version)
                && !string.IsNullOrEmpty(live.Version)
                && !string.Equals(pm.Version, live.Version, StringComparison.Ordinal))
                result.Add(new DriftItem(pm.ModId, name, $"version: profile v{pm.Version} → live v{live.Version}"));
        }
        // Live mods not in the profile. Rescan normally adopts them; the
        // check surfaces the gap before that runs.
        var profileIds = new HashSet<string>(ActiveProfile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (profileIds.Contains(e.ModId)) continue;
            var name = !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : e.ModId;
            result.Add(new DriftItem(e.ModId, name, "live mod not in profile (will be added on sync)"));
        }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>The drift status line, or null when there is nothing to
    /// show (no profile, no drift). The text previews the first two
    /// changes so the user knows WHAT changed without opening the list.</summary>
    public DriftStatus? GetDriftStatus()
    {
        if (ActiveProfile == null) return null;
        var drift = ComputeDrift();
        if (drift.Count == 0) return null;
        var preview = string.Join("; ", drift.Take(2).Select(d => $"{d.DisplayName} ({d.Description})"));
        if (drift.Count > 2) preview += $"; +{drift.Count - 2} more";
        return new DriftStatus(ActiveProfile.Name, drift,
            $"⚠ Drift ({drift.Count}) vs '{ActiveProfile.Name}': " + preview + "  —  click for details.");
    }

    /// <summary>Writes each live mod's enabled / priority / version into
    /// the active profile, adding entries for live mods it doesn't list
    /// — the "I tweaked the live state, save it back" shortcut. `review`
    /// is shown the exact drift first (the drift detail view) and can
    /// cancel by returning false. Returns the number of fields changed,
    /// 0 when there was nothing to sync or the user cancelled, -1 when
    /// the profile couldn't be saved.</summary>
    public int SyncLiveStateIntoActiveProfile(Func<IReadOnlyList<DriftItem>, bool>? review = null)
    {
        if (ActiveProfile == null) return 0;

        // Show the EXACT list first: committing a sync without knowing
        // what it changes is how state-sync flows go wrong.
        var drift = ComputeDrift();
        if (drift.Count == 0)
        {
            Say(StatusArea.Mods, "No drift detected — nothing to sync.");
            RaiseChanged();
            return 0;
        }
        if (review != null && !review(drift)) return 0;

        var changed = 0;
        var byId = ActiveProfile.Mods.ToDictionary(m => m.ModId, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (byId.TryGetValue(e.ModId, out var pm))
            {
                if (pm.IsEnabled != e.IsEnabled) { pm.IsEnabled = e.IsEnabled; changed++; }
                if (pm.Priority != e.Priority) { pm.Priority = e.Priority; changed++; }
                if (!string.IsNullOrEmpty(e.Version)
                    && !string.Equals(pm.Version, e.Version, StringComparison.Ordinal))
                { pm.Version = e.Version; changed++; }
            }
            else
            {
                ActiveProfile.Mods.Add(new ProfileMod
                {
                    ModId = e.ModId,
                    DisplayName = e.DisplayName,
                    Version = e.Version,
                    IsEnabled = e.IsEnabled,
                    Priority = e.Priority,
                    Source = e.Source.Key,
                });
                changed++;
            }
        }
        if (changed == 0)
        {
            Say(StatusArea.Mods, "No drift detected — nothing to sync.");
            RaiseChanged();
            return 0;
        }
        ActiveProfile.UpdatedAt = DateTime.UtcNow;
        try { ActiveProfile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't save profile", ex);
            return -1;
        }
        Say(StatusArea.Mods, $"Synced {changed} change(s) into '{ActiveProfile.Name}'.");
        RaiseChanged();
        return changed;
    }
}
