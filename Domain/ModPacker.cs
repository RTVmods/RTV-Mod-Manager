// Packs a folder or existing .vmz/.zip into a fresh .vmz with an
// edited mod.txt — the "mod creator" side of the manager. Used by
// the Mod Packager UI to:
//   1) Open a source (folder OR archive)
//   2) Apply edits to its mod.txt (manifest fields + [dependencies])
//   3) Zip the result with FORWARD-SLASH entry paths
//
// Forward-slash entries matter because the Vostok mod loader (Godot
// ZipReader) reads paths verbatim and rejects Windows-style
// backslash entries. .NET's ZipFile.CreateFromDirectory writes
// backslashes on Windows, so we build the archive manually via
// ZipArchive.CreateEntry, passing the relative path with '/'
// already substituted in.

using System.IO.Compression;

namespace VostokModManager.Domain;

public static class ModPacker
{
    /// <summary>The full set of fields the packager can edit on the
    /// source's `mod.txt`. Empty strings / null mean "don't touch
    /// the existing value"; only non-empty fields are written.
    /// Dependency lists are ALWAYS written (an empty list rewrites
    /// to `[]` so the section is deterministic).</summary>
    public class PackOptions
    {
        public string ModId       { get; set; } = "";
        public string Name        { get; set; } = "";
        public string Version     { get; set; } = "";
        public string Description { get; set; } = "";
        public int?   Priority    { get; set; }
        public int?   ModWorkshopId { get; set; }
        public List<string> RequiredDependencies { get; set; } = new();
        public List<string> OptionalDependencies { get; set; } = new();
        /// <summary>When true, write the dependencies section even
        /// if both lists are empty (produces `required = []`). When
        /// false, only write if non-empty. Default true so the user
        /// can REMOVE deps by clearing the lists and rebuilding.
        /// </summary>
        public bool WriteEmptyDependencies { get; set; } = true;
        /// <summary>Optional `dep_id → ModWorkshop numeric id` map
        /// written to a `[dependency_sources]` section in mod.txt.
        /// The in-game loader ignores this section; the manager's
        /// install-time resolver uses it to auto-fill the MW URL
        /// for missing dependencies (no user paste needed). Empty
        /// → drop the section entirely.</summary>
        public Dictionary<string, int> DependencySources { get; set; } = new();
    }

    public class PackResult
    {
        public bool   Success      { get; set; }
        public string OutputPath   { get; set; } = "";
        public int    FilesPacked  { get; set; }
        public long   OutputBytes  { get; set; }
        public string ManifestPath { get; set; } = "";
        public List<string> Log    { get; init; } = new();
        public List<string> Errors { get; init; } = new();
    }

    /// <summary>Pack `sourcePath` (a folder OR a .vmz/.zip) into a
    /// fresh .vmz at `outputPath`, applying `opts` to mod.txt. The
    /// source is never modified — if it's an archive we extract to
    /// a temp directory, edit there, and zip THAT to the output.
    ///
    /// Throws nothing — failures land in PackResult.Errors with
    /// Success=false so the dialog can render them inline. Temp
    /// directory is cleaned up via finally regardless.</summary>
    public static PackResult Pack(
        string      sourcePath,
        string      outputPath,
        PackOptions opts)
    {
        var result = new PackResult { OutputPath = outputPath };
        string? tempDir = null;
        try
        {
            // Resolve source → a working folder we'll zip from.
            string workDir;
            if (Directory.Exists(sourcePath))
            {
                // Folder source — copy to temp so we don't mutate
                // the user's working tree when applying manifest
                // edits.
                tempDir = MakeTempDir();
                CopyDirRecursive(sourcePath, tempDir);
                workDir = tempDir;
                result.Log.Add($"Source: folder → temp copy at {tempDir}");
            }
            else if (File.Exists(sourcePath))
            {
                tempDir = MakeTempDir();
                ZipFile.ExtractToDirectory(sourcePath, tempDir,
                    overwriteFiles: true);
                workDir = tempDir;
                result.Log.Add($"Source: archive → extracted {CountFiles(tempDir)} file(s) to temp.");
            }
            else
            {
                result.Errors.Add($"Source does not exist: {sourcePath}");
                return result;
            }

            // Some uploads bury the actual mod inside a single
            // top-level folder (e.g. `MyMod-1.0/mod.txt`). Detect
            // that one-folder-wrap case and unwrap so the resulting
            // .vmz has mod.txt at the archive root, where the
            // loader expects it.
            workDir = UnwrapSingleTopFolder(workDir);

            // Locate or create mod.txt at the work-dir root.
            var manifestPath = Path.Combine(workDir, "mod.txt");
            result.ManifestPath = manifestPath;
            string modTxt = File.Exists(manifestPath)
                ? File.ReadAllText(manifestPath)
                : "";

            // Apply edits in deterministic order — ManifestEditor
            // splices line-by-line so each call is independent.
            if (!string.IsNullOrEmpty(opts.ModId))
                modTxt = ManifestEditor.SetValue(modTxt, "mod", "id", QuoteString(opts.ModId));
            if (!string.IsNullOrEmpty(opts.Name))
                modTxt = ManifestEditor.SetValue(modTxt, "mod", "name", QuoteString(opts.Name));
            if (!string.IsNullOrEmpty(opts.Version))
                modTxt = ManifestEditor.SetValue(modTxt, "mod", "version", QuoteString(opts.Version));
            if (!string.IsNullOrEmpty(opts.Description))
                modTxt = ManifestEditor.SetValue(modTxt, "mod", "description", QuoteString(opts.Description));
            if (opts.Priority is int p)
                modTxt = ManifestEditor.SetModPriority(modTxt, p);
            if (opts.ModWorkshopId is int mw && mw > 0)
                modTxt = ManifestEditor.SetUpdatesModworkshop(modTxt, mw);

            // Dependencies — always write the section so the result
            // is deterministic and removing deps actually shows up
            // in the produced archive.
            if (opts.WriteEmptyDependencies
                || opts.RequiredDependencies.Count > 0)
                modTxt = ManifestEditor.SetDependencyList(
                    modTxt, "required", opts.RequiredDependencies);
            if (opts.WriteEmptyDependencies
                || opts.OptionalDependencies.Count > 0)
                modTxt = ManifestEditor.SetDependencyList(
                    modTxt, "optional", opts.OptionalDependencies);

            // [dependency_sources] — non-standard sidecar section
            // mapping dep slug → MW id. Always rewrite (even when
            // empty, which drops a stale section) so the file is
            // deterministic across packs.
            modTxt = ManifestEditor.SetDependencySourcesSection(
                modTxt, opts.DependencySources);

            // Persist mod.txt back into the work dir.
            File.WriteAllText(manifestPath, modTxt);
            result.Log.Add("mod.txt updated.");

            // Build the .vmz with FORWARD-SLASH entry paths.
            var outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            if (File.Exists(outputPath)) File.Delete(outputPath);

            int packed = 0;
            using (var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(
                    workDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(workDir, file)
                        .Replace('\\', '/');
                    // CompressionLevel.Optimal — typical mod
                    // archives are mostly text + small images;
                    // the size win matters more than a couple of
                    // ms of CPU for what's a one-shot package step.
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var fs = File.OpenRead(file);
                    fs.CopyTo(es);
                    packed++;
                }
            }
            result.FilesPacked = packed;
            result.OutputBytes = new FileInfo(outputPath).Length;
            result.Log.Add($"Packed {packed} file(s) → {Path.GetFileName(outputPath)} ({result.OutputBytes:N0} bytes).");
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* best-effort; %TEMP% cleans itself eventually */ }
            }
        }
        return result;
    }

    /// <summary>Quote a string the way Godot ConfigFile expects:
    /// double-quoted, with embedded double-quotes escaped. Bare
    /// values like `version=1.0.0` are technically legal but the
    /// official mods all quote their strings, and the in-game
    /// parser is happier with the explicit form.</summary>
    private static string QuoteString(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>If `dir` contains exactly one subfolder + nothing
    /// else at the root, return that subfolder's path. Otherwise
    /// return `dir` unchanged. Handles the common case where a
    /// downloaded mod archive wraps its content in a versioned
    /// folder.</summary>
    private static string UnwrapSingleTopFolder(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length != 1) return dir;
        var only = entries[0];
        if (!Directory.Exists(only)) return dir;
        // Refuse to unwrap if mod.txt already lives at the outer
        // level — the user explicitly arranged it that way.
        if (File.Exists(Path.Combine(dir, "mod.txt"))) return dir;
        return only;
    }

    private static string MakeTempDir()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "VostokModPacker_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
        catch { return 0; }
    }

    private static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dst));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dst), overwrite: true);
    }
}
