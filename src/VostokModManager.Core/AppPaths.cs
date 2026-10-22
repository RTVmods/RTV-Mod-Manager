// Where the manager and the game keep their per-user files. Every
// %APPDATA% lookup in Core goes through here so a dev run or a test can
// point the whole app at a sandbox copy instead of the real files:
//
//   set VMM_APPDATA_ROOT=C:\sandbox\AppData\Roaming
//
// (Setting APPDATA itself does not work: .NET's GetFolderPath ignores it.)

namespace VostokModManager;

public static class AppPaths
{
    public const string AppDataOverrideVariable = "VMM_APPDATA_ROOT";

    /// <summary>Set by tests; wins over the environment variable.</summary>
    internal static string? OverrideRoot { get; set; }

    /// <summary>The roaming AppData folder (or the sandbox standing in for it).</summary>
    public static string RoamingAppData
    {
        get
        {
            if (!string.IsNullOrEmpty(OverrideRoot)) return OverrideRoot;
            var env = Environment.GetEnvironmentVariable(AppDataOverrideVariable);
            if (!string.IsNullOrEmpty(env)) return env;
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
    }

    /// <summary>True when the app runs against a sandbox, not the real AppData.</summary>
    public static bool IsSandboxed =>
        !string.IsNullOrEmpty(OverrideRoot)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppDataOverrideVariable));

    /// <summary>%APPDATA%\VostokModManager — settings, profiles, checkpoints.</summary>
    public static string ManagerDir => Path.Combine(RoamingAppData, "VostokModManager");

    /// <summary>%APPDATA%\Road to Vostok — the game's user:// folder.</summary>
    public static string GameUserDir => Path.Combine(RoamingAppData, "Road to Vostok");

    /// <summary>Where manual and first-run backup zips go. Inside the
    /// sandbox when sandboxed, else Documents.</summary>
    public static string BackupsDir => IsSandboxed
        ? Path.Combine(RoamingAppData, "Road to Vostok Mod Manager Backups")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Road to Vostok Mod Manager Backups");
}
