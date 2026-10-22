// AI-driven godot.log diagnoser (AI edition only).
//
// Takes the structured digest produced by GodotLogAnalyzer (load order
// + override clashes + issues), asks Claude to explain the issues and
// propose a load order that minimises them, and parses the JSON verdict.
//
// Mirrors Ai/ConflictResolver exactly: ClaudeCodeRunner.RunAsync with a
// strict-JSON prompt and the three-tier parse fallback (direct →
// code-fence → first-'{'). Lives under Ai/ so the integrated build
// excludes it via the csproj `<Compile Remove="Ai\**\*.cs" />` rule.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ai;

public class LogDiagnoser
{
    private readonly ClaudeCodeRunner _runner;
    private readonly ModRegistry _registry;

    /// <summary>Optional decompiled-game-source folder. Unused for now
    /// (the digest is self-contained) but kept symmetric with
    /// ConflictResolver so MainForm can wire it the same way.</summary>
    public string GameSourcePath { get; set; } = "";

    public LogDiagnoser(ClaudeCodeRunner runner, ModRegistry registry)
    {
        _runner = runner;
        _registry = registry;
    }

    public class DiagIssue
    {
        public string Title { get; set; } = "";
        public string Cause { get; set; } = "";
        public string Fix   { get; set; } = "";
        public List<string> Mods { get; set; } = new();
    }

    public class ProposedPriority
    {
        public string ModId    { get; set; } = "";
        public int    Priority { get; set; }
    }

    public class LogVerdict
    {
        public bool Ok { get; set; }
        public string Summary { get; set; } = "";
        public List<DiagIssue> Issues { get; set; } = new();
        public List<ProposedPriority> ProposedOrder { get; set; } = new();
        public string OrderRationale { get; set; } = "";
        public double CostUsd { get; set; }
        public string RawText { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public async Task<LogVerdict> DiagnoseAsync(
        GodotLogAnalyzer.LogAnalysis analysis,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(analysis);
        var run = await _runner.RunAsync(prompt, ct);
        if (!run.Ok)
            return new LogVerdict { Ok = false, Error = run.Error, RawText = run.Stdout };

        var parsed = ParseVerdictJson(run.Text);
        if (parsed == null)
            return new LogVerdict
            {
                Ok = false,
                Error = "Could not parse Claude's response as JSON.",
                RawText = run.Text,
            };
        parsed.Ok = true;
        parsed.CostUsd = run.CostUsd;
        parsed.RawText = run.Text;
        return parsed;
    }

    // --- prompt + parsing ---------------------------------------------

    private string BuildPrompt(GodotLogAnalyzer.LogAnalysis analysis)
    {
        var digest = GodotLogAnalyzer.BuildClaudeDigest(analysis, _registry.Entries);

        var sb = new StringBuilder();
        sb.AppendLine("# Road to Vostok mod log analysis");
        sb.AppendLine();
        sb.AppendLine(
            "Below is a structured digest extracted from the game's runtime log " +
            "(godot.log) after a modded session. The game is Godot 4.6; mods are " +
            "loaded by the Vostok Mod Loader (MML). Mods with LOWER priority load " +
            "earlier; a mod that overrides the same vanilla script as another and " +
            "loads LATER wins. Your job: explain the issues and propose a load " +
            "order (priority assignment) that minimises conflicts.");
        sb.AppendLine();
        sb.AppendLine("Respond with strict JSON only — no prose outside the JSON.");
        sb.AppendLine("Schema:");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"<2-3 sentence overview of the log's health>\",");
        sb.AppendLine("  \"issues\": [");
        sb.AppendLine("    { \"title\": \"<short>\", \"cause\": \"<why it happens>\", \"fix\": \"<what the user should do>\", \"mods\": [\"<mod_id>\"] }");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"proposed_order\": [ { \"mod_id\": \"<id>\", \"priority\": <int> } ],");
        sb.AppendLine("  \"order_rationale\": \"<why this order reduces conflicts>\"");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(
            "- Only include mods in `proposed_order` whose priority you want to " +
            "CHANGE from the current value shown in the digest; leave the rest out.");
        sb.AppendLine(
            "- Use the mod_id values exactly as given in the digest's load order. " +
            "If a mod has no mod_id shown, omit it from proposed_order.");
        sb.AppendLine(
            "- If the log is healthy and no reorder helps, return an empty " +
            "`issues` and `proposed_order` and say so in `summary`.");
        sb.AppendLine();
        sb.AppendLine("## Log digest");
        sb.AppendLine(digest);
        return sb.ToString();
    }

    private static readonly Regex _jsonFenceRe = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.Compiled);

    /// <summary>Direct parse → code-fence extraction → from-first-'{'.
    /// Same robust fallback ConflictResolver uses.</summary>
    private static LogVerdict? ParseVerdictJson(string text)
    {
        var v = TryParse(text.Trim());
        if (v != null) return v;

        var m = _jsonFenceRe.Match(text);
        if (m.Success)
        {
            v = TryParse(m.Groups[1].Value);
            if (v != null) return v;
        }

        var brace = text.IndexOf('{');
        if (brace >= 0) return TryParse(text.Substring(brace));
        return null;
    }

    private static LogVerdict? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var v = new LogVerdict
            {
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                OrderRationale = root.TryGetProperty("order_rationale", out var orr)
                    ? orr.GetString() ?? "" : "",
            };

            if (root.TryGetProperty("issues", out var iss) && iss.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in iss.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var di = new DiagIssue
                    {
                        Title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Cause = el.TryGetProperty("cause", out var c) ? c.GetString() ?? "" : "",
                        Fix   = el.TryGetProperty("fix", out var f) ? f.GetString() ?? "" : "",
                    };
                    if (el.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
                        foreach (var me in mods.EnumerateArray())
                            di.Mods.Add(me.GetString() ?? "");
                    v.Issues.Add(di);
                }
            }

            if (root.TryGetProperty("proposed_order", out var po) && po.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in po.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    if (!el.TryGetProperty("mod_id", out var mid)) continue;
                    var id = mid.GetString() ?? "";
                    if (string.IsNullOrEmpty(id)) continue;
                    int prio = 0;
                    if (el.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number)
                        prio = pr.GetInt32();
                    v.ProposedOrder.Add(new ProposedPriority { ModId = id, Priority = prio });
                }
            }
            return v;
        }
        catch
        {
            return null;
        }
    }
}
