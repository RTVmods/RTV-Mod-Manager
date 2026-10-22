using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>What a row's status column says, which also picks its colour.</summary>
public enum RowStatusKind { UpToDate, Update, Conflict, Testing, Locked, Disabled, Unlinked, BuiltIn, Ambiguous, Newer, Unknown }

/// <summary>One line of the Mods list: a mod, or a pack's header.</summary>
public sealed partial class ModItem : ObservableObject
{
    private readonly ModsViewModel _owner;

    public ModItem(ModsViewModel owner, ModRow row)
    {
        _owner = owner;
        Row = row;
        _isEnabled = row.Enabled;
        _priorityText = row.Priority.ToString();
        (StatusKind, Status) = StatusOf(row);
    }

    public ModRow Row { get; }
    public ModEntry? Entry => Row.Entry;
    public bool IsPackHeader => Row.IsPackHeader;
    public string Name => IsPackHeader ? Row.PackName : Row.DisplayName;
    public string Version => Row.Version;
    public int Priority => Row.Priority;
    /// <summary>The mod's place in the load among the enabled rows shown ("" when off).</summary>
    public string Position => Row.Position?.ToString() ?? "";
    public bool IsLocked => Row.Locked;
    public bool IsTesting => Row.Testing;
    public bool IsNew => Row.InstalledThisSession;
    public bool InConflict => Row.InConflict;
    public bool HasNote => Row.Note.Length > 0;
    public bool CanLock => !Row.BuiltIn;
    public string PackSummary => Row.PackCollapsed ? $"{Row.PackCount} mods · collapsed" : $"{Row.PackCount} mods";
    public string Status { get; }
    public RowStatusKind StatusKind { get; }

    /// <summary>The row's hover text: what the old grid's name and update
    /// cells said (update state, built in, locked, testing, the note).</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (Row.BuiltIn) lines.Add("Built into the manager; kept on in every profile and updated with it.");
            if (Row.Locked && !Row.BuiltIn) lines.Add("Locked: profile switches leave this mod's file and version alone.");
            if (Row.Testing) lines.Add("Marked as a testing build.");
            if (Row.InstalledThisSession) lines.Add("Installed in this session.");
            if (Row.Update?.Tooltip is { Length: > 0 } u) lines.Add(u);
            if (Row.Note.Length > 0) lines.Add("Note: " + Row.Note);
            return string.Join("\n\n", lines);
        }
    }

    /// <summary>Two-way with the row's checkbox: ticking it enables the mod.</summary>
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (Entry == null || value == Row.Enabled) return;
        _owner.SetEnabled(this, value);
    }

    /// <summary>The load-order box, edited in place; committed on Enter / leaving it.</summary>
    [ObservableProperty] private string _priorityText;

    public void CommitPriority()
    {
        if (Entry == null || PriorityText.Trim() == Row.Priority.ToString()) return;
        if (!_owner.Session.SetModPriorityFromText(Entry, PriorityText)) PriorityText = Row.Priority.ToString();
    }

    /// <summary>0–100 while this mod downloads, -1 for an unknown size,
    /// null when it isn't downloading. Drives the band along the row.</summary>
    [ObservableProperty] private double? _download;
    public bool IsDownloading => Download != null;
    partial void OnDownloadChanged(double? value) => OnPropertyChanged(nameof(IsDownloading));

    [RelayCommand]
    private Task StatusClick() => _owner.ActivateStatusAsync(this);

    private static (RowStatusKind, string) StatusOf(ModRow r)
    {
        if (r.IsPackHeader) return (RowStatusKind.Unknown, "");
        if (r.BuiltIn) return (RowStatusKind.BuiltIn, "Built in");
        var u = r.Update;
        if (u?.State == ModUpdateState.Outdated) return (RowStatusKind.Update, $"Update → {u.Latest}");
        if (r.InConflict) return (RowStatusKind.Conflict, "Conflict");
        if (r.Testing) return (RowStatusKind.Testing, "Testing build");
        if (r.Locked) return (RowStatusKind.Locked, "Locked");
        if (!r.Enabled) return (RowStatusKind.Disabled, "Disabled");
        return u?.State switch
        {
            ModUpdateState.Unlinked => (RowStatusKind.Unlinked, "Not linked"),
            ModUpdateState.Unknown => (RowStatusKind.Unknown, "Not checked yet"),
            ModUpdateState.Ambiguous => (RowStatusKind.Ambiguous, "Same release"),
            ModUpdateState.NewerLocal => (RowStatusKind.Newer, "Newer than site"),
            _ => (RowStatusKind.UpToDate, "Up to date"),
        };
    }
}

/// <summary>A filter tab above the list, with its count.</summary>
public sealed partial class FilterTab : ObservableObject
{
    public FilterTab(string key, string label) { Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;
}

public sealed partial class ModsViewModel : PageViewModel
{
    public ModsViewModel(ManagerSession session) : base(session)
    {
        Tabs = new ObservableCollection<FilterTab>
        {
            new("all", "All"), new("enabled", "Enabled"), new("disabled", "Disabled"),
            new("updates", "Updates"), new("conflicts", "Conflicts"), new("testing", "Testing"),
        };
        Tabs[0].IsActive = true;
    }

    public ObservableCollection<FilterTab> Tabs { get; }
    public ObservableCollection<ModItem> Rows { get; } = new();

    /// <summary>The mods in display order without pack headers: what a
    /// drag reorder counts positions in.</summary>
    public IReadOnlyList<ModEntry> Displayed { get; private set; } = Array.Empty<ModEntry>();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private ModItem? _selected;
    [ObservableProperty] private string _profileName = "";

    // Status chips and footer
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _unlinkedCount;
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private string _installProgress = "";

    /// <summary>Rows the list has selected (several with Ctrl/Shift).</summary>
    public List<ModItem> MultiSelection { get; } = new();

    // Header: profile switcher, setup issues, checkpoint, update summary
    public ObservableCollection<string> ProfileChoices { get; } = new();
    [ObservableProperty] private string _setupIssues = "";
    [ObservableProperty] private string _checkpointText = "";
    [ObservableProperty] private bool _hasCheckpoint;
    [ObservableProperty] private string _updatesSummary = "";

    /// <summary>Set by the shell: the import flow (modpack by URL) and the setup window.</summary>
    public Wpf.Services.ImportFlow? Imports { get; set; }
    public Func<Task>? OpenSetup { get; set; }

    [RelayCommand]
    private void SwitchProfile(string name)
    {
        var target = Session.AllProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target != null) Session.SwitchActiveProfile(target);
    }

    [RelayCommand]
    private async Task RunSetup()
    {
        if (OpenSetup != null) await OpenSetup();
    }

    [RelayCommand]
    private void ClearQuery() => Query = "";

    /// <summary>Refresh: rescan the folder, re-detect conflicts and check
    /// VostokMods again now (the old toolbar's Refresh).</summary>
    [RelayCommand]
    private async Task RefreshAll()
    {
        Busy = true;
        try
        {
            Session.Refresh();
            await Session.CheckUpdatesAsync(forceFresh: true);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void OpenItemPage(ModItem? item) => OpenPage(item);

    partial void OnQueryChanged(string value) => Rebuild();
    partial void OnFilterChanged(string value)
    {
        foreach (var t in Tabs) t.IsActive = t.Key == value;
        Rebuild();
    }

    [RelayCommand]
    private void SetFilter(string key) => Filter = key;

    public override void Reload() => Rebuild();

    private bool _rebuilding;

    private void Rebuild()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            ProfileName = Session.ActiveProfile?.Name ?? "All installed mods";

            // Counts come from the unfiltered list, so the tabs show totals.
            var all = Session.BuildModRows(new ModListQuery()).Rows.Where(r => !r.IsPackHeader).ToList();
            Count("all", all.Count);
            Count("enabled", all.Count(r => r.Enabled));
            Count("disabled", all.Count(r => !r.Enabled));
            Count("updates", all.Count(IsUpdate));
            Count("conflicts", all.Count(r => r.InConflict));
            Count("testing", all.Count(r => r.Testing));
            UpdateCount = all.Count(IsUpdate);
            ConflictCount = Session.LastConflicts.Count;
            UnlinkedCount = Session.UnlinkedMods().Count(e => !Session.IsBuiltInActive(e.ModId));
            DriftText = Session.GetDriftStatus() is { Count: > 0 } d
                ? $"{d.Count} change(s) not saved to '{d.ProfileName}' · Save to profile"
                : "";
            Footer = $"{all.Count(r => r.Enabled)} of {all.Count} enabled";
            UpdatesSummary = string.Join("\n", new[] { Session.SummarizeUpdates(), Session.UnlinkedHint() }.Where(s => s.Length > 0));
            SetupIssues = string.Join("\n", Session.GetSetupIssues());
            var cp = Session.GetCheckpointStatus();
            HasCheckpoint = cp.Any;
            CheckpointText = cp.LastLaunch != null ? $"Checkpoint from before the last launch {cp.LastLaunch.Age}"
                : cp.LastKnownGood != null ? $"Last clean session {cp.LastKnownGood.Age}" : "";
            ProfileChoices.Clear();
            foreach (var p in Session.AllProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) ProfileChoices.Add(p.Name);

            var view = Session.BuildModRows(new ModListQuery(Query.Trim()));
            Displayed = view.Displayed;
            var keep = Selected?.Entry?.Path;
            Rows.Clear();
            foreach (var r in view.Rows)
            {
                if (!r.IsPackHeader && !Passes(r)) continue;
                Rows.Add(new ModItem(this, r));
            }
            // A pack header whose mods were all filtered out goes too.
            for (var i = Rows.Count - 1; i >= 0; i--)
            {
                if (!Rows[i].IsPackHeader || Filter == "all") continue;
                var next = i + 1 < Rows.Count ? Rows[i + 1] : null;
                if (next == null || next.IsPackHeader) Rows.RemoveAt(i);
            }
            Selected = Rows.FirstOrDefault(r => r.Entry != null && r.Entry.Path == keep)
                       ?? Rows.FirstOrDefault(r => !r.IsPackHeader);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static bool IsUpdate(ModRow r) => r.Update?.State == ModUpdateState.Outdated;

    private bool Passes(ModRow r) => Filter switch
    {
        "enabled" => r.Enabled,
        "disabled" => !r.Enabled,
        "updates" => IsUpdate(r),
        "conflicts" => r.InConflict,
        "testing" => r.Testing,
        _ => true,
    };

    private void Count(string key, int n)
    {
        var t = Tabs.FirstOrDefault(x => x.Key == key);
        if (t != null) t.Count = n;
    }

    // ── row actions ───────────────────────────────────────────────────

    internal void SetEnabled(ModItem item, bool on)
    {
        if (item.Entry == null) return;
        var targets = MultiSelection.Count > 1 && MultiSelection.Contains(item)
            ? MultiSelection.Where(m => m.Entry != null).Select(m => m.Entry!).ToList()
            : null;
        if (targets != null) Session.BulkToggleSelected(targets, on);
        else Session.SetModEnabled(item.Entry, on);
    }

    private IReadOnlyList<ModEntry> Targets(ModItem? item)
    {
        if (item?.Entry == null) return Array.Empty<ModEntry>();
        if (MultiSelection.Count > 1 && MultiSelection.Contains(item))
            return MultiSelection.Where(m => m.Entry != null).Select(m => m.Entry!).ToList();
        return new[] { item.Entry };
    }

    [RelayCommand]
    private void TogglePack(ModItem? item)
    {
        if (item is { IsPackHeader: true }) Session.TogglePackCollapse(item.Row.PackName);
        Rebuild();
    }

    [RelayCommand]
    private void SetLoadOrder(ModItem? item)
    {
        var t = Targets(item ?? Selected);
        if (t.Count == 1) Session.PromptModPriority(t[0]);
        else if (t.Count > 1) Session.PromptBulkSetPriority(t);
    }

    [RelayCommand]
    private void ToggleLock(ModItem? item)
    {
        var t = Targets(item ?? Selected);
        if (t.Count == 1) Session.ToggleLock(t[0]);
        else if (t.Count > 1) Session.BulkSetLocked(t, !t.All(Session.IsLocked));
        Rebuild();
    }

    [RelayCommand]
    private void ToggleTesting(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is { } e) Session.ToggleTesting(e);
        Rebuild();
    }

    [RelayCommand]
    private void EditNote(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is { } e && Session.EditModNote(e)) Rebuild();
    }

    [RelayCommand]
    private void Delete(ModItem? item)
    {
        var t = Targets(item ?? Selected);
        if (t.Count == 1) Session.DeleteMod(t[0]);
        else if (t.Count > 1) Session.DeleteMods(t);
    }

    [RelayCommand]
    private void ShowInFolder(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is not { } e) return;
        try { Process.Start("explorer.exe", $"/select,\"{e.Path}\""); } catch { /* nothing to show */ }
    }

    [RelayCommand]
    private void OpenPage(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is not { } e || !e.Source.IsValid) return;
        try { Process.Start(new ProcessStartInfo(e.Source.PageUrl) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void EnableAll() => Session.BulkToggle(true);

    [RelayCommand]
    private void DisableAll() => Session.BulkToggle(false);

    // ── install ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallFromUrl()
    {
        Busy = true;
        try
        {
            var progress = new Progress<(long done, long total)>(p =>
                InstallProgress = p.total > 0 ? $"Downloading … {p.done * 100 / p.total}%" : "Downloading …");
            var result = await Session.InstallFromVostokModsUrlAsync(progress);
            if (result is { InstalledModIds.Count: > 0 })
                await Session.CheckAndPromptMissingDepsAsync(result.InstalledModIds, MissingDepsUi);
        }
        finally { Busy = false; InstallProgress = ""; }
    }

    [RelayCommand]
    private async Task ImportModpackUrl()
    {
        if (Imports == null) return;
        var import = await Session.AskForModpackAsync();
        if (import != null) await Imports.RunAsync(import, null);
    }

    [RelayCommand]
    private void SaveListTemplate()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save a mod-list template",
            FileName = ManagerSession.ModListTemplateFileName,
            Filter = "Mod list (*.json)|*.json",
        };
        if (dlg.ShowDialog() == true) Session.SaveModListTemplate(dlg.FileName);
    }

    /// <summary>Set by the shell: the window that lets the user paste a
    /// page for each missing dependency (null = only fetch known sources).</summary>
    public Func<MissingDependencyResolver, Task>? MissingDepsUi { get; set; }

    [RelayCommand]
    private async Task InstallFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Install mods",
            Filter = "Mods, modpacks and mod lists (*.vmz;*.zip;*.json)|*.vmz;*.zip;*.json|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        await DropAsync(dlg.FileNames);
    }

    /// <summary>Files dropped on the list or picked in Install mod….
    /// Mods install here; modpacks and mod lists go to the import.</summary>
    public async Task DropAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(ManagerSession.IsAcceptedDropPath).ToList();
        if (list.Count == 0) return;
        var progress = new Progress<InstallProgress>(p =>
            InstallProgress = p.Done < p.Total ? $"Installing {p.FileName} ({p.Done + 1} of {p.Total})" : "");
        var result = await Session.HandleFileDropAsync(list, progress);
        InstallProgress = "";
        // What the new mods need and isn't here: fetch the ones with a known source.
        if (result.Install is { InstalledModIds.Count: > 0 } installed)
            await Session.CheckAndPromptMissingDepsAsync(installed.InstalledModIds, MissingDepsUi);
        if (result.ModpackFiles.Count > 0 || result.ModListFiles.Count > 0)
            ImportRequested?.Invoke(result.ModpackFiles.Concat(result.ModListFiles).ToList());
    }

    /// <summary>Modpack zips and mod-list files the user dropped; the
    /// shell hands them to the import.</summary>
    public event Action<IReadOnlyList<string>>? ImportRequested;

    /// <summary>Moves `moved` to the drop position in the display order.</summary>
    public void Reorder(IReadOnlyList<ModItem> moved, ModItem target)
    {
        if (target.Entry == null) return;
        var entries = moved.Where(m => m.Entry != null).Select(m => m.Entry!).ToList();
        var index = Displayed.ToList().IndexOf(target.Entry);
        if (entries.Count == 0 || index < 0) return;
        Session.DragReorder(Displayed, entries, index);
    }

    // ── updates, links, drift ─────────────────────────────────────────

    [ObservableProperty] private string _driftText = "";
    [ObservableProperty] private bool _busy;

    /// <summary>Download progress: the status text, plus the band along
    /// the downloading mod's row (found by path, since rows are rebuilt).</summary>
    private IProgress<DownloadProgress> DownloadReporter(string label)
        => new Progress<DownloadProgress>(p =>
        {
            InstallProgress = p.StatusText(label);
            foreach (var r in Rows)
            {
                if (r.Entry == null) continue;
                var mine = string.Equals(r.Entry.Path, p.Path, StringComparison.OrdinalIgnoreCase);
                r.Download = mine ? (p.Percent < 0 ? -1 : p.Percent) : null;
            }
        });

    private void ClearDownloadBands()
    {
        foreach (var r in Rows) r.Download = null;
    }

    /// <summary>The Status cell's click, as the old Update column: update
    /// an outdated mod, link an unlinked one, explain a "same release".</summary>
    internal async Task ActivateStatusAsync(ModItem item)
    {
        if (item.Entry == null || Busy) return;
        Busy = true;
        try { await Session.ActivateUpdateCellAsync(item.Entry, DownloadReporter(item.Name)); }
        finally { Busy = false; InstallProgress = ""; ClearDownloadBands(); }
    }

    [RelayCommand]
    private async Task UpdateSelected()
    {
        if (Selected?.Entry is not { } e || Busy) return;
        Busy = true;
        try { await Session.UpdateModAsync(e, DownloadReporter(Selected.Name)); }
        finally { Busy = false; InstallProgress = ""; ClearDownloadBands(); }
    }

    [RelayCommand]
    private async Task UpdateAll()
    {
        if (Busy) return;
        var outdated = Session.GetOutdatedMods();
        if (outdated.Count == 0) return;
        if (!Session.Prompt.Confirm("Update all",
                $"Download the latest version of {outdated.Count} mod(s)?\n\n"
                + string.Join("\n", outdated.Take(12).Select(m => "  • " + (string.IsNullOrEmpty(m.DisplayName) ? m.ModId : m.DisplayName)))
                + (outdated.Count > 12 ? $"\n  …and {outdated.Count - 12} more" : "")
                + "\n\nEach mod's current file is backed up first, so it can be reverted."))
            return;
        Busy = true;
        try { await Session.UpdateAllAsync(DownloadReporter("mods"), null); }
        finally { Busy = false; InstallProgress = ""; ClearDownloadBands(); }
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        Busy = true;
        try { await Session.CheckUpdatesAsync(forceFresh: true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task LinkAll()
    {
        Busy = true;
        try { await Session.AutoLinkModsAsync(); }
        catch (Exception ex) { Session.Prompt.Error("Couldn't reach VostokMods", ex); }
        finally { Busy = false; }
    }

    /// <summary>Link a mod to its VostokMods page, or change the page an
    /// already-linked mod points at.</summary>
    [RelayCommand]
    private async Task LinkSelected(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is { } e) await Session.LinkModAsync(e);
    }

    [RelayCommand]
    private void SyncDrift() => Session.SyncLiveStateIntoActiveProfile(DriftReviewer);

    /// <summary>Set by the shell: the full windows for conflicts, the
    /// dependency picker, earlier versions and the drift review.</summary>
    public Action<string?>? ConflictsViewer { get; set; }
    public Func<ModEntry, Task>? DependencyEditor { get; set; }
    public Func<ModEntry, Task>? VersionsViewer { get; set; }
    public Func<IReadOnlyList<DriftItem>, bool>? DriftReviewer { get; set; }

    [RelayCommand]
    private void ShowConflicts(ModItem? item)
    {
        var focus = (item ?? (ConflictsViewer == null ? null : Selected))?.Entry?.ModId;
        if (ConflictsViewer != null) ConflictsViewer(item == null ? null : focus);
        else Filter = "conflicts";
    }

    [RelayCommand]
    private async Task EditDependencies(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is { } e && DependencyEditor != null) await DependencyEditor(e);
    }

    [RelayCommand]
    private async Task Revert(ModItem? item)
    {
        if ((item ?? Selected)?.Entry is { } chosen && VersionsViewer != null)
        {
            await VersionsViewer(chosen);
            return;
        }
        if (Selected?.Entry is not { } e) return;
        var backups = Session.GetBackups(e);
        if (backups.Count == 0)
        {
            Session.Prompt.Inform("Nothing to revert to",
                "There are no earlier versions of this mod. A copy is kept each time the manager updates a mod.");
            return;
        }
        var pick = Session.Prompt.Choose("Revert to an earlier version",
            $"Pick the copy of {Selected.Name} to put back. The current file is kept as a backup too.",
            backups.Take(5).Select(b => $"{b.VersionLabel}  ·  {b.WhenLabel}").ToList());
        if (pick < 0) return;
        Busy = true;
        try { await Session.RevertModFromBackupAsync(e, backups[pick].Path); }
        finally { Busy = false; }
    }

    /// <summary>"Ask Claude": resolves a file overlap (AI edition). A safe
    /// merge can be written into one of the mods; anything else is
    /// explained.</summary>
    [RelayCommand]
    private async Task ResolveConflict(ConflictView view)
    {
#if AI_RESOLVER
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            await Views.ResolutionWindow.ShowAsync(owner, Session, view.Conflict);
#else
        await Task.CompletedTask;
#endif
    }

    // ── the selected mod (details panel) ──────────────────────────────

    [ObservableProperty] private string _detailTab = "overview";
    [ObservableProperty] private string _remoteDescription = "";
    [ObservableProperty] private string _changelogText = "";

    [RelayCommand]
    private void SetDetailTab(string tab)
    {
        DetailTab = tab;
        if (tab == "changelog" && Selected?.Entry is { } e)
        {
            var log = Session.ReadChangelog(e);
            ChangelogText = log == null
                ? "This mod's archive has no changelog file. Its VostokMods page may list the changes."
                : log.Text;
        }
    }

    public string SelectedUpdateLabel => Selected?.Row.Update is { State: ModUpdateState.Outdated } u ? $"Update to {u.Latest}" : "";
    public bool SelectedCanUpdate => SelectedUpdateLabel.Length > 0 && !(Selected?.Row.Locked ?? true);
    public IReadOnlyList<ConflictView> SelectedConflictViews =>
        Selected?.Entry is { } e ? Session.ConflictsInvolving(e) : Array.Empty<ConflictView>();
    public IReadOnlyList<DependencyView> SelectedDependencies =>
        Selected?.Entry is { } e ? Session.GetDependencies(e).Rows : Array.Empty<DependencyView>();

    private int _descriptionToken;

    /// <summary>Shows the cached VostokMods description at once, then
    /// fetches a fresh one in the background (linked mods only).</summary>
    private async void LoadDescription(ModEntry? e)
    {
        var token = ++_descriptionToken;
        RemoteDescription = e == null ? "" : Session.GetCachedDescription(e);
        if (e == null || !e.Source.IsValid) return;
        try
        {
            var r = await Session.FetchDescriptionAsync(e);
            if (token == _descriptionToken && r.Ok) RemoteDescription = r.Text;
        }
        catch { /* the cached text (or the mod.txt one) stays */ }
    }

    [RelayCommand]
    private void RefreshDescription() => LoadDescription(Selected?.Entry);

    public string SelectedName => Selected?.Name ?? "";
    public string SelectedMeta
    {
        get
        {
            if (Selected?.Entry is not { } e) return "";
            var parts = new List<string> { $"v{e.Version}", $"load order {e.Priority}" };
            parts.Add(e.Source.IsValid ? e.Source.Id : "not linked to VostokMods");
            return string.Join(" · ", parts);
        }
    }
    public string SelectedDescription => Selected?.Entry is not { } e ? "" :
        !string.IsNullOrWhiteSpace(RemoteDescription) && !RemoteDescription.StartsWith("(") ? RemoteDescription
        : string.IsNullOrWhiteSpace(e.Description)
            ? "This mod's mod.txt has no description. Linked mods show the description from their VostokMods page."
            : e.Description;

    partial void OnRemoteDescriptionChanged(string value) => OnPropertyChanged(nameof(SelectedDescription));
    public string SelectedNote => Selected?.Entry is { } e ? Session.GetModNote(e) : "";
    public bool SelectedIsLinked => Selected?.Entry?.Source.IsValid == true;
    public bool SelectedCanLock => Selected?.CanLock == true;
    public bool SelectedHasChangelog => Selected?.Entry is { } e && ManagerSession.HasInternalChangelog(e);
    public bool SelectedHasBackups => Selected?.Entry is { } e && Session.GetBackups(e).Count > 0;
    public bool SelectedHasNoDependencies => Selected?.Entry != null && SelectedDependencies.Count == 0;
    public bool SelectedIsTesting => Selected?.IsTesting == true;
    public bool SelectedIsLocked => Selected?.IsLocked == true;
    public IReadOnlyList<string> SelectedRequires => Selected?.Entry?.RequiredDependencies ?? new List<string>();
    public IReadOnlyList<ConflictDetector.Conflict> SelectedConflicts =>
        Selected?.Entry is not { } e || string.IsNullOrEmpty(e.ModId)
            ? Array.Empty<ConflictDetector.Conflict>()
            : Session.LastConflicts.Where(c => c.ModIds.Contains(e.ModId, StringComparer.OrdinalIgnoreCase)).ToList();

    partial void OnSelectedChanged(ModItem? oldValue, ModItem? newValue)
    {
        foreach (var name in new[]
                 {
                     nameof(SelectedName), nameof(SelectedMeta), nameof(SelectedDescription), nameof(SelectedNote),
                     nameof(SelectedIsLinked), nameof(SelectedIsTesting), nameof(SelectedIsLocked),
                     nameof(SelectedRequires), nameof(SelectedConflicts), nameof(SelectedUpdateLabel),
                     nameof(SelectedCanUpdate), nameof(SelectedConflictViews), nameof(SelectedDependencies),
                     nameof(SelectedCanLock), nameof(SelectedHasChangelog), nameof(SelectedHasBackups),
                     nameof(SelectedHasNoDependencies),
                 })
            OnPropertyChanged(name);
        // A rebuild re-selects the same mod; keep its description and tab.
        if (oldValue?.Entry?.Path != newValue?.Entry?.Path)
        {
            LoadDescription(newValue?.Entry);
            if (DetailTab == "changelog") SetDetailTab("changelog");
        }
    }

    /// <summary>The note box in the details panel lost focus.</summary>
    public void SaveNote(string text)
    {
        if (Selected?.Entry is not { } e) return;
        if (text == Session.GetModNote(e)) return;
        Session.SetModNote(e, text);
        Rebuild();
    }
}
