// Tells apart two mods that share a mod_id but are not the same mod.
//
// A mod_id is the loader's identity, and nearly always one id is one
// mod. But ids get reused: "Multi Save Slots" (1.4, abandoned) and
// "Multi Save Slots Reborn" (1.1.0, a different author's successor)
// both declare `multi-save-slots`. Grouped by id alone, the old mod's
// higher version number makes it look like the newest release of the
// new one, so "newest version in the Library" and "old versions to
// clean up" both pick the wrong file.
//
// Two archives with the same mod_id are the same lineage when:
//   • both declare a VostokMods source of the same kind (two slugs or
//     two ids) and the sources match; a slug and an id can't be
//     compared offline, so that pair falls through to the name test;
//   • otherwise, when their display names agree once version numbers,
//     "by <author>" tails and punctuation are stripped. A rename such
//     as "Unload Hotkey" → "Action Hotkey" splits a lineage here, which
//     only costs an extra kept file on clean-up; it never merges two
//     different mods.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class ModLineage
{
    private static readonly Regex VersionToken = new(@"\bv?\d+(?:[.\-_]\d+)+[a-z]?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ByAuthor     = new(@"\bby\s+\S+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DevTag       = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex NonWord      = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex UuidLike     = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The display name reduced to the words that survive a
    /// version bump: lowercase, no version numbers, no "by author", no
    /// "[DEV]" style tags, no punctuation.</summary>
    public static string NormalizeName(string? name)
    {
        var s = (name ?? "").ToLowerInvariant();
        s = DevTag.Replace(s, " ");
        s = ByAuthor.Replace(s, " ");
        s = VersionToken.Replace(s, " ");
        s = NonWord.Replace(s, " ").Trim();
        return s;
    }

    /// <summary>True when two archives sharing a mod_id are releases of
    /// the same mod. Callers must already have matched the mod_id.</summary>
    public static bool Same(ModSource sourceA, string? nameA, ModSource sourceB, string? nameB)
    {
        if (sourceA.IsValid && sourceB.IsValid
            && string.Equals(sourceA.Provider, sourceB.Provider, StringComparison.OrdinalIgnoreCase))
        {
            var ua = UuidLike.IsMatch(sourceA.Id);
            var ub = UuidLike.IsMatch(sourceB.Id);
            // Same source = same mod, whatever it is called now. A
            // differing source is not proof of a different mod (a mod's
            // VostokMods slug can be renamed), so that falls through
            // to the name test like the slug-vs-id case does.
            if (ua == ub && string.Equals(sourceA.Id, sourceB.Id, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var na = NormalizeName(nameA);
        var nb = NormalizeName(nameB);
        if (na.Length == 0 || nb.Length == 0) return true;
        return na == nb;
    }

    /// <summary>Groups library entries into lineages: entries with the
    /// same mod_id that are linked, directly or through other entries,
    /// by <see cref="Same"/>. The chaining matters for a mod that was
    /// renamed and re-slugged over time: the release that carries the
    /// old source with the new name bridges the two halves. Each group
    /// is sorted newest version first.</summary>
    public static List<List<ModLibrary.Entry>> Group(IEnumerable<ModLibrary.Entry> entries)
    {
        var list = entries.Where(e => !string.IsNullOrEmpty(e.ModId)).ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                if (!string.Equals(list[i].ModId, list[j].ModId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Same(list[i].Source, list[i].DisplayName, list[j].Source, list[j].DisplayName)) continue;
                int a = Find(i), b = Find(j);
                if (a != b) parent[a] = b;
            }
        var groups = new Dictionary<int, List<ModLibrary.Entry>>();
        for (int i = 0; i < list.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<ModLibrary.Entry>();
            g.Add(list[i]);
        }
        var result = groups.Values.ToList();
        foreach (var g in result)
            g.Sort((a, b) => ModRegistry.CompareVersions(b.Version, a.Version));
        return result;
    }

    /// <summary>The newest library entry of the lineage a profile entry
    /// belongs to (same mod_id, linked by source or name to the lineage),
    /// or null when no archived version is the same mod.</summary>
    public static ModLibrary.Entry? NewestFor(IEnumerable<ModLibrary.Entry> entries, string modId, ModSource source, string? displayName)
    {
        ModLibrary.Entry? best = null;
        foreach (var g in Group(entries.Where(e => string.Equals(e.ModId, modId, StringComparison.OrdinalIgnoreCase))))
        {
            if (!g.Any(e => Same(source, displayName, e.Source, e.DisplayName))) continue;
            if (best == null || ModRegistry.CompareVersions(g[0].Version, best.Version) > 0) best = g[0];
        }
        return best;
    }
}
