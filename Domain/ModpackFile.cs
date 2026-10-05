// A modpack file: the zip the mod loader lists on its Modpacks tab, and
// the form a VostokMods modpack takes once the loader has imported it.
//
//   <pack>.zip
//     profile.json      metroprofile v1 (see MetroProfile.cs), mods keyed
//                       by source ("vostokmods:<slug>") with the version
//                       each one is pinned to
//     MCM/...           optional mod settings, copied as-is
//
// Nothing else goes in: a modpack names mods, it does not carry them.
// The loader downloads each mod from its source when the pack is
// applied, and the manager does the same when the file is dropped on it.

using System.IO.Compression;
using System.Text.Json;

namespace VostokModManager.Domain;

public static class ModpackFile
{
    /// <summary>One mod in a pack. `Version` is the version the pack
    /// pins ("" = newest); `LoadOrder` starts at 1.</summary>
    public record Entry(
        string Slug,
        string Name,
        string Version,
        int LoadOrder,
        bool Enabled = true,
        string Sha256 = "");

    /// <summary>True when the zip is a modpack: a `profile.json` at
    /// its root. The loader uses the same test.</summary>
    public static bool IsModpackZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("profile.json") != null;
        }
        catch { return false; }
    }

    /// <summary>Writes the pack. `mcmFiles` maps a path under `MCM/`
    /// to its text.</summary>
    public static void Write(
        string path,
        string name,
        string description,
        string author,
        IEnumerable<Entry> mods,
        IReadOnlyDictionary<string, string>? mcmFiles = null)
    {
        var list = mods.Where(m => !string.IsNullOrWhiteSpace(m.Slug)).ToList();
        var profile = new ModProfile
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Mods = list.Select(m => new ProfileMod
            {
                ModId = "",
                DisplayName = string.IsNullOrEmpty(m.Name) ? m.Slug : m.Name,
                Version = m.Version,
                IsEnabled = m.Enabled,
                Priority = Math.Clamp(m.LoadOrder, -999, 999),
                Source = ModSource.ForSlug(m.Slug).Key,
            }).ToList(),
        };
        var json = MetroProfile.Serialize(profile);
        json = AddTopLevel(json, author, list);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("profile.json", CompressionLevel.Optimal);
        using (var sw = new StreamWriter(entry.Open()))
            sw.Write(json);
        if (mcmFiles == null) return;
        foreach (var (rel, text) in mcmFiles)
        {
            var clean = rel.Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0 || clean.Contains("..")) continue;
            var e = zip.CreateEntry("MCM/" + clean, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open());
            w.Write(text);
        }
    }

    /// <summary>Adds the pack-only keys the profile writer has no
    /// field for: `author` and per-mod `checksums`.</summary>
    private static string AddTopLevel(string json, string author, List<Entry> mods)
    {
        var checksums = mods
            .Where(m => m.Sha256.Length == 64)
            .ToDictionary(m => ModSource.ForSlug(m.Slug).Key, m => m.Sha256.ToLowerInvariant());
        if (string.IsNullOrEmpty(author) && checksums.Count == 0) return json;

        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                p.WriteTo(w);
                // Keep the loader's reading order: name, then author.
                if (p.NameEquals("name") && !string.IsNullOrEmpty(author))
                    w.WriteString("author", author);
            }
            if (checksums.Count > 0)
            {
                w.WriteStartObject("checksums");
                foreach (var (k, v) in checksums) w.WriteString(k, v);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Reads a modpack zip into an import list: one entry per
    /// mod, with its source, pinned version and load order, and the
    /// pack's checksums. Returns false when the zip is not a modpack.
    /// A mod the pack lists without a source can't be downloaded and
    /// is left out; `skipped` names them.</summary>
    public static bool TryRead(string path, out ModListImport import, out List<string> skipped)
    {
        import = new ModListImport();
        skipped = new List<string>();
        string json;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var e = zip.GetEntry("profile.json");
            if (e == null) return false;
            using var r = new StreamReader(e.Open());
            json = r.ReadToEnd();
        }
        catch { return false; }

        var profile = MetroProfile.Parse(json);
        if (profile == null) return false;

        import.Name = profile.Name.Length > 0 ? profile.Name : Path.GetFileNameWithoutExtension(path);
        import.Description = profile.Description;
        var order = 0;
        foreach (var m in profile.Mods.OrderBy(m => m.Priority))
        {
            order++;
            var src = m.SourceRef;
            var label = m.DisplayName.Length > 0 ? m.DisplayName
                : (m.ModId.Length > 0 ? m.ModId : src.Id);
            if (!src.IsValid)
            {
                skipped.Add(label);
                continue;
            }
            import.Mods.Add(new ImportEntry
            {
                ModId = m.ModId,
                DisplayName = label,
                Source = src.Key,
                Version = m.Version,
                IsEnabled = m.IsEnabled,
                Priority = m.Priority,
            });
        }
        import.PinVersions = import.Mods.Any(e => !string.IsNullOrEmpty(e.Version));

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("checksums", out var cs) && cs.ValueKind == JsonValueKind.Object)
                foreach (var p in cs.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && ModSource.TryParse(p.Name, out var key))
                        import.Checksums[key.Key] = (p.Value.GetString() ?? "").ToLowerInvariant();
        }
        catch (JsonException) { /* no checksums */ }
        return true;
    }
}
