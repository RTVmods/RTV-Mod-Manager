// Per-mod version snapshots. Every time UpdateModAsync (or the user-
// driven Revert flow) is about to overwrite a mod's .vmz, the
// previous file is copied into:
//
//   <modsDir>/Backups/<safe-mod-id>/v<ver>__<ts>.vmz
//
// where <ver> is the mod's declared version string and <ts> is a
// compact local-time stamp (`yyyyMMdd-HHmmss`). The version + the
// timestamp together let multiple backups of the same version coexist
// (e.g. you reverted, then re-updated, then reverted again — each
// snapshot is its own file).
//
// Layout choice: backups live INSIDE the mods folder under a dedicated
// `Backups/` subfolder. ModRegistry.Scan explicitly skips that name
// the same way it skips `Disabled/`, so neither our scanner nor the
// in-game loader treats backup archives as live mods. Putting them
// next to the mods folder means they travel with the user's mods
// install (backups survive an %APPDATA% wipe) and are easy to inspect
// in Explorer.
//
// Retention: only the last MaxBackupsPerMod (5) snapshots for any
// given mod are kept on disk. Older ones are pruned automatically
// after each MakeBackup. Users can still nuke individual snapshots
// from the Revert dialog before the cap kicks in.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class ModBackup
{
    /// <summary>Cap on how many snapshots we keep per mod. After a
    /// MakeBackup pushes the count past this, the oldest get pruned.</summary>
    public const int MaxBackupsPerMod = 5;

    /// <summary>Subfolder under the mods directory where all per-mod
    /// backup folders live. Parallel to "Disabled".</summary>
    public const string BackupsFolderName = "Backups";

    /// <summary>Root folder for all per-mod backup folders, computed
    /// from the active mods directory.</summary>
    public static string BackupRoot(string modsDir) =>
        System.IO.Path.Combine(modsDir, BackupsFolderName);

    /// <summary>Subfolder for one mod, derived from a sanitised
    /// mod_id. Empty mod_ids fall back to "unknown" so the folder
    /// is still creatable; callers should still avoid backing up
    /// entries with no mod_id since the link back is unreliable.</summary>
    public static string BackupDirFor(string modsDir, string modId)
    {
        var safe = SanitiseModId(modId);
        return System.IO.Path.Combine(BackupRoot(modsDir), safe);
    }

    /// <summary>One snapshot on disk.</summary>
    public class BackupEntry
    {
        public string   Path      { get; set; } = "";
        public string   ModId     { get; set; } = "";
        public string   Version   { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public long     SizeBytes { get; set; }
    }

    /// <summary>Copies the mod's CURRENT .vmz into the backup folder
    /// under a versioned filename, then prunes older snapshots back
    /// to MaxBackupsPerMod. No-op (returns empty) when the entry has
    /// no mod_id, isn't an archive, or the source file is missing —
    /// same defensive gating UpdateModAsync uses upstream.
    /// Returns the destination path on success so callers can log
    /// it.</summary>
    public static string MakeBackup(string modsDir, ModEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ModId)) return "";
        if (!entry.IsArchive) return "";
        if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) return "";

        var dir = BackupDirFor(modsDir, entry.ModId);
        Directory.CreateDirectory(dir);

        var safeVer = SanitiseSegment(string.IsNullOrEmpty(entry.Version)
            ? "unknown"
            : entry.Version);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = System.IO.Path.Combine(dir, $"v{safeVer}__{stamp}.vmz");

        // Collision tail — same-second backups (rare, but possible
        // when the user clicks Revert + something rapid). Append a
        // short counter rather than overwriting an existing snapshot.
        if (File.Exists(dest))
        {
            for (int i = 2; i < 100; i++)
            {
                var alt = System.IO.Path.Combine(dir,
                    $"v{safeVer}__{stamp}_{i}.vmz");
                if (!File.Exists(alt)) { dest = alt; break; }
            }
        }
        File.Copy(entry.Path, dest, overwrite: false);

        // Prune oldest to keep the cap. Done as the very last step
        // so the just-written backup is the one most likely to be
        // kept (it has the freshest CreatedAt) even if some clock
        // skew or filesystem weirdness rearranges timestamps.
        PruneOld(modsDir, entry.ModId);
        return dest;
    }

    /// <summary>Lists all snapshots for a mod, newest first. Parses
    /// the version + timestamp out of the filename so the dialog can
    /// render them without re-cracking each .vmz.</summary>
    public static List<BackupEntry> ListBackups(string modsDir, string modId)
    {
        var dir = BackupDirFor(modsDir, modId);
        if (!Directory.Exists(dir)) return new();
        var result = new List<BackupEntry>();
        foreach (var f in Directory.GetFiles(dir, "*.vmz"))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(f);
            // Expected shape: v<ver>__<stamp> (+ optional _N collision tail).
            // Both pieces are optional so unmatched files still show.
            var m = _reBackupName.Match(name);
            var ver   = m.Success ? m.Groups["ver"].Value   : "";
            var stamp = m.Success ? m.Groups["stamp"].Value : "";
            DateTime createdAt;
            if (!DateTime.TryParseExact(
                    stamp, "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal,
                    out createdAt))
            {
                try { createdAt = File.GetCreationTime(f); }
                catch { createdAt = DateTime.MinValue; }
            }
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            result.Add(new BackupEntry
            {
                Path      = f,
                ModId     = modId,
                Version   = ver,
                CreatedAt = createdAt,
                SizeBytes = size,
            });
        }
        return result
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    /// <summary>Hard-deletes a single backup file. Caller refreshes
    /// the dialog list. Best-effort: failures are silent so a locked
    /// or already-gone file doesn't surface a scary error.</summary>
    public static void DeleteBackup(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Deletes any backups beyond the newest MaxBackupsPerMod
    /// for the given mod. Called automatically after MakeBackup, but
    /// safe to invoke on its own (e.g. on app startup) to reclaim
    /// disk that accumulated before the cap existed.</summary>
    public static void PruneOld(string modsDir, string modId)
    {
        var all = ListBackups(modsDir, modId);
        if (all.Count <= MaxBackupsPerMod) return;
        foreach (var stale in all.Skip(MaxBackupsPerMod))
            DeleteBackup(stale.Path);
    }

    // ── Naming helpers ─────────────────────────────────────────────

    private static readonly Regex _reBackupName = new(
        @"^v(?<ver>.+?)__(?<stamp>\d{8}-\d{6})(?:_\d+)?$",
        RegexOptions.Compiled);

    private static string SanitiseModId(string modId)
    {
        var s = (modId ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return "unknown";
        return SanitiseSegment(s);
    }

    /// <summary>Strips chars that would break the filesystem (or
    /// confuse the regex parser above). Keeps version-friendly chars
    /// like `.` and `-`. Length-capped so a wild manifest doesn't
    /// produce a path past Windows' MAX_PATH.</summary>
    private static string SanitiseSegment(string s)
    {
        s = Regex.Replace(s, @"[^\w\.\-]", "_");
        if (s.Length > 64) s = s[..64];
        return string.IsNullOrEmpty(s) ? "_" : s;
    }
}
