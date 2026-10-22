// The mod Library (ported from Ui/LibraryDialog).
//
// The library (<mods>/Library/) is the canonical store of every .vmz the
// user has ever added — one file per (mod_id, version), so multiple
// versions of the same mod can coexist. The live mods folder is just the
// active profile's working copy; the library survives profile switches
// and deletions.
//
// LibraryModel is the data behind the Library view: every library entry
// with its live/enabled state, the saved profiles that include it, size
// and VostokMods source; sorting, the search filter, delete, and the
// "Clean up old versions" rule. Installing a picked version into the
// live folder is the install pipeline's job: the view hands it
// PathsToInstall(selection).

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>Opens the Library view's model, loaded and sorted by
    /// mod name.</summary>
    public LibraryModel OpenLibrary() => new(this);
}

/// <summary>A library column the view can sort by.</summary>
public enum LibrarySortColumn { Mod, Version, State, Profiles, Size, Source }

/// <summary>How a library entry relates to what is live.</summary>
public enum LibraryLiveState
{
    /// <summary>No mod with this id is live.</summary>
    NotInstalled,
    /// <summary>A different mod that happens to share the id is live.</summary>
    OtherModSameId,
    /// <summary>This exact version is live and enabled.</summary>
    LiveEnabled,
    /// <summary>This exact version is live but disabled.</summary>
    LiveDisabled,
    /// <summary>Another version of this mod is live.</summary>
    OtherVersionLive,
}

/// <summary>One row of the Library view. `ProfilesText` is the short
/// form ("A, B +3"); `Profiles` is the full list.</summary>
public sealed record LibraryRow(
    ModLibrary.Entry Entry,
    string Name,
    string Version,
    LibraryLiveState State,
    string StateText,
    IReadOnlyList<string> Profiles,
    string ProfilesText,
    long SizeBytes,
    string SizeText,
    string SourceText);

/// <summary>What "Clean up old versions" would delete, and how much
/// space it frees.</summary>
public sealed record LibraryCleanupPlan(IReadOnlyList<ModLibrary.Entry> Entries, long Bytes)
{
    public bool IsEmpty => Entries.Count == 0;
}

public sealed class LibraryModel
{
    private readonly ManagerSession _session;

    private List<ModLibrary.Entry> _entries = new();
    /// <summary>mod_id → names of saved profiles that include it.</summary>
    private Dictionary<string, List<string>> _profilesByMod = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"mod_id@version" of every entry in any saved profile: the
    /// versions a clean-up must keep so a later profile switch still
    /// finds its file.</summary>
    private HashSet<string> _pinnedVersions = new(StringComparer.OrdinalIgnoreCase);

    internal LibraryModel(ManagerSession session)
    {
        _session = session;
        Reload();
    }

    public LibrarySortColumn SortColumn { get; private set; } = LibrarySortColumn.Mod;
    public bool SortAscending { get; private set; } = true;

    /// <summary>The search box. Every word typed must appear somewhere
    /// in the name, id, version or source; empty matches everything.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Every library entry, in the current sort order.</summary>
    public IReadOnlyList<ModLibrary.Entry> Entries => _entries;

    /// <summary>The library folder.</summary>
    public string LibraryDir => ModLibrary.LibraryDir(_session.ModsDir);

    /// <summary>Bytes held by every entry (filtered or not).</summary>
    public long TotalBytes => _entries.Sum(e => e.SizeBytes);

    /// <summary>A one-off note from the last clean-up, shown before the
    /// header ("Cleaned up 3 version(s), 1.2 MB freed."). Cleared by
    /// the next Reload.</summary>
    public string Notice { get; private set; } = "";

    /// <summary>The rows the filter lets through, in sort order.</summary>
    public IReadOnlyList<LibraryRow> Rows
        => _entries.Where(MatchesFilter).Select(ToRow).ToList();

    /// <summary>The view's header: the filtered count next to the total
    /// so a narrow filter is obvious.</summary>
    public string HeaderText
    {
        get
        {
            var shown = _entries.Count(MatchesFilter);
            var shownNote = shown == _entries.Count ? "" : $"  ·  {shown} shown";
            var header = $"{_entries.Count} archived version(s){shownNote}  ·  {FormatSize(TotalBytes)} total  ·  "
                + $"in {LibraryDir}";
            return Notice.Length > 0 ? Notice + "  ·  " + header : header;
        }
    }

    /// <summary>True when "Clean up old versions" has anything to look at.</summary>
    public bool CanCleanUp => _entries.Count > 0;

    /// <summary>True once a delete or clean-up removed a file.</summary>
    public bool Changed { get; private set; }

    // ── data ──────────────────────────────────────────────────────────

    /// <summary>Re-reads the library folder and every saved profile.</summary>
    public void Reload()
    {
        try { _entries = ModLibrary.List(_session.ModsDir); }
        catch { _entries = new(); }
        BuildProfilesMap();
        _entries = SortEntries(_entries);
        Notice = "";
    }

    /// <summary>A header click: the same column again flips the
    /// direction, another column sorts ascending by it.</summary>
    public void SortBy(LibrarySortColumn column)
    {
        if (SortColumn == column) SortAscending = !SortAscending;
        else { SortColumn = column; SortAscending = true; }
        Reload();
    }

    /// <summary>True when the entry matches the search filter.</summary>
    public bool MatchesFilter(ModLibrary.Entry e)
    {
        var q = Filter?.Trim() ?? "";
        if (q.Length == 0) return true;
        var hay = $"{e.DisplayName} {e.ModId} {e.Version} {e.Source.Id}";
        foreach (var word in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!hay.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Builds the mod_id → profile-names map from all saved
    /// profiles, so the "In profiles" column shows which loadouts
    /// include each library mod (matched by mod_id, version-agnostic).</summary>
    private void BuildProfilesMap()
    {
        _profilesByMod = new(StringComparer.OrdinalIgnoreCase);
        _pinnedVersions = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in ModProfile.LoadAll())
            {
                foreach (var m in p.Mods)
                {
                    if (string.IsNullOrEmpty(m.ModId)) continue;
                    if (!string.IsNullOrEmpty(m.Version))
                        _pinnedVersions.Add(m.ModId + "@" + m.Version);
                    if (!_profilesByMod.TryGetValue(m.ModId, out var list))
                        _profilesByMod[m.ModId] = list = new List<string>();
                    if (!list.Contains(p.Name)) list.Add(p.Name);
                }
            }
        }
        catch { /* best-effort — an empty map just shows "—" */ }
    }

    /// <summary>Names of the saved profiles that include this entry's mod.</summary>
    public IReadOnlyList<string> ProfilesFor(ModLibrary.Entry e)
        => _profilesByMod.TryGetValue(e.ModId, out var l) ? l : Array.Empty<string>();

    public int ProfileCount(ModLibrary.Entry e) => ProfilesFor(e).Count;

    public string ProfilesText(ModLibrary.Entry e)
    {
        var l = ProfilesFor(e);
        if (l.Count == 0) return "—";
        if (l.Count <= 2) return string.Join(", ", l);
        return $"{l[0]}, {l[1]} +{l.Count - 2}";
    }

    /// <summary>True when a saved profile names this exact version.</summary>
    public bool IsPinned(ModLibrary.Entry e) => _pinnedVersions.Contains(e.ModId + "@" + e.Version);

    private static string NameOf(ModLibrary.Entry e)
        => string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName;

    private List<ModLibrary.Entry> SortEntries(List<ModLibrary.Entry> src)
    {
        var byVersion = Comparer<string>.Create((a, b) => ModRegistry.CompareVersions(a, b));
        IEnumerable<ModLibrary.Entry> q = SortColumn switch
        {
            LibrarySortColumn.Version => src.OrderBy(e => e.Version, byVersion),
            LibrarySortColumn.State => src.OrderBy(e => StateOf(e).Text, StringComparer.OrdinalIgnoreCase),
            // Most-used first feels natural; then by name.
            LibrarySortColumn.Profiles => src.OrderByDescending(ProfileCount)
                .ThenBy(NameOf, StringComparer.OrdinalIgnoreCase),
            LibrarySortColumn.Size => src.OrderBy(e => e.SizeBytes),
            LibrarySortColumn.Source => src.OrderBy(e => e.Source.Id, StringComparer.OrdinalIgnoreCase),
            _ => src.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(e => e.Version, byVersion),
        };
        var list = q.ToList();
        if (!SortAscending) list.Reverse();
        return list;
    }

    /// <summary>Cross-references the live registry to label a library
    /// entry: live+enabled, live+disabled, another version live, a
    /// different mod sharing the id, or not installed.</summary>
    public (LibraryLiveState State, string Text) StateOf(ModLibrary.Entry e)
    {
        var live = _session.Registry.FindById(e.ModId);
        if (live == null)
            return (LibraryLiveState.NotInstalled, "Not installed");
        if (!ModLineage.Same(live.Source, live.DisplayName, e.Source, e.DisplayName))
            return (LibraryLiveState.OtherModSameId, "Other mod, same id, live");
        if (ModRegistry.CompareVersions(live.Version, e.Version) == 0)
            return live.IsEnabled
                ? (LibraryLiveState.LiveEnabled, "✓ Live · enabled")
                : (LibraryLiveState.LiveDisabled, "Live · disabled");
        return (LibraryLiveState.OtherVersionLive, $"Other v{live.Version} live");
    }

    private LibraryRow ToRow(ModLibrary.Entry e)
    {
        var (state, text) = StateOf(e);
        return new LibraryRow(
            e, NameOf(e), e.Version, state, text,
            ProfilesFor(e), ProfilesText(e),
            e.SizeBytes, FormatSize(e.SizeBytes),
            e.Source.IsValid ? e.Source.Id : "—");
    }

    // ── actions ───────────────────────────────────────────────────────

    /// <summary>The library files of the picked entries that are still
    /// on disk, in order: what the install pipeline should install.</summary>
    public IReadOnlyList<string> PathsToInstall(IEnumerable<ModLibrary.Entry> selected)
        => selected.Where(e => File.Exists(e.Path)).Select(e => e.Path).ToList();

    /// <summary>Asks, then hard-deletes the picked versions from the
    /// library (never the live copies). Returns how many were removed
    /// (0 when the user said no). Refreshes the session when anything
    /// went, as MainForm did when the dialog closed.</summary>
    public int Delete(IReadOnlyList<ModLibrary.Entry> selected)
    {
        if (selected.Count == 0) return 0;

        var list = string.Join("\n", selected.Take(10).Select(e => $"   •  {NameOf(e)}  v{e.Version}"));
        if (selected.Count > 10) list += $"\n   …and {selected.Count - 10} more";

        var ok = _session.Prompt.Confirm("Delete from library",
            (selected.Count == 1
                ? "Permanently delete this archived version from the library?"
                : $"Permanently delete these {selected.Count} archived versions from the library?")
            + "\n\n" + list
            + "\n\nThis removes the library copy only. Mods currently installed "
            + "live are NOT affected, but you won't be able to restore these "
            + "versions from the library afterwards.",
            PromptKind.Warning);
        if (!ok) return 0;

        var deleted = RemoveAll(selected);
        AfterChange(deleted);
        Reload();
        return deleted;
    }

    /// <summary>The library entries a clean-up would delete: every
    /// version that is not the newest of its lineage, not the version
    /// currently live, and not pinned by any saved profile. Lineage,
    /// not just mod_id, so an abandoned mod that shares an id with its
    /// successor keeps its own newest file.</summary>
    public LibraryCleanupPlan PlanCleanup()
    {
        var old = new List<ModLibrary.Entry>();
        foreach (var group in ModLineage.Group(_entries))
        {
            // group is newest-first; index 0 stays.
            for (int i = 1; i < group.Count; i++)
            {
                var e = group[i];
                if (IsPinned(e)) continue;
                var live = _session.Registry.FindById(e.ModId);
                if (live != null && ModRegistry.CompareVersions(live.Version, e.Version) == 0) continue;
                old.Add(e);
            }
        }
        return new LibraryCleanupPlan(old, old.Sum(e => e.SizeBytes));
    }

    /// <summary>"Clean up old versions": tells the user when there is
    /// nothing to do, else lists the plan, asks, and deletes. Returns
    /// how many versions were removed.</summary>
    public int CleanUpOldVersions()
    {
        var plan = PlanCleanup();
        if (plan.IsEmpty)
        {
            _session.Prompt.Inform("Clean up old versions",
                "Nothing to clean up: every archived version is either the newest "
                + "of its mod, currently live, or pinned by a saved profile.");
            return 0;
        }
        var old = plan.Entries;
        var list = string.Join("\n", old.Take(14).Select(e => $"   •  {NameOf(e)}  v{e.Version}"));
        if (old.Count > 14) list += $"\n   …and {old.Count - 14} more";

        var ok = _session.Prompt.Confirm("Clean up old versions",
            $"Delete {old.Count} old archived version(s) ({FormatSize(plan.Bytes)}) from the library?\n\n"
            + list
            + "\n\nKept: the newest version of each mod, every version that is live, "
            + "and every version a saved profile pins. Mods installed live are not affected.",
            PromptKind.Warning);
        if (!ok) return 0;

        var deleted = RemoveAll(old);
        AfterChange(deleted);
        Reload();
        Notice = $"Cleaned up {deleted} version(s), {FormatSize(plan.Bytes)} freed.";
        return deleted;
    }

    private int RemoveAll(IEnumerable<ModLibrary.Entry> entries)
    {
        var deleted = 0;
        foreach (var e in entries)
        {
            try { ModLibrary.Remove(_session.ModsDir, e.ModId, e.Version); deleted++; }
            catch { /* best-effort, ModLibrary.Remove is already silent */ }
        }
        return deleted;
    }

    private void AfterChange(int deleted)
    {
        if (deleted == 0) return;
        Changed = true;
        _session.Refresh();
    }

    /// <summary>"1.2 MB", "340 KB", or "—" for nothing.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1.0) return $"{mb:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
