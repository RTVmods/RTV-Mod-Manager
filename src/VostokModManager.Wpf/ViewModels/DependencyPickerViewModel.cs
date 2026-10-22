using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A mod id that can be ticked as required.</summary>
public sealed partial class DependencyChoiceItem : ObservableObject
{
    public DependencyChoiceItem(string modId, bool isChecked, string name, string state, string stateBrushKey)
    {
        ModId = modId;
        _isChecked = isChecked;
        Name = name;
        State = state;
        StateBrushKey = stateBrushKey;
    }

    public string ModId { get; }
    /// <summary>The mod's display name when installed and different from the id.</summary>
    public string Name { get; }
    public string State { get; }
    public string StateBrushKey { get; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>The required-dependency picker: every installed mod id (and
/// declared ids that aren't installed) as a filterable checklist, plus
/// an add-by-id box; Save rewrites the mod's mod.txt.</summary>
public sealed partial class DependencyPickerViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private readonly ModEntry _mod;

    public DependencyPickerViewModel(ManagerSession session, ModEntry mod)
    {
        _session = session;
        _mod = mod;
        ModLabel = !string.IsNullOrEmpty(mod.DisplayName) ? mod.DisplayName
            : !string.IsNullOrEmpty(mod.ModId) ? mod.ModId
            : Path.GetFileName(mod.Path);
        Header = session.GetDependencies(mod).Header;
        foreach (var c in session.GetDependencyChoices(mod)) Items.Add(MakeItem(c.ModId, c.Checked));
        foreach (var i in Items) i.PropertyChanged += OnItemChanged;

        var optional = mod.OptionalDependencies;
        OptionalNote = optional.Count == 0 ? ""
            : $"Optional dependencies ({string.Join(", ", optional)}) stay as they are; this list sets the required ones.";

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is DependencyChoiceItem i && Matches(i);
    }

    public string ModLabel { get; }
    public string Header { get; }
    public string PromptText => $"Tick the mods that `{ModLabel}` requires. Filter the list below, or add an id "
                                + "that isn't installed yet. Saving rewrites `[dependencies] required = …` in its mod.txt.";
    public string OptionalNote { get; }
    public bool HasOptionalNote => OptionalNote.Length > 0;

    public ObservableCollection<DependencyChoiceItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    [ObservableProperty] private string _filter = "";
    partial void OnFilterChanged(string value) => ItemsView.Refresh();

    [ObservableProperty] private string _newId = "";
    partial void OnNewIdChanged(string value) => AddCommand.NotifyCanExecuteChanged();

    [ObservableProperty] private bool _isSaving;

    public string CountText
    {
        get
        {
            var n = Items.Count(i => i.IsChecked);
            return n == 0 ? "No required dependencies ticked" : n == 1 ? "1 required dependency ticked" : $"{n} required dependencies ticked";
        }
    }

    /// <summary>True once Save wrote mod.txt.</summary>
    public bool Saved { get; private set; }

    private bool Matches(DependencyChoiceItem i)
    {
        var f = (Filter ?? "").Trim();
        return f.Length == 0
               || i.ModId.Contains(f, StringComparison.OrdinalIgnoreCase)
               || i.Name.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private DependencyChoiceItem MakeItem(string modId, bool isChecked)
    {
        var e = _session.Registry.FindById(modId);
        if (e == null)
            return new DependencyChoiceItem(modId, isChecked, "", "not installed", "B.AmberText");
        var name = !string.IsNullOrEmpty(e.DisplayName) && !string.Equals(e.DisplayName, modId, StringComparison.OrdinalIgnoreCase)
            ? e.DisplayName : "";
        return e.IsEnabled
            ? new DependencyChoiceItem(modId, isChecked, name, "", "B.Text3")
            : new DependencyChoiceItem(modId, isChecked, name, "disabled", "B.Text3");
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DependencyChoiceItem.IsChecked)) OnPropertyChanged(nameof(CountText));
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewId);

    /// <summary>Adds a typed id ticked (or ticks it when listed), first in
    /// the list, and clears the filter so it shows.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var id = (NewId ?? "").Trim();
        if (id.Length == 0) return;
        var existing = Items.FirstOrDefault(i => string.Equals(i.ModId, id, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.IsChecked = true;
        else
        {
            var item = MakeItem(id, true);
            item.PropertyChanged += OnItemChanged;
            Items.Insert(0, item);
        }
        NewId = "";
        Filter = "";
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand] private void ClearAll() { foreach (var i in Items) i.IsChecked = false; }

    /// <summary>Writes the ticked ids as the mod's required list. The
    /// session may ask to raise the mod's priority above a new dependency.</summary>
    public async Task SaveAsync()
    {
        if (IsSaving) return;
        IsSaving = true;
        try
        {
            var ticked = Items.Where(i => i.IsChecked).Select(i => i.ModId).ToList();
            Saved = await _session.SetModDependenciesAsync(_mod, ticked);
        }
        finally { IsSaving = false; }
    }
}
