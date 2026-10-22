// The manager's live state and workflows, with no UI. This is the logic
// that lived in the WinForms MainForm, ported so any front end can drive
// it: the front end calls these methods, listens to Changed / Status,
// and answers questions through IUserPrompt.
//
// The class is split over partial files by area (ManagerSession.*.cs).
// This file holds the shared state and the refresh pipeline every other
// area finishes with.
//
// Threading: like MainForm, a session belongs to one thread (the UI
// thread). Long work is async; events are raised on the calling thread.

#if AI_RESOLVER
using VostokModManager.Ai;
#endif
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>Which status line a message belongs to. The WinForms front
/// end had one label per area; the new one shows them as chips and
/// toasts.</summary>
public enum StatusArea { Mods, Updates, Conflicts, Profiles, Launch, Manager, Loader, Claude }

public sealed partial class ManagerSession
{
    /// <summary>Default Steam install layout, used when the user has
    /// not set a mods folder.</summary>
    public const string SteamFallbackModsDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Road to Vostok\mods";

    public Settings Settings { get; }
    public IUserPrompt Prompt { get; }

    /// <summary>True when settings.json did not exist when the session
    /// opened: nobody has run the manager on this machine yet.</summary>
    public bool IsFirstRun { get; }

    public ModRegistry Registry { get; } = new();

    /// <summary>The loader's mod_config.cfg, reloaded on every Rescan.</summary>
    public ModConfig Config { get; private set; } = new();

    public VostokModsClient Vm { get; } = new();
    public MmlVersionChecker Mml { get; } = new();
    public ManagerReleaseChecker ManagerReleases { get; } = new();
#if AI_RESOLVER
    public ClaudeCodeRunner Claude { get; } = new();
    public ConflictResolver Resolver { get; }
#endif

    /// <summary>Source key ("vostokmods:&lt;slug&gt;") → latest version
    /// on VostokMods, filled by the update check.</summary>
    public Dictionary<string, string> LatestVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>VostokMods mod UUID → slug, from the site's listing.</summary>
    public Dictionary<string, string> VmSlugById { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ConflictDetector.Conflict> LastConflicts { get; private set; } = new();

    /// <summary>The active profile, or null before the profile model
    /// has been adopted.</summary>
    public ModProfile? ActiveProfile { get; private set; }

    public List<ModProfile> AllProfiles { get; private set; } = new();

    /// <summary>Mod ids installed during this session (in memory only).</summary>
    public HashSet<string> SessionInstalled { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The live mods folder every scan and file action targets.</summary>
    public string ModsDir =>
        !string.IsNullOrEmpty(Settings.ModsDir) ? Settings.ModsDir : SteamFallbackModsDir;

    /// <summary>Raised after anything the UI shows has changed (a
    /// refresh, a profile reload). The UI re-reads the session.</summary>
    public event Action? Changed;

    /// <summary>A one-line status message for an area.</summary>
    public event Action<StatusArea, string>? Status;

    public ManagerSession(Settings settings, IUserPrompt prompt, bool isFirstRun = false)
    {
        Settings = settings;
        Prompt = prompt;
        IsFirstRun = isFirstRun;
        Config = ModConfig.Load(ModConfig.DefaultPath);
        // Stored profiles are kept in the mod loader's profile schema;
        // bring any from an earlier build of the manager up to it before
        // the first one is read.
        MetroProfile.ModloaderVersion = MmlInstall.DetectInstalledVersion(ModsDir);
        try { ModProfile.MigrateStore(); } catch { /* they still load as they are */ }
#if AI_RESOLVER
        Claude.OverridePath = settings.ClaudePath;
        Resolver = new ConflictResolver(Claude, Registry) { GameSourcePath = settings.GameSourcePath };
#endif
    }

    /// <summary>Opens the session the way the app starts: reads
    /// settings.json (noting whether it existed) and mod_config.cfg.</summary>
    public static ManagerSession Open(IUserPrompt prompt)
    {
        var firstRun = !File.Exists(Settings.Path);
        return new ManagerSession(Settings.Load(), prompt, firstRun);
    }

    internal void Say(StatusArea area, string message) => Status?.Invoke(area, message);

    internal void RaiseChanged() => Changed?.Invoke();

    // ── the refresh pipeline ──────────────────────────────────────────

    /// <summary>Rescans the mods folder and recomputes conflicts, then
    /// raises Changed. This is the one call that replaces the
    /// rescan → status → grid → conflicts sequence MainForm repeated
    /// after every action. Returns false when the mods folder could
    /// not be read.</summary>
    public bool Refresh()
    {
        var ok = Rescan();
        LastConflicts = DetectConflictsForActive();
        RaiseChanged();
        return ok;
    }

    /// <summary>Recomputes conflicts and raises Changed without a disk
    /// rescan (the registry is unchanged; the profile scope moved).</summary>
    public void Requery()
    {
        LastConflicts = DetectConflictsForActive();
        RaiseChanged();
    }

    /// <summary>Reloads mod_config.cfg and rescans the mods folder with
    /// cfg as the source of truth for enabled state and priority, then
    /// reconciles the active profile with what is live.</summary>
    public bool Rescan()
    {
        Config = ModConfig.Load(ModConfig.DefaultPath);
        EnsureBuiltIns();
        RecordDownloadedSources();
        var ok = Registry.Scan(ModsDir, Config);
        // A Multi Save Slots archive dropped in later is retired before
        // orphan adoption could put it back into the profile.
        if (RetireOtherSlotMods(everywhere: false) > 0) Registry.Scan(ModsDir, Config);
        AdoptLoaderActiveProfile();
        AutoAdoptOrphansIntoActiveProfile();
        FollowLiveVersions();
        BackfillProfileSources();
        return ok;
    }

    /// <summary>Makes the active profile record the version of each mod
    /// that is actually live. A mod can change version without the
    /// manager doing it (a file dropped into the mods folder, an update
    /// from the in-game loader), and a profile that still names the old
    /// version would reinstall it from the library on the next switch
    /// back. The live file is archived so the switch can restore it,
    /// and the old version's cfg state is carried to the new key when
    /// the new one has none, as the manager's own update does.</summary>
    internal void FollowLiveVersions()
    {
        if (ActiveProfile == null) return;
        var profileDirty = false;
        var cfgDirty = false;
        foreach (var pm in ActiveProfile.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            var live = Registry.FindById(pm.ModId);
            if (live == null || string.IsNullOrEmpty(live.Version)) continue;
            if (string.Equals(pm.Version, live.Version, StringComparison.Ordinal)) continue;

            var oldVersion = pm.Version;
            if (!string.IsNullOrEmpty(oldVersion)
                && Config.HasEntry(pm.ModId, oldVersion)
                && !Config.HasEntry(pm.ModId, live.Version))
            {
                Config.SetEnabled(pm.ModId, live.Version,
                    Config.IsEnabled(pm.ModId, oldVersion, fallback: true));
                Config.SetPriority(pm.ModId, live.Version,
                    Config.Priority(pm.ModId, oldVersion, live.DeclaredPriority));
                Config.RemoveEntry(pm.ModId, oldVersion);
                MoveSourceRecord(pm.ModId, oldVersion, live.Version,
                    Config.GetModSource(pm.ModId, oldVersion));
                cfgDirty = true;
            }
            pm.Version = live.Version;
            if (live.IsArchive && File.Exists(live.Path))
            {
                try { ModLibrary.Add(ModsDir, live.Path); }
                catch { /* best-effort; the live file is still in place */ }
            }
            profileDirty = true;
        }
        if (cfgDirty)
        {
            try { Config.Save(); } catch { /* best-effort */ }
            Registry.Scan(ModsDir, Config);
        }
        if (profileDirty)
        {
            ActiveProfile.UpdatedAt = DateTime.UtcNow;
            try { ActiveProfile.SaveMetadataOnly(); } catch { /* best-effort */ }
        }
    }

    /// <summary>Writes the source of every mod downloaded since the last
    /// scan into mod_config.cfg's [mod_sources], where the registry (and
    /// the in-game loader) look up a mod whose mod.txt declares none.</summary>
    internal void RecordDownloadedSources()
    {
        var pending = DownloadedSources.Drain();
        if (pending.Count == 0) return;
        var dirty = false;
        foreach (var (modId, version, source) in pending)
        {
            if (Config.GetModSource(modId, version) == source) continue;
            Config.SetModSource(modId, version, source);
            dirty = true;
        }
        if (!dirty) return;
        try { Config.Save(); }
        catch { /* best-effort — the mod can be linked again by hand */ }
    }

    /// <summary>Keeps the active profile and the live mods agreeing on
    /// where each mod comes from: a live mod's source is copied into its
    /// profile entry when the profile has none, and a profile entry's
    /// source is recorded for a live mod that has none.</summary>
    internal void BackfillProfileSources()
    {
        if (ActiveProfile == null) return;
        var profileDirty = false;
        var cfgDirty = false;
        foreach (var pm in ActiveProfile.Mods)
        {
            var entry = Registry.FindById(pm.ModId);
            if (entry == null) continue;
            if (!pm.SourceRef.IsValid && entry.Source.IsValid)
            {
                pm.Source = entry.Source.Key;
                profileDirty = true;
            }
            else if (pm.SourceRef.IsValid && !entry.Source.IsValid)
            {
                Config.SetModSource(entry.ModId, entry.Version, pm.SourceRef);
                entry.Source = pm.SourceRef;
                cfgDirty = true;
            }
        }
        if (profileDirty)
        {
            try { ActiveProfile.SaveMetadataOnly(); } catch { /* best-effort */ }
        }
        if (cfgDirty)
        {
            try { Config.Save(); } catch { /* best-effort */ }
        }
    }

    /// <summary>Any mod live in the mods folder but not listed in the
    /// active profile is adopted into it, with a cfg enabled/priority
    /// row, so a mod dropped in through Explorer neither disappears from
    /// the list nor stays off in the loader. Idempotent.</summary>
    internal void AutoAdoptOrphansIntoActiveProfile()
    {
        if (ActiveProfile == null) return;

        var profileIds = new HashSet<string>(
            ActiveProfile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);

        var adopted = 0;
        foreach (var entry in Registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            if (profileIds.Contains(entry.ModId)) continue;
            if (IsBuiltInActive(entry.ModId)) continue;  // the manager's, not the profile's

            // IsEnabled mirrors the on-disk location (a mod parked in
            // Disabled/ stays off); priority is the author's declared one.
            ActiveProfile.Mods.Add(new ProfileMod
            {
                ModId = entry.ModId,
                DisplayName = entry.DisplayName,
                Version = entry.Version,
                IsEnabled = entry.IsEnabled,
                Priority = entry.DeclaredPriority,
                Source = entry.Source.Key,
            });
            profileIds.Add(entry.ModId);

            // The in-game loader needs the cfg row too: it doesn't apply
            // the manager's fallback=true rule.
            Config.SetEnabled(entry.ModId, entry.Version, entry.IsEnabled);
            Config.SetPriority(entry.ModId, entry.Version, entry.DeclaredPriority);
            adopted++;
        }
        if (adopted == 0) return;

        ActiveProfile.UpdatedAt = DateTime.UtcNow;
        try { ActiveProfile.SaveMetadataOnly(); } catch { /* best-effort */ }
        try { Config.Save(); } catch { /* best-effort; rare */ }
        // Re-apply cfg so each adopted entry's Priority reflects it.
        Registry.Scan(ModsDir, Config);
    }

    /// <summary>The game (the Save Slots mod) or the loader's own
    /// launcher can make another profile active. When mod_config.cfg
    /// names a saved profile other than the manager's active one, and
    /// the mods folder really holds that profile's mods, follow it.</summary>
    internal void AdoptLoaderActiveProfile()
    {
        if (string.IsNullOrEmpty(Settings.ActiveProfileName)) return;  // not adopted yet
        var cfgName = Config.ActiveProfile;
        if (string.IsNullOrEmpty(cfgName)
            || string.Equals(cfgName, Settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            return;
        ModProfile? match;
        try
        {
            match = ModProfile.LoadAll().FirstOrDefault(p =>
                string.Equals(p.Name, cfgName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return; }
        if (match == null) return;  // a loader-only profile: leave the manager's as is

        // Only when the folder really is that profile's: the loader's
        // launcher can name another profile active without moving any
        // archive. Locked mods and Save Slots itself may be extra.
        var wanted = new HashSet<string>(
            match.Mods.Select(m => m.ModId).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);
        var locked = new HashSet<string>(Settings.LockedMods, StringComparer.OrdinalIgnoreCase);
        var extras = Registry.Entries
            .Where(e => e.IsArchive && !string.IsNullOrEmpty(e.ModId))
            .Select(e => e.ModId)
            .Where(id => !wanted.Contains(id) && !locked.Contains(id)
                && !string.Equals(id, SaveSlots.CompanionModId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (extras.Count > 0)
        {
            Say(StatusArea.Profiles, $"mod_config.cfg names '{match.Name}' as active, but the mods folder "
                + $"still holds mods it doesn't list ({extras.Count}); keeping '{Settings.ActiveProfileName}'.");
            return;
        }
        var from = Settings.ActiveProfileName;
        Settings.ActiveProfileName = match.Name;
        try { Settings.Save(); } catch { /* best-effort */ }
        ReloadProfiles();
        Say(StatusArea.Profiles, $"Active profile is now '{match.Name}' (switched in the game; was '{from}').");
    }

    /// <summary>Reloads every profile from disk and re-resolves the
    /// active one against Settings.ActiveProfileName.</summary>
    public void ReloadProfiles()
    {
        AllProfiles = ModProfile.LoadAll();
        ActiveProfile = AllProfiles.FirstOrDefault(
            p => string.Equals(p.Name, Settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
        RaiseChanged();
    }

    /// <summary>Conflicts among what the Mods view shows: every entry
    /// before the profile model is adopted, else the active profile's
    /// mods plus locked ones (so a locked-vs-profile conflict doesn't
    /// vanish on a switch).</summary>
    public List<ConflictDetector.Conflict> DetectConflictsForActive()
    {
        if (ActiveProfile == null)
            return ConflictDetector.DetectAll(Registry.Entries);
        var allowed = new HashSet<string>(
            ActiveProfile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
        foreach (var lockId in ProtectedModIds())
            allowed.Add(lockId);
        var scoped = Registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId) && allowed.Contains(e.ModId));
        return ConflictDetector.DetectAll(scoped);
    }

    /// <summary>Pushes the live state of `modId` into the active
    /// profile's entry for it, then persists. Used after toggle and
    /// priority changes so a switch away and back keeps them.</summary>
    public void SyncActiveProfileFromEntry(string modId)
    {
        if (ActiveProfile == null || string.IsNullOrEmpty(modId)) return;
        var entry = Registry.FindById(modId);
        if (entry == null) return;
        var pm = ActiveProfile.Mods.FirstOrDefault(m =>
            string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (pm == null) return;
        if (pm.IsEnabled == entry.IsEnabled
            && pm.Priority == entry.Priority
            && string.Equals(pm.Version, entry.Version, StringComparison.Ordinal))
            return;
        pm.IsEnabled = entry.IsEnabled;
        pm.Priority = entry.Priority;
        pm.Version = entry.Version;
        ActiveProfile.UpdatedAt = DateTime.UtcNow;
        try { ActiveProfile.SaveMetadataOnly(); }
        catch { /* best-effort — in-memory state still reflects the change */ }
    }

    // ── built-in mods ─────────────────────────────────────────────────

    /// <summary>Keeps the built-in mods in the mods folder and on in the
    /// active profile's cfg section (Rescan calls it before every scan).
    /// Writes only what differs, so the folder watcher doesn't loop.</summary>
    internal void EnsureBuiltIns()
    {
        if (!Settings.SaveSlotsBuiltIn || BuiltInMods.SaveSlotsArchive == null) return;
        try
        {
            BuiltInMods.InstallSaveSlots(ModsDir);
            var id = BuiltInMods.SaveSlotsId;
            var ver = BuiltInMods.SaveSlotsVersion;
            if (Config.LoadFailed || ver.Length == 0) return;
            var had = Config.HasEntry(id, ver);
            if (!had || !Config.IsEnabled(id, ver, fallback: false))
            {
                Config.SetEnabled(id, ver, true);
                if (!had) Config.SetPriority(id, ver, 0);
                SaveModConfigSafely();
            }
        }
        catch (Exception ex)
        {
            Say(StatusArea.Mods, "Couldn't install the built-in Save Slots: " + ex.Message);
        }
    }

    /// <summary>True for a mod the manager installs itself and keeps on
    /// in every profile (Save Slots, while built-in save slots are on).</summary>
    public bool IsBuiltInActive(string? modId)
        => Settings.SaveSlotsBuiltIn && BuiltInMods.IsBuiltIn(modId);

    public bool IsLocked(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId)
            && (Settings.LockedMods.Contains(e.ModId) || IsBuiltInActive(e.ModId));

    public bool IsTesting(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId) && Settings.TestingMods.Contains(e.ModId);

    public bool IsInstalledThisSession(ModEntry e)
        => !string.IsNullOrEmpty(e.ModId) && SessionInstalled.Contains(e.ModId);

    /// <summary>Mod ids a profile switch or apply leaves in place: the
    /// user's locked mods plus the built-in ones.</summary>
    public List<string> ProtectedModIds()
    {
        var ids = new List<string>(Settings.LockedMods);
        if (Settings.SaveSlotsBuiltIn) ids.Add(BuiltInMods.SaveSlotsId);
        return ids;
    }

    // ── shared helpers ────────────────────────────────────────────────

    /// <summary>Saves mod_config.cfg, telling the user when it fails
    /// (most likely the game holding it open). True on success.</summary>
    public bool SaveModConfigSafely()
    {
        try { Config.Save(); return true; }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't save mod_config.cfg", ex);
            return false;
        }
    }

    /// <summary>Moves a mod's [mod_sources] record from one version key
    /// to another (after an update), without overwriting a record the
    /// new version already has.</summary>
    internal void MoveSourceRecord(string modId, string oldVersion, string newVersion, ModSource oldSource)
    {
        if (oldSource.IsValid && !Config.GetModSource(modId, newVersion).IsValid)
            Config.SetModSource(modId, newVersion, oldSource);
        Config.RemoveModSource(modId, oldVersion);
    }

    /// <summary>The latest version VostokMods lists for a mod, when the
    /// mod is linked and the last update check covered it.</summary>
    public bool TryGetLatest(ModEntry e, out string latest)
    {
        latest = "";
        if (!e.Source.IsValid) return false;
        if (!LatestVersions.TryGetValue(e.Source.Key, out var v) || string.IsNullOrEmpty(v)) return false;
        latest = v;
        return true;
    }

    /// <summary>Installed mods with no VostokMods source; they can't be
    /// update-checked until they are linked.</summary>
    public List<ModEntry> UnlinkedMods()
        => Registry.Entries.Where(e => !e.Source.IsValid && !string.IsNullOrEmpty(e.ModId)).ToList();

    /// <summary>True when the installed version is strictly older than
    /// the one VostokMods lists (never for formats that can't be ordered).</summary>
    public bool IsOutdated(ModEntry e)
    {
        if (!TryGetLatest(e, out var latest)) return false;
        if (VersionsAreAmbiguous(e.Version, latest)) return false;
        return ModRegistry.CompareVersions(e.Version, latest) < 0;
    }

    /// <summary>True when two version strings can't be reliably ordered
    /// because at least one segment on either side isn't an integer
    /// (e.g. "0.0.420" vs "0.4.20_R": likely the same release written
    /// two ways).</summary>
    public static bool VersionsAreAmbiguous(string? a, string? b)
    {
        a = (a ?? "").Trim().TrimStart('v', 'V');
        b = (b ?? "").Trim().TrimStart('v', 'V');
        if (a == b) return false;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        static bool AllNumeric(string[] parts)
        {
            foreach (var p in parts)
                if (!int.TryParse(p, out _)) return false;
            return true;
        }
        return !(AllNumeric(a.Split('.')) && AllNumeric(b.Split('.')));
    }
}
