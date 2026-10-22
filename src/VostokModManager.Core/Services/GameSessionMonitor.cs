// Best-effort watcher for one play session, started right after a launch.
//
// Steam launches are fire-and-forget, so there is no Process handle: the
// monitor polls for a process that looks like the game for up to a minute
// after the launch, then waits for it to exit and classifies the session:
//
//   • clean  — exit code 0 and at least 60 s of play. A quick quit-out
//              under a minute is more likely "I just tested the launch"
//              than a session worth anchoring as last-known-good.
//   • crash  — a non-zero exit code, or a session under 60 s.
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
    LikelyCrash,
}

/// <summary>How a watched session ended.</summary>
public sealed record GameExit(GameExitKind Kind, int ExitCode = -1, double SessionSeconds = 0);

/// <summary>The real process table. Matches a process whose name starts
/// with "RoadToVostok", or contains "vostok" but not "Manager" (so the
/// manager itself never counts); Godot exports vary by build target.</summary>
public sealed class SystemGameProcessSource : IGameProcessSource
{
    public IGameProcess? FindGame()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                var n = p.ProcessName ?? "";
                if (n.StartsWith("RoadToVostok", StringComparison.OrdinalIgnoreCase)
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
        IGameProcess? game = null;
        var elapsed = TimeSpan.Zero;
        while (elapsed < FindTimeout && !ct.IsCancellationRequested)
        {
            game = _source.FindGame();
            if (game != null) break;
            try { await _delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
            elapsed += PollInterval;
        }
        if (game == null) return new GameExit(GameExitKind.NotFound);

        GameExit exit;
        try
        {
            await game.WaitForExitAsync(ct);
            var sessionSec = (_utcNow() - launchTime).TotalSeconds;
            var exitCode = game.ExitCode;
            // Clean: exit code 0 AND long enough to be a real play.
            // Everything else (non-zero code, or under a minute) is a
            // likely crash.
            var cleanExit = exitCode == 0 && sessionSec >= CleanSessionMinimum.TotalSeconds;
            exit = new GameExit(
                cleanExit ? GameExitKind.CleanExit : GameExitKind.LikelyCrash,
                exitCode, sessionSec);
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
        else CrashDetected?.Invoke(exit);
        return exit;
    }
}
