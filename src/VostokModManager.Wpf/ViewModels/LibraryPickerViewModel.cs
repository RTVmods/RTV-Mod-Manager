using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A library mod that can be ticked to add.</summary>
public sealed partial class LibraryChoiceItem : ObservableObject
{
    private readonly Action _changed;

    public LibraryChoiceItem(LibraryRow row, Action changed)
    {
        Row = row;
        _changed = changed;
    }

    public LibraryRow Row { get; }
    public string Name => Row.Name;
    public string Version => Row.Version;
    public string Detail => Row.Profiles.Count == 0 ? Row.SizeText : $"In {Row.ProfilesText}  ·  {Row.SizeText}";

    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>"Add from the library": the mods the library holds that the
/// active profile doesn't have, newest version of each, as a filterable
/// checklist. Add hands the ticked files to the Mods page's install.</summary>
public sealed partial class LibraryPickerViewModel : ObservableObject
{
    private readonly LibraryModel _library;

    public LibraryPickerViewModel(ManagerSession session)
    {
        _library = session.OpenLibrary();
        ProfileName = session.ActiveProfile?.Name ?? "the live mods";
        foreach (var row in _library.AddableRows())
            Items.Add(new LibraryChoiceItem(row, OnTicked));
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is LibraryChoiceItem i && _library.MatchesFilter(i.Row.Entry);
        OnTicked();
    }

    public string ProfileName { get; }
    public ObservableCollection<LibraryChoiceItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty] private string _filter = "";
    partial void OnFilterChanged(string value)
    {
        _library.Filter = value;
        ItemsView.Refresh();
    }

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _addText = "";

    /// <summary>The ticked library files, set when the user pressed Add.</summary>
    public IReadOnlyList<string> Picked { get; private set; } = Array.Empty<string>();

    private IEnumerable<LibraryChoiceItem> Ticked => Items.Where(i => i.IsChecked);

    private void OnTicked()
    {
        var n = Ticked.Count();
        CountText = IsEmpty
            ? $"Every mod in the library is already in {ProfileName}."
            : $"{Items.Count} mod(s) in the library aren't in {ProfileName}" + (n > 0 ? $"  ·  {n} ticked" : "");
        AddText = n > 1 ? $"Add {n} mods" : "Add";
        AddCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd() => Ticked.Any();

    /// <summary>Raised once Add has recorded the pick; the window closes.</summary>
    public event Action? Done;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        Picked = _library.PathsToInstall(Ticked.Select(i => i.Row.Entry));
        Done?.Invoke();
    }
}
