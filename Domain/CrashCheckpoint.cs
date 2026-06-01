// Two-tier checkpoint store for the mod manager's "the game
// broke after I touched something" rollback feature:
//
//   • LastLaunch     — snapshot taken EVERY time the user clicks
//                      Launch Game, overwriting the previous
//                      LastLaunch snapshot. Useful for "I tweaked
//                      something just now, game broke, undo my
//                      most recent change."
//
//   • LastKnownGood  — promoted from LastLaunch when the game
//                      session ended CLEANLY (zero exit code +
//                      session > ~30s). The "I want to roll back
//                      to a state I know was playable" anchor.
//                      Never overwritten by a crashy launch, so
//                      a chain of crashes still leaves the most
//                      recent good state recoverable.
//
// Each slot stores:
//   • mod_config.cfg   — byte-for-byte snapshot
//   • profile.json     — byte-for-byte copy of active profile's
//                        metadata (when one was active)
//   • marker.json      — {created_at, active_profile_name}
//
// Both slots live under:
//   %APPDATA%/VostokModManager/checkpoints/<slot-name>/

using System.Text.Json;

namespace VostokModManager.Domain;

public enum CheckpointSlot
{
    LastLaunch,
    LastKnownGood,
}

public static class CrashCheckpoint
{
    private static string Root =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VostokModManager", "checkpoints");

    public static string DirFor(CheckpointSlot slot) =>
        System.IO.Path.Combine(Root,
            slot == CheckpointSlot.LastLaunch ? "last-launch" : "last-known-good");

    private static string CfgPath(CheckpointSlot s)     => System.IO.Path.Combine(DirFor(s), "mod_config.cfg");
    private static string ProfilePath(CheckpointSlot s) => System.IO.Path.Combine(DirFor(s), "profile.json");
    private static string MarkerPath(CheckpointSlot s)  => System.IO.Path.Combine(DirFor(s), "marker.json");

    public class Marker
    {
        public DateTime CreatedAt { get; set; }
        public string   ActiveProfileName { get; set; } = "";
    }

    /// <summary>True when a usable snapshot exists in this
    /// slot.</summary>
    public static bool Exists(CheckpointSlot slot)
        => File.Exists(CfgPath(slot)) && File.Exists(MarkerPath(slot));

    /// <summary>Marker metadata for the slot, or null when no
    /// checkpoint exists / the marker is malformed.</summary>
    public static Marker? ReadMarker(CheckpointSlot slot)
    {
        if (!File.Exists(MarkerPath(slot))) return null;
        try { return JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath(slot))); }
        catch { return null; }
    }

    /// <summary>Snapshot mod_config.cfg + the active profile
    /// into the LastLaunch slot. Overwrites any previous
    /// LastLaunch — that slot represents "before THIS launch".
    /// The LastKnownGood slot is untouched here; only
    /// PromoteToKnownGood moves data into it.
    /// Returns true on success; failures are non-fatal.</summary>
    public static bool Capture(string modConfigPath, string activeProfileName, string activeProfileFolder)
        => WriteSnapshot(CheckpointSlot.LastLaunch, modConfigPath, activeProfileName, activeProfileFolder);

    private static bool WriteSnapshot(
        CheckpointSlot slot,
        string         modConfigPath,
        string         activeProfileName,
        string         activeProfileFolder)
    {
        try
        {
            var dir = DirFor(slot);
            Directory.CreateDirectory(dir);

            // Wipe the slot's previous files so a half-written
            // restore can't pick up stale data.
            try { if (File.Exists(CfgPath(slot)))     File.Delete(CfgPath(slot)); }     catch { }
            try { if (File.Exists(ProfilePath(slot))) File.Delete(ProfilePath(slot)); } catch { }
            try { if (File.Exists(MarkerPath(slot)))  File.Delete(MarkerPath(slot)); }  catch { }

            if (File.Exists(modConfigPath))
                File.Copy(modConfigPath, CfgPath(slot));

            if (!string.IsNullOrEmpty(activeProfileFolder))
            {
                var src = System.IO.Path.Combine(activeProfileFolder, "profile.json");
                if (File.Exists(src))
                    File.Copy(src, ProfilePath(slot));
            }

            var marker = new Marker
            {
                CreatedAt         = DateTime.UtcNow,
                ActiveProfileName = activeProfileName ?? "",
            };
            File.WriteAllText(MarkerPath(slot),
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Copy the LastLaunch slot's contents into the
    /// LastKnownGood slot. Called by the game-process watcher
    /// when a session ended cleanly (exit code 0 + session was
    /// long enough to be a real play). The previous LastKnownGood
    /// is overwritten — we always anchor on the most recent
    /// confirmed-good state, not the earliest.</summary>
    public static bool PromoteToKnownGood()
    {
        if (!Exists(CheckpointSlot.LastLaunch)) return false;
        try
        {
            var dst = DirFor(CheckpointSlot.LastKnownGood);
            Directory.CreateDirectory(dst);
            try { if (File.Exists(CfgPath(CheckpointSlot.LastKnownGood)))     File.Delete(CfgPath(CheckpointSlot.LastKnownGood)); }     catch { }
            try { if (File.Exists(ProfilePath(CheckpointSlot.LastKnownGood))) File.Delete(ProfilePath(CheckpointSlot.LastKnownGood)); } catch { }
            try { if (File.Exists(MarkerPath(CheckpointSlot.LastKnownGood)))  File.Delete(MarkerPath(CheckpointSlot.LastKnownGood)); }  catch { }

            File.Copy(CfgPath(CheckpointSlot.LastLaunch), CfgPath(CheckpointSlot.LastKnownGood));
            if (File.Exists(ProfilePath(CheckpointSlot.LastLaunch)))
                File.Copy(ProfilePath(CheckpointSlot.LastLaunch), ProfilePath(CheckpointSlot.LastKnownGood));
            // Marker carries forward the original CreatedAt — the
            // "good" anchor represents the moment the snapshot was
            // taken, not the moment we promoted it.
            File.Copy(MarkerPath(CheckpointSlot.LastLaunch), MarkerPath(CheckpointSlot.LastKnownGood));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restore the slot's snapshot back over the live
    /// files. Returns true on full success.</summary>
    public static bool Restore(CheckpointSlot slot, string modConfigPath, string? activeProfileFolder)
    {
        if (!Exists(slot)) return false;
        try { File.Copy(CfgPath(slot), modConfigPath, overwrite: true); }
        catch { return false; }

        if (!string.IsNullOrEmpty(activeProfileFolder)
            && File.Exists(ProfilePath(slot)))
        {
            try
            {
                var dst = System.IO.Path.Combine(activeProfileFolder, "profile.json");
                File.Copy(ProfilePath(slot), dst, overwrite: true);
            }
            catch { /* best-effort — cfg is the critical part */ }
        }
        return true;
    }

    /// <summary>Hard-delete a single slot. Called after a
    /// successful restore so the status row doesn't keep
    /// offering a stale anchor.</summary>
    public static void Clear(CheckpointSlot slot)
    {
        try { if (Directory.Exists(DirFor(slot))) Directory.Delete(DirFor(slot), recursive: true); }
        catch { /* best-effort */ }
    }
}
