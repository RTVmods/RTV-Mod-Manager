using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A hooked method as the detail table shows it.</summary>
public sealed class HookMethodItem
{
    public HookMethodItem(HookMethodRow row) => Row = row;
    public HookMethodRow Row { get; }
    public string Method => Row.Method;
    public string ModsText => string.Join(", ", Row.ModNames);
    public string StatusText => Row.StatusText;
    public bool IsCollision => Row.IsCollision;
    public string MethodBrushKey => Row.IsCollision ? "B.AmberText" : "B.Text";
    public string StatusBrushKey => Row.IsCollision ? "B.SalmonText" : "B.Green";
}

/// <summary>The conflict detail: for a hook collision the per-method
/// truth (only a method hooked by 2+ mods really clashes) and what the
/// last play session's log confirmed; for every other type each mod's
/// conflicting value.</summary>
public sealed class ConflictDetailViewModel
{
    public ConflictDetailViewModel(ConflictDetail detail, ConflictDetector.Conflict conflict)
    {
        Detail = detail;
        Kind = "CONFLICT · " + conflict.Type.Replace('_', ' ').ToUpperInvariant();
        Severity = ManagerSession.SeverityOf(conflict.Type);
        Methods = detail.Methods.Select(m => new HookMethodItem(m)).ToList();
        CollisionCount = Methods.Count(m => m.IsCollision);
    }

    public ConflictDetail Detail { get; }
    public string Kind { get; }
    public ConflictSeverity Severity { get; }
    public string KindBrushKey => Severity switch
    {
        ConflictSeverity.Blocking => "B.SalmonText",
        ConflictSeverity.Behavior => "B.AmberText",
        _ => "B.Blue",
    };

    public string Title => Detail.Title;
    public string Subtitle => Detail.Subtitle;
    public bool IsHook => Detail.IsHook;
    public string Legend => Detail.Legend;
    public bool LegendIsWarning => CollisionCount > 0;
    public int CollisionCount { get; }
    public IReadOnlyList<HookMethodItem> Methods { get; }
    public string RuntimeNote => Detail.RuntimeNote;
    public IReadOnlyList<RuntimeHookRow> RuntimeRows => Detail.RuntimeRows;
    public IReadOnlyList<ConflictValueRow> Values => Detail.Values;
}
