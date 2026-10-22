using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Api;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>What a mod card says about the local copy, which also picks
/// its button.</summary>
public enum BrowseCardState
{
    /// <summary>Not here, not in another profile, not in the Library.</summary>
    Install,
    /// <summary>Installed, and VostokMods lists a newer version.</summary>
    Update,
    /// <summary>Installed and current.</summary>
    Installed,
    /// <summary>Another profile lists it; the active one doesn't.</summary>
    OtherProfile,
    /// <summary>A copy is in the Library, but no profile lists it.</summary>
    InLibrary,
    /// <summary>The host has no file cleared for download yet.</summary>
    NoFile,
}

/// <summary>A tab above the cards: All, a category, Modpacks, Website.</summary>
public sealed partial class BrowseTab : ObservableObject
{
    public BrowseTab(string key, string label) { Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
    /// <summary>The number after the label; empty until it is known.</summary>
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private bool _isActive;

    /// <summary>Re-sends IsActive, so a tab the user clicked but that did
    /// not become active is drawn unchecked again.</summary>
    public void Touch() => OnPropertyChanged(nameof(IsActive));
}

/// <summary>A profile tick in the details panel's "Install into" list.</summary>
public sealed partial class ProfileTick : ObservableObject
{
    public ProfileTick(ModProfile profile, bool isActive, string note)
    {
        Profile = profile;
        IsActive = isActive;
        Note = note;
        _isChecked = isActive;
    }

    public ModProfile Profile { get; }
    public string Name => Profile.Name;
    public bool IsActive { get; }
    /// <summary>"active", or the version this profile already lists.</summary>
    public string Note { get; }
    [ObservableProperty] private bool _isChecked;
}

/// <summary>One mod from VostokMods, as a card and in the details panel.</summary>
public sealed partial class ModCard : ObservableObject
{
    private readonly BrowseViewModel _owner;
    private bool _thumbRequested;
    private bool _detailRequested;

    public ModCard(BrowseViewModel owner, VmModSummary mod)
    {
        _owner = owner;
        Mod = mod;
    }

    public VmModSummary Mod { get; }
    public string Slug => Mod.Slug;
    public string Name => Mod.Name;
    public string Summary => Mod.Summary.Length > 0 ? Mod.Summary : "No summary.";
    public string Category => Mod.Category.Length > 0 ? Mod.Category : "Mod";
    public string CategoryLabel => Category.ToUpperInvariant();
    public string Author => Mod.Author;

    /// <summary>The newest version VostokMods lists, once it is known
    /// (the update check, or this mod's detail).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta), nameof(DetailMeta))]
    private string _version = "";

    /// <summary>"Gameplay · by Joni · v1.18".</summary>
    public string Meta => string.Join(" · ", new[]
    {
        Category,
        Author.Length > 0 ? "by " + Author : "",
        Version.Length > 0 ? "v" + Version : "",
    }.Where(s => s.Length > 0));

    /// <summary>"v1.18 · by Joni".</summary>
    public string DetailMeta => string.Join(" · ", new[]
    {
        Version.Length > 0 ? "v" + Version : "",
        Author.Length > 0 ? "by " + Author : "",
    }.Where(s => s.Length > 0));

    [ObservableProperty] private ImageSource? _thumb;

    // ── the local copy ──
    [ObservableProperty] private BrowseCardState _state;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailActionText))]
    private string _actionText = "Install";

    /// <summary>The details panel's button: "Open" says where it goes there.</summary>
    public string DetailActionText => State == BrowseCardState.Installed ? "Open in Mods" : ActionText;
    /// <summary>The state line, or the progress while busy, or the last error.</summary>
    [ObservableProperty] private string _line = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    private bool _isBusy;
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string _error = "";

    public ModEntry? Live { get; set; }
    public ModLibrary.Entry? LibraryCopy { get; set; }
    public bool CanAct => !IsBusy && State != BrowseCardState.NoFile;

    partial void OnStateChanged(BrowseCardState value)
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(DetailActionText));
    }
    partial void OnStateTextChanged(string value) => SyncLine();
    partial void OnProgressChanged(string value) => SyncLine();
    partial void OnErrorChanged(string value) => SyncLine();
    partial void OnIsBusyChanged(bool value) => SyncLine();

    private void SyncLine() => Line = IsBusy && Progress.Length > 0 ? Progress
        : Error.Length > 0 ? Error
        : StateText;

    // ── detail ──
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _detailLoading;
    [ObservableProperty] private string _stats = "";

    [RelayCommand] private Task Act() => _owner.ActAsync(this);
    [RelayCommand] private void OpenPage() => BrowseViewModel.OpenUrl(Mod.PageUrl);

    /// <summary>Starts the thumbnail download the first time the card is shown.</summary>
    public async void EnsureThumb()
    {
        if (_thumbRequested) return;
        _thumbRequested = true;
        Thumb = await ThumbnailCache.GetAsync(Mod.ThumbnailUrl);
    }

    /// <summary>Reads the mod's detail (description, versions) the first
    /// time the card is selected.</summary>
    public async Task EnsureDetailAsync(VostokModsClient client)
    {
        if (_detailRequested) return;
        _detailRequested = true;
        DetailLoading = true;
        try
        {
            var d = await client.GetModAsync(Slug);
            if (d.LatestVersion.Length > 0) Version = d.LatestVersion;
            Description = MarkdownToText(d.Description);
            if (Description.Length == 0) Description = Summary;
            Stats = string.Join(" · ", new[]
            {
                d.Downloads == 1 ? "1 download" : $"{d.Downloads:N0} downloads",
                d.UpdatedAt is { } u ? $"updated {u.ToLocalTime():d MMM yyyy}" : "",
                d.License.Length > 0 ? d.License + " licence" : "",
                d.LatestGameVersion.Length > 0 ? $"game {d.LatestGameVersion}" : "",
            }.Where(s => s.Length > 0));
        }
        catch (Exception ex)
        {
            _detailRequested = false;  // try again next time it is selected
            Description = Summary + "\n\nCouldn't read the full description: " + ex.Message;
        }
        finally
        {
            DetailLoading = false;
        }
    }

    /// <summary>Markdown as plain text: no emphasis marks, links as their
    /// text, images and HTML tags dropped, bullets as •.</summary>
    internal static string MarkdownToText(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var s = md.Replace("\r\n", "\n");
        s = Regex.Replace(s, @"^```.*$", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"<[^>]+>", "");
        // A heading becomes a paragraph of its own.
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*(.*)$", "\n$1\n", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^(\s*)[-*+]\s+", "$1• ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^\s*>\s?", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");
        s = Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");
        s = s.Replace("`", "");
        s = Regex.Replace(s, @"^\s*(-{3,}|\*{3,})\s*$", "", RegexOptions.Multiline);

        // A single line break inside a paragraph is a space in Markdown;
        // blank lines and list items keep theirs.
        var sb = new System.Text.StringBuilder();
        var prevBlank = true;
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            var blank = line.Length == 0;
            var listItem = line.StartsWith("• ", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\d+[.)]\s");
            if (sb.Length > 0)
                sb.Append(blank || prevBlank || listItem ? "\n" : " ");
            sb.Append(line);
            prevBlank = blank;
        }
        s = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
        return s.Trim();
    }
}

/// <summary>A modpack card (the Modpacks tab and the featured banner).</summary>
public sealed partial class ModpackCard : ObservableObject
{
    private readonly BrowseViewModel _owner;
    private bool _thumbRequested;

    public ModpackCard(BrowseViewModel owner, VmModpackSummary pack)
    {
        _owner = owner;
        Pack = pack;
        _haveLine = Meta;
    }

    public VmModpackSummary Pack { get; }
    public string Slug => Pack.Slug;
    public string Name => Pack.Name;
    public string Summary => Pack.Summary.Length > 0 ? Pack.Summary : "No summary.";
    public string Meta => string.Join(" · ", new[]
    {
        Pack.ModCount == 1 ? "1 mod" : $"{Pack.ModCount} mods",
        Pack.Author.Length > 0 ? "by " + Pack.Author : "",
        Pack.Downloads > 0 ? $"{Pack.Downloads:N0} downloads" : "",
    }.Where(s => s.Length > 0));

    [ObservableProperty] private ImageSource? _thumb;

    /// <summary>"30 mods · you have 22 · 8 missing from Default", once the
    /// manifest has been read.</summary>
    [ObservableProperty] private string _haveLine = "";
    /// <summary>Mods of the pack the active profile lacks; -1 while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallMissing))]
    private int _missing = -1;
    [ObservableProperty] private string _installMissingText = "Install the pack";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallMissing))]
    private bool _isBusy;

    public bool CanInstallMissing => !IsBusy && Missing != 0;

    [RelayCommand] private Task Import() => _owner.ImportPackAsync(this);
    [RelayCommand] private Task NewProfile() => _owner.NewProfileFromPackAsync(this);
    [RelayCommand] private void OpenPage() => BrowseViewModel.OpenUrl(Pack.PageUrl);

    public async void EnsureThumb()
    {
        if (_thumbRequested) return;
        _thumbRequested = true;
        Thumb = await ThumbnailCache.GetAsync(Pack.ThumbnailUrl);
    }
}

/// <summary>The Browse VostokMods page: the site's mods as cards with what
/// the local install has of each, its modpacks, and the site itself in an
/// embedded browser.</summary>
public sealed partial class BrowseViewModel : PageViewModel
{
    /// <summary>Cards shown at first and added by each "Show more".</summary>
    private const int PageSize = 24;
    /// <summary>Categories shown as tabs; the rest go under "More".</summary>
    private const int InlineCategories = 4;

    private readonly Func<ImportFlow> _imports;
    private readonly Action<string> _openPage;
    private readonly ModsViewModel? _mods;

    /// <summary>Every mod on the host, one card each, in listing order.</summary>
    private readonly List<ModCard> _cards = new();
    private readonly Dictionary<string, string> _slugById = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Category slug → the slugs of the mods in it (fetched per tab).</summary>
    private readonly Dictionary<string, HashSet<string>> _categoryMembers = new(StringComparer.OrdinalIgnoreCase);
    private List<(ModLibrary.Entry Entry, ModSource Recorded)> _library = new();
    private List<ModCard> _filtered = new();
    private int _shown = PageSize;
    private bool _loaded;
    private ModBrowserHooks? _hooks;

    public BrowseViewModel(ManagerSession session, Func<ImportFlow> imports, Action<string> openPage,
        ModsViewModel? mods = null) : base(session)
    {
        _imports = imports;
        _openPage = openPage;
        _mods = mods;
        Tabs.Add(new BrowseTab("all", "All") { IsActive = true });
        TailTabs.Add(new BrowseTab("modpacks", "Modpacks"));
        TailTabs.Add(new BrowseTab("website", "Website"));
    }

    // ── state the view binds to ───────────────────────────────────────

    /// <summary>All mods and the first categories.</summary>
    public ObservableCollection<BrowseTab> Tabs { get; } = new();
    /// <summary>The categories under the "More" tab.</summary>
    public ObservableCollection<BrowseTab> MoreTabs { get; } = new();
    /// <summary>Modpacks and Website, after "More".</summary>
    public ObservableCollection<BrowseTab> TailTabs { get; } = new();

    public ObservableCollection<ModCard> Cards { get; } = new();
    /// <summary>Every pack VostokMods lists; the first is the featured one.</summary>
    public ObservableCollection<ModpackCard> Modpacks { get; } = new();
    public ObservableCollection<ModpackCard> ShownModpacks { get; } = new();
    public ObservableCollection<ProfileTick> Ticks { get; } = new();

    [ObservableProperty] private string _activeKey = "all";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private ModCard? _selected;
    [ObservableProperty] private ModpackCard? _featured;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingText = "";
    /// <summary>A network or install failure, shown inline.</summary>
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _modpackError = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _showMoreText = "";
    [ObservableProperty] private string _ticksNote = "";

    /// <summary>The page's load, for anything that wants to wait on it
    /// (the DEBUG screenshot harness).</summary>
    public Task? Loading { get; private set; }

    public bool IsModsTab => ActiveKey == "all" || ActiveKey.StartsWith("cat:", StringComparison.Ordinal);
    public bool IsModpacksTab => ActiveKey == "modpacks";
    public bool IsWebsiteTab => ActiveKey == "website";
    public bool ShowFeatured => !IsWebsiteTab && Featured != null;
    public bool HasMore => _filtered.Count > Cards.Count;
    public bool NothingFound => IsModsTab && _loaded && !IsLoading && _filtered.Count == 0 && Error.Length == 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(NothingFound));

    /// <summary>The More tab's label: the picked category, else "More".</summary>
    public string MoreLabel => MoreTabs.FirstOrDefault(t => t.IsActive)?.Label ?? "More";
    public bool MoreActive => MoreTabs.Any(t => t.IsActive);
    public bool HasMoreTabs => MoreTabs.Count > 0;

    public string ProfileName => Session.ActiveProfile?.Name ?? "the mods folder";

    partial void OnActiveKeyChanged(string value)
    {
        foreach (var t in Tabs.Concat(MoreTabs).Concat(TailTabs)) t.IsActive = t.Key == value;
        foreach (var n in new[]
                 {
                     nameof(IsModsTab), nameof(IsModpacksTab), nameof(IsWebsiteTab), nameof(ShowFeatured),
                     nameof(MoreLabel), nameof(MoreActive), nameof(NothingFound),
                 })
            OnPropertyChanged(n);
    }

    partial void OnFeaturedChanged(ModpackCard? value) => OnPropertyChanged(nameof(ShowFeatured));

    partial void OnQueryChanged(string value)
    {
        if (IsModpacksTab) FillModpacks();
        else Rebuild();
    }

    partial void OnSelectedChanged(ModCard? value)
    {
        BuildTicks();
        if (value != null) _ = value.EnsureDetailAsync(Session.Vm);
    }

    // ── page lifecycle ────────────────────────────────────────────────

    public override void OnShown()
    {
        OnPropertyChanged(nameof(ProfileName));
        if (!_loaded && Loading == null) Loading = LoadAsync();
        else if (_loaded) _ = RefreshLocalAsync();
    }

    public override void Reload()
    {
        // The session changed (an install, a profile switch): re-read
        // what the cards say about the local copies.
        if (!_loaded) return;
        OnPropertyChanged(nameof(ProfileName));
        RefreshStates();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsLoading) return;
        _categoryMembers.Clear();
        Loading = LoadAsync();
        await Loading;
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        LoadingText = "Reading VostokMods…";
        Error = "";
        try
        {
            var modsTask = Session.Vm.ListAllModsAsync();
            var taxTask = Session.Vm.ListTaxonomiesAsync();
            var packsTask = Session.ListModpacksAsync();
            var libTask = ReadLibraryAsync();

            List<VmModSummary> mods;
            try
            {
                mods = await modsTask;
            }
            catch (Exception ex)
            {
                Error = ex is VostokModsException ? ex.Message : "Couldn't read the mods on VostokMods: " + ex.Message;
                mods = new List<VmModSummary>();
            }

            _slugById.Clear();
            foreach (var m in mods)
                if (m.Id.Length > 0) _slugById[m.Id] = m.Slug;
            var keep = Selected?.Slug;
            _cards.Clear();
            _cards.AddRange(mods.Select(m => new ModCard(this, m)));
            Tabs[0].Count = mods.Count > 0 ? mods.Count.ToString() : "";

            try { BuildCategoryTabs(await taxTask); }
            catch { BuildCategoryTabs(new List<VmTaxonomy>()); }  // no categories: All still works

            _library = await libTask;
            _loaded = true;
            RefreshStates();
            Rebuild(keep);

            var packs = await packsTask;
            ModpackError = packs.Error != null ? packs.Header : "";
            Modpacks.Clear();
            foreach (var p in packs.Packs) Modpacks.Add(new ModpackCard(this, p));
            TailTabs[0].Count = packs.Packs.Count > 0 ? packs.Packs.Count.ToString() : "";
            Featured = Modpacks.FirstOrDefault();
            FillModpacks();
            if (Featured != null)
            {
                Featured.EnsureThumb();
                await CountFeaturedAsync();
            }
        }
        finally
        {
            IsLoading = false;
            LoadingText = "";
            OnPropertyChanged(nameof(NothingFound));
        }
    }

    private void BuildCategoryTabs(List<VmTaxonomy> taxonomies)
    {
        var activeKey = ActiveKey;
        while (Tabs.Count > 1) Tabs.RemoveAt(Tabs.Count - 1);
        MoreTabs.Clear();
        // The site's own order (its sortOrder); only categories, not tags.
        var cats = taxonomies.Where(t => t.GroupSlug.StartsWith("categor", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < cats.Count; i++)
        {
            var tab = new BrowseTab("cat:" + cats[i].Slug, cats[i].Name);
            if (_categoryMembers.TryGetValue(cats[i].Slug, out var set)) tab.Count = set.Count.ToString();
            (i < InlineCategories ? Tabs : MoreTabs).Add(tab);
        }
        if (!Tabs.Concat(MoreTabs).Concat(TailTabs).Any(t => t.Key == activeKey)) activeKey = "all";
        ActiveKey = activeKey;
        OnActiveKeyChanged(activeKey);
        OnPropertyChanged(nameof(HasMoreTabs));
    }

    /// <summary>The Library's archives with the sources they declare or the
    /// config recorded. Read off the UI thread: listing opens every .vmz.</summary>
    private async Task<List<(ModLibrary.Entry, ModSource)>> ReadLibraryAsync()
    {
        var dir = Session.ModsDir;
        List<ModLibrary.Entry> entries;
        try { entries = await Task.Run(() => ModLibrary.List(dir)); }
        catch { return new(); }
        return entries.Select(e => (e, Session.Config.GetModSource(e.ModId, e.Version))).ToList();
    }

    /// <summary>Re-reads the Library, then the card states.</summary>
    private async Task RefreshLocalAsync()
    {
        _library = await ReadLibraryAsync();
        RefreshStates();
        await CountFeaturedAsync();
    }

    // ── tabs, search and paging ───────────────────────────────────────

    [RelayCommand]
    private async Task SetTab(string key)
    {
        ActiveKey = key;
        if (key.StartsWith("cat:", StringComparison.Ordinal))
        {
            var slug = key.Substring(4);
            if (!_categoryMembers.ContainsKey(slug))
            {
                Cards.Clear();
                _filtered.Clear();
                IsLoading = true;
                LoadingText = "Reading the category…";
                try
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var page = 1; page <= 50; page++)
                    {
                        var p = await Session.Vm.ListModsAsync(page, taxonomies: slug, limit: VostokModsClient.MaxPageSize);
                        foreach (var m in p.Items) set.Add(m.Slug);
                        if (!p.HasMore || p.Items.Count == 0) break;
                    }
                    _categoryMembers[slug] = set;
                    var tab = Tabs.Concat(MoreTabs).FirstOrDefault(t => t.Key == key);
                    if (tab != null) tab.Count = set.Count.ToString();
                }
                catch (Exception ex)
                {
                    Error = "Couldn't read that category from VostokMods: " + ex.Message;
                }
                finally
                {
                    IsLoading = false;
                    LoadingText = "";
                }
                if (ActiveKey != key) return;  // the user moved on meanwhile
            }
        }
        if (IsModpacksTab) FillModpacks();
        else if (IsModsTab) Rebuild();
        OnPropertyChanged(nameof(NothingFound));
    }

    /// <summary>The menu under More closed without a pick: draw the tabs
    /// as they are again.</summary>
    public void ResyncTabs()
    {
        foreach (var t in Tabs.Concat(MoreTabs).Concat(TailTabs)) t.Touch();
        OnPropertyChanged(nameof(MoreActive));
    }

    [RelayCommand]
    private void ShowMore()
    {
        _shown += PageSize;
        Fill();
    }

    private void Rebuild(string? keepSlug = null)
    {
        keepSlug ??= Selected?.Slug;
        IEnumerable<ModCard> src = _cards;
        if (ActiveKey.StartsWith("cat:", StringComparison.Ordinal))
        {
            src = _categoryMembers.TryGetValue(ActiveKey.Substring(4), out var set)
                ? src.Where(c => set.Contains(c.Slug))
                : Enumerable.Empty<ModCard>();
        }
        var q = Query.Trim();
        if (q.Length > 0)
            src = src.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || c.Mod.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || c.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || c.Slug.Contains(q, StringComparison.OrdinalIgnoreCase));
        _filtered = src.ToList();
        _shown = PageSize;
        // Keep the selected card on screen when it is still in the list.
        var keepIndex = keepSlug == null ? -1 : _filtered.FindIndex(c => c.Slug == keepSlug);
        if (keepIndex >= _shown) _shown = keepIndex + 1;
        Fill();
        Selected = keepIndex >= 0 ? _filtered[keepIndex] : _filtered.FirstOrDefault();
        ResultText = _loaded
            ? (_filtered.Count == 1 ? "1 mod" : $"{_filtered.Count} mods") + (q.Length > 0 ? $" matching “{q}”" : "")
            : "";
        OnPropertyChanged(nameof(NothingFound));
    }

    private void Fill()
    {
        var want = _filtered.Take(_shown).ToList();
        // Append when the list only grew (Show more), else replace.
        if (Cards.Count <= want.Count && Cards.SequenceEqual(want.Take(Cards.Count)))
        {
            for (var i = Cards.Count; i < want.Count; i++) Cards.Add(want[i]);
        }
        else
        {
            Cards.Clear();
            foreach (var c in want) Cards.Add(c);
        }
        foreach (var c in Cards) c.EnsureThumb();
        var left = _filtered.Count - Cards.Count;
        ShowMoreText = left <= 0 ? "" : $"Show {Math.Min(left, PageSize)} more ({left} left)";
        OnPropertyChanged(nameof(HasMore));
    }

    /// <summary>The Modpacks tab's cards: the packs the search box lets through.</summary>
    private void FillModpacks()
    {
        var keep = ManagerSession.FilterModpacks(Modpacks.Select(p => p.Pack), Query)
            .Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ShownModpacks.Clear();
        foreach (var p in Modpacks.Where(p => keep.Contains(p.Slug)))
        {
            ShownModpacks.Add(p);
            p.EnsureThumb();
        }
    }

    // ── what the local install has of each mod ────────────────────────

    /// <summary>The slug a source names (it may carry the mod's UUID
    /// instead), or "" for a source that isn't VostokMods.</summary>
    private string SlugOf(ModSource s)
    {
        if (!s.IsValid || s.Provider != ModSource.VostokMods) return "";
        return _slugById.TryGetValue(s.Id, out var slug) ? slug : s.Id;
    }

    /// <summary>Sets every card's state from the live mods, the other
    /// profiles and the Library.</summary>
    private void RefreshStates()
    {
        var live = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Session.Registry.Entries)
        {
            var slug = SlugOf(e.Source);
            if (slug.Length > 0 && !live.ContainsKey(slug)) live[slug] = e;
        }

        var active = Session.ActiveProfile;
        var inOthers = new Dictionary<string, List<ModProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Session.AllProfiles)
        {
            if (active != null && Session.IsActiveProfile(p)) continue;
            foreach (var pm in p.Mods)
            {
                var slug = SlugOf(pm.SourceRef);
                if (slug.Length == 0) continue;
                if (!inOthers.TryGetValue(slug, out var list)) inOthers[slug] = list = new();
                if (!list.Contains(p)) list.Add(p);
            }
        }

        var lib = new Dictionary<string, ModLibrary.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, recorded) in _library)
        {
            foreach (var slug in new[] { SlugOf(entry.Source), SlugOf(recorded) })
            {
                if (slug.Length == 0) continue;
                if (!lib.TryGetValue(slug, out var have) || ModRegistry.CompareVersions(entry.Version, have.Version) > 0)
                    lib[slug] = entry;
            }
        }

        foreach (var c in _cards)
        {
            live.TryGetValue(c.Slug, out var e);
            lib.TryGetValue(c.Slug, out var le);
            c.Live = e;
            c.LibraryCopy = le;
            var latest = LatestFor(c, e);
            if (latest.Length > 0) c.Version = latest;

            if (e != null)
            {
                if (Session.IsOutdated(e))
                {
                    c.State = BrowseCardState.Update;
                    c.StateText = $"You have {e.Version}";
                    c.ActionText = "Update";
                }
                else
                {
                    c.State = BrowseCardState.Installed;
                    c.StateText = e.Version.Length > 0 ? $"Installed · v{e.Version}" : "Installed";
                    c.ActionText = "Open";
                }
            }
            else if (inOthers.TryGetValue(c.Slug, out var others))
            {
                c.State = BrowseCardState.OtherProfile;
                c.StateText = others.Count == 1 ? $"In {others[0].Name} only" : $"In {others.Count} other profiles";
                c.ActionText = "Add here";
            }
            else if (le != null)
            {
                c.State = BrowseCardState.InLibrary;
                c.StateText = $"In your Library · v{le.Version}";
                c.ActionText = "Add here";
            }
            else if (!c.Mod.HasDownloadableFile)
            {
                c.State = BrowseCardState.NoFile;
                c.StateText = "No file to download yet";
                c.ActionText = "Install";
            }
            else
            {
                c.State = BrowseCardState.Install;
                c.StateText = c.Mod.Downloads == 1 ? "1 download" : $"{c.Mod.Downloads:N0} downloads";
                c.ActionText = "Install";
            }
        }
        BuildTicks();
    }

    /// <summary>The newest version VostokMods lists for the mod, from the
    /// update check's cache; "" when unknown.</summary>
    private string LatestFor(ModCard c, ModEntry? live)
    {
        if (live != null && Session.TryGetLatest(live, out var l)) return l;
        if (Session.LatestVersions.TryGetValue(ModSource.ForSlug(c.Slug).Key, out var v) && v.Length > 0) return v;
        if (c.Mod.Id.Length > 0
            && Session.LatestVersions.TryGetValue(new ModSource(ModSource.VostokMods, c.Mod.Id).Key, out v) && v.Length > 0)
            return v;
        return c.Version;
    }

    /// <summary>The details panel's "Install into" ticks: every profile,
    /// the active one ticked.</summary>
    private void BuildTicks()
    {
        // A refresh of the same card keeps the user's ticks.
        var card = Selected;
        HashSet<string>? keep = card != null && ReferenceEquals(card, _ticksFor)
            ? Ticks.Where(t => t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        _ticksFor = card;
        Ticks.Clear();
        var active = Session.ActiveProfile;
        if (active == null)
        {
            TicksNote = "No profiles yet: installs go into the mods folder.";
            return;
        }
        TicksNote = "";
        foreach (var p in Session.AllProfiles.OrderByDescending(p => Session.IsActiveProfile(p))
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isActive = Session.IsActiveProfile(p);
            var listed = card == null ? null
                : p.Mods.FirstOrDefault(m => SlugOf(m.SourceRef).Equals(card.Slug, StringComparison.OrdinalIgnoreCase));
            var note = isActive ? "active" : listed != null ? $"has {listed.Version}" : "";
            if (isActive && listed != null) note = $"active · has {listed.Version}";
            var tick = new ProfileTick(p, isActive, note);
            if (keep != null) tick.IsChecked = keep.Contains(p.Name);
            Ticks.Add(tick);
        }
    }

    private ModCard? _ticksFor;

    // ── installing ────────────────────────────────────────────────────

    /// <summary>A card's button: Install, Update, Add here or Open.</summary>
    public async Task ActAsync(ModCard card)
    {
        if (card.IsBusy) return;
        switch (card.State)
        {
            case BrowseCardState.Installed:
                OpenInMods(card);
                return;
            case BrowseCardState.NoFile:
                return;
        }

        // The ticks belong to the selected card; any other card installs
        // into the active profile only.
        var targets = ReferenceEquals(card, Selected) && Ticks.Count > 0
            ? Ticks.Where(t => t.IsChecked).Select(t => t.Profile).ToList()
            : Session.ActiveProfile is { } a ? new List<ModProfile> { a } : new List<ModProfile>();
        if (Session.ActiveProfile != null && targets.Count == 0)
        {
            card.Error = "Tick at least one profile to install into.";
            return;
        }

        card.Error = "";
        card.IsBusy = true;
        try
        {
            switch (card.State)
            {
                case BrowseCardState.Update when card.Live != null:
                    await UpdateAsync(card, card.Live, targets);
                    break;
                case BrowseCardState.OtherProfile or BrowseCardState.InLibrary when card.LibraryCopy != null
                                                                                    && File.Exists(card.LibraryCopy.Path):
                    await AddFromLibraryAsync(card, card.LibraryCopy, targets);
                    break;
                default:
                    await DownloadAndInstallAsync(card, targets);
                    break;
            }
        }
        catch (Exception ex)
        {
            card.Error = ex is VostokModsException ? ex.Message : "Install failed: " + ex.Message;
        }
        finally
        {
            card.IsBusy = false;
            card.Progress = "";
            _ticksFor = null;  // done: the ticks go back to the active profile
            await RefreshLocalAsync();
        }
    }

    private bool IntoActive(IReadOnlyList<ModProfile> targets)
        => Session.ActiveProfile == null || targets.Any(p => Session.IsActiveProfile(p));

    private async Task DownloadAndInstallAsync(ModCard card, IReadOnlyList<ModProfile> targets)
    {
        var temp = Path.Combine(Path.GetTempPath(), "vmm_dl_" + Guid.NewGuid().ToString("N") + ".vmz");
        try
        {
            card.Progress = "Downloading…";
            var bytes = new Progress<(long done, long total)>(p => card.Progress = p.total > 0
                ? $"Downloading… {(int)(p.done * 100.0 / p.total)}%"
                : $"Downloading… {p.done / 1024:N0} KB");
            var file = await Session.Vm.DownloadAsync(card.Slug, temp, progress: bytes);
            if (file.Version.Length > 0) card.Version = file.Version;
            card.Progress = "Installing…";
            var source = ModSource.ForSlug(card.Slug);

            List<ModLibrary.Entry> entries;
            if (IntoActive(targets))
            {
                var result = await Session.InstallDownloadedModAsync(temp, source);
                if (result.InstalledModIds.Count == 0) return;  // the pipeline said why
                entries = LibraryEntriesFor(result.InstalledModIds, source);
                await AddToOtherProfilesAsync(targets, entries);
                await Session.CheckAndPromptMissingDepsAsync(result.InstalledModIds, Views.MissingDependenciesWindow.Resolver(() => System.Windows.Application.Current?.MainWindow));
            }
            else
            {
                // Only other profiles: the file goes to the Library and
                // those profiles list it; the live mods stay as they are.
                var added = ModLibrary.Add(Session.ModsDir, temp);
                if (added == null)
                {
                    card.Error = "The download isn't a mod archive the manager can read.";
                    return;
                }
                entries = new List<ModLibrary.Entry> { WithSource(added, source) };
                await AddToOtherProfilesAsync(targets, entries);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp */ }
        }
    }

    private async Task UpdateAsync(ModCard card, ModEntry live, IReadOnlyList<ModProfile> targets)
    {
        card.Progress = "Downloading…";
        var progress = new Progress<DownloadProgress>(p => card.Progress = $"Downloading… {p.CellText}");
        var result = await Session.UpdateModAsync(live, progress);
        if (result == null) return;
        if (!result.Ok)
        {
            card.Error = result.Message;
            return;
        }
        card.Version = result.NewVersion;
        var entries = LibraryEntriesFor(new[] { result.ModId }, ModSource.ForSlug(card.Slug));
        await AddToOtherProfilesAsync(targets, entries);
    }

    private async Task AddFromLibraryAsync(ModCard card, ModLibrary.Entry copy, IReadOnlyList<ModProfile> targets)
    {
        card.Progress = "Adding from the Library…";
        var entries = new List<ModLibrary.Entry> { WithSource(copy, ModSource.ForSlug(card.Slug)) };
        if (IntoActive(targets))
        {
            var result = await Session.InstallModFilesAsync(new[] { copy.Path });
            if (result.InstalledModIds.Count == 0) return;
            await AddToOtherProfilesAsync(targets, entries);
            await Session.CheckAndPromptMissingDepsAsync(result.InstalledModIds, Views.MissingDependenciesWindow.Resolver(() => System.Windows.Application.Current?.MainWindow));
        }
        else
        {
            await AddToOtherProfilesAsync(targets, entries);
        }
    }

    /// <summary>The ticked profiles other than the active one list the
    /// Library copies too (the active one got them from the install).</summary>
    private Task AddToOtherProfilesAsync(IReadOnlyList<ModProfile> targets, IReadOnlyList<ModLibrary.Entry> entries)
    {
        if (entries.Count == 0) return Task.CompletedTask;
        foreach (var p in targets)
        {
            if (Session.ActiveProfile != null && Session.IsActiveProfile(p)) continue;
            Session.AddLibraryModsToProfile(p, entries);
        }
        return Task.CompletedTask;
    }

    /// <summary>The Library copies of just-installed mods, at the version
    /// that is now live.</summary>
    private List<ModLibrary.Entry> LibraryEntriesFor(IEnumerable<string> modIds, ModSource source)
    {
        var result = new List<ModLibrary.Entry>();
        foreach (var id in modIds)
        {
            var live = Session.Registry.FindById(id);
            if (live == null) continue;
            var path = ModLibrary.Find(Session.ModsDir, id, live.Version);
            if (path.Length == 0) continue;
            result.Add(new ModLibrary.Entry
            {
                Path = path,
                ModId = id,
                Version = live.Version,
                DisplayName = live.DisplayName,
                Source = live.Source.IsValid ? live.Source : source,
            });
        }
        return result;
    }

    private static ModLibrary.Entry WithSource(ModLibrary.Entry e, ModSource source) => new()
    {
        Path = e.Path,
        ModId = e.ModId,
        Version = e.Version,
        DisplayName = e.DisplayName,
        SizeBytes = e.SizeBytes,
        Source = e.Source.IsValid ? e.Source : source,
    };

    /// <summary>"Installed · Open": the Mods page, with the mod selected.</summary>
    private void OpenInMods(ModCard card)
    {
        _openPage("mods");
        if (_mods == null || card.Live == null) return;
        if (_mods.Query.Length > 0) _mods.Query = "";
        var row = _mods.Rows.FirstOrDefault(r => r.Entry != null
            && string.Equals(r.Entry.Path, card.Live.Path, StringComparison.OrdinalIgnoreCase));
        if (row != null) _mods.Selected = row;
    }

    // ── modpacks ──────────────────────────────────────────────────────

    /// <summary>How much of the featured pack the active profile has.</summary>
    private async Task CountFeaturedAsync()
    {
        var pack = Featured;
        if (pack == null) return;
        VmModpackManifest manifest;
        try { manifest = await Session.Vm.GetModpackManifestAsync(pack.Slug); }
        catch
        {
            pack.HaveLine = pack.Meta;
            pack.Missing = -1;
            pack.InstallMissingText = "Install the pack";
            return;
        }
        var live = Session.Registry.Entries.Select(e => SlugOf(e.Source))
            .Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = manifest.Mods.Count;
        var have = manifest.Mods.Count(m => live.Contains(m.Slug));
        var missing = total - have;
        pack.Missing = missing;
        pack.HaveLine = $"{total} mods · you have {have} · {missing} missing from {ProfileName}";
        pack.InstallMissingText = missing == 0 ? "All installed" : $"Install the {missing} missing";
    }

    /// <summary>A pack's Import button (and the banner's Install button):
    /// the regular modpack import, which asks where it goes.</summary>
    public async Task ImportPackAsync(ModpackCard pack)
    {
        if (pack.IsBusy) return;
        pack.IsBusy = true;
        try
        {
            await _imports().RunModpackAsync(pack.Slug);
        }
        catch (Exception ex)
        {
            ModpackError = $"Couldn't import {pack.Name}: {ex.Message}";
        }
        finally
        {
            pack.IsBusy = false;
            await RefreshLocalAsync();
        }
    }

    /// <summary>The banner's "New profile from pack": the import, straight
    /// into a new profile named after the pack.</summary>
    public async Task NewProfileFromPackAsync(ModpackCard pack)
    {
        if (pack.IsBusy) return;
        pack.IsBusy = true;
        try
        {
            var import = await Session.LoadModpackAsync(pack.Slug);
            if (import == null) return;
            var options = Session.GetImportTargetOptions(import.Name);
            if (options == null) return;
            var name = Session.Prompt.AskText("New profile", "Name for the new profile:", options.SuggestedNewName);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!Session.SelectImportTarget(new ImportTargetChoice(ImportTarget.NewProfile, name.Trim()), import)) return;
            var plan = Session.BuildImportPlan(import);
            if (plan == null) return;
            var vm = new ImportPlanViewModel(Session, plan);
            new ImportPlanWindow(vm) { Owner = Application.Current?.MainWindow }.ShowDialog();
            if (vm.Result is { Applied: true } result)
            {
                await Session.CheckUpdatesAsync();
                if (result.InstalledModIds.Count > 0)
                    await Session.CheckAndPromptMissingDepsAsync(result.InstalledModIds, Views.MissingDependenciesWindow.Resolver(() => System.Windows.Application.Current?.MainWindow));
            }
        }
        catch (Exception ex)
        {
            ModpackError = $"Couldn't import {pack.Name}: {ex.Message}";
        }
        finally
        {
            pack.IsBusy = false;
            await RefreshLocalAsync();
        }
    }

    // ── the Website tab (the view hosts the WebView2) ─────────────────

    [ObservableProperty] private string _webAddress = "";
    [ObservableProperty] private string _webStatus = "";
    [ObservableProperty] private string _webInstallText = "Install this mod";
    [ObservableProperty] private bool _webInstallEnabled;
    [ObservableProperty] private string _webError = "";
    private bool _webBusy;

    public string WebHome => VostokModsUrl.ExploreModsUrl;
    public string WebUserDataFolder => Path.Combine(AppPaths.ManagerDir, "webview2");

    /// <summary>The embedded browser's callbacks: install a downloaded
    /// file, and whether a source is already here. Prepared once per
    /// session of the tab (the Library is snapshotted).</summary>
    public ModBrowserHooks Hooks => _hooks ??= Session.PrepareModBrowser();

    /// <summary>The embedded page moved: the address, and what the
    /// install button offers there.</summary>
    public void OnWebNavigated(string url)
    {
        WebAddress = url;
        if (_webBusy) return;
        if (VostokModsUrl.TryParseModpackSlug(url, out var packSlug))
        {
            WebInstallText = "Install this modpack";
            WebInstallEnabled = true;
            WebStatus = $"Modpack {packSlug}: the install adds its mods to a profile you pick.";
        }
        else if (VostokModsUrl.TryParseModPageSlug(url, out var slug))
        {
            if (Hooks.OwnsSource(ModSource.ForSlug(slug)))
            {
                WebInstallText = "Already here";
                WebInstallEnabled = false;
                WebStatus = $"{slug} is installed or in your Library. The page's own Download button re-downloads it.";
            }
            else
            {
                WebInstallText = "Install this mod";
                WebInstallEnabled = true;
                WebStatus = $"Mod {slug}: install it here, or use the page's Download button.";
            }
        }
        else
        {
            WebInstallText = "Install this mod";
            WebInstallEnabled = false;
            WebStatus = "Open a mod or modpack page to install it.";
        }
    }

    /// <summary>The toolbar's install button, for the page on screen.</summary>
    [RelayCommand]
    private async Task WebInstall()
    {
        var url = WebAddress;
        if (VostokModsUrl.TryParseModpackSlug(url, out var packSlug))
        {
            _webBusy = true;
            WebInstallEnabled = false;
            WebStatus = $"Importing modpack {packSlug}…";
            try
            {
                await _imports().RunModpackAsync(packSlug);
                WebStatus = "Modpack import finished.";
            }
            catch (Exception ex) { WebStatus = "Import failed: " + ex.Message; }
            finally
            {
                _webBusy = false;
                await RefreshLocalAsync();
                OnWebNavigated(WebAddress);
            }
            return;
        }
        if (!VostokModsUrl.TryParseModPageSlug(url, out var slug)) return;

        _webBusy = true;
        WebInstallEnabled = false;
        WebStatus = $"Downloading {slug}…";
        var temp = Path.Combine(Path.GetTempPath(), "vmm_dl_" + Guid.NewGuid().ToString("N") + ".vmz");
        try
        {
            await Session.Vm.DownloadAsync(slug, temp);
            await InstallWebDownloadAsync(temp, ModSource.ForSlug(slug));
        }
        catch (Exception ex)
        {
            WebStatus = "Install failed: " + ex.Message;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp */ }
            _webBusy = false;
            OnWebNavigated(WebAddress);
        }
    }

    /// <summary>A file the page downloaded (intercepted by the view), or
    /// the toolbar's own download: through the browser hooks' install,
    /// then the cards catch up.</summary>
    public async Task InstallWebDownloadAsync(string path, ModSource source)
    {
        if (!File.Exists(path))
        {
            WebStatus = "The downloaded file wasn't found.";
            return;
        }
        _webBusy = true;
        WebStatus = "Download complete, installing…";
        try
        {
            await Hooks.InstallDownloaded(path, source);
            WebStatus = "Installed. Browse for more.";
        }
        catch (Exception ex)
        {
            WebStatus = "Install failed: " + ex.Message;
        }
        finally
        {
            _webBusy = false;
            await RefreshLocalAsync();
        }
    }

    public void SetWebStatus(string text) => WebStatus = text;

    // ── shared ────────────────────────────────────────────────────────

    internal static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }
}

/// <summary>Card thumbnails, downloaded a few at a time and kept for the
/// session (the same image is never fetched twice).</summary>
internal static class ThumbnailCache
{
    private const int DecodeWidth = 520;
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(6);
    private static readonly Dictionary<string, Task<ImageSource?>> Cache = new(StringComparer.Ordinal);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VostokModManager (+https://github.com/RTVmods/RTV-Mod-Manager)");
        return c;
    }

    /// <summary>The image, or null when there is none or it won't load
    /// (the card keeps its plain placeholder).</summary>
    public static Task<ImageSource?> GetAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
        lock (Cache)
        {
            if (!Cache.TryGetValue(url, out var t)) Cache[url] = t = FetchAsync(url);
            return t;
        }
    }

    private static async Task<ImageSource?> FetchAsync(string url)
    {
        await Gate.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            return await Task.Run(() => Decode(bytes));
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;  // a format WIC can't read (e.g. WebP without the codec)
        }
    }
}
