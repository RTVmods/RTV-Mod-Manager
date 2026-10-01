// Reads and writes a profile's profile.json in the mod loader's
// "metroprofile" v1 schema, so a profile saved or exported by the manager
// is also a valid modpack for the loader's Modpacks tab, and a modpack
// made by the loader opens in the manager.
//
//   {
//     "metroprofile": 1,
//     "name": "My profile",
//     "description": "...",                       (optional)
//     "modloader_version": "3.4.1",               (optional)
//     "exported_at": "2026-10-01T09:00:00Z",
//     "enabled":  { "<key>": true, ... },
//     "priority": { "<key>": 0, ... },
//     "sources":  { "<key>": { "provider": "vostokmods", "id": "<slug>", "version": "1.0" } },
//     "vmm": { ... }                              (manager-only, see below)
//   }
//
// <key> is the loader's profile key, "<mod_id>@<version>", the same key
// mod_config.cfg uses. A pack imported from VostokMods keys its mods by
// source instead ("vostokmods:<slug>"), because their mod ids are not
// known until they are downloaded.
//
// The loader requires `metroprofile` == 1, a string `name` and an object
// `enabled`, and ignores keys it does not know. What only the manager
// needs lives under "vmm": display names, the bundled archive's file
// name, the pack a mod came from, and the profile's timestamps.
//
// The manager's earlier profile.json ({"Name": ..., "Mods": [...]}) is
// still read.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VostokModManager.Domain;

public static class MetroProfile
{
    /// <summary>The installed mod loader's version, written into
    /// `modloader_version` when known. Set once at startup.</summary>
    public static string ModloaderVersion { get; set; } = "";

    private const string Iso = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>The profile key for a mod: "id@version", the bare id
    /// when it has no version, or its source key when it has no id.
    /// "" when it has neither.</summary>
    public static string KeyFor(ProfileMod m)
    {
        if (string.IsNullOrEmpty(m.ModId)) return m.SourceRef.Key;
        return string.IsNullOrEmpty(m.Version) ? m.ModId : m.ModId + "@" + m.Version;
    }

    /// <summary>True when the JSON is a metroprofile document.</summary>
    public static bool IsMetro(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, _readOpts);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("metroprofile", out _);
        }
        catch (JsonException) { return false; }
    }

    // ── write ─────────────────────────────────────────────────────────

    public static string Serialize(ModProfile p)
    {
        // One entry per key: a second mod with the same id and version
        // could not be told apart by the loader either.
        var mods = new List<(string Key, ProfileMod Mod)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in p.Mods)
        {
            var key = KeyFor(m);
            if (key.Length > 0 && seen.Add(key)) mods.Add((key, m));
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteNumber("metroprofile", 1);
            w.WriteString("name", p.Name);
            if (!string.IsNullOrEmpty(p.Description))
                w.WriteString("description", p.Description);
            if (!string.IsNullOrEmpty(ModloaderVersion))
                w.WriteString("modloader_version", ModloaderVersion);
            w.WriteString("exported_at", (p.UpdatedAt ?? p.CreatedAt).ToUniversalTime().ToString(Iso));

            w.WriteStartObject("enabled");
            foreach (var (key, m) in mods) w.WriteBoolean(key, m.IsEnabled);
            w.WriteEndObject();

            w.WriteStartObject("priority");
            foreach (var (key, m) in mods) w.WriteNumber(key, m.Priority);
            w.WriteEndObject();

            if (mods.Any(x => x.Mod.SourceRef.IsValid))
            {
                w.WriteStartObject("sources");
                foreach (var (key, m) in mods)
                {
                    var src = m.SourceRef;
                    if (!src.IsValid) continue;
                    w.WriteStartObject(key);
                    w.WriteString("provider", src.Provider);
                    w.WriteString("id", src.Id);
                    if (!string.IsNullOrEmpty(m.Version)) w.WriteString("version", m.Version);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }

            w.WriteStartObject("vmm");
            w.WriteString("created_at", p.CreatedAt.ToUniversalTime().ToString(Iso));
            if (p.UpdatedAt is DateTime updated)
                w.WriteString("updated_at", updated.ToUniversalTime().ToString(Iso));
            w.WriteStartObject("mods");
            foreach (var (key, m) in mods)
            {
                w.WriteStartObject(key);
                w.WriteString("name", m.DisplayName);
                if (!string.IsNullOrEmpty(m.ArchiveFileName)) w.WriteString("archive", m.ArchiveFileName);
                if (!string.IsNullOrEmpty(m.PackName)) w.WriteString("pack", m.PackName);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── read ──────────────────────────────────────────────────────────

    private static readonly JsonDocumentOptions _readOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions _legacyOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses a profile.json of either schema. Returns null
    /// when the text is not a profile.</summary>
    public static ModProfile? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, _readOpts);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("metroprofile", out var ver))
                return ParseMetro(root, ver);
            // The manager's earlier schema.
            var legacy = JsonSerializer.Deserialize<ModProfile>(json, _legacyOpts);
            return legacy != null && legacy.Mods != null ? legacy : null;
        }
        catch (JsonException) { return null; }
    }

    private static ModProfile? ParseMetro(JsonElement root, JsonElement ver)
    {
        // The loader's own checks: version 1, a string name, an
        // `enabled` object.
        if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetDouble(out var v) || (int)v != 1)
            return null;
        if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return null;
        if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.Object)
            return null;

        var priority = Obj(root, "priority");
        var sources = Obj(root, "sources");
        var vmm = Obj(root, "vmm");
        var vmmMods = vmm.HasValue ? Obj(vmm.Value, "mods") : null;

        var p = new ModProfile
        {
            Name = nameEl.GetString() ?? "",
            Description = Str(root, "description"),
        };
        var created = vmm.HasValue ? Date(vmm.Value, "created_at") : null;
        p.CreatedAt = created ?? Date(root, "exported_at") ?? DateTime.UtcNow;
        p.UpdatedAt = vmm.HasValue ? Date(vmm.Value, "updated_at") : null;

        foreach (var prop in enabled.EnumerateObject())
        {
            var key = prop.Name;
            if (key.Length == 0) continue;
            var m = new ProfileMod { IsEnabled = Truthy(prop.Value) };

            var record = sources.HasValue && sources.Value.TryGetProperty(key, out var rec)
                         && rec.ValueKind == JsonValueKind.Object ? rec : (JsonElement?)null;
            var recordVersion = "";
            if (record.HasValue)
            {
                var src = ModSource.Parse(Str(record.Value, "provider") + ":" + Str(record.Value, "id"));
                if (src.IsValid) m.Source = src.Key;
                recordVersion = Str(record.Value, "version");
            }

            if (ModSource.TryParse(key, out var keySource))
            {
                // Keyed by source: the mod id is not known yet.
                if (string.IsNullOrEmpty(m.Source)) m.Source = keySource.Key;
                m.Version = recordVersion;
                m.DisplayName = keySource.Id;
            }
            else
            {
                var at = key.IndexOf('@');
                m.ModId = at > 0 ? key.Substring(0, at) : key;
                m.Version = at > 0 ? key.Substring(at + 1) : "";
                m.DisplayName = m.ModId;
            }

            if (priority.HasValue && priority.Value.TryGetProperty(key, out var pr)
                && pr.ValueKind == JsonValueKind.Number && pr.TryGetDouble(out var prv))
                m.Priority = (int)prv;

            if (vmmMods.HasValue && vmmMods.Value.TryGetProperty(key, out var extra)
                && extra.ValueKind == JsonValueKind.Object)
            {
                var name = Str(extra, "name");
                if (name.Length > 0) m.DisplayName = name;
                m.ArchiveFileName = Str(extra, "archive");
                m.PackName = Str(extra, "pack");
            }
            p.Mods.Add(m);
        }

        // The loader writes its keys sorted, so a file it made carries
        // no order of its own: list those by load order.
        if (!vmmMods.HasValue)
            p.Mods = p.Mods
                .OrderBy(m => m.Priority)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        return p;
    }

    private static JsonElement? Obj(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    private static string Str(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

    private static DateTime? Date(JsonElement parent, string name)
    {
        var s = Str(parent, name);
        return s.Length > 0 && DateTime.TryParse(s, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    private static bool Truthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => e.TryGetDouble(out var d) && d != 0,
        _ => false,
    };
}
