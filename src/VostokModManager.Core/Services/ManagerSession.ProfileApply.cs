// Applying a profile: make it the active one by reconciling the live
// mods folder and mod_config.cfg with it, row by row. Ported from
// Ui/ProfileApplyDialog: BuildPlan → BuildApplyPlan, RunApplyAsync →
// ExecuteApplyAsync.
//
// Unlike a switch (ProfileSwitcher), Apply shows a plan first and can
// fetch what the Library lacks: each row restores the exact version
// from the profile's bundle, copies it from the Library, or downloads
// it from VostokMods, as the user picks per row.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>Test seam: stands in for VostokModsClient.DownloadAsync
    /// (slug, destination, version or null for the newest) in Apply and
    /// the mod-list import. Null uses the real client.</summary>
    internal Func<string, string, string?, CancellationToken, Task<VmFile>>? ProfilesDownloadOverride { get; set; }

    private Task<VmFile> ProfilesDownloadAsync(string slug, string dest, string? version, CancellationToken ct)
        => ProfilesDownloadOverride != null
            ? ProfilesDownloadOverride(slug, dest, version, ct)
            : Task.Run(() => Vm.DownloadAsync(slug, dest, version, ct), ct);

    /// <summary>What applying `profile` would do for each of its mods,
    /// against the live registry and the Library. Locked and built-in
    /// mods become LockedSkip rows. Mods live but not in the profile
    /// aren't rows: Apply removes them from the live folder (their
    /// archive stays in the Library), which makes it a real switch
    /// rather than a merge.</summary>
    public ApplyPlan BuildApplyPlan(ModProfile profile)
    {
        var locked = new HashSet<string>(ProtectedModIds(), StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) byId[e.ModId] = e;

        // Library snapshot keyed by mod_id, read once for the plan.
        var libByMod = new Dictionary<string, List<ModLibrary.Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var le in ModLibrary.List(ModsDir))
        {
            if (string.IsNullOrEmpty(le.ModId)) continue;
            if (!libByMod.TryGetValue(le.ModId, out var lst))
                libByMod[le.ModId] = lst = new List<ModLibrary.Entry>();
            lst.Add(le);
        }
        var versionOrder = Comparer<string>.Create(ModRegistry.CompareVersions);

        var rows = new List<ApplyPlanRow>();
        foreach (var pm in profile.Mods)
        {
            byId.TryGetValue(pm.ModId, out var installed);
            var bundle = profile.ResolveBundledArchive(pm);
            var hasBundle = !string.IsNullOrEmpty(bundle);
            var hasSource = pm.SourceRef.IsValid;

            // Exact (mod_id, version) Library match wins; else the newest
            // stored version (older profiles may hold a spec version that
            // differs from the manifest version a download produces) —
            // the same fallback ProfileSwitcher uses.
            var libPath = "";
            if (libByMod.TryGetValue(pm.ModId, out var libEntries) && libEntries.Count > 0)
            {
                var exact = libEntries.FirstOrDefault(e => string.Equals(e.Version, pm.Version, StringComparison.Ordinal));
                var pick = exact ?? libEntries.OrderByDescending(e => e.Version, versionOrder).FirstOrDefault();
                if (pick != null) libPath = pick.Path;
            }
            var hasLibrary = !string.IsNullOrEmpty(libPath);

            ApplyPlanRow row;
            // Locked mods short-circuit every other decision.
            if (installed != null && locked.Contains(pm.ModId))
            {
                row = new ApplyPlanRow(ApplyRowKind.LockedSkip, pm, installed, "", "",
                    "🔒", "Locked — file + state untouched", false);
            }
            else if (installed != null)
            {
                var cmp = ModRegistry.CompareVersions(installed.Version, pm.Version);
                var direction = cmp > 0 ? "downgrade" : "upgrade";
                if (cmp == 0)
                    row = new ApplyPlanRow(ApplyRowKind.Match, pm, installed, "", libPath,
                        "✓", "Installed (match)", false);
                else if (hasBundle)
                    row = new ApplyPlanRow(ApplyRowKind.ReplaceFromBundle, pm, installed, bundle, libPath,
                        "↔", $"Will {direction} from bundle (installed {installed.Version} → {pm.Version})", true);
                else if (hasLibrary)
                    // An already-downloaded copy beats a network round trip.
                    row = new ApplyPlanRow(ApplyRowKind.ReplaceFromLibrary, pm, installed, "", libPath,
                        "↔", $"Will {direction} from library (installed {installed.Version} → {pm.Version})", true);
                else if (cmp < 0 && hasSource)
                    row = new ApplyPlanRow(ApplyRowKind.UpdateDownload, pm, installed, "", "",
                        "⬆", "Outdated — update available from VostokMods", true);
                else
                    // Wrong version and no way to switch: settings are
                    // applied to whatever is there.
                    row = new ApplyPlanRow(ApplyRowKind.Match, pm, installed, "", libPath,
                        "✓", $"Installed (version mismatch: {installed.Version} vs {pm.Version})", false);
            }
            else if (hasBundle)
                row = new ApplyPlanRow(ApplyRowKind.InstallFromBundle, pm, null, bundle, libPath,
                    "⬇", "Will install from bundle", true);
            else if (hasLibrary)
                row = new ApplyPlanRow(ApplyRowKind.InstallFromLibrary, pm, null, "", libPath,
                    "⬇", "Will install from library (already downloaded)", true);
            else if (hasSource)
                row = new ApplyPlanRow(ApplyRowKind.MissingDownload, pm, null, "", "",
                    "⬇", "Not installed — will download from VostokMods", true);
            else
                row = new ApplyPlanRow(ApplyRowKind.NoSource, pm, null, "", "",
                    "✗", "Not installed (no bundle, no library, no VostokMods link)", false);
            rows.Add(row);
        }
        return new ApplyPlan(profile, rows, locked);
    }

    /// <summary>Runs an Apply plan:
    ///   0. every live archive is captured in the Library, then the ones
    ///      not in the profile (and not locked) are removed;
    ///   1. bundle and Library copies, then VostokMods downloads;
    ///   2. rescan;
    ///   3. cfg's active profile becomes the applied one, its section is
    ///      rebuilt from the plan (locked mods keep their state), and each
    ///      profile entry's id / version is reconciled with what landed;
    ///   4. cfg saved; 5. profile.json saved when reconciled.
    /// Each line goes to `log` (and the result). Afterwards settings
    /// follow cfg's active profile and everything is refreshed; the
    /// caller runs its update check.</summary>
    public async Task<ApplyResult> ExecuteApplyAsync(
        ApplyPlan plan, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var profile = plan.Profile;
        var locked = new HashSet<string>(plan.LockedModIds, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        void Log(string m) { lines.Add(m); log?.Report(m); }

        // Snapshot the user's choices.
        var actions = plan.Rows.Select(r => (Row: r, Kind: r.Kind, OptIn: r.WillAct)).ToList();

        // ── Phase 0: ensure-in-library + wipe extras ─────────────────
        // A profile mod can be matched by its string id or by its source
        // (JSON-spec imports may carry the URL slug as their id).
        var profileModIds = new HashSet<string>(
            profile.Mods.Select(p => p.ModId).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);
        var profileSources = new HashSet<ModSource>(profile.Mods.Select(p => p.SourceRef).Where(s => s.IsValid));

        var liveSnapshot = Registry.Entries
            .Where(e => e.IsArchive && !string.IsNullOrEmpty(e.ModId)
                     && !string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
            .ToList();

        // Safety net: every live archive goes into the Library first, so
        // the wipe is reversible. Add is idempotent.
        var ensured = 0;
        foreach (var entry in liveSnapshot)
        {
            if (ct.IsCancellationRequested) break;
            try { if (ModLibrary.Add(ModsDir, entry.Path) != null) ensured++; }
            catch { /* best-effort — wipe still proceeds */ }
        }
        if (ensured > 0) Log($"  +lib captured {ensured} live mod(s) before wipe");

        var wiped = 0;
        foreach (var entry in liveSnapshot)
        {
            if (ct.IsCancellationRequested) break;
            if (locked.Contains(entry.ModId)) continue;
            if (profileModIds.Contains(entry.ModId)) continue;
            if (entry.Source.IsValid && profileSources.Contains(entry.Source)) continue;
            try
            {
                File.Delete(entry.Path);
                wiped++;
            }
            catch (Exception ex)
            {
                Log($"  ✗ couldn't remove {Path.GetFileName(entry.Path)}: {ex.Message}");
            }
        }
        if (wiped > 0) Log($"  − wiped {wiped} mod(s) not in '{profile.Name}'");

        // ── Phase 1: file operations — local sources first ───────────
        foreach (var (row, kind, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (kind != ApplyRowKind.InstallFromBundle && kind != ApplyRowKind.ReplaceFromBundle) continue;
            try
            {
                if (kind == ApplyRowKind.ReplaceFromBundle) MoveAsideReplaced(row.Installed);
                var srcName = Path.GetFileName(row.BundlePath);
                var dst = Path.Combine(ModsDir, srcName);
                // Don't clobber a different file already there.
                if (File.Exists(dst)) dst = Path.Combine(ModsDir, MakeUniqueLiveName(srcName));
                File.Copy(row.BundlePath, dst, overwrite: false);
                Log($"  ✓ {row.ProfileMod.DisplayName}: bundle → {Path.GetFileName(dst)}");
            }
            catch (Exception ex)
            {
                Log($"  ✗ {row.ProfileMod.DisplayName}: bundle copy failed — {ex.Message}");
            }
        }

        // Library copies: canonical already, so not re-added after.
        foreach (var (row, kind, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn) continue;
            if (kind != ApplyRowKind.InstallFromLibrary && kind != ApplyRowKind.ReplaceFromLibrary) continue;
            try
            {
                if (kind == ApplyRowKind.ReplaceFromLibrary) MoveAsideReplaced(row.Installed);
                // Library files are `<safeId>__v<ver>.vmz`; live mods use
                // `<safeId>.vmz`, as a VostokMods download would.
                var pm = row.ProfileMod;
                var liveName = $"{ModProfile.SafeFileName(pm.ModId)}.vmz";
                var dst = Path.Combine(ModsDir, liveName);
                if (File.Exists(dst)) dst = Path.Combine(ModsDir, MakeUniqueLiveName(liveName));
                File.Copy(row.LibraryPath, dst, overwrite: false);
                Log($"  ✓ {pm.DisplayName}: library → {Path.GetFileName(dst)}");
            }
            catch (Exception ex)
            {
                Log($"  ✗ {row.ProfileMod.DisplayName}: library copy failed — {ex.Message}");
            }
        }

        // VostokMods downloads.
        foreach (var (row, _, _) in actions.Where(a => a.OptIn
                     && (a.Kind == ApplyRowKind.UpdateDownload || a.Kind == ApplyRowKind.MissingDownload)))
        {
            if (ct.IsCancellationRequested) break;
            var pm = row.ProfileMod;
            var fileName = $"{ModProfile.SafeFileName(pm.ModId)}.vmz";
            var dest = Path.Combine(ModsDir, fileName);
            var source = pm.SourceRef;
            if (!source.IsValid)
            {
                Log($"  ✗ {pm.DisplayName}: no VostokMods link — can't download.");
                continue;
            }
            Log($"⬇ {pm.DisplayName} (VostokMods {source.Id}) …");
            try
            {
                // The profile's version first; when the host no longer
                // has it, the newest (Phase 3 reconciles the profile).
                var wanted = string.IsNullOrWhiteSpace(pm.Version) ? null : pm.Version;
                try
                {
                    await ProfilesDownloadAsync(source.Id, dest, wanted, ct);
                }
                catch (VostokModsException ex) when (wanted != null && ex.Kind == VostokModsError.VersionNotFound)
                {
                    Log($"  ↳ v{wanted} isn't on VostokMods — downloading the newest version");
                    await ProfilesDownloadAsync(source.Id, dest, null, ct);
                }
                DownloadedSources.Record(dest, source);
                // Seed the Library too, or a later switch would report
                // the mod as missing from it.
                try { ModLibrary.Add(ModsDir, dest); }
                catch { /* best-effort; mod still works from live folder */ }
                Log($"  ✓ → {fileName}");
            }
            catch (OperationCanceledException) { Log("  ✗ Cancelled."); break; }
            catch (Exception ex) { Log($"  ✗ Download failed: {ex.Message}"); }
        }

        if (!ct.IsCancellationRequested)
        {
            // ── Phase 2: rescan ──────────────────────────────────────
            Log("↻ Rescanning mods …");
            Registry.Scan(ModsDir, Config);

            // ── Phase 3a: the applied profile becomes cfg's active one.
            // Locked mods' cfg state is captured from the old section
            // first and re-emitted after; the target section is cleared
            // so it is rebuilt from the plan, not merged with stale keys.
            var lockedSnapshot = new List<(string ModId, string Version, bool Enabled, int Priority)>();
            if (locked.Count > 0)
            {
                foreach (var entry in Registry.Entries)
                {
                    if (string.IsNullOrEmpty(entry.ModId) || !locked.Contains(entry.ModId)) continue;
                    lockedSnapshot.Add((entry.ModId, entry.Version,
                        Config.IsEnabled(entry.ModId, entry.Version, fallback: true),
                        Config.Priority(entry.ModId, entry.Version, fallback: 0)));
                }
            }
            Config.ActiveProfile = profile.Name;
            Config.ClearProfileEntries(profile.Name);

            // ── Phase 3: enabled / priority, and each entry's id and
            // version reconciled with what is on disk. The Library is
            // keyed by (mod_id, MANIFEST version), so a stale spec version
            // would make the next switch miss the file.
            var profileDirty = false;
            foreach (var (row, kind, optIn) in actions)
            {
                if (ct.IsCancellationRequested) break;
                if (kind == ApplyRowKind.NoSource) continue;
                if (kind == ApplyRowKind.LockedSkip)
                {
                    Log($"  🔒 {row.ProfileMod.DisplayName}: locked — left untouched.");
                    continue;
                }
                if (ApplyPlanRow.IsCheckableKind(kind) && !optIn) continue;

                var pm = row.ProfileMod;
                var entry = Registry.Entries.FirstOrDefault(
                    e => string.Equals(e.ModId, pm.ModId, StringComparison.OrdinalIgnoreCase));
                // Fallback by source, for a profile id that is a URL slug
                // rather than the manifest id; the profile adopts the
                // manifest id so the next apply / switch matches directly.
                var pmSource = pm.SourceRef;
                if (entry == null && pmSource.IsValid)
                {
                    entry = Registry.Entries.FirstOrDefault(e => e.Source == pmSource && !string.IsNullOrEmpty(e.ModId));
                    if (entry != null)
                    {
                        Log($"  ↳ {pm.DisplayName}: profile id '{pm.ModId}' → archive id '{entry.ModId}' "
                            + $"(matched via VostokMods {pmSource.Id})");
                        pm.ModId = entry.ModId;
                        profileDirty = true;
                    }
                }
                if (entry == null)
                {
                    Log($"  — {pm.DisplayName}: not found after scan, skipping.");
                    continue;
                }
                if (!string.Equals(pm.Version, entry.Version, StringComparison.Ordinal))
                {
                    pm.Version = entry.Version;
                    profileDirty = true;
                }
                // cfg keys are the manifest id, which entry.ModId is.
                Config.SetEnabled(entry.ModId, entry.Version, pm.IsEnabled);
                Config.SetPriority(entry.ModId, entry.Version, pm.Priority);
                Log($"  ✓ {pm.DisplayName}: {(pm.IsEnabled ? "enabled" : "disabled")}, prio {pm.Priority}");
            }

            // ── Phase 3b: locked entries into the new active section.
            foreach (var (modId, version, enabled, priority) in lockedSnapshot)
            {
                Config.SetEnabled(modId, version, enabled);
                Config.SetPriority(modId, version, priority);
            }

            // ── Phase 4: save cfg ────────────────────────────────────
            try
            {
                Config.Save();
                Log("✓ mod_config.cfg saved.");
            }
            catch (Exception ex)
            {
                Log($"✗ mod_config.cfg save failed: {ex.Message}");
            }

            // ── Phase 5: one profile.json write for the reconciliation.
            if (profileDirty)
            {
                profile.UpdatedAt = DateTime.UtcNow;
                try
                {
                    profile.SaveMetadataOnly();
                    Log("✓ profile.json versions reconciled.");
                }
                catch (Exception ex)
                {
                    Log($"✗ profile.json save failed: {ex.Message}");
                }
            }
        }

        var applied = !ct.IsCancellationRequested;
        Log(applied
            ? "✓ Profile applied."
            : "✗ Cancelled — partial changes may have been applied.");

        // The old dialog's caller mirrored cfg → settings and reloaded on
        // close. Rescan even after a cancel: files may have moved.
        SyncAfterProfileChanges(liveFolderChanged: true);
        if (applied) Say(StatusArea.Profiles, $"Applied '{profile.Name}'.");
        return new ApplyResult(applied, wiped, lines);
    }

    /// <summary>Moves an installed archive aside (.replaced-by-profile.bak)
    /// before a replace, so it can be recovered.</summary>
    private static void MoveAsideReplaced(ModEntry? installed)
    {
        if (installed == null || !installed.IsArchive || !File.Exists(installed.Path)) return;
        var bak = installed.Path + ".replaced-by-profile.bak";
        if (File.Exists(bak)) File.Delete(bak);
        File.Move(installed.Path, bak);
    }

    /// <summary>`name_2.vmz`, `name_3.vmz`… — the first not in the mods folder.</summary>
    private string MakeUniqueLiveName(string filename)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}{ext}";
            if (!File.Exists(Path.Combine(ModsDir, candidate))) return candidate;
        }
        return $"{baseName}_{Guid.NewGuid():N}{ext}";
    }
}
