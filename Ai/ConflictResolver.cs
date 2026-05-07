// AI-driven resolver for file_overlap conflicts on .gd files. Builds a
// structured prompt (game source if available + each mod's version)
// and submits it via ClaudeCodeRunner. Parses Claude's JSON verdict.
//
// Verdict shapes Claude is asked to emit:
//   "merge_safe"      → changes are orthogonal, MergedSource has the
//                       proposed merge.
//   "order_resolves"  → one strict load order avoids the collision;
//                       LoadOrder lists it (earlier first).
//   "incompatible"    → mods truly conflict; Reason explains.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ai;

public class ConflictResolver
{
    private readonly ClaudeCodeRunner _runner;
    private readonly ModRegistry _registry;

    /// <summary>Optional path to the decompiled game source folder.
    /// When set, the resolver looks up the original game script at
    /// `&lt;GameSourcePath&gt;/&lt;rel&gt;` (rel = the conflict's res:// path
    /// with the prefix stripped) and includes it as context for
    /// Claude. Without it, Claude works only from the competing mod
    /// versions and the verdict is lower-quality.</summary>
    public string GameSourcePath { get; set; } = "";

    public ConflictResolver(ClaudeCodeRunner runner, ModRegistry registry)
    {
        _runner = runner;
        _registry = registry;
    }

    public class Verdict
    {
        public bool Ok { get; set; }
        public string ConflictKey { get; set; } = "";
        /// <summary>"merge_safe" | "order_resolves" | "incompatible"</summary>
        public string Result { get; set; } = "";
        public string Reason { get; set; } = "";
        public string MergedSource { get; set; } = "";
        public List<string> LoadOrder { get; set; } = new();
        public double CostUsd { get; set; }
        public string RawText { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public async Task<Verdict> ResolveFileOverlapAsync(
        ConflictDetector.Conflict conflict,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(conflict.Key, conflict.ModIds);
        var run = await _runner.RunAsync(prompt, ct);
        if (!run.Ok)
        {
            return new Verdict
            {
                Ok = false,
                ConflictKey = conflict.Key,
                Error = run.Error,
                RawText = run.Stdout,
            };
        }

        var parsed = ParseVerdictJson(run.Text);
        if (parsed == null)
        {
            return new Verdict
            {
                Ok = false,
                ConflictKey = conflict.Key,
                Error = "Could not parse Claude's verdict as JSON.",
                RawText = run.Text,
            };
        }
        parsed.Ok = true;
        parsed.ConflictKey = conflict.Key;
        parsed.CostUsd = run.CostUsd;
        parsed.RawText = run.Text;
        return parsed;
    }

    // --- prompt + parsing ---------------------------------------------

    private string BuildPrompt(string filePath, IEnumerable<string> modIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Road to Vostok mod conflict resolution");
        sb.AppendLine();
        sb.AppendLine($"Two or more mods write to the same file path: `{filePath}`.");
        sb.AppendLine(
            "Analyze whether the changes can be merged safely, whether one " +
            "load order resolves the issue, or whether the mods are " +
            "genuinely incompatible.");
        sb.AppendLine();
        sb.AppendLine("Respond with strict JSON only — no prose outside the JSON.");
        sb.AppendLine("Schema:");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"verdict\": \"merge_safe\" | \"order_resolves\" | \"incompatible\",");
        sb.AppendLine("  \"reason\": \"<one-paragraph explanation for the user>\",");
        sb.AppendLine("  \"merged_source\": \"<full merged GDScript source, or empty>\",");
        sb.AppendLine("  \"load_order\": [\"<mod_id_first>\", \"<mod_id_second>\", ...]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("- `merge_safe`: changes are orthogonal; provide the merged source in `merged_source`.");
        sb.AppendLine("- `order_resolves`: one strict load order avoids the override collision (e.g. one mod is a superset); list it in `load_order` (earlier first).");
        sb.AppendLine("- `incompatible`: the mods make incompatible changes; explain in `reason`.");
        sb.AppendLine();

        var origText = ReadOriginal(filePath);
        if (!string.IsNullOrEmpty(origText))
        {
            sb.AppendLine($"## Original game script (`{filePath}`)");
            sb.AppendLine("```gdscript");
            sb.AppendLine(origText);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine(
                "_(No original game script available for this path; " +
                "analyze based on the mod versions alone.)_");
            sb.AppendLine();
        }

        foreach (var mid in modIds)
        {
            var entry = _registry.FindById(mid);
            if (entry == null) continue;
            sb.AppendLine($"## Mod `{entry.ModId}` ({entry.DisplayName}, v{entry.Version})");
            if (!string.IsNullOrEmpty(entry.Description))
                sb.AppendLine($"Description: {entry.Description}");
            sb.AppendLine($"Version of `{filePath}`:");
            sb.AppendLine("```gdscript");
            sb.AppendLine(entry.ReadFileText(filePath));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Translates a `res://` path to a filesystem path under
    /// GameSourcePath and reads it. Returns "" if GameSourcePath is
    /// unset, the path doesn't start with res://, or the file doesn't
    /// exist.</summary>
    private string ReadOriginal(string resPath)
    {
        if (string.IsNullOrEmpty(GameSourcePath)) return "";
        if (!resPath.StartsWith("res://")) return "";
        var rel = resPath.Substring("res://".Length);
        var full = Path.Combine(GameSourcePath, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return "";
        try { return File.ReadAllText(full); }
        catch { return ""; }
    }

    private static readonly Regex _jsonFenceRe = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.Compiled);

    /// <summary>Tries direct JSON parse first, then falls back to
    /// extracting a JSON object from inside a markdown code fence,
    /// then tries the substring from the first '{' to end. Returns
    /// null if all three fail.</summary>
    private static Verdict? ParseVerdictJson(string text)
    {
        // 1. Direct parse.
        var v = TryParse(text.Trim());
        if (v != null) return v;

        // 2. Code-fence extraction.
        var m = _jsonFenceRe.Match(text);
        if (m.Success)
        {
            v = TryParse(m.Groups[1].Value);
            if (v != null) return v;
        }

        // 3. From first '{'.
        var brace = text.IndexOf('{');
        if (brace >= 0)
            return TryParse(text.Substring(brace));
        return null;
    }

    private static Verdict? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var v = new Verdict
            {
                Result = root.TryGetProperty("verdict", out var vv)
                    ? vv.GetString() ?? ""
                    : "",
                Reason = root.TryGetProperty("reason", out var rs)
                    ? rs.GetString() ?? ""
                    : "",
                MergedSource = root.TryGetProperty("merged_source", out var ms)
                    ? ms.GetString() ?? ""
                    : "",
            };
            if (root.TryGetProperty("load_order", out var lo)
                && lo.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in lo.EnumerateArray())
                    v.LoadOrder.Add(el.GetString() ?? "");
            }
            return v;
        }
        catch
        {
            return null;
        }
    }
}
