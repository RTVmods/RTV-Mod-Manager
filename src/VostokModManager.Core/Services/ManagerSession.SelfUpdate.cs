// Keeping the mod loader (MML) and the manager itself up to date: the
// cached GitHub release checks behind their status lines, MML's
// download-and-replace, and the manager's download → swap script →
// relaunch. The front end shuts the app down when told to; this file
// never exits the process itself.

using System.Diagnostics;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    public const string MmlReleasesFallbackUrl = "https://github.com/ametrocavich/vostok-mod-loader/releases";

    /// <summary>Set by the last manager check: GitHub has no release at
    /// all (as opposed to the check failing).</summary>
    private bool _managerNoRelease;

    // ── network wrappers ──────────────────────────────────────────────

    private Task<MmlRelease?> FetchMmlReleaseAsync()
        => UpdateNet.FetchMmlRelease != null ? UpdateNet.FetchMmlRelease() : Mml.FetchLatestAsync();

    private Task DownloadMmlAssetAsync(string tag, string asset, string savePath)
        => UpdateNet.DownloadMmlAsset != null
            ? UpdateNet.DownloadMmlAsset(tag, asset, savePath)
            : Mml.DownloadReleaseAssetAsync(tag, asset, savePath);

    private Task<(ManagerReleaseStatus Status, ManagerRelease? Release)> FetchManagerReleaseAsync()
        => UpdateNet.FetchManagerRelease != null ? UpdateNet.FetchManagerRelease() : ManagerReleases.FetchLatestAsync();

    private Task DownloadManagerAssetAsync(ManagerReleaseAsset asset, string savePath)
        => UpdateNet.DownloadManagerAsset != null
            ? UpdateNet.DownloadManagerAsset(asset, savePath)
            : ManagerReleases.DownloadAssetAsync(asset, savePath);

    // ── mod loader ────────────────────────────────────────────────────

    /// <summary>The MML release page to open (the latest one when known).</summary>
    public string MmlReleasesUrl
        => !string.IsNullOrEmpty(Settings.MmlLatestUrl) ? Settings.MmlLatestUrl : MmlReleasesFallbackUrl;

    /// <summary>Compares the installed MODLOADER_VERSION (from
    /// &lt;game&gt;/modloader.gd) with the latest GitHub release, cached
    /// for 24h. A cached latest older than what is installed is out of
    /// date by definition and re-fetched. A failed fetch falls back to
    /// the cached value. Writes the Loader status line.</summary>
    public async Task<MmlStatus> CheckMmlVersionAsync(bool forceFresh = false)
    {
        var installed = MmlInstall.DetectInstalledVersion(ModsDir);

        string latest = "";
        bool fromCache = false;
        var cacheUsable = Settings.IsMmlCacheFresh
            && (string.IsNullOrEmpty(installed)
                || ModRegistry.CompareVersions(installed, Settings.MmlLatestTag) <= 0);
        if (!forceFresh && cacheUsable)
        {
            latest = Settings.MmlLatestTag;
            fromCache = true;
        }
        else
        {
            Say(StatusArea.Loader, "MML: checking for latest release …");
            var release = await FetchMmlReleaseAsync();
            if (release != null)
            {
                latest = release.TagName;
                Settings.MmlLatestTag = release.TagName;
                Settings.MmlLatestUrl = release.HtmlUrl;
                Settings.MmlCheckedAt = DateTime.UtcNow.ToString("o");
                try { Settings.Save(); } catch { /* best-effort */ }
            }
            else if (!string.IsNullOrEmpty(Settings.MmlLatestTag))
            {
                latest = Settings.MmlLatestTag;
                fromCache = true;
            }
        }
        var status = DescribeMml(installed, latest, fromCache);
        Say(StatusArea.Loader, status.Text);
        return status;
    }

    /// <summary>The Loader line for an installed / latest pair.</summary>
    public static MmlStatus DescribeMml(string installed, string latest, bool fromCache)
    {
        var freshness = fromCache ? " (cached)" : "";
        // Release tags carry a leading "v"; the text adds its own.
        var shown = latest.TrimStart('v', 'V');
        var hasInstalled = !string.IsNullOrEmpty(installed);
        var hasLatest = !string.IsNullOrEmpty(shown);

        if (!hasInstalled && !hasLatest)
            return new(installed, latest, fromCache, VersionState.Unknown,
                "MML: not detected and latest-version check failed. Click to open the releases page.");
        if (!hasInstalled)
            return new(installed, latest, fromCache, VersionState.Unknown,
                $"MML: not detected at modloader.gd; latest release v{shown}{freshness} — click to open releases page.");
        if (!hasLatest)
            return new(installed, latest, fromCache, VersionState.Unknown,
                $"MML: installed v{installed}; latest unknown — click to open releases page.");
        var cmp = ModRegistry.CompareVersions(installed, shown);
        var (state, text) = cmp == 0 ? (VersionState.UpToDate, "✓ up to date")
            : cmp > 0 ? (VersionState.Ahead, "↑ ahead of release")
            : (VersionState.Outdated, "⬆ outdated");
        return new(installed, latest, fromCache, state,
            $"MML: installed v{installed}, latest v{shown} {text}{freshness} — click to open releases page.");
    }

    /// <summary>A click on the Loader line. When MML is outdated, offers
    /// to download and install the latest; otherwise (or on "open the
    /// page") returns the releases page for the front end to open.</summary>
    public async Task<SelfUpdateAction> HandleMmlClickAsync()
    {
        var installed = MmlInstall.DetectInstalledVersion(ModsDir);
        var latest = Settings.MmlLatestTag;
        if (string.IsNullOrEmpty(installed)
            || string.IsNullOrEmpty(latest)
            || ModRegistry.CompareVersions(installed, latest) >= 0)
            return SelfUpdateAction.Open(MmlReleasesUrl);

        var choice = Prompt.Choose("Update MML?",
            $"MML v{installed} is installed; latest is {latest}.\n\n"
            + $"Download {latest}'s `modloader.gd` and `override.cfg` and replace the files in your "
            + "game folder?\n\n"
            + "Existing files will be backed up first as <name>.<timestamp>.bak alongside the "
            + "originals — if anything looks wrong after launch, restore the .bak files manually.",
            new[] { "Download + install", "Just open the releases page" });
        if (choice == 0) { await UpdateMmlAsync(latest); return SelfUpdateAction.None; }
        if (choice == 1) return SelfUpdateAction.Open(MmlReleasesUrl);
        return SelfUpdateAction.None;
    }

    /// <summary>Downloads `modloader.gd` and `override.cfg` from the
    /// tag's release, backs up existing copies in the game folder and
    /// replaces them. Both are downloaded to .download paths first, so a
    /// failure on the second file leaves the first one's original
    /// untouched. True when installed.</summary>
    public async Task<bool> UpdateMmlAsync(string tag)
    {
        var gameDir = Path.GetDirectoryName(ModsDir);
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            Prompt.Error("Game folder not found", new Exception(
                $"Couldn't resolve the game folder from ModsDir=\n  {ModsDir}\n"
                + "Set the Mods folder to <game>/mods/ via Settings."));
            return false;
        }
        var modloaderPath = Path.Combine(gameDir, "modloader.gd");
        var overridePath = Path.Combine(gameDir, "override.cfg");
        var modloaderTmp = modloaderPath + ".download";
        var overrideTmp = overridePath + ".download";

        IsUpdating = true;
        Say(StatusArea.Loader, $"MML: downloading v{tag} …");
        try
        {
            // Leftovers from a prior failed attempt.
            DeleteIfExists(modloaderTmp);
            DeleteIfExists(overrideTmp);

            await DownloadMmlAssetAsync(tag, "modloader.gd", modloaderTmp);
            await DownloadMmlAssetAsync(tag, "override.cfg", overrideTmp);

            // modloader.gd must be a GDScript file with the version
            // constant; anything else is a 404 page, a partial download
            // or a moved asset.
            if (!File.Exists(modloaderTmp) || new FileInfo(modloaderTmp).Length < 1000)
                throw new InvalidDataException("Downloaded modloader.gd looks empty / truncated.");
            var probe = File.ReadAllText(modloaderTmp).Substring(
                0, Math.Min(2000, (int)new FileInfo(modloaderTmp).Length));
            if (!probe.Contains("MODLOADER_VERSION"))
                throw new InvalidDataException(
                    "Downloaded modloader.gd doesn't look like a real MML build (no MODLOADER_VERSION "
                    + "constant). Aborting.");

            // Back up existing files (same .timestamp.bak naming as the
            // .vmz patch flow).
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backups = new List<string>();
            if (File.Exists(modloaderPath))
            {
                var bak = $"{modloaderPath}.{stamp}.bak";
                File.Copy(modloaderPath, bak, overwrite: false);
                backups.Add(Path.GetFileName(bak));
            }
            if (File.Exists(overridePath))
            {
                var bak = $"{overridePath}.{stamp}.bak";
                File.Copy(overridePath, bak, overwrite: false);
                backups.Add(Path.GetFileName(bak));
            }

            // Into place; the originals are backed up.
            File.Move(modloaderTmp, modloaderPath, overwrite: true);
            File.Move(overrideTmp, overridePath, overwrite: true);

            await CheckMmlVersionAsync(forceFresh: false);

            var bakNote = backups.Count > 0
                ? $"\n\nBackups: {string.Join(", ", backups)}"
                : "\n\n(No prior install — nothing to back up.)";
            Prompt.Inform("MML updated",
                $"MML updated to v{tag}.\n\n"
                + "Restart Road to Vostok if it's running for the new loader to take effect."
                + bakNote);
            return true;
        }
        catch (Exception ex)
        {
            DeleteIfExists(modloaderTmp);
            DeleteIfExists(overrideTmp);
            Prompt.Error("MML update failed", ex);
            return false;
        }
        finally
        {
            IsUpdating = false;
            // Re-render in case the check above wasn't reached.
            await CheckMmlVersionAsync(forceFresh: false);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    // ── the manager itself ────────────────────────────────────────────

    /// <summary>The running manager's version (Major.Minor.Build, from
    /// the shared &lt;Version&gt;), or "0.0.0" when unreadable.</summary>
    public static string ManagerInstalledVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Cache-first (24h) check of the manager's latest GitHub
    /// release; a fresh result is persisted so a relaunch within 24h
    /// skips the request. Writes the Manager status line.</summary>
    public async Task<ManagerStatus> CheckManagerUpdateAsync(bool forceFresh = false)
    {
        var installed = ManagerInstalledVersion();
        string latest = "";
        bool fromCache = false;

        if (!forceFresh && Settings.IsManagerCacheFresh)
        {
            latest = Settings.ManagerReleaseVersion;
            fromCache = true;
        }
        else
        {
            Say(StatusArea.Manager, "Manager: checking for update …");
            var (status, release) = await FetchManagerReleaseAsync();
            _managerNoRelease = status == ManagerReleaseStatus.NoRelease;
            if (status == ManagerReleaseStatus.Found && release != null)
            {
                latest = release.Version;
                Settings.ManagerReleaseVersion = latest;
                Settings.ManagerReleaseCheckedAt = DateTime.UtcNow.ToString("o");
                try { Settings.Save(); } catch { /* best-effort */ }
            }
            else if (_managerNoRelease)
            {
                // Nothing is published, so a remembered version is not
                // something the user could download.
                if (!string.IsNullOrEmpty(Settings.ManagerReleaseVersion))
                {
                    Settings.ManagerReleaseVersion = "";
                    try { Settings.Save(); } catch { /* best-effort */ }
                }
            }
            else if (!string.IsNullOrEmpty(Settings.ManagerReleaseVersion))
            {
                // The check failed: keep the last good value rather than
                // blanking the line.
                latest = Settings.ManagerReleaseVersion;
                fromCache = true;
            }
        }
        var result = DescribeManager(installed, latest, fromCache, _managerNoRelease);
        Say(StatusArea.Manager, result.Text);
        return result;
    }

    /// <summary>The Manager line for an installed / latest pair. The
    /// outdated state is the one the UI should make stand out.</summary>
    public static ManagerStatus DescribeManager(string installed, string latest, bool fromCache, bool noRelease)
    {
        var freshness = fromCache ? " (cached)" : "";
        if (string.IsNullOrEmpty(latest))
            return new(installed, latest, fromCache, noRelease, VersionState.Unknown, noRelease
                ? $"Manager: v{installed} — no release published yet; click to open the releases page."
                : $"Manager: installed v{installed}; update check failed — click to open the releases page.");
        var cmp = ModRegistry.CompareVersions(installed, latest);
        if (cmp == 0)
            return new(installed, latest, fromCache, noRelease, VersionState.UpToDate,
                $"Manager: v{installed} ✓ up to date{freshness}");
        if (cmp > 0)
            // A local build newer than what's published.
            return new(installed, latest, fromCache, noRelease, VersionState.Ahead,
                $"Manager: v{installed}  ↑ ahead of published v{latest}{freshness}");
        return new(installed, latest, fromCache, noRelease, VersionState.Outdated,
            $"⬆ UPDATE AVAILABLE — Mod Manager v{installed} → v{latest}{freshness}  ·  click to auto-update");
    }

    /// <summary>A click on the Manager line. When an update is available,
    /// offers to install it (download, swap the .exe, relaunch) or open
    /// the releases page; otherwise returns the releases page. When the
    /// result says MustExit, the front end must exit right away.</summary>
    public async Task<SelfUpdateAction> HandleManagerClickAsync()
    {
        var installed = ManagerInstalledVersion();
        var latest = Settings.ManagerReleaseVersion;
        var outdated = !string.IsNullOrEmpty(latest) && ModRegistry.CompareVersions(installed, latest) < 0;
        if (!outdated) return SelfUpdateAction.Open(ManagerReleaseChecker.ReleasesUrl);

        var choice = Prompt.Choose("Update mod manager",
            "A newer version of the mod manager is available "
            + $"(installed v{installed}, latest v{latest}).\n\n"
            + "Install it now to download the new build, swap the .exe in place and relaunch "
            + "automatically, or just open the releases page in your browser.",
            new[] { "Install and relaunch", "Open the releases page" });
        if (choice == 1) return SelfUpdateAction.Open(ManagerReleaseChecker.ReleasesUrl);
        if (choice != 0) return SelfUpdateAction.None;
        var result = await AutoUpdateManagerAsync();
        return result.MustExit ? new SelfUpdateAction(null, true) : SelfUpdateAction.None;
    }

    /// <summary>The running .exe: the entry assembly's location, or the
    /// process's main module for single-file bundles (where Location is
    /// empty).</summary>
    private string CurrentManagerExe()
    {
        if (UpdateNet.CurrentExe != null) return UpdateNet.CurrentExe();
        var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            try { exe = Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { }
        }
        return exe;
    }

    /// <summary>Download + replace + relaunch:
    ///   1. download this edition's build from the latest release into a
    ///      temp folder;
    ///   2. if it's a zip, extract it and find the .exe named like ours
    ///      (else the first .exe);
    ///   3. write swap.bat, which waits for this process to exit, copies
    ///      the new .exe over the current one (retrying until the old one
    ///      is released), starts it and deletes itself;
    ///   4. start the script. The result then says MustExit: the front
    ///      end exits so the file handles on the .exe drop.</summary>
    public async Task<ManagerUpdateResult> AutoUpdateManagerAsync()
    {
        var currentExe = CurrentManagerExe();
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            const string msg = "Couldn't locate the running .exe path — auto-update can't proceed. Open "
                + "the releases page and grab the new build manually.";
            Prompt.Inform("Auto-update aborted", msg, PromptKind.Warning);
            return ManagerUpdateResult.Aborted(msg);
        }

        // Looked up again rather than taken from the cache: the cache
        // holds only the version, not the files.
        Say(StatusArea.Manager, "Manager: looking up the latest release …");
        var (status, release) = await FetchManagerReleaseAsync();
        var exeName = Path.GetFileName(currentExe);
        var asset = release?.AssetFor(exeName);
        if (status != ManagerReleaseStatus.Found || release == null || asset == null)
        {
            var msg = status == ManagerReleaseStatus.Found
                ? $"The latest release (v{release?.Version}) has no build named `{exeName}`. Open the "
                  + "releases page and grab the new build manually."
                : "Couldn't read the latest release from GitHub. Try again in a few minutes, or open the "
                  + "releases page and grab the new build manually.";
            Prompt.Inform("Auto-update aborted", msg, PromptKind.Warning);
            await CheckManagerUpdateAsync();
            return ManagerUpdateResult.Aborted(msg);
        }

        var tempDir = Path.Combine(UpdateNet.TempRoot ?? Path.GetTempPath(),
            "VostokModManagerUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, "download.bin");

        Say(StatusArea.Manager, "Manager: downloading update …");
        try
        {
            await DownloadManagerAssetAsync(asset, downloadPath);
        }
        catch (Exception ex)
        {
            Prompt.Inform("Download failed", $"Couldn't download the update:\n\n{ex.Message}", PromptKind.Warning);
            try { Directory.Delete(tempDir, true); } catch { }
            return ManagerUpdateResult.Aborted(ex.Message);
        }

        // Locate the new .exe in whatever the upload looks like.
        var newExe = FindUpdateExe(downloadPath, tempDir, currentExe);
        if (string.IsNullOrEmpty(newExe))
        {
            var msg = "Downloaded the update, but couldn't find a .exe inside the package. Open the "
                + "releases page and install manually.\n\nThe download is at:\n  " + downloadPath;
            Prompt.Inform("Update format not recognised", msg, PromptKind.Warning);
            return ManagerUpdateResult.Aborted(msg);
        }

        var batPath = Path.Combine(tempDir, "swap.bat");
        try { File.WriteAllText(batPath, BuildSwapScript(newExe, currentExe)); }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't write update script", ex);
            return ManagerUpdateResult.Aborted(ex.Message);
        }

        try
        {
            if (UpdateNet.StartSwapScript != null) UpdateNet.StartSwapScript(batPath);
            else StartSwapScript(batPath);
        }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't launch update script", ex);
            return ManagerUpdateResult.Aborted(ex.Message);
        }
        // Hand over to the script: the app must exit now so the copy
        // can replace the .exe.
        return new ManagerUpdateResult(true, batPath, $"Updating to v{release.Version}; the manager will restart.");
    }

    /// <summary>The swap-and-relaunch script: wait a second, copy the
    /// new .exe over ours (retrying while it is still locked), start it,
    /// then delete the script itself.</summary>
    internal static string BuildSwapScript(string newExe, string currentExe)
        => "@echo off\r\n"
            + ":wait\r\n"
            + "timeout /t 1 /nobreak >nul\r\n"
            + $"copy /y \"{newExe}\" \"{currentExe}\" >nul\r\n"
            + "if errorlevel 1 goto wait\r\n"
            + $"start \"\" \"{currentExe}\"\r\n"
            // Self-delete trick: redirect the goto error to nul, then
            // chain a del so cmd runs it after it has parsed this line.
            + "(goto) 2>nul & del \"%~f0\"\r\n";

    private static void StartSwapScript(string batPath)
        => Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{batPath}\"\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        });

    /// <summary>The .exe inside the downloaded payload: the payload
    /// itself when it is one (copied under our name), else the .exe in
    /// the extracted zip named like ours, else the first one. "" when
    /// there is none.</summary>
    internal static string FindUpdateExe(string downloadPath, string workDir, string currentExe)
    {
        // The author uploaded the binary itself. Sniff the MZ header:
        // the extension may be wrong.
        try
        {
            if (LooksLikeExe(downloadPath))
            {
                var renamed = Path.Combine(workDir, Path.GetFileName(currentExe));
                File.Copy(downloadPath, renamed, overwrite: true);
                return renamed;
            }
        }
        catch { /* fall through to zip handling */ }

        var extractDir = Path.Combine(workDir, "extracted");
        try
        {
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
        }
        catch { return ""; }

        var exes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
        if (exes.Length == 0) return "";
        // An exe named like the running one is this edition's build.
        var preferred = Path.GetFileName(currentExe);
        var match = exes.FirstOrDefault(p =>
            string.Equals(Path.GetFileName(p), preferred, StringComparison.OrdinalIgnoreCase));
        return match ?? exes[0];
    }

    /// <summary>"Is this an MZ-headed .exe" probe, so a single .exe
    /// upload isn't zip-extracted.</summary>
    internal static bool LooksLikeExe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch { return false; }
    }
}
