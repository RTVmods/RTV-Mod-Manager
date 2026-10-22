// AI edition only: the Claude Code status, resolving a file-overlap
// conflict with Claude, applying its merged source to a mod's .vmz (with a
// backup), and the log diagnosis behind "Analyze with Claude".

#if AI_RESOLVER
using VostokModManager.Ai;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>One row of the proposed load order: current → proposed.</summary>
/// <param name="CurrentPriority">Null when the mod isn't installed.</param>
public sealed record ProposedOrderRow(string ModId, string Name, int? CurrentPriority, int ProposedPriority)
{
    public bool Changes => CurrentPriority != ProposedPriority;
}

public sealed partial class ManagerSession
{
    /// <summary>True while a Claude resolve runs; the UI disables the
    /// Resolve buttons.</summary>
    public bool IsResolving { get; private set; }

    /// <summary>The Claude Code status line.</summary>
    public string ClaudeStatusText()
    {
        if (Claude.IsAvailable)
            return $"Claude Code: ✓  {Claude.Version}   ({Claude.ResolvedPath})";
        if (ClaudeCodeRunner.HasMsixInstall())
            return "Claude Code: ✗  Detected Claude Desktop (Microsoft Store), "
                + "but its CLI is sandboxed and unreachable from outside the "
                + "package. Install the standalone CLI: "
                + "`npm install -g @anthropic-ai/claude-code`.";
        return "Claude Code: ✗ not found — AI conflict resolution disabled. "
            + "Install Node.js (nodejs.org), then run "
            + "`npm install -g @anthropic-ai/claude-code`.";
    }

    // HasResolveButton / IsResolvable live with the other conflict helpers
    // in ManagerSession.Conflicts.cs.

    /// <summary>Asks Claude to resolve a file-overlap conflict. Returns the
    /// verdict for the resolution view (which may then ApplyMergeToMod),
    /// or null when the resolve didn't run (busy, unsupported type,
    /// Claude missing; the conflicts status says why). A failed run comes
    /// back as a verdict with Ok=false and Error set.</summary>
    public async Task<ConflictResolver.Verdict?> ResolveAsync(ConflictDetector.Conflict conflict)
    {
        if (IsResolving)
        {
            Say(StatusArea.Conflicts, "(Busy — wait for the current operation to finish.)");
            return null;
        }
        if (!HasResolveButton(conflict))
        {
            Say(StatusArea.Conflicts, $"Resolve doesn't handle [{conflict.Type}] conflicts in v1 — only file_overlap.");
            return null;
        }
        if (!Claude.IsAvailable)
        {
            Say(StatusArea.Conflicts, "Resolve needs Claude Code. "
                + "Install Node.js then `npm install -g @anthropic-ai/claude-code`, "
                + "then restart the manager.");
            return null;
        }

        IsResolving = true;
        Say(StatusArea.Conflicts, $"Resolving {conflict.Key} with Claude Code ...");
        RaiseChanged();  // the grids disable their buttons during the call
        ConflictResolver.Verdict verdict;
        try
        {
            verdict = await Resolver.ResolveFileOverlapAsync(conflict);
        }
        catch (Exception ex)
        {
            verdict = new ConflictResolver.Verdict
            {
                Ok = false,
                ConflictKey = conflict.Key,
                Error = ex.Message,
            };
        }
        finally
        {
            IsResolving = false;
            RaiseChanged();
        }
        Say(StatusArea.Conflicts, verdict.Ok
            ? $"Claude finished with {conflict.Key}."
            : $"Resolving {conflict.Key} failed: {verdict.Error}");
        return verdict;
    }

    /// <summary>The resolution view's "Apply to &lt;mod&gt;": after
    /// confirming, backs up the .vmz to a timestamped .bak beside it and
    /// replaces the contested entry with Claude's merged source, then
    /// rescans so the conflict drops off. Returns true when applied.</summary>
    public bool ApplyMergeToMod(ConflictResolver.Verdict verdict, ConflictResolver.ConflictMod mod)
    {
        if (!mod.IsArchive)
        {
            Prompt.Inform("Not supported",
                "Apply only supports .vmz archive mods at the moment. "
                + $"`{mod.ModId}` is a directory mod — edit it manually for now.");
            return false;
        }
        if (string.IsNullOrEmpty(verdict.ConflictKey) || string.IsNullOrEmpty(verdict.MergedSource))
        {
            Prompt.Inform("Not enough data",
                "Verdict is missing the conflict path or the merged source — can't apply.",
                PromptKind.Warning);
            return false;
        }

        var entryName = verdict.ConflictKey;
        var fileName = Path.GetFileName(mod.ArchivePath);
        if (!Prompt.Confirm($"Apply to {mod.DisplayName}",
                $"Replace `{entryName}` inside `{fileName}` with Claude's merged source?\n\n"
                + "A timestamped .bak backup of the .vmz will be saved alongside the "
                + "original first, so you can roll back if anything goes wrong.\n\n"
                + "Tip: quit Road to Vostok before applying — running games sometimes "
                + "hold .vmz files open and the write will fail.",
                PromptKind.Warning))
            return false;

        string backup;
        try { backup = ZipPatcher.CreateBackup(mod.ArchivePath); }
        catch (Exception ex)
        {
            Prompt.Inform("Apply failed", $"Backup failed: {ex.Message}\n\nNothing was changed.", PromptKind.Error);
            return false;
        }
        try { ZipPatcher.ReplaceEntry(mod.ArchivePath, entryName, verdict.MergedSource); }
        catch (Exception ex)
        {
            Prompt.Inform("Apply failed",
                $"Patch failed: {ex.Message}\n\n"
                + $"Backup at: {Path.GetFileName(backup)}\n"
                + "The original may be partially modified — restore from the "
                + ".bak file if the mod misbehaves.",
                PromptKind.Error);
            return false;
        }

        Prompt.Inform("Apply succeeded",
            $"Applied to `{mod.DisplayName}`.\n\n"
            + $"Backup saved as `{Path.GetFileName(backup)}` (delete it once "
            + "you've confirmed the mod still works).");
        // The .vmz contents changed: rescan + redetect so the conflict
        // (and any related ones) drop off the list.
        Refresh();
        return true;
    }

    /// <summary>"Analyze with Claude": sends the log digest to Claude for
    /// root causes and a proposed load order. A failure is reported to the
    /// user and comes back as a verdict with Ok=false.</summary>
    public async Task<LogDiagnoser.LogVerdict> DiagnoseLogAsync(
        GodotLogAnalyzer.LogAnalysis analysis, CancellationToken ct = default)
    {
        var diagnoser = new LogDiagnoser(Claude, Registry) { GameSourcePath = Settings.GameSourcePath };
        LogDiagnoser.LogVerdict verdict;
        try
        {
            verdict = await diagnoser.DiagnoseAsync(analysis, ct);
        }
        catch (Exception ex)
        {
            verdict = new LogDiagnoser.LogVerdict { Ok = false, Error = ex.Message };
        }
        if (!verdict.Ok)
            Prompt.Inform("Analysis error", "Claude analysis failed:\n" + verdict.Error, PromptKind.Error);
        return verdict;
    }

    /// <summary>The proposed load order as rows (current → proposed),
    /// named from the live mods.</summary>
    public IReadOnlyList<ProposedOrderRow> ProposedOrderRows(LogDiagnoser.LogVerdict verdict)
        => verdict.ProposedOrder.Select(pp =>
        {
            var entry = Registry.FindById(pp.ModId);
            var name = entry != null
                ? (string.IsNullOrEmpty(entry.DisplayName) ? entry.ModId : entry.DisplayName)
                : pp.ModId;
            return new ProposedOrderRow(pp.ModId, name, entry?.Priority, pp.Priority);
        }).ToList();
}
#endif
