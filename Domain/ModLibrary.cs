// Canonical store of every .vmz the user has ever added — the
// backing storage for the profile model.
//
//   <mods>/Library/<safe-mod-id>__v<ver>.vmz
//
// One file per (mod_id, version) pair. Multiple profiles can
// reference the same library file by mod_id+version, so the live
// `<mods>/` folder gets populated by COPY (not move) from here when
// a profile is activated. The library survives profile switches,
// game uninstalls, and "delete from active profile" actions —
// removing a file from the library is an explicit second-stage
// confirmation (handled in MainForm's delete-mod flow).
//
// Naming: matches the Backups/ convention (`<modId>__v<ver>`), so a
// glance at either folder tells you which mod + which version. The
// filename is the lookup key — `Find(modId, version)` simply checks
// for the corresponding path. No catalog file, no manifest cache.
// `ModArchive.Open` is the source of truth for what's INSIDE each
// .vmz; the filename is just the index.
//
// ModRegistry.Scan SKIPS the Library/ folder (same way it skips
// Disabled/ and Backups/) so library files never appear as live
// mods in the manager grid or get loaded by the in-game loader.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class ModLibrary
{
    /// <summary>Subfolder under the mods directory where the library
    /// lives. Parallel to "Disabled" and "Backups".</summary>
    public const string LibraryFolderName = "Library";

    /// <summary>Absolute path to the library root, derived from the
    /// active mods directory.</summary>
    public static string LibraryDir(string modsDir) =>
        System.IO.Path.Combine(modsDir, LibraryFolderName);

    /// <summary>One file in the library.</summary>
    public class Entry
    {
        public string Path          { get; init; } = "";
        public string ModId         { get; init; } = "";
        public string Version       { get; init; } = "";
        public string DisplayName   { get; init; } = "";
        public long   SizeBytes     { get; init; }
        /// <summary>ModWorkshop numeric id from this archive's
        /// `[updates] modworkshop`, or 0 when absent. Surfaced so
        /// callers don't need to re-open the .vmz just to pull
        /// the MW id — `ModLibrary.List` already opens each archive
        /// once for the manifest read, so this is essentially free.
        /// </summary>
        public int    ModWorkshopId { get; init; }
    }

    /// <summary>Deterministic path for a (modId, version) pair.
    /// Doesn't check whether the file exists — see Find() for that.
    /// </summary>
    public static string PathFor(string modsDir, string modId, string version)
    {
        var safeId  = SanitiseSegment(string.IsNullOrEmpty(modId)   ? "unknown" : modId);
        var safeVer = SanitiseSegment(string.IsNullOrEmpty(version) ? "unknown" : version);
        return System.IO.Path.Combine(
            LibraryDir(modsDir), $"{safeId}__v{safeVer}.vmz");
    }

    /// <summary>Copies a .vmz into the library, keyed by the
    /// archive's declared mod_id + version. Returns the library
    /// path on success or null when the source isn't a valid mod
    /// (missing mod.txt, no mod_id). Idempotent: if the same
    /// key already exists in the library, returns that existing
    /// path and DOES NOT overwrite — we trust the on-disk copy as
    /// canonical to avoid clobbering a user's version pin.</summary>
    public static Entry? Add(string modsDir, string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return null;
        using var arch = new ModArchive();
        if (!arch.Open(sourcePath)) return null;
        var modId   = arch.ModId;
        var version = arch.ModVersion;
        if (string.IsNullOrEmpty(modId)) return null;

        var dest = PathFor(modsDir, modId, version);
        var dir  = System.IO.Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(dir);
        if (!File.Exists(dest))
        {
            try { File.Copy(sourcePath, dest); }
            catch { return null; }
        }
        long size = 0;
        try { size = new FileInfo(dest).Length; } catch { }
        return new Entry
        {
            Path        = dest,
            ModId       = modId,
            Version     = version,
            DisplayName = string.IsNullOrEmpty(arch.ModName) ? modId : arch.ModName,
            SizeBytes   = size,
        };
    }

    /// <summary>Locates the library copy of a (modId, version) pair.
    /// Returns empty when the file isn't on disk.</summary>
    public static string Find(string modsDir, string modId, string version)
    {
        var p = PathFor(modsDir, modId, version);
        return File.Exists(p) ? p : "";
    }

    /// <summary>Lists every .vmz currently in the library. Parses
    /// each file via ModArchive (cheap — only the mod.txt is read)
    /// so the returned entries carry display name + size. Files
    /// that don't open cleanly are silently skipped.</summary>
    public static List<Entry> List(string modsDir)
    {
        var dir = LibraryDir(modsDir);
        if (!Directory.Exists(dir)) return new();
        var result = new List<Entry>();
        foreach (var f in Directory.GetFiles(dir, "*.vmz"))
        {
            using var arch = new ModArchive();
            if (!arch.Open(f)) continue;
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            result.Add(new Entry
            {
                Path          = f,
                ModId         = arch.ModId,
                Version       = arch.ModVersion,
                DisplayName   = string.IsNullOrEmpty(arch.ModName) ? arch.ModId : arch.ModName,
                SizeBytes     = size,
                ModWorkshopId = arch.ModWorkshopId,
            });
        }
        return result;
    }

    /// <summary>Hard-deletes a library file. Caller is responsible
    /// for confirming with the user (delete-from-library is the
    /// second stage of the right-click → Delete mod flow). Best-
    /// effort: failures are silent so a locked or already-gone file
    /// doesn't surface a scary error.</summary>
    public static void Remove(string modsDir, string modId, string version)
    {
        var path = Find(modsDir, modId, version);
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); }
        catch { /* best-effort */ }
    }

    /// <summary>Same name-sanitiser shape ModBackup uses, so the two
    /// stores look symmetrical on disk and a manual rename never
    /// drifts between them.</summary>
    private static string SanitiseSegment(string s)
    {
        s = Regex.Replace(s ?? "", @"[^\w\.\-]", "_");
        if (s.Length > 64) s = s[..64];
        return string.IsNullOrEmpty(s) ? "_" : s;
    }
}
