using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>The conflict detail: what happens in game, then each mod's
/// part in it.</summary>
public sealed class ConflictDetailViewModel
{
    public ConflictDetailViewModel(ConflictDetail detail, ConflictDetector.Conflict conflict)
    {
        Detail = detail;
        Kind = "CONFLICT · " + conflict.Type.Replace('_', ' ').ToUpperInvariant();
        Severity = ManagerSession.SeverityOf(conflict.Type);
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
    public string Explanation => Detail.Explanation;
    public bool HasExplanation => !string.IsNullOrEmpty(Detail.Explanation);
    public IReadOnlyList<ConflictValueRow> Values => Detail.Values;
}
