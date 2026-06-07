// Collect Support Package — bundles everything a maintainer needs to
// diagnose a user's problem into one shareable .vmzlog file:
//
//   <profile>__support__<UTC-stamp>.vmzlog   (a plain zip)
//   ├── profile.vmprofile      ← full archive-backed bundle of the active
//   │                            profile (profile.json + mods/*.vmz),
//   │                            directly Import-able in the Profiles Manager
//   ├── profile.json           ← loose metadata copy (read without opening
//   │                            the nested .vmprofile)
//   ├── logs/                  ← the entire %APPDATA%\Road to Vostok\logs\
//   │                            directory (godot.log + rotated logs)
//   ├── extra-logs/            ← (optional) root-level *.log files from the
//   │                            game user dir (modloader_filescope.log, …)
//   ├── mod_config.cfg         ← the in-game loader's enabled/priority state
//   └── support-info.json      ← manager version, edition, MML version,
//                                counts, timestamp
//
// All zip entries use FORWARD-SLASH paths (mirrors ModPacker /
// ModProfile.ExportToZipUsingLibrary) so the file opens cleanly in any
// tool and the inner .vmprofile is accepted by the Godot-side loader.
//
// The inner .vmprofile is built to a TEMP file straight from the live
// entries — we deliberately do NOT call SaveWithBundles, because that
// would overwrite the user's saved profile folder under ProfilesDir.
// Instead we ingest each live archive into the Library (idempotent) and
// then reuse ModProfile.ExportToZipUsingLibrary, exactly like a normal
// profile export, so the bundle is byte-identical to one the user would
// get from Profiles → Export.

using System.IO.Compression;
using System.Reflection;
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
            var appdata = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
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
        public int    ModCount     { get; set; }
        public List<string> Warnings { get; } = new();
    }

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Builds the .vmzlog at `outputPath`.
    ///
    /// <paramref name="activeProfile"/> may be null (pre-migration
    /// "all mods" mode) — the caller is expected to pass a profile
    /// built via ModProfile.FromRegistry in that case, but this method
    /// also tolerates null by snapshotting all live entries.
    ///
    /// <paramref name="progress"/> is invoked as (message, percent 0-100)
    /// on the calling thread; the dialog marshals it to the UI.</summary>
    public static Result Build(
        ModProfile? activeProfile,
        IEnumerable<ModEntry> liveEntries,
        string modsDir,
        string outputPath,
        bool includeRootLogs,
        Action<string, int>? progress = null)
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

        // ── 1. Ingest live archives into the Library so the bundle
        //       export can resolve every (mod_id, version). Idempotent.
        Report("Indexing mods into the library…", 5);
        foreach (var e in entries)
        {
            if (e.IsArchive && File.Exists(e.Path))
            {
                try { ModLibrary.Add(modsDir, e.Path); }
                catch { /* best-effort — export simply marks it no-bundle */ }
            }
        }

        // ── 2. Build the inner .vmprofile to a temp file, reusing the
        //       exact same export path a normal profile export takes.
        var tempVmprofile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vmm_support_" + Guid.NewGuid().ToString("N") + ".vmprofile");
        try
        {
            Report("Bundling active profile…", 25);
            try
            {
                profile.ExportToZipUsingLibrary(tempVmprofile, modsDir);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Profile bundle failed: {ex.Message}");
            }

            // ── 3. Assemble the .vmzlog.
            Report("Writing support package…", 50);
            using (var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                // 3a. inner .vmprofile
                if (File.Exists(tempVmprofile))
                    AddFile(zip, tempVmprofile, "profile.vmprofile", result.Warnings);

                // 3b. loose profile.json metadata at the root
                try
                {
                    var jsonEntry = zip.CreateEntry("profile.json", CompressionLevel.Optimal);
                    using var es = jsonEntry.Open();
                    using var sw = new StreamWriter(es);
                    sw.Write(JsonSerializer.Serialize(profile, _opts));
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"profile.json: {ex.Message}");
                }

                // 3c. whole Godot logs directory
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

                // 3d. (optional) root-level *.log mod logs
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

                // 3e. mod_config.cfg (cheap, high diagnostic value)
                Report("Adding loader state…", 85);
                var cfg = ModConfig.DefaultPath;
                if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg))
                    AddFile(zip, cfg, "mod_config.cfg", result.Warnings);
                else
                    result.Warnings.Add("mod_config.cfg not found.");

                // 3f. support-info.json
                try
                {
                    var info = BuildInfo(profile, entries, modsDir, result.LogFileCount);
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

    /// <summary>Copies one on-disk file into the zip under
    /// <paramref name="entryPath"/> (forward-slash). Reads via a
    /// share-all stream so a log the running game still holds open can
    /// still be copied. Returns true on success; records a warning and
    /// returns false on failure (e.g. exclusive lock).</summary>
    private static bool AddFile(
        ZipArchive zip, string sourcePath, string entryPath, List<string> warnings)
    {
        try
        {
            var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
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

    private static object BuildInfo(
        ModProfile profile, List<ModEntry> entries, string modsDir, int logFileCount)
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
            mmlVersion        = string.IsNullOrEmpty(mml) ? "(not detected)" : mml,
            gameUserDir       = GameUserDir,
            logFileCount,
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
