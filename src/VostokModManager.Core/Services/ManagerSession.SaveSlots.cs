// Saves: the Save Slots mod's slots, each tied to a manager profile, and
// the built-in Save Slots mod the manager carries.
//
// The slot list (the Saves view's cards), launching straight into a slot
// (switching to its profile first), a new game in a slot, rename, delete,
// back up, and the per-slot profile link, written into the slot's
// _meta.cfg (the same key the mod's in-game panel edits). The manager
// never copies save files; the mod does that in-game.
//
// Also the built-in mod's upkeep: turning other save-slot mods off,
// keeping Save Slots out of profile lists, and removing it when the
// feature is turned off.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>One slot, as a card in the Saves view.</summary>
/// <param name="Title">The display name, or "Slot n" without one.</param>
/// <param name="Profile">The profile the slot is tied to; "" = any profile.</param>
/// <param name="ProfileMissing">Tied to a profile that no longer exists.</param>
/// <param name="IsActiveSlot">The slot the mod last had active (the game
/// continues it; shown as "last played").</param>
/// <param name="RunsUnder">The profile a launch of this slot would run
/// under: its own, else the live one.</param>
/// <param name="ModsPresent">Every enabled mod of that profile is live or
/// in the Library, so a switch to it can put them all in place.</param>
/// <param name="MissingMods">Enabled mods of that profile found nowhere
/// ("Name v1.2").</param>
public sealed record SaveSlotView(
    int Number,
    string DisplayName,
    string Title,
    string LastMap,
    DateTime? LastPlayed,
    bool Occupied,
    bool Dead,
    string Profile,
    bool ProfileMissing,
    bool IsActiveSlot,
    string RunsUnder,
    bool ModsPresent,
    IReadOnlyList<string> MissingMods)
{
    public bool CanLaunch => Occupied && !Dead;
    public bool CanRename => Occupied;
    public bool CanDelete => Occupied || DisplayName.Length > 0 || Profile.Length > 0;
}

/// <summary>Everything the Saves view shows.</summary>
/// <param name="LiveProfile">The active profile's name ("" = none).</param>
/// <param name="Warning">SaveSlotsWarning(): why a slot launch would not
/// work as expected, or null.</param>
/// <param name="ProfileNames">Profiles a slot can be tied to, sorted.</param>
public sealed record SavesView(
    string LiveProfile,
    string? Warning,
    IReadOnlyList<string> ProfileNames,
    IReadOnlyList<SaveSlotView> Slots);

public sealed partial class ManagerSession
{
    /// <summary>The Save Slots mod's folder (MultiSaveSlots) beside the game's saves.</summary>
    public string SaveSlotsRoot => SaveSlots.RootDir(SaveSlots.DefaultUserDir);

    /// <summary>Why launching into a slot would not work as expected with
    /// the live mod set, or null when it would.</summary>
    public string? SaveSlotsWarning()
    {
        var companion = Registry.FindById(SaveSlots.CompanionModId);
        var others = SaveSlots.OtherSlotModIds
            .Select(id => Registry.FindById(id))
            .Where(e => e != null && e.IsEnabled)
            .Select(e => string.IsNullOrEmpty(e!.DisplayName) ? e.ModId : e.DisplayName)
            .ToList();
        if (!Settings.SaveSlotsBuiltIn)
            return "Built-in save slots are off (Settings → Built-in save slots), so the game won't pick a slot by itself.";
        if (BuiltInMods.SaveSlotsArchive != null)
        {
            if (others.Count > 0)
                return $"{string.Join(", ", others)} is on in this profile. It manages the same slot folder, "
                    + "and the built-in save slots stand down while it is loaded. Launching a slot offers to turn it off.";
            if (companion == null || !companion.IsEnabled)
                return "The built-in Save Slots mod isn't in the mods folder yet. Reopen the manager to install it.";
            return null;
        }
        if (companion == null || !companion.IsEnabled)
        {
            var note = "The Save Slots mod isn't live in this profile, so the game won't pick a slot by itself. "
                + "Install it from VostokMods (vostokmods.net/mod/save-slots) and enable it in every profile you tie a slot to.";
            if (others.Count > 0)
                note += $" {string.Join(", ", others)} manages the same slot folder and can stay until then.";
            return note;
        }
        if (others.Count > 0)
            return $"{string.Join(", ", others)} is live together with Save Slots. Both manage the same slot folder; "
                + "Save Slots stands down while the other is loaded. Disable one of them.";
        return null;
    }

    // ── the Saves view ────────────────────────────────────────────────

    /// <summary>The Saves view: every slot as a card, plus the header
    /// data (live profile, warning, profiles a slot can be tied to).</summary>
    public SavesView GetSavesView()
    {
        var names = AllProfiles.Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return new SavesView(ActiveProfile?.Name ?? "", SaveSlotsWarning(), names, GetSaveSlots());
    }

    /// <summary>Every slot (1..max(5, highest slot folder)), as cards.</summary>
    public IReadOnlyList<SaveSlotView> GetSaveSlots()
    {
        var root = SaveSlotsRoot;
        var slots = SaveSlots.ReadAll(root);
        var active = SaveSlots.ActiveSlot(root);

        // What a switch can put in place: the live mods and the Library
        // (any version: the switch falls back to the newest one).
        var available = new HashSet<string>(
            Registry.Entries.Select(e => e.ModId).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var lib in ModLibrary.List(ModsDir))
                if (!string.IsNullOrEmpty(lib.ModId)) available.Add(lib.ModId);
        }
        catch { /* the live list still serves */ }

        var list = new List<SaveSlotView>(slots.Count);
        foreach (var s in slots)
        {
            var tied = s.Profile.Length > 0;
            var profile = tied
                ? AllProfiles.FirstOrDefault(p => string.Equals(p.Name, s.Profile, StringComparison.OrdinalIgnoreCase))
                : ActiveProfile;
            var profileMissing = tied && profile == null;
            var missing = new List<string>();
            if (profile != null)
            {
                foreach (var pm in profile.Mods)
                {
                    if (!pm.IsEnabled || string.IsNullOrEmpty(pm.ModId)) continue;
                    if (BuiltInMods.IsBuiltIn(pm.ModId)) continue;  // the manager installs it
                    if (available.Contains(pm.ModId)) continue;
                    var label = string.IsNullOrEmpty(pm.DisplayName) ? pm.ModId : pm.DisplayName;
                    missing.Add(string.IsNullOrEmpty(pm.Version) ? label : $"{label} v{pm.Version}");
                }
            }
            list.Add(new SaveSlotView(
                s.Number,
                s.DisplayName,
                s.Title,
                s.LastMap,
                s.LastPlayed,
                s.Occupied,
                s.Dead,
                s.Profile,
                profileMissing,
                s.Number == active,
                profile?.Name ?? (tied ? s.Profile : ""),
                !profileMissing && missing.Count == 0,
                missing));
        }
        return list;
    }

    /// <summary>Ties slot n to a profile (null or "" = any profile).
    /// Returns false (after telling the user) when the link can't be written.</summary>
    public bool SetSlotProfile(int n, string? profile)
    {
        try
        {
            SaveSlots.SetProfile(SaveSlotsRoot, n, profile ?? "");
            return true;
        }
        catch (Exception ex)
        {
            Prompt.Inform("Couldn't save the link", ex.Message, PromptKind.Error);
            return false;
        }
    }

    /// <summary>Renames an occupied slot (its display_name).</summary>
    public bool RenameSlot(int n, string name)
    {
        var s = SaveSlots.Read(SaveSlotsRoot, n);
        if (!s.Occupied) return false;
        try
        {
            SaveSlots.SetDisplayName(SaveSlotsRoot, n, name ?? "");
            return true;
        }
        catch (Exception ex)
        {
            Prompt.Inform("Couldn't rename", ex.Message, PromptKind.Error);
            return false;
        }
    }

    /// <summary>Deletes slot n after confirming. Its files are zipped to
    /// the backups folder first; for the slot in play, its files in the
    /// game's save folder go too (or the mod's crash recovery copies it
    /// straight back). Returns true when the slot was deleted.</summary>
    public bool DeleteSlot(int n)
    {
        var root = SaveSlotsRoot;
        var s = SaveSlots.Read(root, n);
        if (Launcher.IsGameRunning())
        {
            Prompt.Inform("Delete", "Road to Vostok is running. Quit the game first.");
            return false;
        }
        var inPlay = s.Number == SaveSlots.ActiveSlot(root) && s.Occupied;
        if (!Prompt.Confirm("Delete save",
                $"Delete \"{s.Title}\" (slot {s.Number})?\n\n"
                + $"Its files are zipped to your {AppPaths.BackupsDir} folder first."
                + (inPlay ? "\n\nThis is the save the game would continue now; it is removed from the game's save folder too." : ""),
                PromptKind.Warning))
            return false;
        try
        {
            var zip = SlotBackupPath($"deleted-slot{s.Number}");
            var count = SaveSlots.DeleteSlot(root, SaveSlots.DefaultUserDir, s.Number, zip);
            Prompt.Inform("Deleted", $"Slot {s.Number} deleted ({count} file(s)).\n\nBackup:\n{zip}");
            return true;
        }
        catch (Exception ex)
        {
            Prompt.Inform("Delete failed", ex.Message, PromptKind.Error);
            return false;
        }
    }

    /// <summary>Zips the whole slot folder into the backups folder.
    /// Returns the zip's path, or null when it failed (the user is told
    /// either way).</summary>
    public string? BackUpSlots()
    {
        var zip = Path.Combine(AppPaths.BackupsDir, $"saves-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            var count = SaveSlots.Backup(SaveSlotsRoot, zip);
            Prompt.Inform("Slots backed up", $"{count} file(s) written to:\n\n{zip}");
            return zip;
        }
        catch (Exception ex)
        {
            Prompt.Inform("Backup failed", ex.Message, PromptKind.Error);
            return null;
        }
    }

    private static string SlotBackupPath(string what)
        => Path.Combine(AppPaths.BackupsDir, $"saves-{what}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

    // ── launching into a slot ─────────────────────────────────────────

    /// <summary>Continues slot n: switches to the slot's profile when it
    /// has one and another is live, tells the Save Slots mod which slot to
    /// continue, and launches. Returns true when the game was launched.</summary>
    public bool LaunchSlot(int n)
    {
        var s = SaveSlots.Read(SaveSlotsRoot, n);
        if (!s.Occupied) return false;
        if (s.Dead)
        {
            Prompt.Inform("Slot is dead", $"{s.Title} died. Start a new game in that slot from the game's menu.");
            return false;
        }
        return LaunchIntoSlot(n, "load", "");
    }

    /// <summary>Starts a new run in slot n. An occupied slot is replaced
    /// once the run begins, after confirming and writing a backup zip.
    /// `name` null asks for one (optional; "" keeps none). The game opens
    /// on the mode screen. Returns true when the game was launched.</summary>
    public bool StartNewGameInSlot(int n, string? name = null)
    {
        var root = SaveSlotsRoot;
        var s = SaveSlots.Read(root, n);
        if (Launcher.IsGameRunning())
        {
            Prompt.Inform("New game", "Road to Vostok is running. Quit the game first.");
            return false;
        }
        if (s.Occupied)
        {
            if (!Prompt.Confirm("Replace this save?",
                    $"Slot {s.Number} holds \"{s.Title}\". Starting a new game there replaces it once the "
                    + "run begins (a backup zip is written now).\n\nContinue?",
                    PromptKind.Warning))
                return false;
            try { SaveSlots.Backup(root, SlotBackupPath($"before-new-slot{s.Number}")); }
            catch (Exception ex)
            {
                Prompt.Inform("New game", "The backup failed, so nothing was changed:\n\n" + ex.Message,
                    PromptKind.Error);
                return false;
            }
        }
        name ??= Prompt.AskText($"New game in slot {s.Number}",
            "Name for the new save (optional). The game opens on the mode screen; pick the "
            + "difficulty and season and press Enter to start the run in this slot.",
            s.Occupied ? "" : s.DisplayName);
        if (name == null) return false;
        return LaunchIntoSlot(n, "new", name.Trim());
    }

    /// <summary>`kind` "load" continues slot n; "new" starts a new run in
    /// it named `name` (the game opens on the mode screen).</summary>
    internal bool LaunchIntoSlot(int n, string kind, string name)
    {
        var root = SaveSlotsRoot;
        var slot = SaveSlots.Read(root, n);
        if (kind == "load" && !slot.Occupied) return false;
        if (Launcher.IsGameRunning())
        {
            Prompt.Inform("Save slots", "Road to Vostok is already running. Quit the game first.");
            return false;
        }
        // A new run belongs to the profile it starts under, unless the
        // slot was already tied to one.
        if (kind == "new" && slot.Profile.Length == 0 && ActiveProfile != null)
        {
            try
            {
                SaveSlots.SetProfile(root, n, ActiveProfile.Name);
                slot = SaveSlots.Read(root, n);
            }
            catch (Exception ex)
            {
                Prompt.Error("Couldn't link the slot to the profile", ex);
                return false;
            }
        }

        var switching = slot.Profile.Length > 0
            && !string.Equals(slot.Profile, ActiveProfile?.Name, StringComparison.OrdinalIgnoreCase);
        var target = switching
            ? AllProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, slot.Profile, StringComparison.OrdinalIgnoreCase))
            : ActiveProfile;
        if (switching && target == null)
        {
            Prompt.Inform("Profile missing",
                $"{slot.Title} is tied to the profile '{slot.Profile}', which no longer exists.\n\n"
                + "Pick another profile for the slot, or '(any profile)' to run it with the live one.",
                PromptKind.Warning);
            return false;
        }

        // The game can only open the slot by itself when the profile it
        // starts under runs Save Slots, and no other slot mod. Settle that
        // before switching, so a "no" leaves everything as it was.
        if (target != null && !EnsureSaveSlotsIn(target, isActive: !switching))
            return false;

        if (switching)
        {
            if (!SwitchActiveProfile(target!)
                || !string.Equals(ActiveProfile?.Name, target!.Name, StringComparison.OrdinalIgnoreCase))
                return false;  // cancelled or failed; the switch said why
        }

        var warning = SaveSlotsWarning();
        if (warning != null
            && !Prompt.Confirm("Save Slots",
                warning + "\n\nLaunch anyway? The game will start at the main menu.",
                PromptKind.Warning))
            return false;

        try { SaveSlots.WritePending(root, n, ActiveProfile?.Name ?? "", kind, name); }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't prepare the slot", ex);
            return false;
        }
        if (!LaunchGame())
        {
            SaveSlots.ClearPending(root);
            return false;
        }
        Say(StatusArea.Launch, _lastLaunchStatus + (kind == "new"
            ? $"  ·  new game in slot {n}: pick the mode and press Enter"
            : $"  ·  continuing {slot.Title}"));
        return true;
    }

    /// <summary>Makes sure `profile` runs Save Slots and no other slot
    /// mod, asking first. False when the user declines or built-in save
    /// slots are off. For the active profile the live cfg is updated too;
    /// for another profile its profile.json is enough, the switch that
    /// follows applies it.</summary>
    internal bool EnsureSaveSlotsIn(ModProfile profile, bool isActive)
    {
        if (!Settings.SaveSlotsBuiltIn || BuiltInMods.SaveSlotsArchive == null)
        {
            Prompt.Inform("Save slots",
                "Built-in save slots are off, so the game can't open a slot by itself.\n\n"
                + "Turn them on in Settings → Built-in save slots, then launch the slot again.");
            return false;
        }
        // Save Slots itself is the manager's and on everywhere; only another
        // slot mod in the profile can stop the slot from opening.
        var others = SaveSlots.OtherSlotModIds;
        var clashing = profile.Mods.Where(m => m.IsEnabled && others.Any(o =>
            string.Equals(m.ModId, o, StringComparison.OrdinalIgnoreCase))).ToList();
        if (clashing.Count == 0) return true;
        return TurnOffOtherSlotMods(new[] { profile }, ask: true, slotLaunch: true);
    }

    // ── the built-in Save Slots mod ───────────────────────────────────

    /// <summary>Built-in save slots were turned off: remove the archive
    /// (mods folder and Library) and its row in the active profile's cfg
    /// section.</summary>
    internal void RemoveBuiltInSaveSlotsFromCfg()
    {
        try
        {
            BuiltInMods.RemoveSaveSlots(ModsDir);
            var ver = BuiltInMods.SaveSlotsVersion;
            if (ver.Length > 0 && !Config.LoadFailed)
            {
                Config.RemoveEntry(BuiltInMods.SaveSlotsId, ver);
                SaveModConfigSafely();
            }
        }
        catch (Exception ex) { Prompt.Error("Couldn't remove Save Slots", ex); }
    }

    /// <summary>Startup (and turning the feature on): built-in mods are
    /// taken out of every saved profile's list, where an earlier install
    /// may have put them, and once, other save-slot mods that are on in
    /// saved profiles are offered to be turned off.</summary>
    internal void SetUpBuiltInSaveSlots()
    {
        if (!Settings.SaveSlotsBuiltIn || BuiltInMods.SaveSlotsArchive == null) return;
        List<ModProfile> profiles;
        try { profiles = ModProfile.LoadAll(); }
        catch { return; }
        var changed = false;
        foreach (var p in profiles)
        {
            if (string.IsNullOrEmpty(p.FolderPath)) continue;
            if (p.Mods.RemoveAll(m => BuiltInMods.IsBuiltIn(m.ModId)) == 0) continue;
            try { p.SaveMetadataOnly(); changed = true; } catch { /* left as is */ }
        }
        if (changed) ReloadProfiles();
        if (Settings.SaveSlotsOtherModsAsked) return;

        var others = SaveSlots.OtherSlotModIds;
        var hits = profiles.Where(p => !string.IsNullOrEmpty(p.FolderPath)
            && p.Mods.Any(m => m.IsEnabled && others.Any(o =>
                string.Equals(m.ModId, o, StringComparison.OrdinalIgnoreCase)))).ToList();
        Settings.SaveSlotsOtherModsAsked = true;
        try { Settings.Save(); } catch { /* asked again next start */ }
        if (hits.Count > 0) TurnOffOtherSlotMods(hits, ask: true, slotLaunch: false);
    }

    /// <summary>Turns other save-slot mods off in `profiles` (after asking
    /// when `ask`): in each profile.json, and for the active profile in
    /// mod_config.cfg too. Their archives stay where they are. Returns
    /// false when the user declined or a profile couldn't be saved.</summary>
    internal bool TurnOffOtherSlotMods(IReadOnlyList<ModProfile> profiles, bool ask, bool slotLaunch)
    {
        var others = SaveSlots.OtherSlotModIds;
        bool IsOther(ProfileMod m) => m.IsEnabled && others.Any(o =>
            string.Equals(m.ModId, o, StringComparison.OrdinalIgnoreCase));
        if (ask)
        {
            var lines = profiles.Select(p =>
                $"  • {p.Name}: {string.Join(", ", p.Mods.Where(IsOther).Select(m => string.IsNullOrEmpty(m.DisplayName) ? m.ModId : m.DisplayName).Distinct())}");
            var intro = slotLaunch
                ? "This profile has another save-slot mod on. It manages the same slot folder, and the "
                    + "built-in save slots stand down while it is loaded, so the slot wouldn't open.\n\n"
                : "Save slots are now built into the manager. These profiles have another save-slot "
                    + "mod on, which manages the same slot folder; the built-in one stands down while "
                    + "it is loaded.\n\n";
            if (!Prompt.Confirm("Save slots",
                    intro + "Turn it off in:\n\n" + string.Join("\n", lines)
                    + "\n\nIts archive stays in the mods folder, and your slots stay as they are."))
                return false;
        }
        var activeTouched = false;
        foreach (var p in profiles)
        {
            var hit = p.Mods.Where(IsOther).ToList();
            if (hit.Count == 0) continue;
            foreach (var m in hit) m.IsEnabled = false;
            p.UpdatedAt = DateTime.UtcNow;
            try { p.SaveMetadataOnly(); }
            catch (Exception ex) { Prompt.Error($"Couldn't update '{p.Name}'", ex); return false; }
            if (string.Equals(p.Name, ActiveProfile?.Name, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var m in hit) Config.SetEnabled(m.ModId, m.Version, false);
                activeTouched = true;
            }
        }
        if (activeTouched) SaveModConfigSafely();
        ReloadProfiles();
        if (activeTouched) Refresh();
        return true;
    }
}
