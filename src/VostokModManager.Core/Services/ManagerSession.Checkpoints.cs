// Crash checkpoints: the last-launch snapshot taken on every launch and
// the last-known-good one promoted after a clean session (see
// Domain/CrashCheckpoint.cs). This is the status row, the slot picker's
// data and the restore.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>One checkpoint slot as the picker shows it.</summary>
/// <param name="Name">"last-launch" or "last-known-good".</param>
/// <param name="Age">"(5m ago)", "(3h ago)", "(2d ago)".</param>
/// <param name="Blurb">What restoring this slot undoes.</param>
public sealed record CheckpointInfo(
    CheckpointSlot Slot,
    string Name,
    DateTime CreatedAt,
    string ActiveProfileName,
    string Age,
    string Blurb);

/// <summary>The checkpoint status row: hidden when neither slot exists.</summary>
public sealed record CheckpointStatus(CheckpointInfo? LastLaunch, CheckpointInfo? LastKnownGood, string Text)
{
    public bool HasLastLaunch => LastLaunch != null;
    public bool HasLastKnownGood => LastKnownGood != null;
    public bool Any => LastLaunch != null || LastKnownGood != null;
    /// <summary>Restoring needs a pick between the two slots.</summary>
    public bool NeedsPick => LastLaunch != null && LastKnownGood != null;
}

public sealed partial class ManagerSession
{
    private const string LastLaunchBlurb =
        "Undo the change you made right before the most recent Launch click.";
    private const string LastKnownGoodBlurb =
        "Roll back to the state from the most recent session that ended cleanly (zero exit code, ≥ 60s).";

    /// <summary>The checkpoint status row:
    ///   • none — hidden (Text empty);
    ///   • last-launch only — "undo your most recent cfg / profile change";
    ///   • last-known-good only — "roll back to your last clean session";
    ///   • both — "click to choose which to restore".</summary>
    public CheckpointStatus GetCheckpointStatus()
    {
        var launch = ReadCheckpoint(CheckpointSlot.LastLaunch);
        var good = ReadCheckpoint(CheckpointSlot.LastKnownGood);
        string text;
        if (launch == null && good == null) text = "";
        else if (launch != null && good != null)
            text = $"🔁 Checkpoints: last-launch {launch.Age} "
                + $"+ last-known-good {good.Age} "
                + "— click to choose which to restore.";
        else if (launch != null)
            text = $"🔁 Last-launch checkpoint {launch.Age} "
                + "— click to undo your most recent cfg / profile change.";
        else
            text = $"🔁 Last-known-good checkpoint {good!.Age} "
                + "— click to roll back to your last clean session's state.";
        return new CheckpointStatus(launch, good, text);
    }

    private static CheckpointInfo? ReadCheckpoint(CheckpointSlot slot)
    {
        if (!CrashCheckpoint.Exists(slot)) return null;
        var marker = CrashCheckpoint.ReadMarker(slot);
        var launch = slot == CheckpointSlot.LastLaunch;
        return new CheckpointInfo(
            slot,
            launch ? "last-launch" : "last-known-good",
            marker?.CreatedAt ?? DateTime.MinValue,
            marker?.ActiveProfileName ?? "",
            FormatCheckpointAge(marker),
            launch ? LastLaunchBlurb : LastKnownGoodBlurb);
    }

    internal static string FormatCheckpointAge(CrashCheckpoint.Marker? marker)
    {
        if (marker == null) return "";
        var age = DateTime.UtcNow - marker.CreatedAt;
        if (age.TotalMinutes < 60) return $"({(int)age.TotalMinutes}m ago)";
        if (age.TotalHours < 48) return $"({(int)age.TotalHours}h ago)";
        return $"({(int)age.TotalDays}d ago)";
    }

    /// <summary>The checkpoint row's click: restores the only slot there
    /// is, or asks which one when both exist (the picker). Returns true
    /// when a checkpoint was restored.</summary>
    public bool RestoreCheckpoint()
    {
        var status = GetCheckpointStatus();
        if (!status.Any) return false;

        CheckpointSlot slot;
        if (status.NeedsPick)
        {
            static string Row(CheckpointInfo c, string title)
                => $"▶ {title}\nTaken: {c.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} {c.Age}.\n"
                    + $"Active profile: '{c.ActiveProfileName}'.\n{c.Blurb}";
            var pick = Prompt.Choose("Restore checkpoint — pick a slot",
                "Pick which checkpoint to restore:\n\n"
                + Row(status.LastLaunch!, "Last-launch") + "\n\n"
                + Row(status.LastKnownGood!, "Last-known-good"),
                new[] { "Last-launch", "Last-known-good" });
            if (pick < 0) return false;
            slot = pick == 0 ? CheckpointSlot.LastLaunch : CheckpointSlot.LastKnownGood;
        }
        else slot = status.HasLastLaunch ? CheckpointSlot.LastLaunch : CheckpointSlot.LastKnownGood;

        return RestoreCheckpoint(slot);
    }

    /// <summary>Restores one slot after confirming: overwrites
    /// mod_config.cfg and the active profile's profile.json with the
    /// snapshot. Mods on disk aren't touched; only the enabled /
    /// priority / version metadata rolls back. Returns true on success.</summary>
    public bool RestoreCheckpoint(CheckpointSlot slot)
    {
        var marker = CrashCheckpoint.ReadMarker(slot);
        var slotName = slot == CheckpointSlot.LastLaunch ? "last-launch" : "last-known-good";
        var msg = marker != null
            ? $"Restore the {slotName} checkpoint taken "
              + $"{marker.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}?\n\n"
              + $"Active profile at snapshot time: '{marker.ActiveProfileName}'.\n\n"
            : $"Restore the {slotName} checkpoint?\n\n";
        msg += "Overwrites mod_config.cfg + the active profile's "
             + "profile.json with the snapshot. Mods themselves on "
             + "disk aren't touched — only the enabled/priority/"
             + "version metadata rolls back.";
        if (!Prompt.Confirm($"Restore {slotName} checkpoint", msg, PromptKind.Warning)) return false;

        var ok = CrashCheckpoint.Restore(slot, ModConfig.DefaultPath, ActiveProfile?.FolderPath);
        if (!ok)
        {
            Prompt.Inform("Restore failed",
                "Couldn't restore the checkpoint — file write failed. "
                + "The snapshot is still on disk; check folder permissions.",
                PromptKind.Warning);
            return false;
        }
        // Clear only the restored slot. The other one still represents a
        // valid anchor (restoring last-launch doesn't invalidate
        // last-known-good).
        CrashCheckpoint.Clear(slot);
        ReloadProfiles();
        Refresh();
        Say(StatusArea.Mods, $"{slotName} checkpoint restored.");
        return true;
    }
}
