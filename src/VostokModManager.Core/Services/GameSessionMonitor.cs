// Best-effort watcher for one play session, started right after a launch.
//
// Steam launches are fire-and-forget, so there is no Process handle: the
// monitor polls for a process that looks like the game for up to a minute
// after the launch, then waits for it to exit. The Mod Loader applies the
// mods in a first pass and restarts the game into a second one, so when
// the process exits the monitor looks for a new one for a while and
// follows it. When the last one exits it classifies the session:
//
//   • clean  — exit code 0 and at least 60 s of play.
//   • short  — exit code 0 under a minute: more likely "I just tested the
//              launch" than a session worth anchoring as last-known-good,
//              but not a crash either.
//   • crash  — a non-zero exit code.
//
// When no candidate process turns up the watch ends quietly: Steam runs
// the game under various names and a missed crash beats a false alarm.
//
// Process access sits behind IGameProcessSource, and time behind a clock
// and a delay function, so tests drive a whole session without a game.

using System.Diagnostics;

namespace VostokModManager.Services;

/// <summary>A running game process the monitor waits on.</summary>
public interface IGameProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>The exit code once exited; -1 when it can't be read.</summary>
    int ExitCode { get; }
}

/// <summary>Finds the game's process.</summary>
public interface IGameProcessSource
{
    /// <summary>A running process that looks like Road to Vostok, or null.</summary>
    IGameProcess? FindGame();
}

public enum GameExitKind
{
    /// <summary>No game process appeared within the find timeout (or the
    /// watch was cancelled before one did).</summary>
    NotFound,
    /// <summary>The process went away before its state could be read.</summary>
    Lost,
    CleanExit,
    /// <summary>Exit code 0 under CleanSessionMinimum: nothing to restore,
    /// nothing to promote.</summary>
    ShortExit,
    LikelyCrash,
}

/// <summary>How a watched session ended.</summary>
public sealed record GameExit(GameExitKind Kind, int ExitCode = -1, double SessionSeconds = 0);

/// <summary>The real process table. Matches the game's own exe, RTV.exe
/// (the Steam build's name; SaveSlots.GameRunning looks for it too), a
/// process whose name starts with "RoadToVostok", or one that contains
/// "vostok" but not "Manager" (so the manager itself never counts).</summary>
public sealed class SystemGameProcessSource : IGameProcessSource
{
    public IGameProcess? FindGame()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                var n = p.ProcessName ?? "";
                if (string.Equals(n, "RTV", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("RoadToVostok", StringComparison.OrdinalIgnoreCase)
                    || (n.IndexOf("vostok", StringComparison.OrdinalIgnoreCase) >= 0
                        && !n.Contains("Manager", StringComparison.OrdinalIgnoreCase)))
                    return new SystemGameProcess(p);
            }
        }
        catch { /* permission denied on some processes — keep polling */ }
        return null;
    }

    private sealed class SystemGameProcess : IGameProcess
    {
        private readonly Process _p;
        public SystemGameProcess(Process p) => _p = p;

        public Task WaitForExitAsync(CancellationToken ct) => _p.WaitForExitAsync(ct);

        public int ExitCode
        {
            get
            {
                try { return _p.ExitCode; }
                catch { return -1; }  // may throw if the process is gone
            }
        }

        public void Dispose()
        {
            try { _p.Dispose(); } catch { }
        }
    }
}

public sealed class GameSessionMonitor
{
    private readonly IGameProcessSource _source;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>How long after the launch to look for the game's process.</summary>
    public TimeSpan FindTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A zero exit code counts as clean only after this long.</summary>
    public TimeSpan CleanSessionMinimum { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long after the game exits to look for the process it
    /// restarted into (the Mod Loader's second pass).</summary>
    public TimeSpan RestartGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Restarts followed in one session, so a game that keeps
    /// relaunching can't hold the watch forever.</summary>
    public int MaxRestarts { get; init; } = 3;

    /// <summary>Raised when a watched session ends with a likely crash.
    /// Raised on the thread that awaited WatchAsync's continuation (the
    /// UI thread when it was started there).</summary>
    public event Action<GameExit>? CrashDetected;

    /// <summary>Raised when a watched session ends cleanly.</summary>
    public event Action<GameExit>? CleanExit;

    public GameSessionMonitor(
        IGameProcessSource? source = null,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _source = source ?? new SystemGameProcessSource();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    /// <summary>Watches one session from the moment of the launch.
    /// Never throws; cancellation ends the watch as NotFound / Lost
    /// without raising an event.</summary>
    public async Task<GameExit> WatchAsync(CancellationToken ct = default)
    {
        var launchTime = _utcNow();
        var game = await FindGameAsync(FindTimeout, ct);
        if (game == null) return new GameExit(GameExitKind.NotFound);

        GameExit exit;
        try
        {
            int exitCode;
            var restarts = 0;
            while (true)
            {
                await game.WaitForExitAsync(ct);
                exitCode = game.ExitCode;
                if (restarts >= MaxRestarts) break;
                var next = await FindGameAsync(RestartGrace, ct);
                if (next == null) break;
                game.Dispose();
                game = next;
                restarts++;
            }
            var sessionSec = (_utcNow() - launchTime).TotalSeconds;
            var kind = exitCode != 0 ? GameExitKind.LikelyCrash
                : sessionSec >= CleanSessionMinimum.TotalSeconds ? GameExitKind.CleanExit
                : GameExitKind.ShortExit;
            exit = new GameExit(kind, exitCode, sessionSec);
        }
        catch
        {
            // Process gone before we could read state, or the watch was
            // cancelled; nothing to do.
            return new GameExit(GameExitKind.Lost);
        }
        finally
        {
            game.Dispose();
        }

        if (exit.Kind == GameExitKind.CleanExit) CleanExit?.Invoke(exit);
        else if (exit.Kind == GameExitKind.LikelyCrash) CrashDetected?.Invoke(exit);
        return exit;
    }

    /// <summary>Polls for the game's process for up to `within`; null
    /// when none turns up or the watch is cancelled.</summary>
    private async Task<IGameProcess?> FindGameAsync(TimeSpan within, CancellationToken ct)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed < within && !ct.IsCancellationRequested)
        {
            var game = _source.FindGame();
            if (game != null) return game;
            try { await _delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
            elapsed += PollInterval;
        }
        return null;
    }
}
