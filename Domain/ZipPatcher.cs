// Replaces a single entry inside a .vmz archive with new content,
// after backing up the original. Used by the AI conflict resolver's
// "Apply merged source" action: takes Claude's proposed merge, swaps
// it in for the contested entry, and writes a backup the user can
// roll back to.
//
// Backup naming: <archive_path>.<yyyyMMddHHmmss>.bak — sits next to
// the original. Backups accumulate; we don't auto-prune. The user can
// delete them once they're satisfied.

using System.IO.Compression;
using System.Text;

namespace VostokModManager.Domain;

public static class ZipPatcher
{
    /// <summary>Copies `archivePath` to a sibling .bak file with a
    /// timestamp suffix. Returns the backup path. Throws on failure
    /// — callers should NOT proceed to the patch step if backup
    /// fails.</summary>
    public static string CreateBackup(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException(
                $"Archive does not exist: {archivePath}",
                archivePath);
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var bakPath = $"{archivePath}.{stamp}.bak";
        // Don't overwrite — if the same-second backup already exists,
        // tack on a counter rather than clobber.
        if (File.Exists(bakPath))
        {
            var i = 2;
            while (File.Exists($"{bakPath}.{i}")) i++;
            bakPath = $"{bakPath}.{i}";
        }
        File.Copy(archivePath, bakPath, overwrite: false);
        return bakPath;
    }

    /// <summary>Replaces the zip entry at `entryName` inside
    /// `archivePath` with `newContent`. The entry is written as UTF-8
    /// without a BOM (matches how Godot's ConfigFile / GDScript
    /// expect text files). Creates the entry if it didn't exist
    /// (shouldn't happen for a real conflict, but safe).</summary>
    public static void ReplaceEntry(string archivePath, string entryName, string newContent)
    {
        // Open the file with FileShare.None to make sure no other
        // process can race us mid-update — corrupting a zip mid-write
        // would be very bad.
        using var stream = File.Open(
            archivePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
        var existing = archive.GetEntry(entryName);
        existing?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(newContent);
    }
}
