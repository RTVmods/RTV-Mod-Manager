// Linking installed mods to their VostokMods pages (by hand, or in bulk
// by exact name match), and the shared mod.txt edit helper the link and
// dependency flows write through.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>Reads the mod's mod.txt, applies `transform` and writes
    /// it back, handling the archive vs folder split and the .bak backup
    /// in one place. Returns "Backup: &lt;file&gt;" on success, or null
    /// after telling the user what failed.</summary>
    public async Task<string?> EditModTxtAsync(ModEntry e, Func<string, string> transform)
    {
        try
        {
            if (e.IsArchive)
            {
                string oldText;
                using (var arch = new ModArchive())
                {
                    if (!arch.Open(e.Path))
                        throw new IOException("Failed to open the .vmz archive for reading.");
                    oldText = arch.ReadText("mod.txt");
                    if (string.IsNullOrEmpty(oldText))
                        throw new InvalidDataException("mod.txt is missing or empty inside the archive.");
                }
                var newText = transform(oldText);
                var backup = ZipPatcher.CreateBackup(e.Path);
                ZipPatcher.ReplaceEntry(e.Path, "mod.txt", newText);
                return $"Backup: {Path.GetFileName(backup)}";
            }
            else
            {
                var modTxtPath = Path.Combine(e.Path, "mod.txt");
                if (!File.Exists(modTxtPath))
                    throw new FileNotFoundException("mod.txt not found in the mod folder.", modTxtPath);
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var bakPath = $"{modTxtPath}.{stamp}.bak";
                File.Copy(modTxtPath, bakPath, overwrite: false);
                var newText = transform(await File.ReadAllTextAsync(modTxtPath));
                await File.WriteAllTextAsync(modTxtPath, newText);
                return $"Backup: {Path.GetFileName(bakPath)}";
            }
        }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't update mod.txt", ex);
            return null;
        }
    }

    /// <summary>Asks for the mod's VostokMods page (URL or slug) and
    /// links the mod to it; see the overload taking the input.</summary>
    public async Task<bool> LinkModAsync(ModEntry e)
    {
        var current = e.Source.IsValid ? e.Source.PageUrl : "";
        var input = Prompt.AskText("Link to VostokMods",
            $"Paste the VostokMods page for `{e.DisplayName}`.\n\n"
            + "Accepts the mod page URL (e.g. https://vostokmods.net/mod/eventmodifier) or just its "
            + "slug (eventmodifier).\n\n"
            + "The manager uses the link to check for updates and download them.",
            current);
        if (input == null) return false;
        return await LinkModAsync(e, input);
    }

    /// <summary>Checks the page exists and links the mod to it. The link
    /// is recorded in mod_config.cfg's [mod_sources], next to the ones
    /// the in-game loader keeps, so the archive is left alone — except
    /// for a mod whose mod.txt already declares a source: that outranks
    /// the record, so it is the one rewritten (archives are backed up to
    /// a .bak first). Rescans and re-checks updates on success.</summary>
    public async Task<bool> LinkModAsync(ModEntry e, string input)
    {
        if (!VostokModsUrl.TryParseSlug(input, out var slug))
        {
            Prompt.Inform("Invalid input",
                $"That isn't a VostokMods mod:\n  {input}\n\n"
                + "Expected a URL like https://vostokmods.net/mod/eventmodifier or the slug on its "
                + "own. Numeric ids and links to other sites are not accepted.",
                PromptKind.Warning);
            return false;
        }

        // Confirm the page exists before recording it, and take the
        // slug as the site spells it.
        VmModDetail detail;
        try { detail = await FetchModDetailAsync(slug); }
        catch (VostokModsException ex)
        {
            Prompt.Inform("Not linked",
                ex.Kind == VostokModsError.NotFound
                    ? $"VostokMods has no mod at `{slug}`. Check the URL and try again."
                    : $"Couldn't reach VostokMods to check `{slug}`:\n{ex.Message}",
                PromptKind.Warning);
            return false;
        }
        var source = detail.Source;
        if (detail.Matches(e.Source))
        {
            Say(StatusArea.Mods, $"`{e.DisplayName}` is already linked to {source.PageUrl}.");
            return true;
        }

        var note = "";
        if (e.DeclaredSource.IsValid)
        {
            var bak = await EditModTxtAsync(e, txt => ManifestEditor.SetUpdatesSource(txt, source));
            if (bak == null) return false;
            note = " " + bak;
        }
        else
        {
            Config.SetModSource(e.ModId, e.Version, source);
            if (!SaveModConfigSafely()) return false;
        }
        Say(StatusArea.Mods, $"Linked `{e.DisplayName}` → {detail.Name} ({source.PageUrl}).{note}");

        // Re-scan + re-check updates so the new link kicks in.
        Refresh();
        await CheckUpdatesAsync(forceFresh: true);
        return true;
    }

    /// <summary>Links installed mods with no source to their VostokMods
    /// pages by matching names against the site's listing. Only
    /// unambiguous exact-name matches are proposed, and nothing is
    /// recorded until the user confirms the list. Returns how many mods
    /// were linked.</summary>
    public async Task<int> AutoLinkModsAsync()
    {
        var unlinked = UnlinkedMods();
        if (unlinked.Count == 0)
        {
            Say(StatusArea.Updates, "Updates: every installed mod is linked to VostokMods.");
            return 0;
        }

        Say(StatusArea.Updates, "Updates: reading the VostokMods mod list ...");
        List<VmModSummary> listing;
        try { listing = await FetchModListingAsync(); }
        catch (Exception ex)
        {
            Say(StatusArea.Updates, $"Updates: couldn't read the VostokMods mod list — {ex.Message}");
            return 0;
        }
        foreach (var m in listing)
            if (m.Id.Length > 0) VmSlugById[m.Id] = m.Slug;

        var matches = ModLinker.Match(unlinked, listing.Select(m => (m.Slug, m.Name)));
        var unmatched = unlinked.Count - matches.Count;
        const string byHand = "Right-click a mod → Link to VostokMods… to link it by hand.";
        if (matches.Count == 0)
        {
            Say(StatusArea.Updates, $"Updates: {SummarizeUpdates()}");
            Prompt.Inform("Link to VostokMods",
                $"None of the {unlinked.Count} unlinked mod" + (unlinked.Count == 1 ? "" : "s")
                + " has an exact name match on VostokMods.\n\n" + byHand);
            return 0;
        }

        var ordered = matches
            .OrderBy(m => m.Key.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        const int shown = 20;
        var lines = string.Join("\n", ordered.Take(shown).Select(m =>
            $"   {(string.IsNullOrEmpty(m.Key.DisplayName) ? m.Key.ModId : m.Key.DisplayName)}"
            + $"  →  vostokmods.net/mod/{m.Value}"));
        if (ordered.Count > shown) lines += $"\n   …and {ordered.Count - shown} more";
        var yes = Prompt.Confirm("Link to VostokMods",
            $"Found a VostokMods page with the same name for {matches.Count} of "
            + $"{unlinked.Count} unlinked mod" + (unlinked.Count == 1 ? "" : "s") + ":\n\n"
            + lines + "\n\n"
            + "Link them? The mod files are not changed, and any link can be changed later from "
            + "the mod's right-click menu."
            + (unmatched > 0
                ? $"\n\n{unmatched} other mod" + (unmatched == 1 ? " has" : "s have")
                  + " no exact name match and will stay unlinked."
                : ""));
        if (!yes)
        {
            Say(StatusArea.Updates, $"Updates: {SummarizeUpdates()}");
            return 0;
        }

        foreach (var (entry, slug) in matches)
            Config.SetModSource(entry.ModId, entry.Version, ModSource.ForSlug(slug));
        if (!SaveModConfigSafely()) return 0;

        Refresh();
        Say(StatusArea.Mods, $"Linked {matches.Count} mod" + (matches.Count == 1 ? "" : "s")
            + " to VostokMods."
            + (unmatched > 0 ? $" {unmatched} still unlinked. {byHand}" : ""));
        await CheckUpdatesAsync(forceFresh: true);
        return matches.Count;
    }
}
