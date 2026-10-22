// Mod updates from VostokMods: the version check (with its 1h cache),
// the per-mod Update cell state, updating one mod or every outdated
// one, and rolling a mod back to one of its on-disk backups.
//
// Every network call this area makes goes through UpdateNet, so tests
// can stand in for VostokMods and GitHub.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>The network calls the updates / self-update area makes.
/// Each is null in the app (the real client is used); tests set them so
/// nothing leaves the machine.</summary>
internal sealed class UpdateNetwork
{
    /// <summary>(slugs, forceFresh, progress) → slug → latest version.</summary>
    public Func<IReadOnlyList<string>, bool, IProgress<(int done, int total)>?, Task<Dictionary<string, string>>>? CheckVersions;

    /// <summary>(slug, savePath, progress): download a mod's newest file.</summary>
    public Func<string, string, IProgress<(long done, long total)>?, Task>? DownloadMod;

    /// <summary>slug → the mod's VostokMods detail.</summary>
    public Func<string, Task<VmModDetail>>? GetMod;

    /// <summary>The whole VostokMods listing.</summary>
    public Func<Task<List<VmModSummary>>>? ListAllMods;

    public Func<Task<(ManagerReleaseStatus Status, ManagerRelease? Release)>>? FetchManagerRelease;
    public Func<ManagerReleaseAsset, string, Task>? DownloadManagerAsset;
    public Func<Task<MmlRelease?>>? FetchMmlRelease;

    /// <summary>(tag, asset name, savePath).</summary>
    public Func<string, string, string, Task>? DownloadMmlAsset;

    /// <summary>Starts the manager's swap script (throws when it can't).</summary>
    public Action<string>? StartSwapScript;

    /// <summary>The running manager .exe (defaults to the process's).</summary>
    public Func<string>? CurrentExe;

    /// <summary>Where the manager update is unpacked (defaults to %TEMP%).</summary>
    public string? TempRoot;
}

/// <summary>An IProgress that calls back inline, on whatever thread
/// reports. The client reports from the same async flow that awaits it,
/// which resumes on the UI thread, so nothing needs marshalling — and
/// reports can't arrive after the download has been cleared.</summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public InlineProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}

public sealed partial class ManagerSession
{
    internal UpdateNetwork UpdateNet { get; } = new();

    /// <summary>True while a mod is being downloaded, updated or
    /// reverted; the front end disables row actions meanwhile.</summary>
    public bool IsUpdating { get; private set; }

    /// <summary>The download in flight (its row shows a progress band),
    /// or null.</summary>
    public DownloadProgress? CurrentDownload { get; private set; }

    // ── network wrappers (real client unless a test stood in) ─────────

    private Task<Dictionary<string, string>> FetchVersionsAsync(
        IReadOnlyList<string> slugs, bool forceFresh, IProgress<(int done, int total)>? progress)
        => UpdateNet.CheckVersions != null
            ? UpdateNet.CheckVersions(slugs, forceFresh, progress)
            : Vm.CheckVersionsAsync(slugs, forceFresh, progress);

    private async Task DownloadModFileAsync(
        string slug, string savePath, IProgress<(long done, long total)>? progress)
    {
        if (UpdateNet.DownloadMod != null) await UpdateNet.DownloadMod(slug, savePath, progress);
        else await Vm.DownloadAsync(slug, savePath, progress: progress);
    }

    private Task<VmModDetail> FetchModDetailAsync(string slug)
        => UpdateNet.GetMod != null ? UpdateNet.GetMod(slug) : Vm.GetModAsync(slug);

    private Task<List<VmModSummary>> FetchModListingAsync()
        => UpdateNet.ListAllMods != null ? UpdateNet.ListAllMods() : Vm.ListAllModsAsync();

    // ── the update check ──────────────────────────────────────────────

    /// <summary>Fills LatestVersions for every enabled linked mod. Uses
    /// the persisted cache while it is inside the 1h TTL (saves a round
    /// of requests on every launch); `forceFresh` (the Refresh button)
    /// re-fetches. Reports to the Updates status line and raises Changed
    /// when the versions changed.</summary>
    public async Task CheckUpdatesAsync(bool forceFresh = false)
    {
        if (!forceFresh && Settings.IsCacheFresh)
        {
            LatestVersions.Clear();
            foreach (var (k, v) in Settings.CachedVersions)
                LatestVersions[k] = v;
            var ageMin = (int)Math.Round(Settings.CacheAge.TotalMinutes);
            Say(StatusArea.Updates,
                $"Updates: {SummarizeUpdates()}  (cached ~{ageMin} min ago — Refresh to recheck)");
            RaiseChanged();
            return;
        }

        var sources = Registry.Enabled()
            .Select(e => e.Source)
            .Where(s => s.IsValid)
            .Distinct()
            .ToList();
        if (sources.Count == 0)
        {
            Say(StatusArea.Updates, "Updates: no enabled mods are linked to VostokMods" + UnlinkedHint());
            return;
        }
        Say(StatusArea.Updates, $"Updates: checking {sources.Count} mods on VostokMods ...");
        // VostokMods has no batch endpoint: one request per mod, so
        // show how far along the check is.
        var progress = new InlineProgress<(int done, int total)>(p =>
            Say(StatusArea.Updates, $"Updates: checking mods on VostokMods ... {p.done}/{p.total}"));
        try
        {
            var versions = await FetchVersionsAsync(
                sources.Select(s => s.Id).ToList(), forceFresh, progress);
            LatestVersions.Clear();
            foreach (var s in sources)
                if (versions.TryGetValue(s.Id, out var v)) LatestVersions[s.Key] = v;
            // Persist for the next launch.
            Settings.CachedVersions = new Dictionary<string, string>(LatestVersions);
            Settings.CacheTimestamp = DateTime.UtcNow.ToString("o");
            Settings.Save();
            Say(StatusArea.Updates, $"Updates: {SummarizeUpdates()}");
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Say(StatusArea.Updates, $"Updates: check failed — {ex.Message}");
        }
    }

    /// <summary>Outdated / current / mismatch / unknown counts over the
    /// enabled linked mods, plus how many installed mods are unlinked.</summary>
    public UpdateSummary GetUpdateSummary()
    {
        int outdated = 0, current = 0, unknown = 0, mismatch = 0;
        foreach (var e in Registry.Enabled())
        {
            if (!e.Source.IsValid) continue;
            if (!TryGetLatest(e, out var latest)) { unknown++; continue; }
            if (latest == e.Version) { current++; continue; }
            // Ambiguous-format pair (e.g. mod.txt 0.0.420 vs the
            // listing's 0.4.20_R) — same release, two encodings — is a
            // separate category from "outdated". So is a local build
            // that is ahead of the listing.
            if (VersionsAreAmbiguous(e.Version, latest)) { mismatch++; continue; }
            if (ModRegistry.CompareVersions(e.Version, latest) > 0) { current++; continue; }
            outdated++;
        }
        return new UpdateSummary(outdated, current, mismatch, unknown, UnlinkedMods().Count);
    }

    /// <summary>The Updates line's body: "N outdated, N current, …".</summary>
    public string SummarizeUpdates()
    {
        var s = GetUpdateSummary();
        return $"{s.Outdated} outdated, {s.Current} current"
            + (s.Mismatch > 0 ? $", {s.Mismatch} mismatch" : "")
            + $", {s.Unknown} unknown"
            + UnlinkedHint();
    }

    /// <summary>Tail for the Updates line naming how many installed
    /// mods have no VostokMods source, or "" when all are linked.</summary>
    public string UnlinkedHint()
    {
        var n = UnlinkedMods().Count;
        return n == 0 ? "" : $"  ·  {n} not linked to VostokMods — click to link";
    }

    /// <summary>What a mod's Update cell shows.</summary>
    public UpdateCellState GetUpdateState(ModEntry e)
    {
        if (IsBuiltInActive(e.ModId))
            return new(UpdateStatus.BuiltIn, "⚙", "Built into RTV Mod Manager; updated with the manager.", "");
        if (!e.Source.IsValid)
            return new(UpdateStatus.Unlinked, "—",
                "Not linked to VostokMods — can't check for updates. Click to link it.", "");
        if (!TryGetLatest(e, out var latest))
            return new(UpdateStatus.Unknown, "—",
                "Update status unknown (cache miss). Click Refresh to check.", "");

        var cmp = ModRegistry.CompareVersions(e.Version, latest);
        if (cmp == 0)
            return new(UpdateStatus.Current, "✓", $"Up to date (v{latest}).", latest);
        if (VersionsAreAmbiguous(e.Version, latest))
            // The two strings format differently and can't be ordered:
            // the file IS the latest the site serves, the author's
            // mod.txt just doesn't agree with the listing.
            return new(UpdateStatus.Mismatch, "≠",
                $"Version mismatch: mod.txt v{e.Version}, VostokMods reports v{latest}.\n\n"
                + "The two strings format differently and can't be reliably ordered — likely "
                + "the same release written two ways. The local file matches what VostokMods "
                + "currently serves; the author just hasn't matched mod.txt to the listing.",
                latest);
        if (cmp > 0)
            // Dev build, a manual bump, or the API cache lagging.
            return new(UpdateStatus.Ahead, "↑",
                $"Local v{e.Version} is newer than VostokMods' reported v{latest}. Likely a dev "
                + "build, a manual bump, or the API cache catching up.",
                latest);
        return new(UpdateStatus.Outdated, "⬆",
            $"Update available: v{e.Version} → v{latest}. Click to install.", latest);
    }

    /// <summary>A click on a mod's Update cell: updates an outdated mod,
    /// links an unlinked one, explains a ≠ mismatch; otherwise nothing.</summary>
    public async Task ActivateUpdateCellAsync(ModEntry entry, IProgress<DownloadProgress>? progress = null)
    {
        if (IsUpdating) return;
        if (IsBuiltInActive(entry.ModId)) return;  // updated with the manager
        if (IsOutdated(entry))
            await UpdateModAsync(entry, progress);
        // The muted "—" state means the mod isn't linked: the click
        // links it, the most discoverable place to fix that.
        else if (!entry.Source.IsValid)
            await LinkModAsync(entry);
        // ≠: re-downloading the same file would be wasteful, so say why
        // nothing happens.
        else if (TryGetLatest(entry, out var latest) && VersionsAreAmbiguous(entry.Version, latest))
            Say(StatusArea.Updates,
                $"≠ {entry.DisplayName}: mod.txt v{entry.Version} vs VostokMods v{latest} — same "
                + "release written two ways. Local file matches what the site serves; no download "
                + "needed. (Hover the ≠ icon for the long explanation.)");
    }

    // ── updating ──────────────────────────────────────────────────────

    /// <summary>Downloads the latest version of a linked mod over its
    /// live file. The current file is backed up first (for Revert), the
    /// user's enabled/priority/source cfg state is carried to the new
    /// version's key, the new version goes into the library and the
    /// active profile, and the version cache is refreshed. Returns null
    /// when the mod isn't linked.</summary>
    public Task<ModUpdateResult?> UpdateModAsync(ModEntry e, IProgress<DownloadProgress>? progress = null)
        => UpdateModAsync(e, progress, recheck: true);

    internal async Task<ModUpdateResult?> UpdateModAsync(
        ModEntry e, IProgress<DownloadProgress>? progress, bool recheck)
    {
        var source = e.Source;
        if (!source.IsValid) return null;
        var label = string.IsNullOrEmpty(e.DisplayName) ? Path.GetFileName(e.Path) : e.DisplayName;
        var modId = e.ModId;
        var oldVersion = e.Version;
        // Locked mods are protected from being overwritten: say so,
        // rather than failing silently or clobbering the archive.
        if (IsLocked(e))
        {
            var msg = $"🔒 {label} is locked — update skipped. Unlock it via right-click → Unlock to update.";
            Say(StatusArea.Updates, msg);
            return new ModUpdateResult(modId, label, false, oldVersion, oldVersion, msg);
        }
        // Capture pre-update cfg state: cfg is keyed by mod-id@version,
        // so a new version string starts with no entries unless the
        // user's enable + priority customisations are carried over.
        var oldEnabled = Config.IsEnabled(modId, oldVersion, fallback: true);
        var hadCfgPriority = Config.HasEntry(modId, oldVersion);
        var oldCfgPriority = Config.Priority(modId, oldVersion, e.DeclaredPriority);
        var oldSource = Config.GetModSource(modId, oldVersion);

        var finalPath = e.Path;
        var tempPath = finalPath + ".download";

        IsUpdating = true;
        Say(StatusArea.Updates, $"Downloading {label} ...");
        RaiseChanged();  // re-render to disable row actions
        try
        {
            // Clean up any leftover from a prior failed attempt.
            if (File.Exists(tempPath)) File.Delete(tempPath);

            // Snapshot the current .vmz BEFORE touching it, for "Revert
            // to previous version…". Best-effort: a failure only leaves
            // the rollback unavailable.
            try { ModBackup.MakeBackup(ModsDir, e); }
            catch { /* best-effort; earlier backups still work */ }

            var token = new object();
            _downloadToken = token;
            var bytes = new InlineProgress<(long done, long total)>(p =>
            {
                if (ReferenceEquals(_downloadToken, token))
                    SetDownloadProgress(finalPath, p.done, p.total, label, progress);
            });
            SetDownloadProgress(finalPath, 0, 0, label, progress);
            try
            {
                await DownloadModFileAsync(source.Id, tempPath, bytes);
            }
            finally
            {
                ClearDownloadProgress();
            }
            DownloadedSources.Record(tempPath, source);

            // Swap the file. If the original is locked (the game reading
            // it), File.Delete throws: leave the temp file and say so.
            if (File.Exists(finalPath))
            {
                try { File.Delete(finalPath); }
                catch (Exception ex)
                {
                    var msg = $"Downloaded {label} to {Path.GetFileName(tempPath)}, but "
                        + $"couldn't replace existing file ({ex.Message}). "
                        + "Quit the game and rename the .download file manually.";
                    Say(StatusArea.Updates, msg);
                    return new ModUpdateResult(modId, label, false, oldVersion, oldVersion, msg);
                }
            }
            File.Move(tempPath, finalPath);

            // First rescan so the new mod.txt's version is known.
            Rescan();

            // Migrate cfg state if the declared version changed, so the
            // previous priority override and enabled state survive.
            var migrated = false;
            var newEntry = Registry.FindById(modId);
            var newVersion = newEntry?.Version ?? oldVersion;
            if (!string.IsNullOrEmpty(modId)
                && !string.Equals(newVersion, oldVersion, StringComparison.Ordinal))
            {
                Config.SetEnabled(modId, newVersion, oldEnabled);
                if (hadCfgPriority)
                    Config.SetPriority(modId, newVersion, oldCfgPriority);
                Config.RemoveEntry(modId, oldVersion);
                MoveSourceRecord(modId, oldVersion, newVersion, oldSource);
                SaveModConfigSafely();
                Rescan();
                migrated = true;
            }

            // Seed the library with the new version so a later profile
            // switch finds it.
            try { ModLibrary.Add(ModsDir, finalPath); }
            catch { /* best-effort; live file is still in place */ }

            // Mirror the new version into the active profile so the
            // next switch looks up the post-update version.
            SyncActiveProfileFromEntry(modId);

            // Force-refresh the version cache: the cached value is what
            // said "outdated" in the first place. Best-effort.
            if (recheck)
            {
                try { await CheckUpdatesAsync(forceFresh: true); }
                catch { /* keep going with the stale cache */ }
            }

            var apiVersion = LatestVersions.TryGetValue(source.Key, out var av) ? av : "";
            string message;
            if (string.IsNullOrEmpty(apiVersion)
                || string.Equals(newVersion, apiVersion, StringComparison.Ordinal))
                message = $"Updated {label} → v{newVersion}" + (migrated ? " (cfg state carried over)." : ".");
            else
                // Either side could be lagging (mod.txt not bumped, or
                // the listing not caught up); describe, don't take sides.
                message = $"Updated {label}: file's mod.txt is v{newVersion}, "
                    + $"VostokMods reports v{apiVersion}. The Update column may still show ⬆ "
                    + "until both sides agree — click Refresh in a couple of minutes.";
            Say(StatusArea.Updates, message);
            return new ModUpdateResult(modId, label, true, oldVersion, newVersion, message);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            { try { File.Delete(tempPath); } catch { } }
            Prompt.Error($"Update failed for {label}", ex);
            return new ModUpdateResult(modId, label, false, oldVersion, oldVersion, ex.Message);
        }
        finally
        {
            IsUpdating = false;
            // The new version may change what conflicts.
            Requery();
        }
    }

    /// <summary>Installed mods VostokMods lists a newer version of,
    /// leaving out locked and built-in ones (they are never updated
    /// from here).</summary>
    public List<ModEntry> GetOutdatedMods()
        => Registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId) && !IsLocked(e) && IsOutdated(e))
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Updates every outdated mod (or the given ones) one after
    /// another, then re-checks versions once. Progress is reported per
    /// mod; the results list one entry per mod attempted.</summary>
    public async Task<List<ModUpdateResult>> UpdateAllAsync(
        IProgress<DownloadProgress>? progress = null, IEnumerable<string>? onlyModIds = null)
    {
        var results = new List<ModUpdateResult>();
        if (IsUpdating) return results;
        // Ids, not entries: each update rescans and replaces the entries.
        var ids = (onlyModIds ?? GetOutdatedMods().Select(e => e.ModId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var id in ids)
        {
            var entry = Registry.FindById(id);
            if (entry == null || !IsOutdated(entry)) continue;
            var r = await UpdateModAsync(entry, progress, recheck: false);
            if (r != null) results.Add(r);
        }
        if (results.Any(r => r.Ok))
        {
            try { await CheckUpdatesAsync(forceFresh: true); }
            catch { /* keep the stale cache */ }
        }
        var ok = results.Count(r => r.Ok);
        var failed = results.Count - ok;
        Say(StatusArea.Updates, results.Count == 0
            ? "Updates: nothing to update."
            : $"Updated {ok} mod" + (ok == 1 ? "" : "s")
              + (failed > 0 ? $"; {failed} failed or skipped." : "."));
        return results;
    }

    // ── download progress ─────────────────────────────────────────────

    private object? _downloadToken;

    /// <summary>Records how far the download of the mod at `path` has
    /// got, says so in the Updates line and forwards it to the caller.</summary>
    private void SetDownloadProgress(
        string path, long done, long total, string label, IProgress<DownloadProgress>? progress)
    {
        var p = new DownloadProgress(path, done, total);
        CurrentDownload = p;
        Say(StatusArea.Updates, p.StatusText(label));
        progress?.Report(p);
    }

    private void ClearDownloadProgress()
    {
        _downloadToken = null;
        CurrentDownload = null;
    }

    /// <summary>"1.5 MB"-style byte counts for progress lines.</summary>
    public static string FormatBytes(long n)
    {
        if (n >= 1L << 30) return $"{n / (double)(1L << 30):0.00} GB";
        if (n >= 1L << 20) return $"{n / (double)(1L << 20):0.0} MB";
        if (n >= 1L << 10) return $"{n / (double)(1L << 10):0} KB";
        return $"{n} B";
    }

    // ── backups / revert ──────────────────────────────────────────────

    /// <summary>Every on-disk snapshot of the mod, newest first (the
    /// Revert dialog's list). Empty for a mod without an id.</summary>
    public List<ModBackupView> GetBackups(ModEntry e)
        => string.IsNullOrEmpty(e.ModId)
            ? new List<ModBackupView>()
            : ModBackup.ListBackups(ModsDir, e.ModId).Select(ModBackupView.From).ToList();

    /// <summary>Deletes one backup after confirming. True when deleted.</summary>
    public bool DeleteBackup(ModBackupView b)
    {
        if (!Prompt.Confirm("Delete backup",
                $"Delete backup v{b.Version}  ({b.SizeLabel})?\n\n"
                + "This removes the snapshot file from disk. You will no longer be able to "
                + "revert to this version unless you have another copy elsewhere.",
                PromptKind.Warning))
            return false;
        ModBackup.DeleteBackup(b.Path);
        return !File.Exists(b.Path);
    }

    /// <summary>Rolls the mod back to the backup at `backupPath`. The
    /// current file is snapshotted first so the revert is reversible,
    /// and cfg state follows the version (as an update does, in the
    /// other direction). Returns null when nothing was attempted.</summary>
    public async Task<ModUpdateResult?> RevertModFromBackupAsync(ModEntry e, string backupPath)
    {
        if (IsLocked(e))
        {
            var msg = $"🔒 {e.DisplayName} is locked — revert skipped. Unlock it via right-click → Unlock to revert.";
            Say(StatusArea.Updates, msg);
            return new ModUpdateResult(e.ModId, e.DisplayName, false, e.Version, e.Version, msg);
        }
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return null;

        var modId = e.ModId;
        var oldVersion = e.Version;
        var oldEnabled = Config.IsEnabled(modId, oldVersion, fallback: true);
        var hadCfgPriority = Config.HasEntry(modId, oldVersion);
        var oldCfgPriority = Config.Priority(modId, oldVersion, e.DeclaredPriority);
        var oldSource = Config.GetModSource(modId, oldVersion);

        IsUpdating = true;
        Say(StatusArea.Updates, $"Reverting {e.DisplayName} …");
        RaiseChanged();
        try
        {
            // Snapshot the current version BEFORE overwriting, so the
            // rollback is itself reversible. Best-effort.
            try { ModBackup.MakeBackup(ModsDir, e); } catch { }

            if (File.Exists(e.Path))
            {
                try { File.Delete(e.Path); }
                catch (Exception ex)
                {
                    var msg = $"Couldn't replace `{Path.GetFileName(e.Path)}` ({ex.Message}). Quit the game and try again.";
                    Say(StatusArea.Updates, msg);
                    return new ModUpdateResult(modId, e.DisplayName, false, oldVersion, oldVersion, msg);
                }
            }
            File.Copy(backupPath, e.Path, overwrite: false);

            // Same cfg migration as an update, the version going down
            // (or sideways) instead of up.
            Rescan();
            var newEntry = Registry.FindById(modId);
            var newVersion = newEntry?.Version ?? oldVersion;
            if (!string.IsNullOrEmpty(modId)
                && !string.Equals(newVersion, oldVersion, StringComparison.Ordinal))
            {
                Config.SetEnabled(modId, newVersion, oldEnabled);
                if (hadCfgPriority)
                    Config.SetPriority(modId, newVersion, oldCfgPriority);
                Config.RemoveEntry(modId, oldVersion);
                MoveSourceRecord(modId, oldVersion, newVersion, oldSource);
                SaveModConfigSafely();
                Rescan();
            }

            // The reverted version belongs in the library (Add is
            // idempotent) and in the active profile.
            try { ModLibrary.Add(ModsDir, e.Path); }
            catch { /* best-effort */ }
            SyncActiveProfileFromEntry(modId);

            var done = $"Reverted {e.DisplayName}: v{oldVersion} → v{newVersion}.";
            Say(StatusArea.Updates, done);
            return new ModUpdateResult(modId, e.DisplayName, true, oldVersion, newVersion, done);
        }
        catch (Exception ex)
        {
            Prompt.Error($"Revert failed for {e.DisplayName}", ex);
            return new ModUpdateResult(modId, e.DisplayName, false, oldVersion, oldVersion, ex.Message);
        }
        finally
        {
            IsUpdating = false;
            Requery();
            await Task.CompletedTask;
        }
    }
}
