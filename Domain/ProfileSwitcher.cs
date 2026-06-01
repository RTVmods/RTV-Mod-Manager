// Switches the active mod profile by reconciling THREE things at
// once: the live <mods>/*.vmz files, the in-game mod_config.cfg, and
// the manager's notion of which profile is active.
//
// Storage model (decided in the design pass for Phase 2):
//
//   <mods>/Library/        ← canonical store; every .vmz ever installed lives here
//   <mods>/*.vmz           ← live area; rewritten to match the active profile
//   <mods>/Disabled/       ← per-profile disable folder (existing convention)
//   <mods>/Backups/        ← per-mod rollback snapshots (existing convention)
//   <mods>/Profiles/ — reserved name; profile.json still under %APPDATA% for now
//
// Switching is destructive on the live folder side — the previous
// profile's .vmz files are deleted from `<mods>/` (they still exist
// in Library, so it's never data loss). Library is treated as
// idempotent: if the live file isn't in the library yet, EnsureInLib
// copies it in before we delete the live copy.
//
// Lock semantics: a locked mod_id stays untouched in BOTH stages —
// its live .vmz is preserved through the wipe, its cfg state is
// preserved through the rewrite. The user's "this mod is sacred"
// signal applies regardless of profile.
//
// Failure modes the switcher handles cleanly:
//   • Live .vmz locked by the running game → abort BEFORE deleting
//     anything; report which file is locked.
//   • Profile mod missing from the Library → log + skip that mod;
//     other mods still activate.
//   • Cfg save throws → file moves still committed; surfaces the
//     cfg error so the caller can prompt the user to retry.
//
// Phase 5 will add Library auto-population from the .json/.vmprofile
// import paths so a "profile mod missing from Library" case can
// fall back to a ModWorkshop download.

namespace VostokModManager.Domain;

public static class ProfileSwitcher
{
    /// <summary>Detailed outcome of a switch. The caller renders the
    /// log / errors however it wants (status row, dialog, both).</summary>
    public class SwitchResult
    {
        public bool   Success           { get; set; }
        public string PreviousProfile   { get; set; } = "";
        public string NewProfile        { get; set; } = "";
        public int    CopiedFromLibrary { get; set; }
        public int    KeptLocked        { get; set; }
        public int    RemovedFromLive   { get; set; }
        public List<string> MissingFromLibrary { get; init; } = new();
        public List<string> Errors             { get; init; } = new();
        public List<string> Log                { get; init; } = new();
    }

    /// <summary>Activates `target`. Idempotent in the sense that
    /// running it twice with the same target is safe — every step
    /// checks state before mutating. Returns a SwitchResult with
    /// Success=false when any pre-flight check fails; nothing is
    /// changed on disk in that case.</summary>
    public static SwitchResult SetActive(
        string                 modsDir,
        ModProfile             target,
        ModConfig              modConfig,
        IEnumerable<string>?   lockedModIds = null)
    {
        var result = new SwitchResult
        {
            PreviousProfile = modConfig.ActiveProfile,
            NewProfile      = target.Name,
        };
        var locked = new HashSet<string>(
            lockedModIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Live files + their (mod_id, version) read from the manifest
        // ONCE here. Filename-based lock detection was unreliable —
        // any mod installed with its original filename (legacy /
        // pre-migration) wouldn't match the library's
        // `<safeId>__v<ver>.vmz` convention and got mis-classified
        // as "not locked", which then deleted the locked file in
        // Stage 2. Reading the manifest is ~10ms per file; for a
        // typical 50-mod loadout the total is well under a second
        // and is a one-shot price per switch.
        var liveFiles = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.vmz", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        var liveModIds   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var liveVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in liveFiles)
        {
            using var arch = new ModArchive();
            if (!arch.Open(f)) continue;
            liveModIds[f]   = arch.ModId;
            liveVersions[f] = arch.ModVersion;
        }
        bool IsLockedFile(string path) =>
            liveModIds.TryGetValue(path, out var id)
            && !string.IsNullOrEmpty(id)
            && locked.Contains(id);

        // ── Pre-flight: any LIVE .vmz currently locked by the
        //    running game? If so, abort before touching anything.
        var blockers = new List<string>();
        foreach (var f in liveFiles)
        {
            // Only check files we'd actually try to remove. Locked
            // mods are kept in place, so their files don't need to
            // be writable for the switch to proceed.
            if (IsLockedFile(f)) continue;
            if (!CanOpenExclusive(f))
                blockers.Add(System.IO.Path.GetFileName(f));
        }
        if (blockers.Count > 0)
        {
            result.Errors.Add(
                $"Aborted — {blockers.Count} live file(s) are locked "
                + "by another process (game running?):  "
                + string.Join(", ", blockers.Take(6))
                + (blockers.Count > 6 ? $" + {blockers.Count - 6} more" : ""));
            return result;
        }

        // ── Stage 1: ensure every live .vmz is captured in Library
        //    before we delete any of them. ModLibrary.Add is a no-op
        //    when the (modId,version) is already present.
        foreach (var f in liveFiles)
        {
            if (IsLockedFile(f)) continue;
            var added = ModLibrary.Add(modsDir, f);
            if (added != null)
                result.Log.Add($"  +lib {added.ModId} v{added.Version}");
        }

        // ── Stage 2: clear non-locked live .vmz files. Library has
        //    a copy for each (asserted by Stage 1), so this is safe.
        foreach (var f in liveFiles)
        {
            if (IsLockedFile(f))
            {
                result.KeptLocked++;
                continue;
            }
            try
            {
                File.Delete(f);
                result.RemovedFromLive++;
            }
            catch (Exception ex)
            {
                result.Errors.Add(
                    $"Couldn't remove {System.IO.Path.GetFileName(f)}: {ex.Message}");
            }
        }

        // ── Stage 3: copy active profile's mods from Library → live.
        //    Locked mods are skipped — they're already on disk and
        //    we don't replace them mid-switch.
        // Build a cheap (mod_id → newest library file) index once
        // so the fallback lookup below doesn't enumerate the
        // library directory per missing mod.
        var libByModId = new Dictionary<string, ModLibrary.Entry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var lib in ModLibrary.List(modsDir))
        {
            if (string.IsNullOrEmpty(lib.ModId)) continue;
            if (!libByModId.TryGetValue(lib.ModId, out var cur))
                libByModId[lib.ModId] = lib;
            else if (ModRegistry.CompareVersions(lib.Version, cur.Version) > 0)
                libByModId[lib.ModId] = lib;
        }

        foreach (var pm in target.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            if (locked.Contains(pm.ModId)) continue;
            var libPath = ModLibrary.Find(modsDir, pm.ModId, pm.Version);
            // Fallback: profile.json recorded version doesn't match
            // anything in the library (typical after a JSON-spec
            // import where the MW download's manifest version
            // differs from the spec — same root cause as the ≠
            // mismatch glyph in the main grid). Use the newest
            // library file for this mod_id and log the substitution
            // so the user knows what happened.
            var usedFallback = false;
            if (string.IsNullOrEmpty(libPath)
                && libByModId.TryGetValue(pm.ModId, out var fallback))
            {
                libPath      = fallback.Path;
                usedFallback = true;
                result.Log.Add(
                    $"  ↳ {pm.ModId}: library has v{fallback.Version}, "
                    + $"profile asked for v{pm.Version} — using newest.");
            }
            if (string.IsNullOrEmpty(libPath))
            {
                result.MissingFromLibrary.Add($"{pm.DisplayName} v{pm.Version}");
                continue;
            }
            var fileName = System.IO.Path.GetFileName(libPath);
            var dest     = System.IO.Path.Combine(modsDir, fileName);
            try
            {
                File.Copy(libPath, dest, overwrite: false);
                result.CopiedFromLibrary++;
                if (!usedFallback)
                    result.Log.Add($"  +live {pm.ModId} v{pm.Version}");
            }
            catch (IOException) when (File.Exists(dest))
            {
                // Library file already at destination — a previous
                // partial switch left it there. Acceptable.
                result.Log.Add($"  =live {pm.ModId} v{pm.Version} (already present)");
            }
            catch (Exception ex)
            {
                result.Errors.Add(
                    $"Copy failed for {pm.DisplayName}: {ex.Message}");
            }
        }

        // ── Stage 4: rewrite cfg's active profile section to match
        //    profile.json. profile.json is the source of truth for
        //    non-locked mods; LOCKED mods' cfg entries are captured
        //    BEFORE the wipe and restored after, so the "don't
        //    modify this mod" lock semantic applies to file AND cfg
        //    state regardless of whether the locked mod appears in
        //    the target profile's Mods list.
        var preservedLocked =
            new List<(string ModId, string Version, bool Enabled, int Priority)>();
        if (locked.Count > 0)
        {
            // The cfg-key is `mod_id@version`, so we need the version
            // string the mod is registered under in the OLD profile.
            // Use the live manifest's version when we have it, since
            // the cfg state's version key has to match a real entry.
            // If a locked mod has no live file (rare — would mean
            // the lock outlived its target), we skip it: nothing to
            // preserve.
            // We're still on modConfig.ActiveProfile = previous, so
            // IsEnabled/Priority read from the previous profile's
            // section. Stash them, then advance to the new section.
            foreach (var f in liveFiles)
            {
                if (!IsLockedFile(f)) continue;
                var modId   = liveModIds[f];
                var version = liveVersions.TryGetValue(f, out var v) ? v : "";
                if (string.IsNullOrEmpty(version)) continue;
                preservedLocked.Add((
                    modId,
                    version,
                    modConfig.IsEnabled(modId, version, fallback: true),
                    modConfig.Priority(modId, version, fallback: 0)));
            }
        }

        modConfig.ActiveProfile = target.Name;
        modConfig.ClearProfileEntries(target.Name);
        foreach (var pm in target.Mods)
        {
            if (string.IsNullOrEmpty(pm.ModId)) continue;
            // Skip locked mods here — they'll be restored from
            // preservedLocked below using their REAL cfg state,
            // not whatever the target profile happens to say.
            if (locked.Contains(pm.ModId)) continue;
            modConfig.SetEnabled(pm.ModId, pm.Version, pm.IsEnabled);
            modConfig.SetPriority(pm.ModId, pm.Version, pm.Priority);
        }
        foreach (var (modId, version, enabled, priority) in preservedLocked)
        {
            modConfig.SetEnabled(modId, version, enabled);
            modConfig.SetPriority(modId, version, priority);
        }
        try
        {
            modConfig.Save();
            result.Log.Add("✓ mod_config.cfg updated");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Cfg save failed: {ex.Message}");
        }

        result.Success = result.Errors.Count == 0;
        return result;
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>Cheap "can I delete this?" probe — opens the file
    /// with exclusive write share. If something else is holding it
    /// (game running, antivirus mid-scan, …), the open throws and
    /// we know not to attempt the delete.</summary>
    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var fs = File.Open(path,
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch { return false; }
    }
}
