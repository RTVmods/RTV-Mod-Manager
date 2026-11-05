// What the manager says after the game crashes, and the ways back.
//
// A crash is found two ways: the watch on a launch from here (a non-zero
// exit code, or the crash handler's block in the session's log when the
// exit code couldn't be read), and at startup, from the newest game log,
// for a game started outside the manager.
//
// The report reads the log and the checkpoints for likely causes:
//   • mods whose lines come right before the crash,
//   • mods named in script errors,
//   • mods turned on, updated or moved since the last session that ended
//     cleanly (the last-known-good checkpoint),
// and lists recovery steps that fit what it found: go back to the last
// clean session, turn the suspects off, update outdated mods, look at
// conflicts / missing dependencies, read the log analysis (and ask
// Claude, in the AI edition), build a support package.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>A mod the crash report points at, with why.</summary>
public sealed record CrashSuspect(string ModId, string Name, string Why, int Score, bool CanTurnOff)
{
    /// <summary>More than one clue, or a strong one: ticked by default.</summary>
    public bool Likely => Score >= 4;
}

/// <summary>A change to the loadout since the last clean session.</summary>
public sealed record CrashChange(string ModId, string Name, string What);

public enum CrashAction
{
    RestoreLastKnownGood,
    TurnOffSuspects,
    UpdateMods,
    ReviewConflicts,
    ReviewMissingDependencies,
    OpenLogAnalysis,
    AnalyzeWithClaude,
    BuildSupportPackage,
    OpenLogFile,
}

/// <summary>One way forward the report suggests.</summary>
public sealed record CrashStep(CrashAction Action, string Title, string Detail, string Button);

/// <summary>Everything the crash window shows.</summary>
/// <param name="FromWatch">Found by the watch on a launch from here (false:
/// found in the newest log at startup).</param>
public sealed record CrashReport(
    string Headline,
    string Summary,
    string ExitMeaning,
    CrashLogInfo Log,
    int ErrorCount,
    IReadOnlyList<CrashSuspect> Suspects,
    IReadOnlyList<CrashChange> ChangesSinceGood,
    string LastGoodAge,
    IReadOnlyList<CrashStep> Steps,
    bool FromWatch)
{
    public bool HasSuspects => Suspects.Count > 0;
    public bool HasChanges => ChangesSinceGood.Count > 0;
    public bool HasTail => Log.Tail.Count > 0;
}

public sealed partial class ManagerSession
{
    /// <summary>Raised when a watched session crashed (or the newest log
    /// shows one), with the report. Without a handler the session falls
    /// back to the checkpoint restore offer.</summary>
    public event Action<CrashReport>? GameCrashed;

    /// <summary>The newest crash report, for the launch panel's "Review".
    /// Cleared by the next launch and by a clean session.</summary>
    public CrashReport? LastCrash { get; private set; }

    /// <summary>Builds the report for a crash: `exit` from the watch (null
    /// when found in a log), `log` the session's log (null reads the newest).</summary>
    public CrashReport BuildCrashReport(GameExit? exit, CrashLogInfo? log = null)
    {
        log ??= CrashLog.ReadNewest();
        var analysis = log.Found ? GodotLogAnalyzer.Analyze(log.LogPath) : new GodotLogAnalyzer.LogAnalysis();

        // ── what happened ────────────────────────────────────────────
        var parts = new List<string>();
        var exitMeaning = "";
        if (exit != null)
        {
            var mins = (int)(exit.SessionSeconds / 60);
            var secs = (int)exit.SessionSeconds % 60;
            var after = mins > 0 ? $"{mins} min {secs} s" : $"{secs} s";
            parts.Add(exit.ExitCode is 0 or -1
                ? $"Road to Vostok closed after {after}, and its log shows a crash."
                : $"Road to Vostok closed after {after} with exit code "
                  + (exit.ExitCode is > 0 and < 256 ? $"{exit.ExitCode}." : $"0x{unchecked((uint)exit.ExitCode):X8}."));
            exitMeaning = CrashLog.DescribeExitCode(exit.ExitCode);
        }
        else
        {
            var when = log.WrittenUtc == DateTime.MinValue ? "" : $" ({log.WrittenUtc.ToLocalTime():ddd d MMM, HH:mm})";
            parts.Add($"The last game session{when} ended in a crash, according to its log.");
        }
        if (log.HasCrashSignature)
            parts.Add("The engine's crash handler wrote a backtrace to the log.");
        var errors = analysis.Issues.Count(i => i.Severity is "SCRIPT_ERROR" or "ERROR");
        if (errors > 0)
            parts.Add($"The log has {errors} distinct error{(errors == 1 ? "" : "s")}.");

        // ── likely causes ────────────────────────────────────────────
        var suspects = new Dictionary<string, (ModEntry Entry, List<string> Why, int Score)>(StringComparer.OrdinalIgnoreCase);
        void Suspect(ModEntry e, string why, int score)
        {
            if (IsBuiltInActive(e.ModId)) return;
            if (!suspects.TryGetValue(e.ModId, out var s)) s = (e, new List<string>(), 0);
            if (!s.Why.Contains(why)) s.Why.Add(why);
            suspects[e.ModId] = (s.Entry, s.Why, s.Score + score);
        }

        // Lines just before the crash: only meaningful with a crash block,
        // else the "last lines" are the normal quit.
        // The last mod to write weighs most; the ones before it were only
        // busy around the same time.
        if (log.HasCrashSignature)
        {
            var weight = new[] { 4, 2, 1 };
            var rank = 0;
            foreach (var tag in log.TailTags)
            {
                if (rank >= weight.Length) break;
                if (MatchInstalledMod(tag) is not { } e) continue;
                Suspect(e, rank == 0 ? "was the last mod to write to the log before the crash"
                    : "wrote to the log shortly before the crash", weight[rank]);
                rank++;
            }
        }

        // Script errors, by the mod they name.
        foreach (var g in analysis.Issues
                     .Where(i => i.ModHint.Length > 0 && i.Severity is "SCRIPT_ERROR" or "ERROR")
                     .GroupBy(i => i.ModHint, StringComparer.OrdinalIgnoreCase))
        {
            if (MatchInstalledMod(g.Key) is not { } e) continue;
            var n = g.Count();
            Suspect(e, n == 1 ? "named in an error in the log" : $"named in {n} errors in the log", Math.Min(4, 1 + n));
        }

        // Changes since the last clean session.
        var changes = ChangesSinceLastKnownGood();
        foreach (var c in changes)
        {
            var e = Registry.FindById(c.ModId);
            if (e == null || !e.IsEnabled) continue;
            var score = c.What.StartsWith("load order", StringComparison.Ordinal) ? 1 : 3;
            Suspect(e, c.What + " since your last clean session", score);
        }

        var suspectList = suspects.Values
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(s => new CrashSuspect(s.Entry.ModId, NameOf(s.Entry), string.Join("; ", s.Why), s.Score,
                s.Entry.IsEnabled && !IsLocked(s.Entry)))
            .ToList();

        // ── ways forward ─────────────────────────────────────────────
        var steps = new List<CrashStep>();
        var good = ReadCheckpoint(CheckpointSlot.LastKnownGood);
        if (good != null && changes.Count > 0)
            steps.Add(new CrashStep(CrashAction.RestoreLastKnownGood,
                "Go back to your last clean session",
                $"Puts the loadout back the way it was {good.Age.Trim('(', ')')}, when the game last closed normally: "
                + $"undoes {changes.Count} change{(changes.Count == 1 ? "" : "s")} (listed below). The mod files stay where they are.",
                "Restore"));
        var canTurnOff = suspectList.Where(s => s.CanTurnOff).ToList();
        var likely = canTurnOff.Where(s => s.Likely).ToList();
        if (likely.Count == 0) likely = canTurnOff;
        if (canTurnOff.Count > 0)
            steps.Add(new CrashStep(CrashAction.TurnOffSuspects,
                likely.Count == 1 ? $"Turn off {likely[0].Name}" : $"Turn off the {likely.Count} likely causes",
                likely.Count == 1
                    ? "Launch again without it. If the crash is gone, check the mod's page for an update or a known issue, "
                      + "or tell its author (a support package helps)."
                    : "Launch again without them. If the crash is gone, turn them back on one at a time to find the one "
                      + "that does it, then check its page for an update or a known issue.",
                "Turn off"));
        var outdated = GetLoadoutUpdates();
        if (outdated.Count > 0)
            steps.Add(new CrashStep(CrashAction.UpdateMods,
                $"Update {outdated.Count} outdated mod{(outdated.Count == 1 ? "" : "s")}",
                "Crash fixes often ship in updates, especially right after a game update: "
                + string.Join(", ", outdated.Take(4).Select(NameOf)) + (outdated.Count > 4 ? $" and {outdated.Count - 4} more." : "."),
                "Update"));
        var readiness = GetLaunchReadiness();
        if (readiness.MissingDependencyCount > 0)
            steps.Add(new CrashStep(CrashAction.ReviewMissingDependencies,
                $"Install {readiness.MissingDependencyCount} missing dependenc{(readiness.MissingDependencyCount == 1 ? "y" : "ies")}",
                "A mod in the loadout needs a mod that isn't on: " + string.Join(", ", readiness.MissingDependencyIds.Take(4)) + ".",
                "Review"));
        var clashes = analysis.Clashes.Count + analysis.HookClashes.Count(h => h.Mods.Count >= 3);
        if (readiness.ConflictCount > 0 || clashes > 0)
            steps.Add(new CrashStep(CrashAction.ReviewConflicts,
                "Check the conflicts",
                (readiness.ConflictCount > 0 ? $"{readiness.ConflictCount} conflict{(readiness.ConflictCount == 1 ? "" : "s")} in the loadout. " : "")
                + (analysis.Clashes.Count > 0 ? $"{analysis.Clashes.Count} game script{(analysis.Clashes.Count == 1 ? " is" : "s are")} replaced by more than one mod in this session; only the last one wins. " : "")
                + "Changing who wins, or the load order, can stop a crash.",
                "Review"));
        if (log.Found)
        {
            steps.Add(new CrashStep(CrashAction.OpenLogAnalysis,
                "Read the log analysis",
                "Errors and warnings by mod, and which mods replaced or hooked the same game scripts in this session.",
                "Open"));
#if AI_RESOLVER
            steps.Add(new CrashStep(CrashAction.AnalyzeWithClaude,
                "Ask Claude what went wrong",
                "Claude reads the log, explains the likely cause and can propose a load order that avoids it.",
                "Analyze"));
#endif
        }
        steps.Add(new CrashStep(CrashAction.BuildSupportPackage,
            "Report it to the mod's author",
            "A support package is one file with your mod list, mod_config.cfg and the game's logs: attach it to "
            + "a bug report on the mod's page or in its Discord thread.",
            "Build…"));

        var headline = exit != null ? "The game crashed" : "Your last game session crashed";
        return new CrashReport(headline, string.Join(" ", parts), exitMeaning, log, errors,
            suspectList, changes, good?.Age.Trim('(', ')') ?? "", steps, exit != null);
    }

    /// <summary>What changed in the active loadout since the last-known-good
    /// checkpoint: mods turned on, turned off, updated or moved in the load
    /// order. Empty without that checkpoint, or when it was for another
    /// profile.</summary>
    public List<CrashChange> ChangesSinceLastKnownGood()
    {
        var result = new List<CrashChange>();
        if (!CrashCheckpoint.Exists(CheckpointSlot.LastKnownGood)) return result;
        ModConfig good;
        try { good = ModConfig.Load(CrashCheckpoint.ConfigPathFor(CheckpointSlot.LastKnownGood)); }
        catch { return result; }
        if (!string.Equals(good.ActiveProfile, Config.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return result;

        static Dictionary<string, ModConfig.ProfileEntry> On(ModConfig c)
        {
            var d = new Dictionary<string, ModConfig.ProfileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in c.ProfileEntries(c.ActiveProfile))
                if (e.Enabled != false && !string.IsNullOrEmpty(e.ModId)) d[e.ModId] = e;
            return d;
        }
        var before = On(good);
        var now = On(Config);
        string Name(string id) => Registry.FindById(id) is { } e ? NameOf(e) : id;

        foreach (var (id, e) in now)
        {
            if (IsBuiltInActive(id)) continue;
            if (!before.TryGetValue(id, out var b))
                result.Add(new CrashChange(id, Name(id), "turned on"));
            else if (!string.Equals(b.Version, e.Version, StringComparison.Ordinal))
                result.Add(new CrashChange(id, Name(id), $"updated v{b.Version} → v{e.Version}"));
            else if ((b.Priority ?? 0) != (e.Priority ?? 0))
                result.Add(new CrashChange(id, Name(id), $"load order moved ({b.Priority ?? 0} → {e.Priority ?? 0})"));
        }
        foreach (var (id, _) in before)
            if (!now.ContainsKey(id) && !IsBuiltInActive(id))
                result.Add(new CrashChange(id, Name(id), "turned off"));
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The installed mod a log token names: a "[Tag]", a
    /// res://mods/&lt;folder&gt;/ folder or a .vmz name. Compared without
    /// case, spaces or punctuation against the id, the name, the file name
    /// and the folders inside the archive.</summary>
    public ModEntry? MatchInstalledMod(string token)
    {
        var key = Squash(token);
        if (key.Length < 3) return null;
        foreach (var e in Registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (Squash(e.ModId) == key || Squash(e.DisplayName) == key) return e;
            var file = Path.GetFileNameWithoutExtension(e.Path);
            var cut = file.IndexOf("__v", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) file = file[..cut];
            if (Squash(file) == key) return e;
        }
        var folder = "mods/" + token.Trim().Trim('/') + "/";
        return Registry.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.ModId)
            && e.Files.Any(f => f.Replace('\\', '/').Contains(folder, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Squash(string? s)
        => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NameOf(ModEntry e) => string.IsNullOrEmpty(e.DisplayName) ? e.ModId : e.DisplayName;

    /// <summary>Turns off the given mods after a crash (locked and built-in
    /// ones stay on), saves, keeps the active profile in step and says
    /// what it did. Returns how many were turned off.</summary>
    public int TurnOffModsAfterCrash(IReadOnlyList<string> modIds)
    {
        var entries = modIds
            .Select(id => Registry.FindById(id))
            .Where(e => e != null && e.IsEnabled && !IsLocked(e) && !IsBuiltInActive(e.ModId))
            .Cast<ModEntry>()
            .ToList();
        if (entries.Count == 0) return 0;
        var names = entries.Select(NameOf).ToList();
        var ids = entries.Select(e => e.ModId).ToList();
        BulkToggleSelected(entries, false);
        foreach (var id in ids) SyncActiveProfileFromEntry(id);
        Requery();
        Say(StatusArea.Mods, $"Turned off {string.Join(", ", names)}. Launch again to see whether the crash is gone.");
        return entries.Count;
    }

    /// <summary>The startup check for a crash in a session the manager
    /// didn't watch: the newest log, when it has the crash handler's block
    /// and is newer than the last crash already shown. Null otherwise.</summary>
    public CrashReport? FindUnseenCrash()
    {
        var log = CrashLog.ReadNewest();
        if (!log.HasCrashSignature || log.WrittenUtc <= Settings.LastCrashSeenUtc) return null;
        var report = BuildCrashReport(null, log);
        LastCrash = report;
        return report;
    }

    /// <summary>Remembers that the crash in `report` has been shown.</summary>
    public void MarkCrashSeen(CrashReport report)
    {
        if (report.Log.WrittenUtc <= Settings.LastCrashSeenUtc) return;
        Settings.LastCrashSeenUtc = report.Log.WrittenUtc;
        try { Settings.Save(); } catch { /* shown again next start at worst */ }
    }

    /// <summary>Forgets the newest crash (a new launch, a clean session).</summary>
    public void ClearLastCrash()
    {
        if (LastCrash == null) return;
        LastCrash = null;
        RaiseChanged();
    }

    /// <summary>A watched session crashed: build the report and hand it to
    /// the front end, or offer the checkpoint restore without one.</summary>
    internal void ReportCrash(GameExit exit, CrashLogInfo log)
    {
        var report = BuildCrashReport(exit, log);
        LastCrash = report;
        if (log.Found) MarkCrashSeen(report);
        if (GameCrashed != null) GameCrashed.Invoke(report);
        else OfferCrashRestore(exit);
    }
}
