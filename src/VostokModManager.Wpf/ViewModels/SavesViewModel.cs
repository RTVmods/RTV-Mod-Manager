using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One save slot's card.</summary>
public sealed partial class SlotCard : ObservableObject
{
    private readonly SavesViewModel _owner;
    private bool _loading;

    public SlotCard(SavesViewModel owner, SaveSlotView slot, IReadOnlyList<string> profileChoices)
    {
        _owner = owner;
        Slot = slot;
        ProfileChoices = profileChoices;
        _loading = true;
        TiedProfile = slot.Profile.Length == 0 ? AnyProfile : slot.Profile;
        _loading = false;
    }

    public const string AnyProfile = "(any profile)";

    public SaveSlotView Slot { get; }
    public IReadOnlyList<string> ProfileChoices { get; }

    public string SlotLabel => $"SLOT {Slot.Number}";
    public string Title => Slot.Occupied ? Slot.Title : $"Slot {Slot.Number}";
    public string Where => string.Join(" · ", new[]
    {
        Slot.LastMap,
        Slot.LastPlayed is { } t ? Ago(t) : "",
    }.Where(s => s.Length > 0));
    public string RunsUnder => Slot.RunsUnder.Length == 0 ? "Runs with the live profile" : $"{Slot.RunsUnder} profile";

    /// <summary>The line about the mods this save needs, and its tone.</summary>
    public string ModCheck => !Slot.Occupied ? ""
        : Slot.ProfileMissing ? $"Tied to '{Slot.Profile}', which no longer exists"
        : Slot.ModsPresent ? "Every mod this save's profile needs is here"
        : $"Missing: {string.Join(", ", Slot.MissingMods.Take(3))}{(Slot.MissingMods.Count > 3 ? $" +{Slot.MissingMods.Count - 3}" : "")}";
    public bool ModCheckOk => Slot.Occupied && Slot.ModsPresent && !Slot.ProfileMissing;
    public bool ModCheckBad => Slot.Occupied && (!Slot.ModsPresent || Slot.ProfileMissing);
    public bool IsNext => Slot.IsActiveSlot && Slot.Occupied;
    public bool IsEmpty => !Slot.Occupied;

    [ObservableProperty] private string _tiedProfile = AnyProfile;

    [RelayCommand] private void Launch() => _owner.LaunchCommand.Execute(this);
    [RelayCommand] private void NewGame() => _owner.NewGameCommand.Execute(this);
    [RelayCommand] private void Rename() => _owner.RenameCommand.Execute(this);
    [RelayCommand] private void Delete() => _owner.DeleteCommand.Execute(this);

    partial void OnTiedProfileChanged(string value)
    {
        if (_loading) return;
        _owner.Session.SetSlotProfile(Slot.Number, value == AnyProfile ? null : value);
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "played just now";
        if (d.TotalMinutes < 60) return $"played {(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"played {(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "played yesterday";
        if (d.TotalDays < 14) return $"played {(int)d.TotalDays} days ago";
        return $"played {t:d MMM yyyy}";
    }
}

/// <summary>The Saves page: the Save Slots mod's slots as cards.</summary>
public sealed partial class SavesViewModel : PageViewModel
{
    public SavesViewModel(ManagerSession session) : base(session) { }

    public ObservableCollection<SlotCard> Slots { get; } = new();
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _liveProfile = "";

    public override void OnShown() => Load();
    public override void Reload() => Load();

    private void Load()
    {
        SavesView view;
        try { view = Session.GetSavesView(); }
        catch (Exception ex)
        {
            Warning = "Couldn't read the save slots: " + ex.Message;
            Slots.Clear();
            return;
        }
        Warning = view.Warning ?? "";
        LiveProfile = view.LiveProfile;
        var choices = new List<string> { SlotCard.AnyProfile };
        choices.AddRange(view.ProfileNames);
        Slots.Clear();
        foreach (var s in view.Slots) Slots.Add(new SlotCard(this, s, choices));
    }

    [RelayCommand]
    private void Launch(SlotCard card)
    {
        if (card.Slot.CanLaunch) Session.LaunchSlot(card.Slot.Number);
    }

    [RelayCommand]
    private void NewGame(SlotCard card) => Session.StartNewGameInSlot(card.Slot.Number);

    [RelayCommand]
    private void Rename(SlotCard card)
    {
        var name = Session.Prompt.AskText("Rename slot", $"A name for slot {card.Slot.Number}:", card.Slot.DisplayName);
        if (name != null && Session.RenameSlot(card.Slot.Number, name)) Load();
    }

    [RelayCommand]
    private void Delete(SlotCard card)
    {
        if (Session.DeleteSlot(card.Slot.Number)) Load();
    }

    [RelayCommand]
    private void BackUpAll()
    {
        var zip = Session.BackUpSlots();
        if (zip != null && Session.Prompt.Confirm("Backed up", $"All slots were saved to:\n\n{zip}\n\nShow it in Explorer?", PromptKind.Info))
        {
            try { Process.Start("explorer.exe", $"/select,\"{zip}\""); } catch { }
        }
    }

    [RelayCommand]
    private void OpenBackups()
    {
        Directory.CreateDirectory(AppPaths.BackupsDir);
        try { Process.Start("explorer.exe", $"\"{AppPaths.BackupsDir}\""); } catch { }
    }
}
