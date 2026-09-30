// Remembers where each downloaded mod file came from, until the main
// window can write that into mod_config.cfg's [mod_sources].
//
// Downloads happen in several dialogs, none of which own the config.
// Each calls Record() with the file it just saved; the main window
// drains the pending records on its next rescan and saves them. Records
// are keyed by the archive's own mod id and version (read here, at
// record time), so moving or renaming the file afterwards does not
// matter.

namespace VostokModManager.Domain;

public static class DownloadedSources
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (string ModId, string Version, ModSource Source)> _pending
        = new(StringComparer.Ordinal);

    /// <summary>Notes that the mod archive at `archivePath` was
    /// downloaded from `source`. Does nothing for an invalid source or
    /// a file that is not a readable mod archive.</summary>
    public static void Record(string archivePath, ModSource source)
    {
        if (!source.IsValid || string.IsNullOrEmpty(archivePath)) return;
        string modId, version;
        using (var arch = new ModArchive())
        {
            if (!arch.Open(archivePath)) return;
            modId = arch.ModId;
            version = arch.ModVersion;
        }
        if (string.IsNullOrEmpty(modId)) return;
        lock (_lock) _pending[modId + "@" + version] = (modId, version, source);
    }

    /// <summary>Returns the pending records and forgets them.</summary>
    public static List<(string ModId, string Version, ModSource Source)> Drain()
    {
        lock (_lock)
        {
            var list = _pending.Values.ToList();
            _pending.Clear();
            return list;
        }
    }
}
