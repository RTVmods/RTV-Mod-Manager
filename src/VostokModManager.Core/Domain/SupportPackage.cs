// Collect Support Package — bundles everything a maintainer needs to
// diagnose a user's problem into one shareable .vmzlog file:
//
//   <profile>__support__<UTC-stamp>.vmzlog   (a plain zip)
//   ├── mods.json              ← every mod in the mods folder (enabled or
//   │                            not): id, version, file, size, SHA-256,
//   │                            load order and VostokMods source
//   ├── profile.json           ← the active profile's mod list
//   ├── logs/                  ← the entire %APPDATA%\Road to Vostok\logs\
//   │                            directory (godot.log + rotated logs)
//   ├── extra-logs/            ← (optional) root-level *.log files from the
//   │                            game user dir (modloader_filescope.log, …)
//   ├── mod_config.cfg         ← the in-game loader's enabled/priority state
//   ├── saves/listing.txt      ← the game's save files and the Save Slots
//   │                            folder (MultiSaveSlots): names, sizes and
//   │                            modification times, never the save contents
//   ├── saves/MultiSaveSlots/… ← the slot folder's small state files only:
//   │                            state.cfg, pending.cfg, each slot's
//   │                            _meta.cfg, each backup's _backup.cfg
//   ├── support-info.json      ← manager version, edition, MML version,
//   │                            counts, timestamp
//   └── profile.vmprofile      ← (opt-in) the active profile with its mod
//                                archives, directly Import-able in the
//                                Profiles Manager
//
// The mod archives stay out by default. A big modlist is gigabytes of
// them (a 108-mod profile made a 2.4 GB package) and diagnosing a log
// never needed them: the checksums in mods.json tell exactly which build
// of each mod was installed, and a mismatch against the VostokMods file
// shows a repacked or locally edited mod.
//
// All zip entries use FORWARD-SLASH paths (mirrors ModPacker /
// ModProfile.ExportToZipUsingLibrary) so the file opens cleanly in any
// tool and the inner .vmprofile is accepted by the Godot-side loader.
//
// When the mod files are asked for, the inner .vmprofile is built to a
// TEMP file straight from the live entries — we deliberately do NOT call
// SaveWithBundles, because that would overwrite the user's saved profile
// folder under ProfilesDir. Instead we ingest each live archive into the
// Library (idempotent) and then reuse ModProfile.ExportToZipUsingLibrary,
// exactly like a normal profile export, so the bundle is byte-identical
// to one the user would get from Profiles → Export.

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VostokModManager.Domain;

public static class SupportPackage
{
    /// <summary>The Godot runtime log directory for Road to Vostok:
    /// %APPDATA%\Road to Vostok\logs\. Mirrors the user-data convention
    /// ModConfig.DefaultPath uses for mod_config.cfg.</summary>
    public static string GodotLogsDir =>
        System.IO.Path.Combine(GameUserDir, "logs");

    /// <summary>The game's Godot user-data root:
    /// %APPDATA%\Road to Vostok\. Root-level mod logs (modloader_filescope.log,
    /// etc.) and mod_config.cfg live directly here.</summary>
    public static string GameUserDir
    {
        get
        {
            var appdata = AppPaths.RoamingAppData;
            return string.IsNullOrEmpty(appdata)
                ? ""
                : System.IO.Path.Combine(appdata, "Road to Vostok");
        }
    }

    /// <summary>Number of *.log files currently in the Godot logs
    /// directory — used by the dialog to show "Logs found: N" before
    /// the user commits to building the package.</summary>
    public static int CountLogFiles()
    {
        try
        {
            return Directory.Exists(GodotLogsDir)
                ? Directory.GetFiles(GodotLogsDir, "*.log").Length
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>Outcome of a Build() call. `Warnings` collects
    /// non-fatal problems (a locked godot.log, missing mod_config.cfg,
    /// a mod with no library copy) so the dialog can show them without
    /// the whole operation having failed.</summary>
    public class Result
    {
        public string OutputPath  { get; init; } = "";
        public long   BytesWritten { get; set; }
        public int    LogFileCount { get; set; }
        /// <summary>Mods in the active profile (or snapshot).</summary>
        public int    ModCount     { get; set; }
        /// <summary>Rows in mods.json: every mod in the mods folder.</summary>
        public int    ListedModCount { get; set; }
        /// <summary>The mod archives went in (profile.vmprofile).</summary>
        public bool   ModFilesIncluded { get; set; }
        public List<string> Warnings { get; } = new();
    }

    /// <summary>One row of mods.json. `File` is the path under the mods
    /// folder with forward slashes ("Disabled/alpha-1.0.vmz").
    /// `SizeBytes` and `Sha256` (lower-case hex) are null for a folder
    /// mod or an unreadable archive. `Source` is "vostokmods:&lt;slug&gt;",
    /// or "" when the mod is not linked.</summary>
    public sealed record ModListRow(
        string Id,
        string Name,
        string Version,
        string File,
        bool Enabled,
        int Priority,
        bool IsFolder,
        long? SizeBytes,
        string? Sha256,
        string Source,
        string PageUrl,
        bool InActiveProfile,
        bool BuiltIn);

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions _rowOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Builds the .vmzlog at `outputPath`.
    ///
    /// <paramref name="activeProfile"/> may be null (pre-migration
    /// "all mods" mode) — the caller is expected to pass a profile
    /// built via ModProfile.FromRegistry in that case, but this method
    /// also tolerates null by snapshotting all live entries.
    ///
    /// <paramref name="includeModFiles"/> adds profile.vmprofile with
    /// the profile's mod archives; without it the package holds only
    /// the mod list with checksums.
    ///
    /// <paramref name="progress"/> is invoked as (message, percent 0-100)
    /// on the calling thread; the dialog marshals it to the UI.</summary>
    public static Result Build(
        ModProfile? activeProfile,
        IEnumerable<ModEntry> liveEntries,
        string modsDir,
        string outputPath,
        bool includeRootLogs,
        Action<string, int>? progress = null,
        bool includeModFiles = false)
    {
        var entries = liveEntries?.ToList() ?? new List<ModEntry>();
        var profile = activeProfile
            ?? ModProfile.FromRegistry("Support Snapshot", "", entries);

        var result = new Result
        {
            OutputPath = outputPath,
            ModCount   = profile.Mods.Count,
        };

        void Report(string msg, int pct) => progress?.Invoke(msg, pct);

        var outDir = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        // ── 1. The mod list with a checksum per archive (5–45 %).
        var rows = ListMods(profile, entries, modsDir, result.Warnings,
            (msg, done, total) => Report(msg, 5 + (total == 0 ? 40 : 40 * done / total)));
        result.ListedModCount = rows.Count;

        var tempVmprofile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vmm_support_" + Guid.NewGuid().ToString("N") + ".vmprofile");
        try
        {
            // ── 2. (opt-in) the profile with its mod archives.
            if (includeModFiles)
            {
                // Ingest live archives into the Library so the bundle
                // export can resolve every (mod_id, version). Idempotent.
                Report("Indexing mods into the library…", 46);
                foreach (var e in entries)
                {
                    if (e.IsArchive && File.Exists(e.Path))
                    {
                        try { ModLibrary.Add(modsDir, e.Path); }
                        catch { /* best-effort — export simply marks it no-bundle */ }
                    }
                }
                Report("Bundling the mod files…", 50);
                try
                {
                    profile.ExportToZipUsingLibrary(tempVmprofile, modsDir);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Profile bundle failed: {ex.Message}");
                }
            }

            // ── 3. Assemble the .vmzlog.
            Report("Writing support package…", 60);
            using (var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                // 3a. mods.json
                try
                {
                    var listEntry = zip.CreateEntry("mods.json", CompressionLevel.Optimal);
                    using var es = listEntry.Open();
                    using var sw = new StreamWriter(es);
                    sw.Write(JsonSerializer.Serialize(rows, _rowOpts));
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"mods.json: {ex.Message}");
                }

                // 3b. (opt-in) inner .vmprofile. Archives are already
                // compressed: storing them saves minutes of deflate.
                if (includeModFiles && File.Exists(tempVmprofile))
                    result.ModFilesIncluded = AddFile(zip, tempVmprofile, "profile.vmprofile",
                        result.Warnings, CompressionLevel.NoCompression);

                // 3c. loose profile.json metadata at the root
                try
                {
                    var jsonEntry = zip.CreateEntry("profile.json", CompressionLevel.Optimal);
                    using var es = jsonEntry.Open();
                    using var sw = new StreamWriter(es);
                    sw.Write(MetroProfile.Serialize(profile));
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"profile.json: {ex.Message}");
                }

                // 3d. whole Godot logs directory
                Report("Collecting Godot logs…", 70);
                if (Directory.Exists(GodotLogsDir))
                {
                    string[] logs;
                    try { logs = Directory.GetFiles(GodotLogsDir); }
                    catch (Exception ex)
                    {
                        logs = Array.Empty<string>();
                        result.Warnings.Add($"logs/: {ex.Message}");
                    }
                    foreach (var f in logs)
                    {
                        var name = System.IO.Path.GetFileName(f);
                        if (AddFile(zip, f, "logs/" + name, result.Warnings))
                            result.LogFileCount++;
                    }
                }
                else
                {
                    result.Warnings.Add(
                        "Godot logs directory not found: " + GodotLogsDir);
                }

                // 3e. (optional) root-level *.log mod logs
                if (includeRootLogs && Directory.Exists(GameUserDir))
                {
                    string[] rootLogs;
                    try { rootLogs = Directory.GetFiles(GameUserDir, "*.log"); }
                    catch { rootLogs = Array.Empty<string>(); }
                    foreach (var f in rootLogs)
                    {
                        var name = System.IO.Path.GetFileName(f);
                        AddFile(zip, f, "extra-logs/" + name, result.Warnings);
                    }
                }

                // 3f. mod_config.cfg (cheap, high diagnostic value)
                Report("Adding loader state…", 85);
                var cfg = ModConfig.DefaultPath;
                if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg))
                    AddFile(zip, cfg, "mod_config.cfg", result.Warnings);
                else
                    result.Warnings.Add("mod_config.cfg not found.");

                // 3h. The saves: what is there and how old it is. A save
                // that loaded wrong (an older run, the wrong shelter) is
                // diagnosed from the file dates and the Save Slots state,
                // so the listing goes in with every package; the save
                // contents stay out.
                Report("Listing the saves…", 88);
                var savesListed = 0;
                try
                {
                    var listing = ListSaves(GameUserDir, out savesListed);
                    var listEntry = zip.CreateEntry("saves/listing.txt", CompressionLevel.Optimal);
                    using (var es = listEntry.Open())
                    using (var sw = new StreamWriter(es))
                        sw.Write(listing);
                    foreach (var f in SaveStateFiles(GameUserDir))
                    {
                        var rel = System.IO.Path.GetRelativePath(GameUserDir, f).Replace('\\', '/');
                        AddFile(zip, f, "saves/" + rel, result.Warnings);
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"saves/: {ex.Message}");
                }

                // 3g. support-info.json
                try
                {
                    var info = BuildInfo(profile, entries, modsDir, result.LogFileCount, result.ModFilesIncluded, savesListed);
                    var infoEntry = zip.CreateEntry("support-info.json", CompressionLevel.Optimal);
                    using var es = infoEntry.Open();
                    using var sw = new StreamWriter(es);
                    sw.Write(JsonSerializer.Serialize(info, _opts));
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"support-info.json: {ex.Message}");
                }
            }
        }
        finally
        {
            try { if (File.Exists(tempVmprofile)) File.Delete(tempVmprofile); }
            catch { /* temp cleanup — best-effort */ }
        }

        try { result.BytesWritten = new FileInfo(outputPath).Length; } catch { }
        Report("Done.", 100);
        return result;
    }

    /// <summary>The mods.json rows: every live entry, enabled or not, in
    /// load order. Each archive is hashed with a streaming SHA-256, so a
    /// texture pack of several hundred MB never sits in memory.
    /// `progress` gets (message, mods done, mods total).</summary>
    public static List<ModListRow> ListMods(
        ModProfile? profile, IEnumerable<ModEntry> entries, string modsDir,
        List<string>? warnings = null, Action<string, int, int>? progress = null)
    {
        var list = entries.ToList();
        var inProfile = new HashSet<string>(
            (profile?.Mods ?? new List<ProfileMod>())
                .Where(m => !string.IsNullOrEmpty(m.ModId))
                .Select(m => m.ModId + "@" + m.Version),
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<ModListRow>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var file = RelativeToMods(modsDir, e.Path);
            progress?.Invoke($"Checksumming mods… {i + 1}/{list.Count}", i, list.Count);

            long? size = null;
            string? sha = null;
            if (e.IsArchive)
            {
                try
                {
                    using var fs = new FileStream(e.Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    size = fs.Length;
                    sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    warnings?.Add($"mods.json: couldn't checksum {file}: {ex.Message}");
                }
            }

            rows.Add(new ModListRow(
                Id: e.ModId,
                Name: e.DisplayName,
                Version: e.Version,
                File: file,
                Enabled: e.IsEnabled,
                Priority: e.Priority,
                IsFolder: !e.IsArchive,
                SizeBytes: size,
                Sha256: sha,
                Source: e.Source.Key,
                PageUrl: e.Source.PageUrl,
                InActiveProfile: inProfile.Contains(e.ModId + "@" + e.Version),
                BuiltIn: BuiltInMods.IsBuiltIn(e.ModId)));
        }
        progress?.Invoke($"Checksummed {list.Count} mod(s).", list.Count, list.Count);

        return rows
            .OrderByDescending(r => r.Enabled)
            .ThenBy(r => r.Priority)
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>`path` under the mods folder with forward slashes, or the
    /// bare file name when it lies elsewhere.</summary>
    private static string RelativeToMods(string modsDir, string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(modsDir))
            {
                var rel = System.IO.Path.GetRelativePath(modsDir, path);
                if (!rel.StartsWith("..") && !System.IO.Path.IsPathRooted(rel))
                    return rel.Replace('\\', '/');
            }
        }
        catch { /* fall through */ }
        return System.IO.Path.GetFileName(path);
    }

    /// <summary>Copies one on-disk file into the zip under
    /// <paramref name="entryPath"/> (forward-slash). Reads via a
    /// share-all stream so a log the running game still holds open can
    /// still be copied. Returns true on success; records a warning and
    /// returns false on failure (e.g. exclusive lock).</summary>
    private static bool AddFile(
        ZipArchive zip, string sourcePath, string entryPath, List<string> warnings,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        try
        {
            var entry = zip.CreateEntry(entryPath, level);
            using var es = entry.Open();
            // FileShare.ReadWrite so a live godot.log doesn't block us.
            using var rs = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            rs.CopyTo(es);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"{entryPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The save files under the game's user folder and the Save
    /// Slots folder as text, one line per file: modification time (UTC),
    /// size and path. Root-level logs are left out (they go in logs/).
    /// <paramref name="count"/> gets the number of files listed.</summary>
    public static string ListSaves(string userDir, out int count)
    {
        var sb = new System.Text.StringBuilder();
        count = 0;
        sb.AppendLine("modified (UTC)        size  path");
        if (string.IsNullOrEmpty(userDir) || !Directory.Exists(userDir))
        {
            sb.AppendLine("(game user folder not found: " + userDir + ")");
            return sb.ToString();
        }
        var files = new List<string>();
        try
        {
            files.AddRange(Directory.GetFiles(userDir)
                .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { sb.AppendLine("(root: " + ex.Message + ")"); }
        var slots = System.IO.Path.Combine(userDir, SaveSlots.FolderName);
        if (Directory.Exists(slots))
        {
            try { files.AddRange(Directory.GetFiles(slots, "*", SearchOption.AllDirectories)); }
            catch (Exception ex) { sb.AppendLine("(" + SaveSlots.FolderName + ": " + ex.Message + ")"); }
        }
        else
        {
            sb.AppendLine("(no " + SaveSlots.FolderName + " folder)");
        }
        foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fi = new FileInfo(f);
                var rel = System.IO.Path.GetRelativePath(userDir, f).Replace('\\', '/');
                sb.Append(fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("  ").Append(fi.Length.ToString().PadLeft(10))
                  .Append("  ").AppendLine(rel);
                count++;
            }
            catch { /* unreadable: skip the line */ }
        }
        return sb.ToString();
    }

    /// <summary>The Save Slots folder's small state files, worth copying
    /// into the package as they are: state.cfg, pending.cfg, fallback.cfg,
    /// file_rules.cfg, every slot's _meta.cfg and every backup's
    /// _backup.cfg. Never a save file.</summary>
    public static IEnumerable<string> SaveStateFiles(string userDir)
    {
        if (string.IsNullOrEmpty(userDir)) yield break;
        var slots = System.IO.Path.Combine(userDir, SaveSlots.FolderName);
        if (!Directory.Exists(slots)) yield break;
        foreach (var name in new[] { "state.cfg", "pending.cfg", "fallback.cfg", "file_rules.cfg" })
        {
            var p = System.IO.Path.Combine(slots, name);
            if (File.Exists(p)) yield return p;
        }
        string[] metas;
        try { metas = Directory.GetFiles(slots, "_meta.cfg", SearchOption.AllDirectories); }
        catch { metas = Array.Empty<string>(); }
        foreach (var m in metas.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)) yield return m;
        string[] backups;
        try { backups = Directory.GetFiles(slots, "_backup.cfg", SearchOption.AllDirectories); }
        catch { backups = Array.Empty<string>(); }
        foreach (var b in backups.OrderBy(b => b, StringComparer.OrdinalIgnoreCase)) yield return b;
    }

    private static object BuildInfo(
        ModProfile profile, List<ModEntry> entries, string modsDir, int logFileCount, bool modFilesIncluded,
        int saveFilesListed)
    {
        var asmVer = Assembly.GetExecutingAssembly().GetName().Version
                   ?? new Version(0, 0, 0);
        var edition =
#if AI_RESOLVER
            "AI";
#else
            "Integrated";
#endif
        string mml;
        try { mml = MmlInstall.DetectInstalledVersion(modsDir); }
        catch { mml = ""; }

        return new
        {
            managerVersion    = asmVer.ToString(),
            edition,
            generatedAtUtc    = DateTime.UtcNow.ToString("o"),
            activeProfileName = profile.Name,
            modCount          = profile.Mods.Count,
            enabledCount      = profile.Mods.Count(m => m.IsEnabled),
            liveModCount      = entries.Count,
            modFilesIncluded,
            mmlVersion        = string.IsNullOrEmpty(mml) ? "(not detected)" : mml,
            gameUserDir       = GameUserDir,
            logFileCount,
            saveFilesListed,
        };
    }

    /// <summary>Suggested output filename (no directory):
    /// "<safe-profile>__support__<UTC-stamp>.vmzlog".</summary>
    public static string SuggestedFileName(string profileName, DateTime utcNow)
    {
        var safe  = ModProfile.SafeFileName(
            string.IsNullOrEmpty(profileName) ? "Support Snapshot" : profileName);
        var stamp = utcNow.ToString("yyyyMMdd-HHmmss");
        return $"{safe}__support__{stamp}.vmzlog";
    }
}
