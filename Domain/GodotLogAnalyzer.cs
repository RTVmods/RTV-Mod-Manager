// Deterministic parser for Road to Vostok's godot.log.
//
// The Vostok Mod Loader (MML) writes highly-structured lines we can
// extract without any AI:
//
//   Load order:
//     [ModLoader][Info]   [5] Armed Enhancement … | weapon-attachment-spawner__v1.3.0.vmz [priority=0]
//   Script override / hook applied (the runtime ground truth for
//   "which mod overrode which vanilla script, and in what order"):
//     [ModLoader][Info] [Overrides] Applied: res://Scripts/Tooltip.gd -> res://mods/ImmersiveAmmoCheck/Tooltip.gd [Immersive Ammo Check]
//   Issues:
//     SCRIPT ERROR: Compile Error: …
//     ERROR: …
//     WARNING: [ModLoader][Warning] …
//
// From the override lines we derive OverrideClash entries: any vanilla
// path overridden by 2+ distinct mods, with the LAST applier marked as
// the winner (MML applies in load order; last wins).
//
// This class is pure parsing — no AI, no Claude — so it ships in BOTH
// editions. The AI-edition LogDiagnoser consumes BuildClaudeDigest()
// output for its prompt.

using System.Text;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class GodotLogAnalyzer
{
    // ── Model ─────────────────────────────────────────────────────────

    public class LogOverride
    {
        public string VanillaPath  { get; set; } = "";
        public string OverridePath { get; set; } = "";
        public string ModName      { get; set; } = "";
        public int    LineNo       { get; set; }
    }

    /// <summary>A single method-level hook declaration from the loader:
    /// `Hook declared: res://Scripts/X.gd :: Method [Mod Name]`. This is
    /// the finest-grained signal MML emits for "which function does this
    /// mod hook" — distinct from the file-level [Overrides] Applied
    /// lines (LogOverride).</summary>
    public class LogHook
    {
        public string VanillaPath { get; set; } = "";
        public string Method      { get; set; } = "";
        public string ModName     { get; set; } = "";
        public int    LineNo      { get; set; }
        /// <summary>"path :: method" — the collision key.</summary>
        public string Target => $"{VanillaPath} :: {Method}";
    }

    /// <summary>A single hook target (path::method) declared by 2+ mods.
    /// `Mods` is in declaration order. With MML's hook chain, multiple
    /// mods CAN coexist on one method (each wraps the previous), so this
    /// is informational — it tells the user exactly which mods stack on
    /// the same function, where ordering bugs are most likely.</summary>
    public class HookClash
    {
        public string       VanillaPath { get; set; } = "";
        public string       Method      { get; set; } = "";
        public List<string> Mods        { get; set; } = new();
        public string Target => $"{VanillaPath} :: {Method}";
    }

    public class LogLoadEntry
    {
        public int    Index    { get; set; }
        public string ModName  { get; set; } = "";
        public string FileName { get; set; } = "";
        public int    Priority { get; set; }
    }

    public class LogIssue
    {
        /// <summary>"ERROR" | "SCRIPT_ERROR" | "WARNING".</summary>
        public string Severity { get; set; } = "";
        public string Message  { get; set; } = "";
        /// <summary>Best-guess mod this issue belongs to (from a
        /// res://mods/&lt;x&gt; or &lt;…&gt;.vmz token in the message), or "".</summary>
        public string ModHint  { get; set; } = "";
        public int    LineNo   { get; set; }
    }

    /// <summary>A vanilla script overridden by 2+ distinct mods. `Mods`
    /// is in apply order (load order); `Winner` is the last applier —
    /// the one whose version actually takes effect.</summary>
    public class OverrideClash
    {
        public string       VanillaPath { get; set; } = "";
        public List<string> Mods        { get; set; } = new();
        public string       Winner      { get; set; } = "";
    }

    public class LogAnalysis
    {
        public string LogPath       { get; set; } = "";
        public string EngineVersion { get; set; } = "";
        public List<LogOverride>   Overrides   { get; set; } = new();
        public List<LogHook>       Hooks       { get; set; } = new();
        public List<LogLoadEntry>  LoadOrder   { get; set; } = new();
        public List<LogIssue>      Issues      { get; set; } = new();
        public List<OverrideClash> Clashes     { get; set; } = new();
        public List<HookClash>     HookClashes { get; set; } = new();
    }

    // ── Log discovery ─────────────────────────────────────────────────

    /// <summary>All *.log files in the Godot logs directory, newest
    /// first. Empty when the directory doesn't exist.</summary>
    public static List<string> AvailableLogs()
    {
        var dir = SupportPackage.GodotLogsDir;
        if (!Directory.Exists(dir)) return new();
        try
        {
            return Directory.GetFiles(dir, "*.log")
                .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>The newest log (current session is godot.log), or ""
    /// when there are none.</summary>
    public static string LatestLog() => AvailableLogs().FirstOrDefault() ?? "";

    // ── Regexes ───────────────────────────────────────────────────────

    private static readonly Regex _reLoad = new(
        @"^\[ModLoader\]\[Info\]\s+\[(\d+)\]\s+(.+?)\s+\|\s+(\S+?)\s+\[priority=(-?\d+)\]",
        RegexOptions.Compiled);

    private static readonly Regex _reOverride = new(
        @"\[Overrides\]\s+Applied:\s+(res://\S+)\s+->\s+(res://\S+)\s+\[(.+?)\]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex _reHook = new(
        @"Hook declared:\s+(res://\S+)\s+::\s+(\S+)\s+\[(.+?)\]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex _reIssue = new(
        @"^(SCRIPT ERROR|ERROR|WARNING):\s*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex _reEngine = new(
        @"^Godot Engine (v\S+)",
        RegexOptions.Compiled);

    // Mod-hint extraction: a res://mods/<slug>/ token, or a <name>__v…vmz
    // / <name>.vmz filename anywhere in the message.
    private static readonly Regex _reModResPath = new(
        @"res://mods/([A-Za-z0-9_\-\.]+)/",
        RegexOptions.Compiled);
    private static readonly Regex _reModVmz = new(
        @"([A-Za-z0-9_\-\.]+?)(?:__v[0-9][^/\s]*)?\.vmz",
        RegexOptions.Compiled);

    // Lines so noisy / repetitive they'd drown the issue list. The MML
    // ext_resource invalid-UID warnings are cosmetic and fire dozens of
    // times; collapse those out of the issue feed (still counted).
    private static readonly Regex _reNoiseWarning = new(
        @"invalid UID:|ext_resource, invalid UID",
        RegexOptions.Compiled);

    // ── Parse ─────────────────────────────────────────────────────────

    /// <summary>Reads and parses a godot.log. Never throws for a
    /// malformed log — returns whatever it could extract.</summary>
    public static LogAnalysis Analyze(string logPath)
    {
        var a = new LogAnalysis { LogPath = logPath };
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return a;

        string[] lines;
        try
        {
            // Share-all so a live log the running game still holds open
            // can be read. Cap at a sane ceiling so a runaway log can't
            // OOM us.
            using var fs = new FileStream(
                logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var list = new List<string>();
            string? line;
            int n = 0;
            while ((line = sr.ReadLine()) != null && n < 400_000)
            {
                list.Add(line);
                n++;
            }
            lines = list.ToArray();
        }
        catch { return a; }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;

            if (a.EngineVersion.Length == 0)
            {
                var em = _reEngine.Match(line);
                if (em.Success) { a.EngineVersion = em.Groups[1].Value; }
            }

            var lm = _reLoad.Match(line);
            if (lm.Success)
            {
                a.LoadOrder.Add(new LogLoadEntry
                {
                    Index    = int.TryParse(lm.Groups[1].Value, out var idx) ? idx : 0,
                    ModName  = lm.Groups[2].Value.Trim(),
                    FileName = lm.Groups[3].Value.Trim(),
                    Priority = int.TryParse(lm.Groups[4].Value, out var pr) ? pr : 0,
                });
                continue;
            }

            var om = _reOverride.Match(line);
            if (om.Success)
            {
                a.Overrides.Add(new LogOverride
                {
                    VanillaPath  = om.Groups[1].Value,
                    OverridePath = om.Groups[2].Value,
                    ModName      = om.Groups[3].Value.Trim(),
                    LineNo       = lineNo,
                });
                continue;
            }

            var hm = _reHook.Match(line);
            if (hm.Success)
            {
                a.Hooks.Add(new LogHook
                {
                    VanillaPath = hm.Groups[1].Value,
                    Method      = hm.Groups[2].Value,
                    ModName     = hm.Groups[3].Value.Trim(),
                    LineNo      = lineNo,
                });
                continue;
            }

            var im = _reIssue.Match(line);
            if (im.Success)
            {
                var sev = im.Groups[1].Value == "SCRIPT ERROR" ? "SCRIPT_ERROR" : im.Groups[1].Value;
                var msg = im.Groups[2].Value.Trim();
                if (msg.Length == 0) continue;
                if (_reNoiseWarning.IsMatch(msg)) continue; // cosmetic spam
                a.Issues.Add(new LogIssue
                {
                    Severity = sev,
                    Message  = msg,
                    ModHint  = GuessMod(msg),
                    LineNo   = lineNo,
                });
            }
        }

        a.Clashes = DeriveClashes(a.Overrides);
        a.HookClashes = DeriveHookClashes(a.Hooks);
        DedupIssues(a);
        return a;
    }

    /// <summary>Groups hook declarations by (path :: method); any target
    /// declared by 2+ distinct mods is a hook clash — multiple mods hook
    /// the same function. Mods preserve declaration order.</summary>
    private static List<HookClash> DeriveHookClashes(List<LogHook> hooks)
    {
        var byTarget = new Dictionary<string, HookClash>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var h in hooks)
        {
            if (!byTarget.TryGetValue(h.Target, out var hc))
            {
                byTarget[h.Target] = hc = new HookClash
                {
                    VanillaPath = h.VanillaPath,
                    Method      = h.Method,
                };
                order.Add(h.Target);
            }
            if (!hc.Mods.Contains(h.ModName)) hc.Mods.Add(h.ModName);
        }
        var result = new List<HookClash>();
        foreach (var t in order)
            if (byTarget[t].Mods.Count >= 2) result.Add(byTarget[t]);
        return result.OrderByDescending(c => c.Mods.Count)
                     .ThenBy(c => c.Target, StringComparer.Ordinal)
                     .ToList();
    }

    /// <summary>Groups overrides by vanilla path; any path with 2+
    /// distinct mods is a clash. Mods preserve first-seen apply order;
    /// the last is the winner.</summary>
    private static List<OverrideClash> DeriveClashes(List<LogOverride> overrides)
    {
        var byPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var o in overrides)
        {
            if (!byPath.TryGetValue(o.VanillaPath, out var list))
                byPath[o.VanillaPath] = list = new List<string>();
            if (!list.Contains(o.ModName)) list.Add(o.ModName);
        }
        var result = new List<OverrideClash>();
        foreach (var (path, mods) in byPath)
        {
            if (mods.Count < 2) continue;
            result.Add(new OverrideClash
            {
                VanillaPath = path,
                Mods        = mods,
                Winner      = mods[^1],
            });
        }
        return result.OrderBy(c => c.VanillaPath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Collapses identical (severity, message) issues so a
    /// once-per-frame error doesn't appear 500 times. Keeps the first
    /// occurrence; appends an "(×N)" count when repeated.</summary>
    private static void DedupIssues(LogAnalysis a)
    {
        var seen = new Dictionary<string, LogIssue>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var iss in a.Issues)
        {
            var key = iss.Severity + "|" + iss.Message;
            if (!seen.ContainsKey(key)) { seen[key] = iss; order.Add(key); counts[key] = 1; }
            else counts[key]++;
        }
        var deduped = new List<LogIssue>();
        foreach (var key in order)
        {
            var iss = seen[key];
            if (counts[key] > 1) iss.Message += $"  (×{counts[key]})";
            deduped.Add(iss);
        }
        a.Issues = deduped;
    }

    private static string GuessMod(string message)
    {
        var pm = _reModResPath.Match(message);
        if (pm.Success) return pm.Groups[1].Value;
        var vm = _reModVmz.Match(message);
        if (vm.Success) return vm.Groups[1].Value;
        return "";
    }

    // ── Claude digest ─────────────────────────────────────────────────

    /// <summary>Builds a compact, token-cheap text digest for the AI
    /// diagnoser. Includes the load order (with priorities), the
    /// override clash table, and the issue list. `live` lets us map a
    /// log ModName / vmz back to a mod_id so Claude can emit mod_ids in
    /// its proposed_order.</summary>
    public static string BuildClaudeDigest(LogAnalysis a, IEnumerable<ModEntry> live)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Engine: {a.EngineVersion}");
        sb.AppendLine();

        sb.AppendLine("## Load order (index, mod, priority) — lower priority loads earlier, later overrides earlier:");
        // Map log file name → mod_id so Claude can reference mod_ids.
        var idByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in live)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            var fn = Path.GetFileName(e.Path);
            if (!string.IsNullOrEmpty(fn)) idByFile[fn] = e.ModId;
            if (!string.IsNullOrEmpty(e.DisplayName)) idByName[e.DisplayName] = e.ModId;
        }
        foreach (var l in a.LoadOrder)
        {
            var modId = "";
            if (!idByFile.TryGetValue(l.FileName, out modId))
                idByName.TryGetValue(l.ModName, out modId);
            sb.AppendLine($"  [{l.Index}] {l.ModName}  (mod_id={modId ?? ""}, priority={l.Priority})");
        }
        sb.AppendLine();

        if (a.Clashes.Count > 0)
        {
            sb.AppendLine("## Script override clashes (vanilla path : mods in apply order → winner):");
            foreach (var c in a.Clashes)
                sb.AppendLine($"  {c.VanillaPath} : {string.Join(" → ", c.Mods)}  (winner: {c.Winner})");
            sb.AppendLine();
        }

        if (a.HookClashes.Count > 0)
        {
            sb.AppendLine("## Hook clashes (path :: method declared by multiple mods — these stack/chain; order matters):");
            foreach (var c in a.HookClashes)
                sb.AppendLine($"  {c.Target} : {string.Join(", ", c.Mods)}");
            sb.AppendLine();
        }

        if (a.Issues.Count > 0)
        {
            sb.AppendLine("## Issues from the log:");
            foreach (var iss in a.Issues)
            {
                var hint = string.IsNullOrEmpty(iss.ModHint) ? "" : $" [mod: {iss.ModHint}]";
                sb.AppendLine($"  {iss.Severity}: {iss.Message}{hint}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
