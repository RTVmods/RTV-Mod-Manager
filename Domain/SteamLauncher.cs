// Auto-detects Road to Vostok's Steam app ID by walking up from
// the ModsDir to the steamapps/ folder and matching an
// appmanifest_<id>.acf entry whose `installdir` field equals the
// game folder name. Then launches via Steam's URL protocol so
// achievements, playtime, and overlay all work correctly.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class SteamLauncher
{
    private static readonly Regex _installDirRe = new(
        @"""installdir""\s+""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Discovers the Steam app id whose appmanifest's
    /// installdir matches the parent folder of `modsDir`. Returns 0
    /// when no matching manifest exists (e.g. the mods folder isn't
    /// under a Steam library, or the user moved files around outside
    /// of Steam).</summary>
    public static int FindAppId(string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir)) return 0;

        // mods/ is typically <library>/steamapps/common/<game>/mods.
        // Walk up three levels to land on steamapps/.
        var gameDir = Path.GetDirectoryName(modsDir);
        if (string.IsNullOrEmpty(gameDir)) return 0;
        var installDirName = Path.GetFileName(gameDir);
        var commonDir = Path.GetDirectoryName(gameDir);
        if (string.IsNullOrEmpty(commonDir)) return 0;
        var steamappsDir = Path.GetDirectoryName(commonDir);
        if (string.IsNullOrEmpty(steamappsDir) || !Directory.Exists(steamappsDir))
            return 0;

        foreach (var manifest in EnumerateManifestsSafe(steamappsDir))
        {
            try
            {
                var text = File.ReadAllText(manifest);
                var m = _installDirRe.Match(text);
                if (!m.Success) continue;
                if (!string.Equals(
                        m.Groups[1].Value, installDirName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileNameWithoutExtension(manifest);
                const string prefix = "appmanifest_";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(name.Substring(prefix.Length), out var id))
                    return id;
            }
            catch
            {
                // Skip unreadable / malformed manifest; fall through
                // to the next file.
            }
        }
        return 0;
    }

    private static IEnumerable<string> EnumerateManifestsSafe(string steamappsDir)
    {
        try
        {
            return Directory.EnumerateFiles(steamappsDir, "appmanifest_*.acf");
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Launches the given Steam app via Steam's URL protocol
    /// (`steam://rungameid/&lt;id&gt;`). UseShellExecute = true so the
    /// OS resolves the protocol handler. Returns false on any
    /// exception so callers can surface a useful message.</summary>
    public static bool LaunchAppId(int appId)
    {
        if (appId <= 0) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{appId}",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
