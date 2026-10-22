// First-run setup: the five-step wizard's data and actions, the profile
// model migration (adopting the mods folder and the mod loader's own
// profiles), and the "setup needed" notes.
//
// Wizard steps, each with a Get…Status and an Apply…:
//   1. Game folder — detect / validate the install, point ModsDir at it.
//   2. Mod loader — the installed MML version and whether it can skip its
//      launcher; the skip-launcher setting. (Installing or updating the
//      loader is the updates area's.)
//   3. Mods & profiles — adopt the loader's profiles (migration) or start
//      fresh with one profile; or decline.
//   4. Save slots — the built-in Save Slots mod on or off.
//   5. VostokMods — how many mods are linked to a VostokMods page. Counts
//      only; linking is the mods/sources area's.

using System.Text.RegularExpressions;
#if AI_RESOLVER
using VostokModManager.Api;
#endif
using VostokModManager.Domain;

namespace VostokModManager.Services;

public enum SetupStepId { GameFolder, ModLoader, Profiles, SaveSlots, VostokMods }

public enum SetupState
{
    /// <summary>Nothing to do.</summary>
    Done,
    /// <summary>The manager can't work properly until this is done.</summary>
    NeedsAction,
    /// <summary>Worth a look; the manager works without it.</summary>
    Optional,
}

/// <summary>One wizard step's headline.</summary>
public sealed record SetupStepStatus(SetupStepId Id, string Title, SetupState State, string Summary);

/// <summary>A folder checked as the game install (or its mods folder).</summary>
/// <param name="Ok">The mods folder exists: the manager can use it.</param>
/// <param name="HasModloader">modloader.gd sits beside the game.</param>
/// <param name="Message">What's wrong, or a one-line confirmation.</param>
public sealed record GameFolderCheck(
    bool Ok, string GameDir, string ModsDir, bool GameDirExists, bool HasModloader, string Message);

/// <summary>Step 1: the folder in use, and installs found in the Steam libraries.</summary>
public sealed record GameFolderStatus(GameFolderCheck Current, bool IsDefaultSteamPath, IReadOnlyList<string> DetectedModsDirs);

/// <summary>Step 2.</summary>
/// <param name="InstalledVersion">MODLOADER_VERSION from modloader.gd, "" when not installed.</param>
/// <param name="SupportsSkipUi">The loader knows the skip-launcher marker (3.4.2+).</param>
/// <param name="SkipLoaderUi">The setting: launches from here skip the loader's launcher.</param>
public sealed record ModLoaderStatus(string InstalledVersion, bool SupportsSkipUi, bool SkipLoaderUi)
{
    public bool Installed => InstalledVersion.Length > 0;
}

/// <summary>One of the mod loader's own profiles in mod_config.cfg,
/// offered for import.</summary>
public sealed record LoaderProfileInfo(string Name, int ModCount);

/// <summary>What the migration screen shows.</summary>
/// <param name="ModCount">Archive mods in the mods folder (they go to the Library).</param>
/// <param name="DefaultName">The name the cfg already calls the active profile.</param>
/// <param name="LoaderProfiles">The loader's other profiles, offered for import.</param>
public sealed record MigrationOffer(int ModCount, string DefaultName, IReadOnlyList<LoaderProfileInfo> LoaderProfiles);

/// <summary>A finished migration.</summary>
public sealed record MigrationResult(string ProfileName, int LibraryAdded, int LibrarySkipped, string ImportNote);

/// <summary>Step 3.</summary>
/// <param name="Adopted">The profile model is in use (an active profile is set).</param>
public sealed record ProfilesSetupStatus(bool Adopted, string ActiveProfile, bool Declined, MigrationOffer Offer);

/// <summary>Step 4.</summary>
/// <param name="ArchiveAvailable">This build carries the Save Slots mod.</param>
public sealed record SaveSlotsSetupStatus(bool On, bool ArchiveAvailable, string BuiltInVersion, string? Warning);

/// <summary>Step 5: mods with and without a VostokMods source.</summary>
public sealed record VostokModsSetupStatus(int Linked, int Unlinked, IReadOnlyList<string> UnlinkedNames);

public sealed partial class ManagerSession
{
    // ── setup notes (the old setup banner) ────────────────────────────

    /// <summary>What still needs setting up, one note each; empty when
    /// nothing does.</summary>
    public IReadOnlyList<string> GetSetupIssues()
    {
        var msgs = new List<string>();
#if AI_RESOLVER
        if (!Claude.IsAvailable)
        {
            msgs.Add(ClaudeCodeRunner.HasMsixInstall()
                ? "Claude Desktop (Microsoft Store) detected — that install is sandboxed and "
                    + "unreachable from outside the package. Install the standalone CLI: install "
                    + "Node.js from nodejs.org, then in a NEW terminal run "
                    + "`npm install -g @anthropic-ai/claude-code`. Restart the Manager when done."
                : "Claude Code not detected. Install Node.js from nodejs.org, then in a NEW "
                    + "terminal run `npm install -g @anthropic-ai/claude-code`. Or set a known "
                    + "claude.exe path in Settings.");
        }
#endif
        if (!Directory.Exists(ModsDir))
        {
            msgs.Add($"Mods folder `{ModsDir}` doesn't exist. The default Steam install path "
                + "doesn't apply on your machine — set the Mods folder to wherever Road to Vostok lives.");
        }
#if AI_RESOLVER
        if (string.IsNullOrEmpty(Settings.GameSourcePath) || !Directory.Exists(Settings.GameSourcePath))
        {
            msgs.Add("Game source (Decomp/) not configured. AI conflict resolution still works "
                + "without it, but produces better merges when given the original game script "
                + "as context. Set your Decomp/ folder in Settings.");
        }
#endif
        return msgs;
    }

    // ── the wizard ────────────────────────────────────────────────────

    /// <summary>All five steps' headlines, in order.</summary>
    public IReadOnlyList<SetupStepStatus> GetSetupSteps()
    {
        var folder = GetGameFolderStatus().Current;
        var loader = GetModLoaderStatus();
        var profiles = GetProfilesSetupStatus();
        var slots = GetSaveSlotsSetupStatus();
        var vm = GetVostokModsSetupStatus();
        return new[]
        {
            new SetupStepStatus(SetupStepId.GameFolder, "Game folder",
                folder.Ok ? SetupState.Done : SetupState.NeedsAction, folder.Message),
            new SetupStepStatus(SetupStepId.ModLoader, "Mod loader",
                !loader.Installed ? SetupState.NeedsAction
                    : loader.SupportsSkipUi ? SetupState.Done : SetupState.Optional,
                !loader.Installed
                    ? "The mod loader isn't installed beside the game (no modloader.gd)."
                    : $"Mod loader {loader.InstalledVersion}"
                        + (loader.SupportsSkipUi
                            ? (loader.SkipLoaderUi ? ", launches skip its launcher." : ", its launcher shows on every launch.")
                            : "; this version always shows its launcher (3.4.2+ can skip it).")),
            new SetupStepStatus(SetupStepId.Profiles, "Mods & profiles",
                profiles.Adopted ? SetupState.Done
                    : ShouldOfferMigration ? SetupState.NeedsAction : SetupState.Optional,
                profiles.Adopted
                    ? $"Active profile: '{profiles.ActiveProfile}'."
                    : profiles.Offer.ModCount > 0
                        ? $"{profiles.Offer.ModCount} mod(s) in the mods folder aren't in a profile yet."
                        : "No profile yet."),
            new SetupStepStatus(SetupStepId.SaveSlots, "Save slots",
                !slots.On || slots.Warning == null ? SetupState.Done : SetupState.Optional,
                slots.On
                    ? (slots.Warning ?? "Built-in save slots are on.")
                    : "Built-in save slots are off."),
            new SetupStepStatus(SetupStepId.VostokMods, "VostokMods",
                vm.Unlinked == 0 ? SetupState.Done : SetupState.Optional,
                vm.Unlinked == 0
                    ? $"All {vm.Linked} mod(s) are linked to VostokMods."
                    : $"{vm.Linked} linked, {vm.Unlinked} not linked (no update checks for those)."),
        };
    }

    // step 1 — game folder

    public GameFolderStatus GetGameFolderStatus()
        => new(CheckGameFolder(ModsDir),
            string.Equals(ModsDir, SteamFallbackModsDir, StringComparison.OrdinalIgnoreCase),
            DetectModsDirCandidates());

    /// <summary>Checks a folder picked as the game install. Accepts the
    /// install folder itself or its mods folder.</summary>
    public static GameFolderCheck CheckGameFolder(string path)
    {
        path = (path ?? "").Trim().TrimEnd('\\', '/');
        if (path.Length == 0)
            return new GameFolderCheck(false, "", "", false, false, "Pick the Road to Vostok install folder.");
        string gameDir, modsDir;
        if (string.Equals(Path.GetFileName(path), "mods", StringComparison.OrdinalIgnoreCase))
        {
            modsDir = path;
            gameDir = Path.GetDirectoryName(path) ?? "";
        }
        else
        {
            gameDir = path;
            modsDir = Path.Combine(path, "mods");
        }
        var gameExists = gameDir.Length > 0 && Directory.Exists(gameDir);
        var modsExists = Directory.Exists(modsDir);
        var hasLoader = File.Exists(MmlInstall.ResolveModloaderPath(modsDir));
        string msg;
        if (!gameExists) msg = $"`{gameDir}` doesn't exist.";
        else if (!modsExists)
            msg = $"`{gameDir}` has no mods folder. Pick the Road to Vostok install folder, "
                + "or create a `mods` folder in it.";
        else msg = $"Mods folder: {modsDir}";
        return new GameFolderCheck(modsExists, gameDir, modsDir, gameExists, hasLoader, msg);
    }

    /// <summary>Points the manager at the picked install (see
    /// CheckGameFolder) through the normal settings change, so the
    /// folder is rescanned. Null (after telling the user) when the
    /// folder isn't usable.</summary>
    public SettingsApplyResult? ApplyGameFolderStep(string path)
    {
        var check = CheckGameFolder(path);
        if (!check.Ok)
        {
            Prompt.Inform("Mods folder not found", check.Message, PromptKind.Warning);
            return null;
        }
        var before = SettingsValues.From(Settings);
        return ApplySettingsChange(before, before with { ModsDir = check.ModsDir });
    }

    /// <summary>Road to Vostok mods folders found in the Steam libraries
    /// (libraryfolders.vdf), plus the default Steam path when it exists.</summary>
    public static IReadOnlyList<string> DetectModsDirCandidates()
    {
        var found = new List<string>();
        void Consider(string modsDir)
        {
            if (Directory.Exists(Path.GetDirectoryName(modsDir) ?? "")
                && !found.Contains(modsDir, StringComparer.OrdinalIgnoreCase))
                found.Add(modsDir);
        }
        Consider(SteamFallbackModsDir);
        try
        {
            var steamRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
                Path.GetDirectoryName(SteamFallbackModsDir))))!;  // …\Steam
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    var lib = m.Groups[1].Value.Replace("\\\\", "\\");
                    Consider(Path.Combine(lib, "steamapps", "common", "Road to Vostok", "mods"));
                }
            }
        }
        catch { /* best-effort */ }
        return found;
    }

    // step 2 — mod loader

    public ModLoaderStatus GetModLoaderStatus()
        => new(MmlInstall.DetectInstalledVersion(ModsDir), MmlInstall.SupportsSkipUi(ModsDir), Settings.SkipLoaderUi);

    /// <summary>Sets whether launches from the manager skip the loader's launcher.</summary>
    public bool ApplyModLoaderStep(bool skipLoaderUi)
    {
        var before = SettingsValues.From(Settings);
        return ApplySettingsChange(before, before with { SkipLoaderUi = skipLoaderUi }).Saved;
    }

    // step 3 — mods & profiles

    public ProfilesSetupStatus GetProfilesSetupStatus()
        => new(!string.IsNullOrEmpty(Settings.ActiveProfileName), Settings.ActiveProfileName,
            Settings.MigrationDeclined, GetMigrationOffer());

    /// <summary>Adopts the profile model: one profile named `name` from
    /// the mods folder, plus the ticked loader profiles. Start fresh =
    /// no loader profiles. See AdoptProfileModel.</summary>
    public MigrationResult? ApplyProfilesStep(string name, IReadOnlyList<string>? importLoaderProfiles = null)
        => AdoptProfileModel(name, importLoaderProfiles);

    // step 4 — save slots

    public SaveSlotsSetupStatus GetSaveSlotsSetupStatus()
        => new(Settings.SaveSlotsBuiltIn, BuiltInMods.SaveSlotsArchive != null,
            BuiltInMods.SaveSlotsVersion, Settings.SaveSlotsBuiltIn ? SaveSlotsWarning() : null);

    /// <summary>Turns the built-in save slots on or off (installs or
    /// removes the mod) through the normal settings change.</summary>
    public bool ApplySaveSlotsStep(bool on)
    {
        var before = SettingsValues.From(Settings);
        return ApplySettingsChange(before, before with { SaveSlotsBuiltIn = on }).Saved;
    }

    // step 5 — VostokMods

    public VostokModsSetupStatus GetVostokModsSetupStatus()
    {
        var unlinked = UnlinkedMods().Where(e => !IsBuiltInActive(e.ModId)).ToList();
        var linked = Registry.Entries.Count(e => !string.IsNullOrEmpty(e.ModId) && e.Source.IsValid);
        return new VostokModsSetupStatus(linked, unlinked.Count,
            unlinked.Select(e => string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName).ToList());
    }

    // ── profile model migration ───────────────────────────────────────

    /// <summary>The migration is offered when the profile model hasn't
    /// been adopted on this install, the user hasn't declined it, and
    /// there are installed archive mods worth migrating.</summary>
    public bool ShouldOfferMigration
        => string.IsNullOrEmpty(Settings.ActiveProfileName)
            && !Settings.MigrationDeclined
            && Registry.Entries.Any(e => e.IsArchive);

    public MigrationOffer GetMigrationOffer()
    {
        var modCount = Registry.Entries.Count(e => e.IsArchive && !string.IsNullOrEmpty(e.ModId));
        // Default the name to what mod_config.cfg already calls the active
        // profile: keeps cfg and profile.json aligned and matches what the
        // user sees in the in-game loader.
        var defaultName = string.IsNullOrWhiteSpace(Config.ActiveProfile) ? "Default" : Config.ActiveProfile;
        // The loader's other profiles, offered for import. The one the cfg
        // calls active is the mods folder's current state and becomes the
        // profile named above, whatever name is chosen.
        var loaderProfiles = Config.ProfileNames()
            .Where(n => !string.Equals(n, Config.ActiveProfile, StringComparison.Ordinal))
            .Select(n => new LoaderProfileInfo(n,
                Config.ProfileEntries(n)
                    .Where(e => !string.IsNullOrEmpty(e.ModId))
                    .Select(e => e.ModId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()))
            .Where(p => p.ModCount > 0)
            .ToList();
        return new MigrationOffer(modCount, defaultName, loaderProfiles);
    }

    /// <summary>"Skip for now": remembered, so the offer doesn't return
    /// on every launch.</summary>
    public void DeclineProfileMigration()
    {
        Settings.MigrationDeclined = true;
        try { Settings.Save(); } catch { /* best-effort */ }
        Say(StatusArea.Mods, "Profile model: migration skipped. You can adopt it "
            + "later by deleting Settings.json or via a future menu option.");
    }

    /// <summary>Ingests every live .vmz into the Library, builds a profile
    /// named `name` from the current registry state and activates it,
    /// then imports the ticked loader profiles. Non-destructive: the live
    /// mods folder, mod_config.cfg's entries and the lock list are left
    /// alone; only the Library and new profile.json files appear. Null
    /// when the name is empty or the profile couldn't be written (the
    /// user is told).</summary>
    public MigrationResult? AdoptProfileModel(string name, IReadOnlyList<string>? importLoaderProfiles = null)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            Prompt.Inform("Name required", "Profile name can't be empty.", PromptKind.Warning);
            return null;
        }

        int libAdded = 0, libSkipped = 0;
        var importNote = "";

        // 1. Ingest every live archive mod into the Library.
        foreach (var entry in Registry.Entries)
        {
            if (!entry.IsArchive) continue;
            if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) continue;
            var added = ModLibrary.Add(ModsDir, entry.Path);
            if (added != null) libAdded++;
            else libSkipped++;
        }

        // 2. Snapshot the current state into a new profile.
        var profile = ModProfile.FromRegistry(
            name,
            $"Migrated from existing mods folder — {DateTime.Now:yyyy-MM-dd HH:mm}",
            Registry.Entries);
        try { profile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't write profile metadata", ex);
            return null;
        }

        // 3. Align cfg's active_profile with the new profile name. The cfg
        // state is kept (no ClearProfileEntries: that destructive step is
        // the profile switcher's).
        if (!string.Equals(Config.ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
        {
            Config.ActiveProfile = name;
            SaveModConfigSafely();
        }

        // 4. Record the adoption.
        Settings.ActiveProfileName = name;
        Settings.MigrationDeclined = false;
        try { Settings.Save(); } catch { /* best-effort */ }

        // 5. The loader's other profiles the user ticked.
        if (importLoaderProfiles is { Count: > 0 })
            importNote = ImportLoaderProfiles(importLoaderProfiles, name);

        ReloadProfiles();
        Requery();

        var libNote = libSkipped > 0 ? $", {libSkipped} skipped (no mod_id)" : "";
        Say(StatusArea.Mods, $"Migrated to profile '{name}'  ·  "
            + $"{libAdded} mod(s) added to Library{libNote}." + importNote);
        return new MigrationResult(name, libAdded, libSkipped, importNote);
    }

    /// <summary>Creates a manager profile for each named loader profile
    /// from its sections in mod_config.cfg (ModProfile.FromLoaderProfile).
    /// A name the manager already has (`skipName`, or a saved profile) is
    /// left alone. Nothing in the cfg or the mods folder changes. Returns
    /// a status-line note.</summary>
    internal string ImportLoaderProfiles(IReadOnlyList<string> names, string skipName)
    {
        // What the manager can see: live mods and the Library, which step
        // 1 of the adoption has just filled from the mods folder.
        var known = new List<ModProfile.KnownMod>();
        foreach (var e in Registry.Entries)
        {
            if (!e.IsArchive || string.IsNullOrEmpty(e.ModId)) continue;
            known.Add(new ModProfile.KnownMod(
                e.ModId, e.Version,
                string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName,
                e.Source.Key, e.DeclaredPriority));
        }
        try
        {
            foreach (var lib in ModLibrary.List(ModsDir))
                known.Add(new ModProfile.KnownMod(lib.ModId, lib.Version, lib.DisplayName, lib.Source.Key, 0));
        }
        catch { /* the live list still serves */ }

        var existing = new HashSet<string>(
            ModProfile.LoadAll().Select(p => p.Name), StringComparer.OrdinalIgnoreCase)
        {
            skipName,
        };

        int imported = 0, skipped = 0, missing = 0;
        var failed = new List<string>();
        foreach (var n in names)
        {
            if (existing.Contains(n)) { skipped++; continue; }
            try
            {
                var r = ModProfile.FromLoaderProfile(n, Config, known);
                r.Profile.SaveMetadataOnly();
                existing.Add(n);
                imported++;
                missing += r.NotOnDisk.Count;
            }
            catch (Exception ex)
            {
                failed.Add($"{n}: {ex.Message}");
            }
        }

        if (failed.Count > 0)
            Prompt.Inform("Profile import",
                "These mod loader profiles could not be imported:\n\n" + string.Join("\n", failed),
                PromptKind.Warning);

        var note = $"  ·  {imported} loader profile(s) imported";
        if (missing > 0) note += $" ({missing} listed mod(s) not installed)";
        if (skipped > 0) note += $", {skipped} already present";
        return note;
    }
}
