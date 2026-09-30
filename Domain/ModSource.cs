// Where a mod comes from: a provider plus that provider's opaque id.
//
// Mirrors the Metro Mod Loader's "host ref" so the manager and the loader
// agree on identity. The string form is "<provider>:<id>" and is the same
// grammar MML uses for mod.txt's `[updates] source=` value, the
// `[mod_sources]` cache in mod_config.cfg and the `sources` map in a
// modpack profile.json:
//
//     [updates]
//     source="vostokmods:eventmodifier"
//
// VostokMods (vostokmods.net) is the only provider. Its id is the mod's
// slug, the kebab-case name in the mod page URL.

namespace VostokModManager.Domain;

public readonly struct ModSource : IEquatable<ModSource>
{
    public const string VostokMods = "vostokmods";

    /// <summary>Providers a source string may name. Anything else does
    /// not parse: guessing the host is how a mod downloads a stranger's
    /// upload.</summary>
    private static readonly string[] KnownProviders = { VostokMods };

    private readonly string? _provider;
    private readonly string? _id;

    public ModSource(string provider, string id)
    {
        _provider = (provider ?? "").Trim().ToLowerInvariant();
        _id = (id ?? "").Trim();
    }

    /// <summary>No source. Also the default value of the struct.</summary>
    public static ModSource None => default;

    public string Provider => _provider ?? "";
    public string Id => _id ?? "";

    /// <summary>True when both halves are present.</summary>
    public bool IsValid => Provider.Length > 0 && Id.Length > 0;

    /// <summary>"vostokmods:&lt;slug&gt;", or "" when there is no
    /// source. Stable, so it doubles as a cache and dictionary key.</summary>
    public string Key => IsValid ? Provider + ":" + Id : "";

    /// <summary>The mod's page on its host, or "" when there is no
    /// source.</summary>
    public string PageUrl => IsValid && Provider == VostokMods
        ? VostokModsUrl.ModPageUrl(Id)
        : "";

    public static ModSource ForSlug(string slug) => new(VostokMods, slug);

    /// <summary>Parses "&lt;provider&gt;:&lt;id&gt;". Splits on the first
    /// colon only, since ids are opaque. The provider half is
    /// case-insensitive; the id half is kept as written. Returns false
    /// for an unknown provider, an empty half, or a bare value with no
    /// colon.</summary>
    public static bool TryParse(string? key, out ModSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var s = key.Trim();
        var sep = s.IndexOf(':');
        if (sep <= 0) return false;
        var provider = s.Substring(0, sep).Trim().ToLowerInvariant();
        if (Array.IndexOf(KnownProviders, provider) < 0) return false;
        var id = s.Substring(sep + 1).Trim();
        if (id.Length == 0) return false;
        source = new ModSource(provider, id);
        return true;
    }

    /// <summary>Parses a source key, returning <see cref="None"/> when it
    /// does not parse.</summary>
    public static ModSource Parse(string? key)
        => TryParse(key, out var s) ? s : default;

    public bool Equals(ModSource other)
        => string.Equals(Provider, other.Provider, StringComparison.Ordinal)
        && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ModSource o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Provider, Id);
    public static bool operator ==(ModSource a, ModSource b) => a.Equals(b);
    public static bool operator !=(ModSource a, ModSource b) => !a.Equals(b);
    public override string ToString() => Key;
}
