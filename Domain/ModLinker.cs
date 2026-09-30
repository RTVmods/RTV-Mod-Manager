// Matches installed mods that have no source against the VostokMods
// listing, by name. Pure: no UI, no network.
//
// A mod is matched only when its display name or mod id, compared
// ignoring case, spacing and punctuation, equals the name or slug of
// exactly one listed mod, and no other installed mod claims that same
// listing. Anything ambiguous is left for the user to link by hand.

namespace VostokModManager.Domain;

public static class ModLinker
{
    /// <summary>Returns installed-mod → slug for every unambiguous
    /// match. `listing` is (slug, name) for each mod on the host.</summary>
    public static Dictionary<ModEntry, string> Match(
        IEnumerable<ModEntry> unlinked,
        IEnumerable<(string Slug, string Name)> listing)
    {
        // Normalised name or slug → the slugs it could mean.
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string text, string slug)
        {
            var key = Normalize(text);
            if (key.Length == 0) return;
            if (!index.TryGetValue(key, out var set))
                index[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(slug);
        }
        foreach (var (slug, name) in listing)
        {
            if (string.IsNullOrEmpty(slug)) continue;
            Add(slug, slug);
            Add(name, slug);
        }

        var candidates = new List<(ModEntry Entry, string Slug)>();
        foreach (var e in unlinked)
        {
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in new[] { e.DisplayName, e.ModId })
                if (index.TryGetValue(Normalize(text), out var set))
                    slugs.UnionWith(set);
            if (slugs.Count == 1) candidates.Add((e, slugs.First()));
        }

        return candidates
            .GroupBy(c => c.Slug, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.First())
            .ToDictionary(c => c.Entry, c => c.Slug);
    }

    /// <summary>Lowercase letters and digits only.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = new char[text.Length];
        var n = 0;
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) chars[n++] = char.ToLowerInvariant(c);
        return new string(chars, 0, n);
    }
}
