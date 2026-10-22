// The data behind the "Required dependencies" picker (ported from
// Ui/DependencyPickerDialog): a checklist of installed mod ids, so the
// user can't mistype an id, plus a manual-add for a mod not installed
// yet. The caller (Set dependencies on a mod) reads Result on OK.

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>A dependency picker for `subject` over the live mods,
    /// pre-ticked with its current required dependencies.</summary>
    public DependencyPicker OpenDependencyPicker(ModEntry subject)
        => new(subject, Registry.Entries, subject.RequiredDependencies);
}

/// <summary>One id in the picker's list, with its tick.</summary>
public sealed record DependencyPickItem(string ModId, bool Checked);

public sealed class DependencyPicker
{
    private readonly List<string> _allModIds;
    private readonly HashSet<string> _checked;

    public DependencyPicker(ModEntry subject, IEnumerable<ModEntry> registry, IEnumerable<string> currentDeps)
    {
        SubjectLabel = DisplayLabel(subject);
        _allModIds = registry
            .Select(e => e.ModId)
            .Where(id => !string.IsNullOrEmpty(id)
                         && !string.Equals(id, subject.ModId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _checked = new HashSet<string>(currentDeps, StringComparer.OrdinalIgnoreCase);
        // Carry forward declared deps that aren't installed — missing on
        // this machine, but the declaration should survive a round-trip.
        foreach (var dep in _checked)
            if (!_allModIds.Contains(dep, StringComparer.OrdinalIgnoreCase))
                _allModIds.Insert(0, dep);
    }

    /// <summary>The subject's name (else id, else file name).</summary>
    public string SubjectLabel { get; }

    public string Title => $"Required dependencies — {SubjectLabel}";

    public string PromptText => $"Tick the mods that `{SubjectLabel}` requires. "
        + "Filter by ID below, or paste a not-yet-installed ID into "
        + "the manual-add box.";

    /// <summary>The filter box: a case-insensitive substring of the id.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Every id offered, manual and missing ones first.</summary>
    public IReadOnlyList<string> AllModIds => _allModIds;

    /// <summary>The ids the filter lets through, with their ticks. Ticks
    /// on hidden ids are kept.</summary>
    public IReadOnlyList<DependencyPickItem> Visible()
    {
        var f = (Filter ?? "").Trim().ToLowerInvariant();
        return _allModIds
            .Where(id => f.Length == 0 || id.ToLowerInvariant().Contains(f))
            .Select(id => new DependencyPickItem(id, _checked.Contains(id)))
            .ToList();
    }

    public bool IsChecked(string modId) => _checked.Contains(modId);

    public void SetChecked(string modId, bool on)
    {
        if (on) _checked.Add(modId); else _checked.Remove(modId);
    }

    /// <summary>Adds a typed id (ticked), listing it first when it is
    /// new. Blank input is ignored. Returns the trimmed id, or "".</summary>
    public string AddManual(string raw)
    {
        var id = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(id)) return "";
        if (!_allModIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            _allModIds.Insert(0, id);
        _checked.Add(id);
        return id;
    }

    /// <summary>The ticked ids — the new required list on OK (empty
    /// means the user cleared it).</summary>
    public List<string> Result => _checked.ToList();

    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);
}
