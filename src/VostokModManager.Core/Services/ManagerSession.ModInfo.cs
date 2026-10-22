// A mod's VostokMods description (cached in settings.json) and the
// changelog it ships inside its archive. Data only: rendering the
// markdown is the front end's job.

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>File names checked for a changelog, in priority order:
    /// CHANGELOG first (the conventional name), README as a fallback for
    /// mods that put everything in one file, CHANGES for older ones.</summary>
    public static readonly IReadOnlyList<string> ChangelogCandidates = new[]
    {
        "CHANGELOG.md", "CHANGELOG.txt",
        "README.md", "README.txt",
        "CHANGES.md", "CHANGES.txt",
    };

    /// <summary>The last fetched description, or "" when none is cached
    /// (the view then fetches one right away).</summary>
    public string GetCachedDescription(ModEntry e)
        => e.Source.IsValid && Settings.CachedDescriptions.TryGetValue(e.Source.Key, out var cached)
            ? cached ?? ""
            : "";

    /// <summary>The header line of the description view.</summary>
    public static string DescriptionHeader(ModEntry e)
        => $"{EntryLabel(e)} (v{e.Version})"
            + (e.Source.IsValid ? $"  ·  VostokMods {e.Source.Id}" : "  ·  no VostokMods link");

    /// <summary>Fetches the mod's description from VostokMods and caches
    /// it in settings.json so the next open is instant.</summary>
    public async Task<DescriptionResult> FetchDescriptionAsync(ModEntry e)
    {
        if (!e.Source.IsValid)
            return new DescriptionResult(false,
                "(no description available — mod isn't linked to VostokMods)",
                "Not linked to VostokMods — link the mod from its right-click menu.");
        try
        {
            var details = await FetchModDetailAsync(e.Source.Id);
            var desc = details.Description?.Trim() ?? "";
            Settings.CachedDescriptions[e.Source.Key] = desc;
            try { Settings.Save(); }
            catch { /* the description cache is best-effort */ }
            return new DescriptionResult(true,
                string.IsNullOrEmpty(desc) ? "(VostokMods returned no description for this mod.)" : desc,
                "Last fetched just now" + (string.IsNullOrEmpty(details.Author) ? "" : $" · author: {details.Author}"));
        }
        catch (Exception ex)
        {
            return new DescriptionResult(false, GetCachedDescription(e), $"Fetch failed: {ex.Message}");
        }
    }

    /// <summary>True when the archive holds one of the changelog files
    /// at its root (so "Show changelog…" can be disabled otherwise).</summary>
    public static bool HasInternalChangelog(ModEntry e)
    {
        if (!e.IsArchive) return false;
        foreach (var name in ChangelogCandidates)
            if (e.Files.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    /// <summary>Reads the first non-empty changelog file from the mod's
    /// archive. Tells the user and returns null when there is none (and
    /// returns null silently for a folder mod). No network.</summary>
    public ModChangelog? ReadChangelog(ModEntry e)
    {
        if (!e.IsArchive) return null;
        foreach (var name in ChangelogCandidates)
        {
            // The stored name may differ in case; read by the stored
            // name so the zip's exact-match lookup hits.
            var match = e.Files.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match)) continue;
            try
            {
                var text = e.ReadFileText(match);
                if (!string.IsNullOrWhiteSpace(text))
                    return new ModChangelog(e.DisplayName, match, text);
            }
            catch { /* try the next candidate */ }
        }
        Prompt.Inform("No changelog",
            $"`{e.DisplayName}` doesn't ship a CHANGELOG / README file the manager can find. Try the "
            + "mod's VostokMods page for release notes.");
        return null;
    }
}
