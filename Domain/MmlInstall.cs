// Local detection of the installed MML (Vostok Mod Loader)
// version. The Windows installer drops a single `modloader.gd`
// file at the game install root (alongside the .exe) — that
// script is the loader. Inside it lives:
//
//   const MODLOADER_VERSION := "3.1.1"
//
// release-please bumps the constant on every published version,
// so a regex against this single line is a stable identifier
// of what the user actually has running.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class MmlInstall
{
    private static readonly Regex _versionRe = new(
        @"MODLOADER_VERSION\s*:?=\s*""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>Reads `<gameDir>/modloader.gd` (where gameDir is
    /// the parent of `modsDir`) and returns the MODLOADER_VERSION
    /// string. Returns empty when the file isn't there or doesn't
    /// contain a recognisable version constant — caller decides
    /// how to phrase that to the user.</summary>
    public static string DetectInstalledVersion(string modsDir)
    {
        var path = ResolveModloaderPath(modsDir);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
        try
        {
            // The file is ~500KB but the version constant lives in
            // the first ~50 lines of the header section. Read the
            // whole thing rather than streaming — small enough that
            // the simplicity wins, and we run this at most once
            // per scan.
            var text = File.ReadAllText(path);
            var m = _versionRe.Match(text);
            return m.Success ? m.Groups[1].Value : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The file the loader looks for beside the game to skip
    /// its launcher for one launch (MML 3.4.2+). A manager that
    /// starts the game through Steam cannot pass a command-line
    /// argument, so this is the Steam-safe form of
    /// `--modloader-skip-ui`. The loader deletes it on that launch.</summary>
    public const string SkipUiOnceFile = "modloader_skip_ui_once";

    /// <summary>True when the installed modloader.gd knows the
    /// skip-launcher marker. Read from the script itself rather than
    /// its version, so a build of the loader that carries the feature
    /// under another version string still counts.</summary>
    public static bool SupportsSkipUi(string modsDir)
    {
        var path = ResolveModloaderPath(modsDir);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try { return File.ReadAllText(path).Contains(SkipUiOnceFile, StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>Writes the skip-launcher marker beside the game.
    /// Returns false when the loader doesn't support it or the file
    /// couldn't be written; the launch goes ahead either way, the
    /// loader just shows its launcher as usual.</summary>
    public static bool WriteSkipUiOnce(string modsDir)
    {
        if (!SupportsSkipUi(modsDir)) return false;
        var gameDir = Path.GetDirectoryName(modsDir);
        if (string.IsNullOrEmpty(gameDir)) return false;
        try
        {
            File.WriteAllText(Path.Combine(gameDir, SkipUiOnceFile), "");
            return true;
        }
        catch { return false; }
    }

    /// <summary>The expected on-disk path for the modloader script,
    /// derived from `modsDir`. Returns empty when modsDir is empty
    /// or has no parent.</summary>
    public static string ResolveModloaderPath(string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir)) return "";
        var gameDir = Path.GetDirectoryName(modsDir);
        if (string.IsNullOrEmpty(gameDir)) return "";
        return Path.Combine(gameDir, "modloader.gd");
    }
}
