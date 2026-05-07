// Drives the user's locally-installed `claude` CLI as a subprocess to
// power AI-assisted conflict resolution. The user's existing Claude
// auth (Pro/Max subscription, API key, etc.) is reused — this app
// never sees credentials.
//
// Tools are explicitly disabled (--disallowed-tools "*") so the prompt
// can't trigger filesystem or shell side-effects. The subprocess
// returns its result as JSON via --output-format json; we parse it.

using System.Diagnostics;
using System.Text.Json;

namespace VostokModManager.Api;

public class ClaudeCodeRunner
{
    public string ResolvedPath { get; private set; } = "";
    public bool IsAvailable { get; private set; }
    public string Version { get; private set; } = "";

    /// <summary>Manual override path supplied by the user via Settings.
    /// Tried first in Detect() before the built-in candidate list.</summary>
    public string OverridePath { get; set; } = "";

    /// <summary>True if Anthropic's Claude Desktop is installed via
    /// Microsoft Store (MSIX). Detected by the per-package reparse
    /// point at %APPDATA%/Claude — that folder only exists when the
    /// MSIX package is registered. Surfaced when detection fails so
    /// we can tell the user "your MSIX install is sandboxed, install
    /// the npm CLI instead."</summary>
    public static bool HasMsixInstall()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appdata)
            ? false
            : Directory.Exists(Path.Combine(appdata, "Claude"));
    }

    /// <summary>Probes for `claude` on PATH and at known install
    /// locations. Sets IsAvailable accordingly. Safe to call
    /// repeatedly.</summary>
    public void Detect()
    {
        IsAvailable = false;
        Version = "";
        ResolvedPath = "";

        var paths = new List<string>();
        if (!string.IsNullOrEmpty(OverridePath)) paths.Add(OverridePath);
        paths.AddRange(CandidatePaths());

        foreach (var candidate in paths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var output = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(5000)) continue;
                if (proc.ExitCode != 0) continue;
                ResolvedPath = candidate;
                Version = (output ?? "").Trim().Split('\n').FirstOrDefault() ?? "";
                IsAvailable = true;
                return;
            }
            catch
            {
                // File not found, access denied, etc. — try the next.
            }
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var paths = new List<string> { "claude" };
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Anthropic Claude Desktop drops claude-code into
        // %APPDATA%/Claude/claude-code/<version>/claude.exe — but its
        // MSIX sandbox makes it unreachable from external processes
        // for the user we tested with. We still list it: a non-MSIX
        // install at the same path would be reachable.
        if (!string.IsNullOrEmpty(appdata))
        {
            var claudeCodeRoot = Path.Combine(appdata, "Claude", "claude-code");
            if (Directory.Exists(claudeCodeRoot))
            {
                try
                {
                    var versions = Directory.EnumerateDirectories(claudeCodeRoot)
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .OrderByDescending(n => n, StringComparer.Ordinal)
                        .ToList();
                    foreach (var v in versions)
                        paths.Add(Path.Combine(claudeCodeRoot, v!, "claude.exe"));
                }
                catch
                {
                    // Permission denied on directory enum — skip.
                }
            }
            paths.Add(Path.Combine(appdata, "npm", "claude.cmd"));
        }

        if (!string.IsNullOrEmpty(userProfile))
        {
            paths.Add(Path.Combine(userProfile, ".claude", "local", "claude.exe"));
            paths.Add(Path.Combine(userProfile, ".claude", "local", "claude.cmd"));
            paths.Add(Path.Combine(userProfile, ".claude", "local", "claude"));
        }

        if (!string.IsNullOrEmpty(localAppData))
            paths.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));

        return paths;
    }

    /// <summary>Runs claude with the given prompt and returns the
    /// result. Awaitable on the UI thread; the actual subprocess runs
    /// without blocking the message loop.</summary>
    public async Task<RunResult> RunAsync(string prompt, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return RunResult.Failure("Claude Code not available; call Detect() first.");

        var psi = new ProcessStartInfo
        {
            FileName = ResolvedPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--disallowed-tools");
        psi.ArgumentList.Add("*");

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return RunResult.Failure($"Process.Start failed: {ex.Message}");
        }
        if (proc == null)
            return RunResult.Failure("Could not start claude process.");

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            _ = await stderrTask; // captured in case we want to surface

            if (proc.ExitCode != 0)
                return RunResult.Failure(
                    $"claude exited with code {proc.ExitCode}",
                    stdout: stdout);

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                var text = root.TryGetProperty("result", out var r)
                    ? r.GetString() ?? ""
                    : "";
                var cost = 0.0;
                if (root.TryGetProperty("total_cost_usd", out var c)
                    && c.ValueKind == JsonValueKind.Number)
                    cost = c.GetDouble();
                return RunResult.Success(text, cost, stdout);
            }
            catch
            {
                return RunResult.Failure(
                    "Could not parse Claude Code JSON output.",
                    stdout: stdout);
            }
        }
        finally
        {
            proc.Dispose();
        }
    }

    public class RunResult
    {
        public bool Ok { get; init; }
        public string Text { get; init; } = "";
        public double CostUsd { get; init; }
        public string Stdout { get; init; } = "";
        public string Error { get; init; } = "";

        public static RunResult Success(string text, double cost, string stdout)
            => new() { Ok = true, Text = text, CostUsd = cost, Stdout = stdout };

        public static RunResult Failure(string error, string stdout = "")
            => new() { Ok = false, Error = error, Stdout = stdout };
    }
}
