// Static analysis of GDScript source. Recognizes:
//   - extends "res://..."     (path-based)
//   - extends ClassName       (name-based)
//   - class_name Foo
//   - super() / super.method()  per function
//   - take_over_path("res://...")
//
// Mirrors the regex set from the v0.2 GDScript implementation. Function
// tracking is line-based and naive — "the next func line starts a new
// function" — which works because GDScript has no nested funcs. Inner
// classes aren't handled separately (we treat their funcs as flat).
//
// Used to derive load-order constraints (a mod that calls super() in
// foo() must load AFTER any mod that overrides foo() without super()).

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class GDScriptAnalyzer
{
    private static readonly Regex _reExtendsPath = new(
        @"^\s*extends\s+""((?:res://|user://)[^""]+)""",
        RegexOptions.Compiled);
    private static readonly Regex _reExtendsClass = new(
        @"^\s*extends\s+([A-Za-z_][A-Za-z0-9_]*)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex _reClassName = new(
        @"^\s*class_name\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);
    private static readonly Regex _reFunc = new(
        @"^\s*(?:static\s+)?func\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex _reSuperBare = new(
        @"\bsuper\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex _reSuperMethod = new(
        @"\bsuper\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex _reTakeover = new(
        @"take_over_path\s*\(\s*""([^""]+)""\s*\)",
        RegexOptions.Compiled);

    public class FunctionInfo
    {
        public bool CallsSuper { get; set; }
        public List<string> SuperMethods { get; set; } = new();
    }

    public class Analysis
    {
        public string ExtendsPath { get; set; } = "";
        public string ExtendsClass { get; set; } = "";
        public string ClassName { get; set; } = "";
        public Dictionary<string, FunctionInfo> Functions { get; set; } = new();
        public List<string> TakeOverPaths { get; set; } = new();
    }

    public static Analysis Analyze(string source)
    {
        var result = new Analysis();
        var currentFunc = "";
        foreach (var rawLine in source.Replace("\r\n", "\n").Split('\n'))
        {
            // Strip trailing comments to avoid false matches in commented code.
            var hashIdx = IndexOfUnescapedHash(rawLine);
            var code = hashIdx >= 0 ? rawLine.Substring(0, hashIdx) : rawLine;

            if (string.IsNullOrEmpty(result.ExtendsPath)
                && string.IsNullOrEmpty(result.ExtendsClass))
            {
                var m = _reExtendsPath.Match(code);
                if (m.Success)
                {
                    result.ExtendsPath = m.Groups[1].Value;
                }
                else
                {
                    m = _reExtendsClass.Match(code);
                    if (m.Success) result.ExtendsClass = m.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(result.ClassName))
            {
                var m = _reClassName.Match(code);
                if (m.Success) result.ClassName = m.Groups[1].Value;
            }

            var fm = _reFunc.Match(code);
            if (fm.Success)
            {
                currentFunc = fm.Groups[1].Value;
                if (!result.Functions.ContainsKey(currentFunc))
                    result.Functions[currentFunc] = new FunctionInfo();
                continue;
            }

            if (currentFunc != "")
            {
                if (_reSuperBare.IsMatch(code))
                    result.Functions[currentFunc].CallsSuper = true;
                var sm = _reSuperMethod.Match(code);
                if (sm.Success)
                {
                    result.Functions[currentFunc].CallsSuper = true;
                    var method = sm.Groups[1].Value;
                    if (!result.Functions[currentFunc].SuperMethods.Contains(method))
                        result.Functions[currentFunc].SuperMethods.Add(method);
                }
            }

            var tm = _reTakeover.Match(code);
            if (tm.Success)
            {
                var p = tm.Groups[1].Value;
                if (!result.TakeOverPaths.Contains(p))
                    result.TakeOverPaths.Add(p);
            }
        }
        return result;
    }

    /// <summary>Index of the first '#' in `line` not inside a quoted
    /// string. -1 if none.</summary>
    private static int IndexOfUnescapedHash(string line)
    {
        var inString = false;
        var quote = '\0';
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == quote) inString = false;
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inString = true;
                    quote = c;
                }
                else if (c == '#')
                {
                    return i;
                }
            }
            i++;
        }
        return -1;
    }
}
