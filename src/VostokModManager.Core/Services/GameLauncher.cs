// The seam between the session and the outside world when it starts the
// game: finding Road to Vostok's Steam app id, firing the Steam launch, and
// asking whether the game is already running. The default goes through
// Steam (SteamLauncher); tests swap in a recorder so nothing is launched.

using VostokModManager.Domain;

namespace VostokModManager.Services;

public interface IGameLauncher
{
    /// <summary>The Steam app id of the game whose install holds
    /// `modsDir`, or 0 when it can't be found.</summary>
    int FindAppId(string modsDir);

    /// <summary>Starts the game through Steam. False when the launch
    /// could not be handed to Steam.</summary>
    bool LaunchAppId(int appId);

    /// <summary>True while Road to Vostok is running.</summary>
    bool IsGameRunning();
}

/// <summary>Launches through Steam's URL protocol
/// (steam://rungameid/&lt;id&gt;), so achievements, playtime and the
/// overlay work. The .exe is never started directly.</summary>
public sealed class SteamGameLauncher : IGameLauncher
{
    public int FindAppId(string modsDir) => SteamLauncher.FindAppId(modsDir);

    public bool LaunchAppId(int appId) => SteamLauncher.LaunchAppId(appId);

    public bool IsGameRunning() => SaveSlots.GameRunning();
}
