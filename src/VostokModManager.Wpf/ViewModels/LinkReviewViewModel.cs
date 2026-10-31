using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One unlinked mod in the link review.</summary>
public sealed class LinkReviewRow
{
    public LinkReviewRow(LinkCandidate c) => Candidate = c;

    public LinkCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public string Detail => $"{Candidate.Entry.ModId}  ·  v{Candidate.Entry.Version}";
    public bool HasMatch => Candidate.Slug != null;
    public string MatchName => Candidate.PageName ?? "";
    public string MatchUrl => Candidate.Slug == null ? "" : VostokModsUrl.ModPageUrl(Candidate.Slug);
}

/// <summary>"N not linked to VostokMods": every mod the manager can't check
/// for updates, with the VostokMods page of the same name where there is
/// one. Links all matches at once, one at a time, or by hand.</summary>
public sealed partial class LinkReviewViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private List<VmModSummary>? _listing;
    private bool _listingFailed;

    public LinkReviewViewModel(ManagerSession session)
    {
        _session = session;
        Rebuild();
    }

    public ObservableCollection<LinkReviewRow> Rows { get; } = new();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _matchCount;
    [ObservableProperty] private string _linkAllText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LinkAllCommand), nameof(LinkOneCommand), nameof(LinkByHandCommand))]
    private bool _isBusy;

    public bool IsEmpty => Rows.Count == 0;
    private bool NotBusy => !IsBusy;

    /// <summary>Reads the VostokMods list once, then shows the matches.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Looking for each mod's page on VostokMods…";
        try { _listing = await _session.FetchLinkListingAsync(); }
        catch { _listingFailed = true; }
        finally { IsBusy = false; }
        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var c in _session.ReviewUnlinked(_listing)) Rows.Add(new LinkReviewRow(c));
        MatchCount = Rows.Count(r => r.HasMatch);
        Title = Rows.Count == 0 ? "Every mod is linked"
            : Rows.Count == 1 ? "1 mod isn't linked to VostokMods"
            : $"{Rows.Count} mods aren't linked to VostokMods";
        LinkAllText = MatchCount == 1 ? "Link the 1 match" : $"Link the {MatchCount} matches";
        Status = _listingFailed ? "Couldn't read the VostokMods mod list, so no page could be matched. Link by hand works."
            : _listing == null ? Status
            : Rows.Count == 0 ? "The manager can check every mod for updates."
            : MatchCount == 0 ? "None of these has a VostokMods page with the same name. Link by hand if it is on VostokMods under another name."
            : MatchCount == Rows.Count ? "Each has a VostokMods page with the same name."
            : $"{MatchCount} have a VostokMods page with the same name; the others need linking by hand, if they are on VostokMods.";
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task LinkAll() => Link(Rows.Where(r => r.HasMatch).Select(r => r.Candidate).ToList());

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task LinkOne(LinkReviewRow? row) => row == null ? Task.CompletedTask : Link(new[] { row.Candidate });

    private async Task Link(IReadOnlyCollection<LinkCandidate> picked)
    {
        IsBusy = true;
        Status = "Linking, then checking VostokMods for updates…";
        try { await _session.LinkMatchesAsync(picked); }
        finally { IsBusy = false; }
        Rebuild();
    }

    /// <summary>Asks for the page (URL or slug), as the mod's right-click
    /// Link to VostokMods… does.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LinkByHand(LinkReviewRow? row)
    {
        if (row == null) return;
        IsBusy = true;
        try { await _session.LinkModAsync(row.Candidate.Entry); }
        finally { IsBusy = false; }
        Rebuild();
    }

    [RelayCommand]
    private void OpenMatch(LinkReviewRow? row)
    {
        if (row?.HasMatch == true) BrowseViewModel.OpenUrl(row.MatchUrl);
    }
}
