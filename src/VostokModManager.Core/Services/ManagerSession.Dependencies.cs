// Mod dependencies: what a mod declares and the state of each, editing
// a mod's required list, the dependency-filter view of the grid, and
// resolving the missing dependencies of freshly installed mods (from
// the library first, then by download from VostokMods).

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>One dependency a freshly installed mod declares that is
/// neither installed nor in the library. The resolver fills
/// `SourceInput` (from the parent's [dependency_sources] when it has
/// one, else the user types it) and updates `Status`.</summary>
public sealed class MissingDependency
{
    public string ModId { get; init; } = "";
    /// <summary>The mod that declared the dependency.</summary>
    public string ParentName { get; init; } = "";
    public bool Required { get; init; }
    /// <summary>A VostokMods page URL or slug.</summary>
    public string SourceInput { get; set; } = "";
    public MissingDependencyStatus Status { get; set; } = MissingDependencyStatus.Pending;
    public string StatusText { get; set; } = "Pending";
    /// <summary>The installed file, once downloaded.</summary>
    public string InstalledPath { get; set; } = "";

    public string Kind => Required ? "required" : "optional";
}

public enum MissingDependencyStatus { Pending, Ready, Invalid, Downloading, Installed, Failed }

/// <summary>The Missing Dependencies dialog without the dialog: the
/// rows, per-row and download-all actions, and a log. The front end
/// renders `Rows` and `Log`, lets the user edit SourceInput, and calls
/// DownloadAsync / DownloadAllAsync.</summary>
public sealed class MissingDependencyResolver
{
    private readonly ManagerSession _session;

    public IReadOnlyList<MissingDependency> Rows { get; }
    public int LibraryResolved { get; }

    /// <summary>True once at least one dependency was installed.</summary>
    public bool AnyDownloaded { get; private set; }

    public List<string> Log { get; } = new();

    /// <summary>Raised after a row's status or the log changes.</summary>
    public event Action? Changed;

    internal MissingDependencyResolver(
        ManagerSession session, IEnumerable<MissingDependency> rows, int libraryResolved)
    {
        _session = session;
        LibraryResolved = libraryResolved;
        // Required first (the user cares more), then alphabetical.
        Rows = rows
            .OrderByDescending(d => d.Required)
            .ThenBy(d => d.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int RequiredCount => Rows.Count(r => r.Required);
    public int OptionalCount => Rows.Count - RequiredCount;

    public string Intro =>
        $"{RequiredCount} required + {OptionalCount} optional dependency entries weren't found in "
        + "your installed mods or your local library. Paste a VostokMods mod page URL or slug into "
        + "the row and click Download. Use “Find” to open a browser search if you don't have the "
        + "URL handy.";

    /// <summary>The VostokMods search page for a dependency's id (the
    /// row's Find button). The id often differs from the display name,
    /// but it is a useful starting term.</summary>
    public static string SearchUrl(string modId)
        => VostokModsUrl.ExploreModsUrl + "?q=" + Uri.EscapeDataString(modId);

    /// <summary>Reads a row's input as a VostokMods mod page URL or
    /// slug. A bare number or a URL on another site is rejected: a slug
    /// is a name, and only vostokmods.net hosts mods.</summary>
    public static bool TryParseSourceInput(string? input, out ModSource source)
    {
        source = ModSource.None;
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.All(char.IsDigit)) return false;
        if (!VostokModsUrl.TryParseSlug(s, out var slug)) return false;
        source = ModSource.ForSlug(slug);
        return true;
    }

    /// <summary>Downloads one row from its SourceInput. True when it
    /// was installed.</summary>
    public async Task<bool> DownloadAsync(MissingDependency row)
    {
        if (!TryParseSourceInput(row.SourceInput, out var source))
        {
            row.Status = MissingDependencyStatus.Invalid;
            if (string.IsNullOrWhiteSpace(row.SourceInput))
                row.StatusText = "(paste URL or slug first)";
            else
            {
                row.StatusText = "(not a VostokMods URL)";
                Write($"✗ {row.ModId}: “{row.SourceInput.Trim()}” isn't a VostokMods mod page URL "
                    + "or slug. Expected something like " + VostokModsUrl.ModPageUrl("mod-name")
                    + " or just the slug.");
            }
            Changed?.Invoke();
            return false;
        }
        return await DoDownloadAsync(row, source);
    }

    /// <summary>Downloads every row whose input parses (the "Download
    /// all filled rows" button), skipping ones already installed.</summary>
    public async Task<int> DownloadAllAsync()
    {
        var n = 0;
        foreach (var row in Rows)
        {
            if (row.Status == MissingDependencyStatus.Installed) continue;
            if (!TryParseSourceInput(row.SourceInput, out var source)) continue;
            if (await DoDownloadAsync(row, source)) n++;
        }
        return n;
    }

    private async Task<bool> DoDownloadAsync(MissingDependency row, ModSource source)
    {
        row.Status = MissingDependencyStatus.Downloading;
        row.StatusText = "Downloading…";
        Write($"⬇ {row.ModId} ({source.Id}) …");
        try
        {
            // The dependency's id names the live file, so it is
            // recognisable whatever the author called the upload.
            var modsDir = _session.ModsDir;
            var fileName = $"{ModProfile.SafeFileName(row.ModId)}.vmz";
            var dest = Path.Combine(modsDir, fileName);
            if (File.Exists(dest))
                // Don't clobber whatever is there; bump to a unique name.
                dest = Path.Combine(modsDir, MakeUniqueName(modsDir, fileName));
            await _session.DownloadDependencyFileAsync(source.Id, dest);
            DownloadedSources.Record(dest, source);

            // Library snapshot so future profile switches don't have
            // to download it again.
            try { ModLibrary.Add(modsDir, dest); } catch { }

            row.Status = MissingDependencyStatus.Installed;
            row.StatusText = "✓ Installed";
            row.InstalledPath = dest;
            AnyDownloaded = true;
            Write($"  ✓ → {Path.GetFileName(dest)}");
            return true;
        }
        catch (Exception ex)
        {
            row.Status = MissingDependencyStatus.Failed;
            row.StatusText = "✗ Failed";
            Write($"  ✗ {ex.Message}");
            return false;
        }
    }

    private static string MakeUniqueName(string modsDir, string filename)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}{ext}";
            if (!File.Exists(Path.Combine(modsDir, candidate))) return candidate;
        }
        return $"{baseName}_{Guid.NewGuid():N}{ext}";
    }

    private void Write(string line)
    {
        Log.Add(line);
        Changed?.Invoke();
    }
}

public sealed partial class ManagerSession
{
    /// <summary>Downloads a dependency's newest file (the resolver's
    /// network call, through the same seam as updates).</summary>
    internal Task DownloadDependencyFileAsync(string slug, string savePath)
        => DownloadModFileAsync(slug, savePath, null);

    // ── a mod's declared dependencies ─────────────────────────────────

    /// <summary>A mod's declared required/optional dependencies, each
    /// classified enabled / disabled / not installed (the Dependencies
    /// dialog).</summary>
    public DependencyReport GetDependencies(ModEntry subject)
    {
        // First-wins on duplicate ids: the registry shouldn't have any.
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            byId.TryAdd(entry.ModId, entry);
        }
        var required = subject.RequiredDependencies;
        var optional = subject.OptionalDependencies;
        var label = EntryLabel(subject);
        var header = required.Count == 0 && optional.Count == 0
            ? $"`{label}` doesn't declare any dependencies in its mod.txt.\n\n"
              + "Mod authors can add a `[dependencies]` section with `required = a, b, c` to opt in."
            : $"`{label}` declares {required.Count} required and {optional.Count} optional "
              + "dependency entries:";
        var rows = new List<DependencyView>();
        foreach (var (deps, isRequired) in new[] { (required, true), (optional, false) })
            foreach (var depId in deps)
                rows.Add(new DependencyView(depId, isRequired,
                    byId.TryGetValue(depId, out var dep)
                        ? dep.IsEnabled ? DependencyState.Enabled : DependencyState.Disabled
                        : DependencyState.NotInstalled));
        return new DependencyReport(label, header, rows);
    }

    /// <summary>The mod ids the grid narrows to while the Dependencies
    /// view is open: every mod (in the active profile, or all of them
    /// before profiles are adopted) that declares a dependency, plus
    /// every mod those declarations name. Deps that aren't installed
    /// simply don't match a row.</summary>
    public HashSet<string> BuildDependencyFilterSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileIds = ActiveProfile != null
            ? new HashSet<string>(ActiveProfile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var entry in Registry.Entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)) continue;
            if (profileIds != null && !profileIds.Contains(entry.ModId)) continue;
            var required = entry.RequiredDependencies;
            var optional = entry.OptionalDependencies;
            if (required.Count == 0 && optional.Count == 0) continue;
            set.Add(entry.ModId);
            foreach (var d in required) set.Add(d);
            foreach (var d in optional) set.Add(d);
        }
        return set;
    }

    // ── editing a mod's required dependencies ─────────────────────────

    /// <summary>The required-dependency picker's list: every installed
    /// mod id but the subject's own, alphabetical, ticked where already
    /// required. Declared deps that aren't installed are kept, at the
    /// top, so the declaration survives a round-trip.</summary>
    public List<DependencyChoice> GetDependencyChoices(ModEntry subject)
    {
        var installed = Registry.Entries
            .Select(e => e.ModId)
            .Where(id => !string.IsNullOrEmpty(id)
                && !string.Equals(id, subject.ModId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var current = new HashSet<string>(subject.RequiredDependencies, StringComparer.OrdinalIgnoreCase);
        var choices = installed.Select(id => new DependencyChoice(id, current.Contains(id), true)).ToList();
        foreach (var dep in current)
            if (!installed.Contains(dep, StringComparer.OrdinalIgnoreCase))
                choices.Insert(0, new DependencyChoice(dep, true, false));
        return choices;
    }

    /// <summary>Rewrites `[dependencies] required = …` in the mod's
    /// mod.txt with `newDeps` (empty clears it). When a newly added dep
    /// loads at or after the mod, offers to raise the mod's priority
    /// above it (in mod_config.cfg). True when mod.txt was written.</summary>
    public async Task<bool> SetModDependenciesAsync(ModEntry e, IReadOnlyCollection<string> newDeps)
    {
        // Set comparison: the picker returns ticks in click order, and
        // order doesn't matter for deps anyway.
        var existing = new HashSet<string>(e.RequiredDependencies, StringComparer.OrdinalIgnoreCase);
        var picked = new HashSet<string>(newDeps, StringComparer.OrdinalIgnoreCase);
        if (existing.SetEquals(picked))
        {
            Say(StatusArea.Mods, $"`{e.DisplayName}` dependencies unchanged.");
            return false;
        }

        // Among the deps just added, an installed one at priority >= the
        // subject's would load first the wrong way round: offer to bump
        // the subject above the highest. Pre-existing violations are
        // already in the conflicts list.
        int? bumpTo = null;
        var added = picked.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0)
        {
            var registryById = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Registry.Entries)
            {
                if (string.IsNullOrEmpty(entry.ModId)) continue;
                registryById.TryAdd(entry.ModId, entry);
            }
            var blockers = added
                .Where(id => registryById.ContainsKey(id))
                .Select(id => registryById[id])
                .Where(d => d.Priority >= e.Priority)
                .ToList();
            if (blockers.Count > 0)
            {
                var maxBlocker = blockers.OrderByDescending(d => d.Priority).First();
                var newPrio = maxBlocker.Priority + 1;
                var blockerNames = string.Join(", ",
                    blockers.OrderByDescending(d => d.Priority).Select(d => $"{d.ModId} (prio {d.Priority})"));
                var choice = Prompt.Choose("Bump priority?",
                    $"`{e.DisplayName}` is at priority {e.Priority}, but now requires:\n  {blockerNames}\n\n"
                    + "Dependents must load AFTER their dependencies, so "
                    + $"`{e.DisplayName}`'s priority needs to be > {maxBlocker.Priority}.\n\n"
                    + $"Bump `{e.DisplayName}`'s priority {e.Priority} → {newPrio} as part of this edit?",
                    new[] { $"Bump to {newPrio}", "Keep the priority" });
                if (choice < 0) return false;
                if (choice == 0) bumpTo = newPrio;
                // Keep → deps only; the conflicts list will show the
                // dependency_order violation.
            }
        }

        // Deps live in mod.txt (no cfg equivalent), priority in
        // mod_config.cfg: two writes.
        var bak = await EditModTxtAsync(e, txt => ManifestEditor.SetDependencyList(txt, "required", newDeps));
        if (bak == null) return false;
        if (bumpTo.HasValue && !string.IsNullOrEmpty(e.ModId))
        {
            Config.SetPriority(e.ModId, e.Version, bumpTo.Value);
            SaveModConfigSafely();
        }
        var depsMsg = newDeps.Count == 0
            ? $"Cleared `{e.DisplayName}` required dependencies."
            : $"Set `{e.DisplayName}` required dependencies ({newDeps.Count}).";
        var prioMsg = bumpTo.HasValue ? $" Priority {e.Priority} → {bumpTo.Value}." : "";
        Say(StatusArea.Mods, $"{depsMsg}{prioMsg} {bak}");

        // Dependency edits can satisfy or break missing_dependency /
        // dependency_order conflicts elsewhere.
        Refresh();
        return true;
    }

    // ── missing dependencies of freshly installed mods ────────────────

    /// <summary>For each freshly installed mod, reconciles its declared
    /// required/optional dependencies against the live mods and the
    /// library. Library copies are put in place automatically; the rest
    /// are offered for download: after the user agrees, `showResolver`
    /// presents the resolver (the front end's Missing Dependencies view)
    /// and completes when the user closes it. Without one, the rows
    /// whose source the parent mod declared are downloaded directly.
    /// Newly downloaded deps are checked the same way in turn.</summary>
    public async Task CheckAndPromptMissingDepsAsync(
        IEnumerable<string> newlyInstalledModIds,
        Func<MissingDependencyResolver, Task>? showResolver = null)
    {
        var ids = newlyInstalledModIds
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        var liveById = LiveById();

        // Library snapshot keyed by mod id (newest version per id).
        var libByMod = new Dictionary<string, ModLibrary.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var le in ModLibrary.List(ModsDir))
        {
            if (string.IsNullOrEmpty(le.ModId)) continue;
            if (!libByMod.TryGetValue(le.ModId, out var cur)
                || ModRegistry.CompareVersions(le.Version, cur.Version) > 0)
                libByMod[le.ModId] = le;
        }

        // Every (dep, parent, required?) from the new mods, deduped on
        // the dep id (the first parent naming it is the "needed by").
        // Parents' [dependency_sources] are gathered too: a mod packed
        // with the Mod Packager carries the source of every declared
        // dep, so it can be downloaded without asking for a URL.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string DepId, string ParentName, bool Required)>();
        var knownSources = new Dictionary<string, ModSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var modId in ids)
        {
            if (!liveById.TryGetValue(modId, out var entry)) continue;
            var parentLabel = string.IsNullOrEmpty(entry.DisplayName) ? entry.ModId : entry.DisplayName;
            foreach (var dep in entry.RequiredDependencies)
                if (seen.Add(dep)) pending.Add((dep, parentLabel, true));
            foreach (var dep in entry.OptionalDependencies)
                if (seen.Add(dep)) pending.Add((dep, parentLabel, false));
            foreach (var kvp in entry.DependencySources)
                knownSources[kvp.Key] = kvp.Value;
        }
        if (pending.Count == 0) return;

        // First pass: from the library. Skip deps already live.
        var libraryResolved = 0;
        var missing = new List<MissingDependency>();
        foreach (var (depId, parent, required) in pending)
        {
            if (liveById.ContainsKey(depId)) continue;
            if (libByMod.TryGetValue(depId, out var libEntry))
            {
                try
                {
                    var dst = Path.Combine(ModsDir, $"{ModProfile.SafeFileName(depId)}.vmz");
                    if (!File.Exists(dst))
                    {
                        File.Copy(libEntry.Path, dst, overwrite: false);
                        libraryResolved++;
                    }
                    continue;
                }
                catch { /* fall through into "missing" — the user can retry */ }
            }
            var row = new MissingDependency { ModId = depId, ParentName = parent, Required = required };
            // Pre-fill the slug the parent's [dependency_sources] recorded.
            if (knownSources.TryGetValue(depId, out var known) && known.IsValid)
            {
                row.SourceInput = known.Id;
                row.Status = MissingDependencyStatus.Ready;
                row.StatusText = "Ready (auto-filled)";
            }
            missing.Add(row);
        }

        // Rescan when anything came from the library, then drop rows a
        // freshly copied file now satisfies.
        if (libraryResolved > 0)
        {
            Refresh();
            liveById = LiveById();
            missing = missing.Where(m => !liveById.ContainsKey(m.ModId)).ToList();
        }

        if (missing.Count == 0)
        {
            if (libraryResolved > 0)
                Say(StatusArea.Mods, $"Auto-resolved {libraryResolved} dependency mod"
                    + (libraryResolved == 1 ? "" : "s") + " from your library.");
            return;
        }

        // Ask first, rather than popping a busy view without warning.
        int reqCount = missing.Count(m => m.Required);
        int optCount = missing.Count - reqCount;
        var question = "The freshly-installed mod" + (ids.Count == 1 ? "" : "s") + " declared "
            + (reqCount > 0 ? $"{reqCount} required" : "")
            + (reqCount > 0 && optCount > 0 ? " + " : "")
            + (optCount > 0 ? $"{optCount} optional" : "")
            + " dependency mod" + ((reqCount + optCount) == 1 ? "" : "s")
            + " that aren't installed"
            + (libraryResolved > 0
                ? $" ({libraryResolved} other dep(s) auto-copied from your library)."
                : ".")
            + "\n\nOpen the Resolve dialog to paste VostokMods URLs and download them?";
        if (!Prompt.Confirm("Missing dependencies", question)) return;

        var resolver = new MissingDependencyResolver(this, missing, libraryResolved);
        if (showResolver != null) await showResolver(resolver);
        else await resolver.DownloadAllAsync();

        if (resolver.AnyDownloaded)
        {
            Refresh();
            // Downloaded deps may declare deps of their own. Recursing
            // ends once every declared dep is present.
            var newIds = Registry.Entries
                .Where(e => missing.Any(m => string.Equals(m.ModId, e.ModId, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.ModId)
                .ToList();
            if (newIds.Count > 0)
                await CheckAndPromptMissingDepsAsync(newIds, showResolver);
        }
    }

    private Dictionary<string, ModEntry> LiveById()
    {
        var map = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) map[e.ModId] = e;
        return map;
    }
}
