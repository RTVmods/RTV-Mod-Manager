// Profiles: switching the active profile, and managing the saved ones
// (clone, new modlist, import, export, rename, describe, delete) plus
// inline edits of a profile's mod list. Ported from MainForm's
// active-profile glue and Ui/ProfileManagerDialog.
//
// Edits made here change profile.json only. Like the WinForms profile
// manager, they don't touch the live mods folder or mod_config.cfg, even
// for the active profile: a switch or an Apply is what makes a profile
// live.

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    // ── switching ─────────────────────────────────────────────────────

    /// <summary>Activates `target` through ProfileSwitcher (live folder
    /// swapped from the Library, cfg section rewritten, locked mods
    /// kept), then refreshes everything. Returns true when `target` is
    /// the active profile afterwards: already active, or switched. On a
    /// switcher failure (files locked by the game, cfg save error) the
    /// user is told and the partial state is left in place, since the
    /// switcher's stages are each best-effort.</summary>
    public bool SwitchActiveProfile(ModProfile target)
    {
        if (target == null) return false;
        if (SameProfileName(target.Name, ActiveProfile?.Name)) return true;
        if (!OfferNewerLibraryVersions(target)) return false;

        var result = ProfileSwitcher.SetActive(ModsDir, target, Config, ProtectedModIds());
        if (!result.Success)
        {
            Prompt.Error("Profile switch failed", new Exception(string.Join("\n", result.Errors)));
            return false;
        }

        Settings.ActiveProfileName = target.Name;
        try { Settings.Save(); } catch { /* best-effort */ }

        ReloadProfiles();
        Refresh();

        var missingNote = result.MissingFromLibrary.Count > 0
            ? $"  ·  {result.MissingFromLibrary.Count} mod(s) missing from library (skipped)"
            : "";
        Say(StatusArea.Profiles,
            $"Switched to '{target.Name}'  ·  {result.CopiedFromLibrary} copied  ·  "
            + $"{result.KeptLocked} locked kept{missingNote}");
        return true;
    }

    /// <summary>The target profile's mods pinned to a version older than
    /// the newest one of the same lineage in the Library (typically
    /// because another profile updated them). Locked mods are left out:
    /// the switch leaves their live file alone, so a version change
    /// would only be on paper.</summary>
    public List<NewerVersionCandidate> FindNewerLibraryVersions(ModProfile target)
    {
        var candidates = new List<NewerVersionCandidate>();
        if (target.Mods.Count == 0) return candidates;
        List<ModLibrary.Entry> library;
        try { library = ModLibrary.List(ModsDir); }
        catch { return candidates; /* no library to compare against */ }

        var protectedIds = ProtectedModIds();
        foreach (var pm in target.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId) || string.IsNullOrEmpty(pm.Version)) continue;
            if (protectedIds.Contains(pm.ModId, StringComparer.OrdinalIgnoreCase)) continue;
            // Same mod_id is not enough: an abandoned mod and its
            // successor can share an id (ModLineage has the example),
            // so only a release of the same lineage counts as newer.
            var le = ModLineage.NewestFor(library, pm.ModId, pm.SourceRef, pm.DisplayName);
            if (le == null || string.IsNullOrEmpty(le.Version)) continue;
            if (VersionsAreAmbiguous(pm.Version, le.Version)) continue;
            if (ModRegistry.CompareVersions(le.Version, pm.Version) <= 0) continue;
            var dev = le.DisplayName.Contains("[DEV]", StringComparison.OrdinalIgnoreCase);
            candidates.Add(new NewerVersionCandidate(pm, le.Version, le.DisplayName, dev));
        }
        return candidates;
    }

    /// <summary>Before a switch, asks which of the target's mods should
    /// move to the newest Library version (FindNewerLibraryVersions).
    /// Keeps profiles from drifting apart after an update while still
    /// letting a profile run an older version on purpose. DEV builds
    /// (name carrying "[DEV]") start unticked. Returns false when the
    /// user cancels the switch or the profile can't be saved.</summary>
    internal bool OfferNewerLibraryVersions(ModProfile target)
    {
        var candidates = FindNewerLibraryVersions(target);
        if (candidates.Count == 0) return true;

        var items = candidates
            .Select(c => new PickItem<NewerVersionCandidate>(
                c,
                string.IsNullOrEmpty(c.Mod.DisplayName) ? c.Mod.ModId : c.Mod.DisplayName,
                $"profile v{c.Mod.Version} → library v{c.NewestVersion}" + (c.IsDevBuild ? "  ·  DEV build" : ""),
                Checked: !c.IsDevBuild))
            .ToList();
        var accepted = Prompt.Pick(
            $"Newer versions in the Library — {target.Name}",
            $"{candidates.Count} mod(s) in this profile are pinned to a version older than the newest one "
            + "in the Library (usually because another profile updated them).\n"
            + "Tick the ones this profile should move to the newest version. Unticked mods keep the "
            + "profile's own version, so a profile can still run a mod at an older version on purpose.",
            items,
            "Use newest");
        if (accepted == null) return false;       // cancel the switch
        if (accepted.Count == 0) return true;     // keep every profile version

        foreach (var c in accepted)
        {
            c.Mod.Version = c.NewestVersion;
            if (!string.IsNullOrEmpty(c.NewestDisplayName)) c.Mod.DisplayName = c.NewestDisplayName;
        }
        target.UpdatedAt = DateTime.UtcNow;
        try { target.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't save the profile", ex);
            return false;
        }
        return true;
    }

    /// <summary>The tail of the old profile-dialog round trip: an Apply
    /// writes the applied profile's name into mod_config.cfg only, so
    /// cfg's active profile is mirrored into settings; then the profiles
    /// are reloaded and conflicts recomputed. With `liveFolderChanged`
    /// the mods folder is rescanned too (the caller then runs its update
    /// check, as MainForm did).</summary>
    public void SyncAfterProfileChanges(bool liveFolderChanged)
    {
        if (!SameProfileName(Settings.ActiveProfileName, Config.ActiveProfile))
        {
            Settings.ActiveProfileName = Config.ActiveProfile;
            try { Settings.Save(); } catch { /* best-effort */ }
        }
        ReloadProfiles();
        if (liveFolderChanged) Refresh();
        else Requery();
    }

    // ── profile cards ─────────────────────────────────────────────────

    /// <summary>A card per saved profile, in AllProfiles order, with the
    /// Save Slots slots tied to each.</summary>
    public List<ProfileSummary> GetProfileSummaries()
    {
        List<SaveSlots.Slot> slots;
        try { slots = SaveSlots.ReadAll(SaveSlots.RootDir(SaveSlots.DefaultUserDir)); }
        catch { slots = new(); }

        return AllProfiles.Select(p => new ProfileSummary(
                p,
                p.Name,
                p.Description,
                p.Mods.Count,
                p.Mods.Count(m => m.IsEnabled),
                p.CreatedAt,
                p.UpdatedAt,
                IsActiveProfile(p),
                p.BundledArchivesCount(),
                p.BundledArchivesSize(),
                slots.Where(s => s.Profile.Length > 0 && SameProfileName(s.Profile, p.Name)).ToList()))
            .ToList();
    }

    public bool IsActiveProfile(ModProfile p) =>
        ActiveProfile != null && SameProfileName(p.Name, ActiveProfile.Name);

    /// <summary>A profile's mods for its detail list, by load order then
    /// name, filtered (case-insensitive) by name, id or version.</summary>
    public List<ProfileModView> GetProfileMods(ModProfile profile, string? filter = null)
    {
        var f = (filter ?? "").Trim();
        bool Matches(ProfileMod m) =>
            f.Length == 0
            || (m.DisplayName ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
            || (m.ModId ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
            || (m.Version ?? "").Contains(f, StringComparison.OrdinalIgnoreCase);

        return profile.Mods
            .Where(Matches)
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.DisplayName)
            .Select(m =>
            {
                // Mods without a bundle fall back to the Library or a
                // VostokMods download on apply, when linked.
                var hasBundle = !string.IsNullOrEmpty(profile.ResolveBundledArchive(m));
                var note = hasBundle
                    ? $"bundled: {m.ArchiveFileName}"
                    : m.SourceRef.IsValid
                        ? $"no bundled archive — will fall back to VostokMods ({m.SourceRef.Id})"
                        : "no bundled archive, no VostokMods link";
                return new ProfileModView(m, hasBundle, note);
            })
            .ToList();
    }

    // ── create / import / delete ──────────────────────────────────────

    /// <summary>Clones `source` under a new name (asked for when `name`
    /// is null). Same mods, enabled state and priorities; not activated.
    /// Bundles aren't copied: the clone resolves its mods from the
    /// Library at apply / switch time. Returns the new profile, or null
    /// when cancelled or the write failed.</summary>
    public ModProfile? CloneProfile(ModProfile source, string? name = null)
    {
        name ??= Prompt.AskText("Clone Profile", $"Clone of '{source.Name}'. New profile name:", $"{source.Name} (copy)");
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!ConfirmProfileOverwrite(name, "")) return null;

        var clone = new ModProfile
        {
            Name        = name,
            Description = $"Clone of '{source.Name}' — {DateTime.Now:yyyy-MM-dd HH:mm}",
            CreatedAt   = DateTime.UtcNow,
            Mods        = source.Mods.Select(m => new ProfileMod
            {
                ModId       = m.ModId,
                DisplayName = m.DisplayName,
                Version     = m.Version,
                IsEnabled   = m.IsEnabled,
                Priority    = m.Priority,
                Source      = m.Source,
                // ArchiveFileName intentionally NOT copied.
            }).ToList(),
        };
        try { clone.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            Prompt.Inform("Clone failed", $"Couldn't write the new profile:\n{ex.Message}", PromptKind.Warning);
            return null;
        }
        return AfterProfilesEdited(name);
    }

    /// <summary>Creates a "new modlist" profile holding only the user's
    /// locked mods (or nothing), each bundled. Applying it later doesn't
    /// disable anything else on its own; duplicates are settled per row
    /// in the Apply plan. Returns the new profile, or null.</summary>
    public ModProfile? CreateNewModlistProfile(string? name = null)
    {
        var lockedIds = new HashSet<string>(ProtectedModIds(), StringComparer.OrdinalIgnoreCase);
        var lockedEntries = Registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId)
                     && lockedIds.Contains(e.ModId)
                     && !BuiltInMods.IsBuiltIn(e.ModId))  // the manager's, not the profile's
            .ToList();

        if (name == null)
        {
            // The prompt doubles as the confirmation.
            var preview = lockedEntries.Count > 0
                ? $"Starts with {lockedEntries.Count} locked mod(s). Applying it later won't disable "
                  + "anything else — duplicates are handled per-row in the apply dialog."
                : "Starts from zero mods. Applying it later won't disable anything else — "
                  + "duplicates are handled per-row in the apply dialog.";
            name = Prompt.AskText("New Modlist", "Name the new-modlist profile:\n\n" + preview,
                $"Modlist  {DateTime.Now:yyyy-MM-dd HH:mm}");
        }
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!ConfirmProfileOverwrite(name, "")) return null;

        var profile = new ModProfile
        {
            Name        = name,
            Description = "New modlist — " + (lockedEntries.Count > 0
                ? $"starts with {lockedEntries.Count} locked mod(s)."
                : "starts from zero mods."),
            CreatedAt   = DateTime.UtcNow,
            Mods        = lockedEntries.Select(e => new ProfileMod
            {
                ModId       = e.ModId,
                DisplayName = string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName,
                Version     = e.Version,
                IsEnabled   = e.IsEnabled,
                Priority    = e.Priority,
                Source      = e.Source.Key,
            }).ToList(),
        };
        List<ProfileMod> failed;
        try { failed = profile.SaveWithBundles(lockedEntries); }
        catch (Exception ex)
        {
            Prompt.Inform("New modlist failed", $"Couldn't write the new profile:\n{ex.Message}", PromptKind.Warning);
            return null;
        }
        if (failed.Count > 0)
        {
            var sample = string.Join("\n  • ", failed.Take(6).Select(m => $"{m.DisplayName} ({m.Version})"));
            var more = failed.Count > 6 ? $"\n  … and {failed.Count - 6} more" : "";
            Prompt.Inform("Partial bundle",
                $"Empty profile saved, but {failed.Count} locked mod(s) couldn't be bundled:\n  • {sample}{more}\n\n"
                + "Usually directory mods or files locked at copy time. The profile still applies correctly — "
                + "those entries just have no bundled .vmz to restore from later.");
        }
        return AfterProfilesEdited(name);
    }

    /// <summary>Imports a profile file: a .vmprofile / .zip bundle (its
    /// archives are added to the Library so a switch can find them) or a
    /// .json spec (metadata only; the user is asked whether to apply it
    /// now, which needs downloads). Null when the file isn't a profile.</summary>
    public ProfileImportResult? ImportProfileFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var isZip = ext is ".vmprofile" or ".zip";
        var profile = isZip ? ModProfile.LoadFromZip(path) : ModProfile.LoadFromJsonFile(path);
        if (profile == null)
        {
            Prompt.Inform("Import failed",
                "Could not parse the selected file as a mod profile.\n\n"
                + "Expected a .vmprofile zip (bundled archives) or a .json file (metadata only).",
                PromptKind.Warning);
            return null;
        }

        // A bare .json gets its folder now; a zip unpacked itself into
        // one during LoadFromZip.
        if (string.IsNullOrEmpty(profile.FolderPath))
        {
            try { profile.SaveMetadataOnly(); }
            catch (Exception ex)
            {
                Prompt.Error("Import failed", ex);
                return null;
            }
        }

        // Every bundled archive goes into the Library so the profile
        // model can find it on activate. ModLibrary.Add is idempotent.
        var libraryAdded = 0;
        var librarySkipped = 0;
        if (isZip && !string.IsNullOrEmpty(profile.FolderPath))
        {
            var bundleDir = Path.Combine(profile.FolderPath, "mods");
            if (Directory.Exists(bundleDir))
            {
                foreach (var vmz in Directory.GetFiles(bundleDir, "*.vmz"))
                {
                    ModLibrary.Entry? added = null;
                    try { added = ModLibrary.Add(ModsDir, vmz); } catch { /* counted as skipped */ }
                    if (added != null) libraryAdded++;
                    else librarySkipped++;
                }
            }
        }

        var reloaded = AfterProfilesEdited(profile.Name) ?? profile;

        // .vmprofile imports are self-contained once their bundles are
        // in the Library; a .json spec names mods but brings no archives.
        var missingFromLib = 0;
        var applyRequested = false;
        if (isZip)
        {
            var libraryNote = libraryAdded > 0
                ? $"  ·  {libraryAdded} archive(s) added to Library"
                  + (librarySkipped > 0 ? $" ({librarySkipped} skipped)" : "")
                : "  ·  no archives ingested";
            Prompt.Inform("Imported",
                $"Profile '{reloaded.Name}' imported{libraryNote}.\n\n"
                + "Switch to it from the Active profile selector to activate.");
        }
        else
        {
            foreach (var pm in reloaded.Mods)
            {
                if (string.IsNullOrEmpty(pm.ModId)) continue;
                if (string.IsNullOrEmpty(ModLibrary.Find(ModsDir, pm.ModId, pm.Version))) missingFromLib++;
            }
            var dlBlurb = missingFromLib > 0
                ? $"{missingFromLib} mod(s) need to be downloaded from VostokMods. "
                : "";
            applyRequested = Prompt.Confirm("Apply now?",
                $"Profile '{reloaded.Name}' imported (JSON spec).\n\n"
                + dlBlurb
                + "Apply it now? Apply runs the per-row plan (downloads + activate).");
        }
        return new ProfileImportResult(reloaded, isZip, libraryAdded, librarySkipped, missingFromLib, applyRequested);
    }

    /// <summary>Deletes a profile's folder, bundled archives included,
    /// after a confirmation. The live mods folder is not touched.</summary>
    public bool DeleteProfile(ModProfile profile)
    {
        var activeNote = IsActiveProfile(profile)
            ? "\n\nIt is the active profile: the mods folder keeps its mods, but no profile will be active."
            : "";
        if (!Prompt.Confirm("Delete profile",
                $"Delete profile '{profile.Name}'?\nThis cannot be undone.{activeNote}",
                PromptKind.Warning))
            return false;
        profile.Delete();
        AfterProfilesEdited(null);
        return true;
    }

    // ── rename / description ──────────────────────────────────────────

    /// <summary>Renames a profile (asked for when `newName` is null): its
    /// profile.json, its folder, its section in mod_config.cfg, the
    /// active-profile name when it is the active one, and the Save Slots
    /// slots tied to it. False when cancelled or the name is taken.</summary>
    public bool RenameProfile(ModProfile profile, string? newName = null)
    {
        var oldName = profile.Name;
        newName ??= Prompt.AskText("Rename Profile", $"New name for '{oldName}':", oldName);
        newName = newName?.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, oldName, StringComparison.Ordinal))
            return false;
        if (AllProfiles.Any(p => !ReferenceEquals(p, profile)
                && !SameProfileName(p.Name, oldName)
                && SameProfileName(p.Name, newName)))
        {
            Prompt.Inform("Name taken", $"A profile named '{newName}' already exists. Pick another name.",
                PromptKind.Warning);
            return false;
        }

        var wasActive = SameProfileName(Settings.ActiveProfileName, oldName);
        var oldFolder = profile.FolderPath;
        var legacyJson = Path.Combine(ModProfile.ProfilesDir, ModProfile.SafeFileName(oldName) + ".json");

        // The json first, in the folder it already has: a profile is
        // known by the name inside profile.json, not by its folder.
        profile.Name = newName;
        profile.UpdatedAt = DateTime.UtcNow;
        try { profile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            profile.Name = oldName;
            Prompt.Error("Couldn't rename the profile", ex);
            return false;
        }

        // Then the folder, best-effort: a folder still named after the
        // old name loads the same.
        var newFolder = profile.DefaultFolderPath;
        if (!string.IsNullOrEmpty(oldFolder)
            && Directory.Exists(oldFolder)
            && !string.Equals(Path.GetFullPath(oldFolder).TrimEnd('\\', '/'),
                Path.GetFullPath(newFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(newFolder))
        {
            try
            {
                Directory.Move(oldFolder, newFolder);
                profile.FolderPath = newFolder;
            }
            catch { /* keeps its old folder name */ }
        }
        // A legacy top-level <name>.json just got a folder of its own.
        if (string.IsNullOrEmpty(oldFolder) && File.Exists(legacyJson))
        {
            try { File.Delete(legacyJson); } catch { /* best-effort */ }
        }

        RenameConfigProfileSection(oldName, newName);

        if (wasActive)
        {
            Settings.ActiveProfileName = newName;
            try { Settings.Save(); } catch { /* best-effort */ }
        }

        // Slots tied to the old name would otherwise point at nothing.
        try
        {
            var root = SaveSlots.RootDir(SaveSlots.DefaultUserDir);
            foreach (var slot in SaveSlots.ReadAll(root))
                if (slot.Profile.Length > 0 && SameProfileName(slot.Profile, oldName))
                    SaveSlots.SetProfile(root, slot.Number, newName);
        }
        catch { /* best-effort */ }

        AfterProfilesEdited(newName);
        Say(StatusArea.Profiles, $"Renamed '{oldName}' to '{newName}'.");
        return true;
    }

    /// <summary>Moves a profile's `[profile.&lt;name&gt;.*]` sections in
    /// mod_config.cfg to the new name (and the active-profile pointer,
    /// when it names it). Keys without a mod id (`zip:` keys) can't be
    /// re-emitted and are dropped.</summary>
    private void RenameConfigProfileSection(string oldName, string newName)
    {
        var entries = Config.ProfileEntries(oldName);
        var pointed = string.Equals(Config.ActiveProfile, oldName, StringComparison.Ordinal);
        if (entries.Count == 0 && !pointed) return;

        var previous = Config.ActiveProfile;
        Config.ActiveProfile = newName;
        Config.ClearProfileEntries(newName);
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (e.Enabled.HasValue) Config.SetEnabled(e.ModId, e.Version, e.Enabled.Value);
            if (e.Priority.HasValue) Config.SetPriority(e.ModId, e.Version, e.Priority.Value);
        }
        Config.ClearProfileEntries(oldName);
        Config.ActiveProfile = pointed ? newName : previous;
        SaveModConfigSafely();
    }

    /// <summary>Sets a profile's description (asked for, multi-line,
    /// when `description` is null).</summary>
    public bool SetProfileDescription(ModProfile profile, string? description = null)
    {
        description ??= Prompt.AskText("Profile description", $"Description of '{profile.Name}':",
            profile.Description, multiline: true);
        if (description == null) return false;
        description = description.Trim();
        if (string.Equals(description, profile.Description, StringComparison.Ordinal)) return false;
        profile.Description = description;
        return SaveProfileEdit(profile);
    }

    // ── export ────────────────────────────────────────────────────────

    /// <summary>The file name an export dialog suggests.</summary>
    public static string DefaultExportFileName(ModProfile profile)
        => ModProfile.SafeFileName(profile.Name) + ".vmprofile";

    /// <summary>The format an export path's extension asks for:
    /// .zip → loader modpack, .vmprofile → bundle, anything else → JSON.</summary>
    public static ProfileExportFormat ExportFormatFor(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".zip"       => ProfileExportFormat.LoaderModpack,
            ".vmprofile" => ProfileExportFormat.Bundle,
            _            => ProfileExportFormat.Json,
        };

    /// <summary>Writes `profile` to `path` (format from the extension
    /// unless given) and tells the user where it went. False on failure.</summary>
    public bool ExportProfile(ModProfile profile, string path, ProfileExportFormat? format = null)
    {
        try
        {
            switch (format ?? ExportFormatFor(path))
            {
                case ProfileExportFormat.LoaderModpack:
                    // The mod list only; the loader downloads each mod
                    // from its recorded source.
                    profile.ExportAsModpackZip(path);
                    break;
                case ProfileExportFormat.Bundle:
                    // A legacy profile carries its own mods/ folder of
                    // bundled archives; a current one is served from the
                    // Library by (mod_id, version).
                    if (profile.BundledArchivesCount() > 0) profile.ExportToZip(path);
                    else profile.ExportToZipUsingLibrary(path, ModsDir);
                    break;
                default:
                    // Metadata only, with no archive references for the
                    // recipient to look for.
                    var copy = new ModProfile
                    {
                        Name        = profile.Name,
                        Description = profile.Description,
                        CreatedAt   = profile.CreatedAt,
                        UpdatedAt   = profile.UpdatedAt,
                        Mods = profile.Mods.Select(m => new ProfileMod
                        {
                            ModId       = m.ModId,
                            DisplayName = m.DisplayName,
                            Version     = m.Version,
                            IsEnabled   = m.IsEnabled,
                            Priority    = m.Priority,
                            Source      = m.Source,
                            PackName    = m.PackName,
                        }).ToList(),
                    };
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, MetroProfile.Serialize(copy));
                    break;
            }
            Prompt.Inform("Exported", $"Profile exported to:\n{path}");
            return true;
        }
        catch (Exception ex)
        {
            Prompt.Inform("Export error", $"Export failed:\n{ex.Message}", PromptKind.Error);
            return false;
        }
    }

    // ── inline edits of a profile's mods ──────────────────────────────

    /// <summary>Turns a mod on or off inside the profile (profile.json
    /// only).</summary>
    public bool SetProfileModEnabled(ModProfile profile, ProfileMod mod, bool enabled)
    {
        if (mod.IsEnabled == enabled) return false;
        mod.IsEnabled = enabled;
        return SaveProfileEdit(profile);
    }

    /// <summary>Sets a mod's load order inside the profile.</summary>
    public bool SetProfileModPriority(ModProfile profile, ProfileMod mod, int priority)
    {
        if (mod.Priority == priority) return false;
        mod.Priority = priority;
        return SaveProfileEdit(profile);
    }

    /// <summary>Drops mods from a profile after one confirmation: their
    /// bundled archives are deleted and profile.json rewritten once.
    /// The live mods folder is untouched.</summary>
    public bool RemoveModsFromProfile(ModProfile profile, IReadOnlyList<ProfileMod> mods, bool confirm = true)
    {
        if (mods.Count == 0) return false;
        if (confirm)
        {
            var body = mods.Count == 1
                ? $"Remove '{mods[0].DisplayName}' from profile '{profile.Name}'?"
                : $"Remove these {mods.Count} mods from profile '{profile.Name}'?\n\n  • "
                  + string.Join("\n  • ", mods.Take(8).Select(m => m.DisplayName))
                  + (mods.Count > 8 ? $"\n  … and {mods.Count - 8} more" : "");
            if (!Prompt.Confirm("Remove from profile",
                    body + "\n\nThis drops the entries from the profile and deletes their bundled "
                    + "archives (if any). The live mods folder is untouched.",
                    PromptKind.Warning))
                return false;
        }

        // Bundles first, then the list; one save at the end keeps
        // profile.json consistent even if a delete fails mid-batch.
        foreach (var pm in mods)
        {
            var bundlePath = profile.ResolveBundledArchive(pm);
            if (!string.IsNullOrEmpty(bundlePath))
            {
                try { File.Delete(bundlePath); }
                catch { /* leave the orphan; not worth blocking */ }
            }
            profile.Mods.Remove(pm);
        }
        SaveProfileEdit(profile);
        return true;
    }

    /// <summary>Adds Library archives to a profile. A mod the profile
    /// already lists is moved to the picked version; a new one is added
    /// enabled at its declared priority. Returns how many entries were
    /// added or changed.</summary>
    public int AddLibraryModsToProfile(ModProfile profile, IEnumerable<ModLibrary.Entry> entries)
    {
        var changed = 0;
        foreach (var le in entries)
        {
            if (string.IsNullOrEmpty(le.ModId) || BuiltInMods.IsBuiltIn(le.ModId)) continue;
            var existing = profile.Mods.FirstOrDefault(m =>
                string.Equals(m.ModId, le.ModId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.Equals(existing.Version, le.Version, StringComparison.Ordinal)) continue;
                existing.Version = le.Version;
                if (!string.IsNullOrEmpty(le.DisplayName)) existing.DisplayName = le.DisplayName;
                if (!existing.SourceRef.IsValid && le.Source.IsValid) existing.Source = le.Source.Key;
                // The profile's bundle (if any) is another version now.
                existing.ArchiveFileName = "";
                changed++;
                continue;
            }
            var priority = 0;
            try
            {
                using var arch = new ModArchive();
                if (arch.Open(le.Path)) priority = arch.ModPriority;
            }
            catch { /* priority 0 */ }
            profile.Mods.Add(new ProfileMod
            {
                ModId       = le.ModId,
                DisplayName = string.IsNullOrEmpty(le.DisplayName) ? le.ModId : le.DisplayName,
                Version     = le.Version,
                IsEnabled   = true,
                Priority    = priority,
                Source      = le.Source.Key,
            });
            changed++;
        }
        if (changed == 0) return 0;
        SaveProfileEdit(profile);
        Say(StatusArea.Profiles, $"Added {changed} Library mod(s) to '{profile.Name}'.");
        return changed;
    }

    /// <summary>Copies mods into another profile (the drag-drop between
    /// profiles), skipping ids it already lists. Each copy is a new
    /// metadata-only entry: the source profile's bundled archives stay
    /// its own, and the copy resolves from the Library or VostokMods on
    /// apply. No-op when `source` is `target`.</summary>
    public ProfileCopyResult CopyModsToProfile(ModProfile target, IReadOnlyList<ProfileMod> mods, ModProfile? source = null)
    {
        if (target == null || mods == null || mods.Count == 0) return new ProfileCopyResult(0, 0);
        if (source != null && ReferenceEquals(source, target)) return new ProfileCopyResult(0, 0);

        var existingIds = new HashSet<string>(target.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;
        foreach (var src in mods)
        {
            if (string.IsNullOrEmpty(src.ModId) || existingIds.Contains(src.ModId)) { skipped++; continue; }
            target.Mods.Add(new ProfileMod
            {
                ModId           = src.ModId,
                DisplayName     = src.DisplayName,
                Version         = src.Version,
                IsEnabled       = src.IsEnabled,
                Priority        = src.Priority,
                Source          = src.Source,
                ArchiveFileName = "",
            });
            existingIds.Add(src.ModId);
            added++;
        }
        if (added > 0)
        {
            target.UpdatedAt = DateTime.UtcNow;
            try { target.SaveMetadataOnly(); }
            catch { /* best-effort; in-memory state still reflects the change */ }
            Requery();
        }

        // Silent success makes it unclear whether the drop did anything.
        var msg = added > 0
            ? $"Copied {added} mod{(added == 1 ? "" : "s")} into '{target.Name}'"
              + (skipped > 0 ? $"  ({skipped} skipped — already present)" : "") + "."
            : skipped > 0
                ? $"Nothing added — all {skipped} dragged mod(s) are already in '{target.Name}'."
                : "Nothing to copy.";
        Prompt.Inform("Drag-and-drop copy", msg, added > 0 ? PromptKind.Info : PromptKind.Warning);
        return new ProfileCopyResult(added, skipped);
    }

    // ── dependency rollup ─────────────────────────────────────────────

    /// <summary>Every mod in the active profile (or locked) that declares
    /// dependencies, each dependency classed enabled / disabled /
    /// missing against the live registry. Read-only.</summary>
    public ProfileDependencyReport GetProfileDependencies()
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) byId.TryAdd(e.ModId, e);

        if (ActiveProfile == null)
            return new ProfileDependencyReport(null, Array.Empty<ProfileDependent>(), 0, 0,
                "Set an active profile from the Profiles page to see its dependency rollup.");

        var locked = new HashSet<string>(ProtectedModIds(), StringComparer.OrdinalIgnoreCase);
        var profileIds = new HashSet<string>(ActiveProfile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);

        static string Label(ModEntry e) =>
            !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId) ? e.ModId
            : Path.GetFileName(e.Path);

        ProfileDependencyState State(string depId, bool required)
        {
            if (byId.TryGetValue(depId, out var dep))
                return dep.IsEnabled
                    ? new ProfileDependencyState(depId, required, DependencyStatus.Enabled, "Installed and enabled")
                    : new ProfileDependencyState(depId, required, DependencyStatus.Disabled, "Installed but currently disabled");
            return new ProfileDependencyState(depId, required, DependencyStatus.Missing, required
                ? "Not installed — required, install or disable the dependent"
                : "Not installed (optional)");
        }

        var dependents = new List<ProfileDependent>();
        foreach (var entry in Registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            if (!profileIds.Contains(entry.ModId) && !locked.Contains(entry.ModId)) continue;
            var required = entry.RequiredDependencies;
            var optional = entry.OptionalDependencies;
            if (required.Count == 0 && optional.Count == 0) continue;
            var deps = required.Select(d => State(d, true))
                .Concat(optional.Select(d => State(d, false)))
                .ToList();
            dependents.Add(new ProfileDependent(entry, Label(entry), deps));
        }
        dependents.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

        var missing = dependents.Sum(d => d.Dependencies.Count(x => x.Required && x.Status == DependencyStatus.Missing));
        var disabled = dependents.Sum(d => d.Dependencies.Count(x => x.Required && x.Status == DependencyStatus.Disabled));

        string summary;
        if (dependents.Count == 0)
        {
            summary = "No mods in this profile declare a `[dependencies]` section. Mod authors opt in "
                + "via mod.txt — most RTV mods don't yet.";
        }
        else
        {
            var bits = new List<string> { $"{dependents.Count} mod(s) with declared deps" };
            if (missing > 0) bits.Add($"{missing} required dep(s) MISSING");
            if (disabled > 0) bits.Add($"{disabled} required dep(s) disabled");
            if (missing == 0 && disabled == 0) bits.Add("all required deps satisfied ✓");
            summary = string.Join("  ·  ", bits);
        }
        return new ProfileDependencyReport(ActiveProfile.Name, dependents, missing, disabled, summary);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static bool SameProfileName(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks before a new profile replaces one of the same name.</summary>
    private bool ConfirmProfileOverwrite(string name, string detail)
    {
        if (!AllProfiles.Any(p => SameProfileName(p.Name, name))) return true;
        return Prompt.Confirm("Overwrite?",
            $"A profile named '{name}' already exists.\n\nOverwrite it{detail}?");
    }

    /// <summary>After a profile was created, imported, renamed or
    /// deleted: reload them all, recompute conflicts, and return the
    /// one named `select` (if any).</summary>
    private ModProfile? AfterProfilesEdited(string? select)
    {
        ReloadProfiles();
        Requery();
        return select == null ? null : AllProfiles.FirstOrDefault(p => SameProfileName(p.Name, select));
    }

    /// <summary>Saves an in-place edit of a profile's metadata, warning
    /// the user when profile.json can't be written. The edit stays in
    /// memory either way.</summary>
    private bool SaveProfileEdit(ModProfile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        var ok = true;
        try { profile.SaveMetadataOnly(); }
        catch (Exception ex)
        {
            Prompt.Inform("Save failed", $"Couldn't save profile change:\n{ex.Message}", PromptKind.Warning);
            ok = false;
        }
        Requery();
        return ok;
    }
}
