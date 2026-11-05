// Reads a godot.log for what a crash leaves behind.
//
// Godot's crash handler writes a block like this before the process dies:
//
//   ================================================================
//   CrashHandlerException: Program crashed with signal 11
//   Engine version: Godot Engine v4.6.stable.official
//   Dumping the backtrace. Please include this when reporting the bug ...
//   [1] ...
//   -- END OF BACKTRACE --
//   ================================================================
//
// That block is the surest sign the session crashed, even when the
// exit code couldn't be read (a launch through Steam the manager lost
// track of, or a game started from Steam itself). The lines just before
// it are what the game was doing: mods tag their lines "[Mod Name] ...",
// so the tags there name the mods that were running at the time.

using System.Text;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

/// <summary>What one log says about how its session ended.</summary>
/// <param name="CrashLine">The crash handler's first line, or "".</param>
/// <param name="Backtrace">Up to 16 backtrace lines after it.</param>
/// <param name="Tail">The last lines before the crash block (or before
/// the end of the log when there is none), oldest first.</param>
/// <param name="TailTags">The "[Tag]" prefixes in Tail, most recent first,
/// without the mod loader's own.</param>
public sealed record CrashLogInfo(
    string LogPath,
    DateTime WrittenUtc,
    bool HasCrashSignature,
    string CrashLine,
    IReadOnlyList<string> Backtrace,
    IReadOnlyList<string> Tail,
    IReadOnlyList<string> TailTags)
{
    public static readonly CrashLogInfo None = new("", DateTime.MinValue, false, "",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool Found => LogPath.Length > 0;
}

public static class CrashLog
{
    /// <summary>Lines that only Godot's crash handler (or a fatal engine
    /// error) writes.</summary>
    private static readonly string[] Signatures =
    {
        "CrashHandlerException",
        "Program crashed",
        "Dumping the backtrace",
        "-- END OF BACKTRACE --",
    };

    private static readonly Regex _reTag = new(@"^\s*\[([^\[\]]{2,48})\]", RegexOptions.Compiled);

    /// <summary>Tags that are the loader or the engine, not a mod.</summary>
    private static readonly HashSet<string> NotModTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModLoader", "Overrides", "Hooks", "MML", "Info", "Warning", "Error", "Debug",
    };

    /// <summary>True when a line belongs to the crash handler's block.</summary>
    public static bool IsCrashLine(string line)
    {
        foreach (var s in Signatures)
            if (line.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Reads `path`. Never throws: an unreadable log is None.</summary>
    public static CrashLogInfo Read(string? path, int tailLines = 14)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return CrashLogInfo.None;
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch { written = DateTime.MinValue; }

        // A ring of recent lines, so the tail before the crash block is
        // at hand without holding the whole log.
        var recent = new Queue<string>();
        var crashLine = "";
        List<string>? beforeCrash = null;
        var backtrace = new List<string>();
        var inBacktrace = false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            var n = 0;
            while ((line = sr.ReadLine()) != null && n++ < 400_000)
            {
                if (crashLine.Length == 0 && IsCrashLine(line))
                {
                    crashLine = line.Trim();
                    // What the game was doing: the lines before the block,
                    // minus its "=====" rule.
                    beforeCrash = recent.Where(l => !l.StartsWith("=====", StringComparison.Ordinal)).ToList();
                    inBacktrace = true;
                    continue;
                }
                if (inBacktrace)
                {
                    var t = line.Trim();
                    if (t.Contains("END OF BACKTRACE", StringComparison.Ordinal) || backtrace.Count >= 16)
                        inBacktrace = false;
                    else if (t.Length > 0 && !t.StartsWith("=====", StringComparison.Ordinal))
                        backtrace.Add(t);
                    continue;
                }
                if (line.Trim().Length == 0) continue;
                recent.Enqueue(line.TrimEnd());
                if (recent.Count > 200) recent.Dequeue();
            }
        }
        catch { return CrashLogInfo.None with { LogPath = path, WrittenUtc = written }; }

        var source = beforeCrash ?? recent.ToList();
        var tail = source.Skip(Math.Max(0, source.Count - tailLines)).ToList();
        var tags = new List<string>();
        for (var i = source.Count - 1; i >= Math.Max(0, source.Count - 40); i--)
        {
            var m = _reTag.Match(source[i]);
            if (!m.Success) continue;
            var tag = m.Groups[1].Value.Trim();
            if (NotModTags.Contains(tag) || tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            tags.Add(tag);
        }
        return new CrashLogInfo(path, written, crashLine.Length > 0, crashLine, backtrace, tail, tags);
    }

    /// <summary>The newest log in the game's log folder, read; None when
    /// there is none or it was last written before `sinceUtc`.</summary>
    public static CrashLogInfo ReadNewest(DateTime? sinceUtc = null)
    {
        var path = GodotLogAnalyzer.LatestLog();
        if (string.IsNullOrEmpty(path)) return CrashLogInfo.None;
        if (sinceUtc is { } since)
        {
            try { if (File.GetLastWriteTimeUtc(path) < since) return CrashLogInfo.None; }
            catch { return CrashLogInfo.None; }
        }
        return Read(path);
    }

    /// <summary>What a Windows exit code usually means, or "" when it says
    /// nothing useful. Exit codes arrive as signed ints.</summary>
    public static string DescribeExitCode(int code) => unchecked((uint)code) switch
    {
        0xC0000005 => "Access violation: the game touched memory it shouldn't. Usually a mod "
                      + "script, an outdated mod after a game update, or the graphics driver.",
        0xC00000FD => "Stack overflow: something called itself endlessly. Very often two mods "
                      + "hooking or replacing the same function and calling each other.",
        0xC0000409 => "The game stopped itself after a fatal error (fail-fast).",
        0xC0000374 => "Heap corruption: memory was damaged, usually by a mod or a driver.",
        0xC000001D => "Illegal instruction: the CPU was asked to run something it can't.",
        0xC0000017 or 0xC000012D => "The system ran out of memory.",
        0xE06D7363 => "An unhandled exception inside the engine.",
        0x80000003 => "The engine hit a fatal assertion.",
        0xCFFFFFFF => "Windows closed the game because it stopped responding.",
        1          => "Ended with an error code (or closed from Task Manager).",
        _          => "",
    };
}
