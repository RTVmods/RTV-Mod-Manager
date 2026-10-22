// The Mod Packager (ported from Ui/ModPackagerDialog): the mod creator's
// "pack me a .vmz" tool. Takes a source (an existing .vmz / .zip OR a
// folder), holds the editable manifest fields (mod_id, name, version,
// priority, VostokMods source, description, author) and a dependency
// picker, then writes a fresh .vmz with the edits applied. The same mod
// set can also go out as a mod-pack JSON or a mod loader modpack zip.
//
// The dependency picker has two complementary modes:
//   1) Ticks against every installed mod in the registry, so common
//      deps (MCM, the various API libs, etc.) are a single click each.
//   2) Free-form text for mods the author depends on but doesn't have
//      installed (a VostokMods page URL or a mod_id, one per line or
//      CSV).
// Both merge into the same Required / Optional buckets the packed
// mod.txt's [dependencies] section ends up with. The source is never
// modified; the output is a separate .vmz.
//
// Everything the form edited lives in PackagerState; everything it
// wrote to its log box goes through Log / Logged.

using System.Text.Json;
using System.Text.Json.Serialization;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>Opens the Mod Packager over the live registry and the
    /// active profile (whose entries carry sources a live mod.txt may
    /// be missing). Packing writes a new .vmz outside the mods folder,
    /// so nothing here rescans.</summary>
    public PackagerService OpenPackager() => new(Registry, Prompt, ActiveProfile);
}

/// <summary>Whether the packager's source is an archive or a folder
/// (the form's radio buttons; it picks the Browse dialog).</summary>
public enum PackagerSourceKind { Archive, Folder }

/// <summary>What a Validate call checks for.</summary>
public enum PackagerOutput { Vmz, PackJson, Modpack }

/// <summary>Everything the Mod Packager form edits.</summary>
public sealed class PackagerState
{
    public const int MinPriority = -1000;
    public const int MaxPriority = 10000;

    public PackagerSourceKind SourceKind { get; set; } = PackagerSourceKind.Archive;

    /// <summary>The Main mod: a .vmz / .zip or a folder holding mod.txt.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Where Build writes the .vmz.</summary>
    public string OutputPath { get; set; } = "";

    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";

    private int _priority;
    /// <summary>[mod] priority, clamped to the form's range.</summary>
    public int Priority
    {
        get => _priority;
        set => _priority = Math.Clamp(value, MinPriority, MaxPriority);
    }

    /// <summary>The VostokMods box: a mod page URL or slug, or empty.</summary>
    public string SourceText { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>[mod] author. Written as the modpack's author; a .vmz
    /// keeps whatever author its mod.txt already has.</summary>
    public string Author { get; set; } = "";

    /// <summary>The dependency grid's filter box.</summary>
    public string DependencyFilter { get; set; } = "";

    /// <summary>Ticked rows, keyed by manifest mod_id. Kept outside the
    /// (filtered) row list so a filter never drops a tick; Build and the
    /// exports read these sets directly.</summary>
    public HashSet<string> CheckedRequired { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CheckedOptional { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The free-form Required / Optional boxes: VostokMods page
    /// URLs or mod_ids, one per line or CSV.</summary>
    public string CustomRequired { get; set; } = "";
    public string CustomOptional { get; set; } = "";
}

/// <summary>One installed mod in the dependency grid. ModId is the
/// manifest id (what mod.txt's [dependencies] needs and the loader
/// matches); Source is the mod's VostokMods source, whose slug the
/// grid shows as the primary identifier.</summary>
public sealed record PackagerDepRow(
    string ModId,
    ModSource Source,
    string DisplayName,
    bool IsInstalledHere)
{
    public string SourceText => Source.IsValid ? Source.Id : "—";
}

/// <summary>A dependency grid row as shown, with its ticks.</summary>
public sealed record PackagerDepView(PackagerDepRow Row, bool Required, bool Optional);

/// <summary>A custom-text dependency entry resolved to the id written
/// to mod.txt and the source recorded for it. `IdIsManifest` is false
/// only when a VostokMods URL could not be matched to an installed mod
/// and the slug stands in for the id. An empty Id means skipped.</summary>
public sealed record ResolvedDependency(string Id, ModSource Source, bool IdIsManifest);

/// <summary>The dependency lists Build writes into mod.txt.</summary>
public sealed record PackDependencies(
    List<string> Required,
    List<string> Optional,
    Dictionary<string, ModSource> Sources);

/// <summary>The mod set the exports write: entries plus the pack's
/// name and description.</summary>
public sealed record PackEntrySet(List<ImportEntry> Entries, string Name, string Description);

public sealed class PackagerService
{
    private readonly ModRegistry _registry;
    private readonly IUserPrompt _prompt;

    /// <summary>Optional active profile — its ProfileMod entries are a
    /// second place to find a source the registry / mod.txt lacks (a mod
    /// added via a JSON import that carried it). The profile wins when
    /// both disagree because it is where the user's intent lives.</summary>
    private readonly ModProfile? _activeProfile;

    private List<PackagerDepRow> _depRows = new();

    /// <summary>mod_id → source, from the library, the registry and the
    /// active profile, plus sources picked up from dropped mods and
    /// opened pack JSONs.</summary>
    private Dictionary<string, ModSource> _modIdToSourceIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sources learned from dropped mods and opened pack JSONs.
    /// Kept apart so rebuilding the index from disk keeps them.</summary>
    private readonly Dictionary<string, ModSource> _learnedSources = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _log = new();

    public PackagerService(ModRegistry registry, IUserPrompt prompt, ModProfile? activeProfile = null)
    {
        _registry = registry;
        _prompt = prompt;
        _activeProfile = activeProfile;
        RefreshDependencyRows();
    }

    public PackagerState State { get; } = new();

    /// <summary>Every line written to the packager's log.</summary>
    public IReadOnlyList<string> LogLines => _log;

    /// <summary>Raised for each new log line.</summary>
    public event Action<string>? Logged;

    private void Log(string m)
    {
        _log.Add(m);
        Logged?.Invoke(m);
    }

    // ── dependency rows ──────────────────────────────────────────────

    /// <summary>Every installed mod the grid offers, sorted by name.</summary>
    public IReadOnlyList<PackagerDepRow> DependencyRows => _depRows;

    /// <summary>The rows State.DependencyFilter lets through, with their
    /// ticks: matched against mod_id, name and VostokMods slug.</summary>
    public IReadOnlyList<PackagerDepView> VisibleDependencyRows()
    {
        var f = (State.DependencyFilter ?? "").Trim().ToLowerInvariant();
        bool Matches(PackagerDepRow r) =>
            f.Length == 0
            || r.ModId.ToLowerInvariant().Contains(f)
            || r.DisplayName.ToLowerInvariant().Contains(f)
            || (r.Source.IsValid && r.Source.Id.ToLowerInvariant().Contains(f));
        return _depRows.Where(Matches)
            .Select(r => new PackagerDepView(r,
                State.CheckedRequired.Contains(r.ModId),
                State.CheckedOptional.Contains(r.ModId)))
            .ToList();
    }

    /// <summary>Ticks or unticks a row's Required box. The two boxes
    /// are mutually exclusive: ticking one unticks the other.</summary>
    public void SetRequired(string modId, bool on)
    {
        if (string.IsNullOrEmpty(modId)) return;
        if (on) { State.CheckedRequired.Add(modId); State.CheckedOptional.Remove(modId); }
        else State.CheckedRequired.Remove(modId);
    }

    /// <summary>Ticks or unticks a row's Optional box (unticking Required).</summary>
    public void SetOptional(string modId, bool on)
    {
        if (string.IsNullOrEmpty(modId)) return;
        if (on) { State.CheckedOptional.Add(modId); State.CheckedRequired.Remove(modId); }
        else State.CheckedOptional.Remove(modId);
    }

    /// <summary>Rebuilds the source index and the grid's rows. The index
    /// is filled from three places, later ones winning: library entries
    /// (every mod ever installed, so mods neither live nor in the
    /// profile), the registry (live mod.txts), then the active profile.
    /// Sources learned from drops and pack JSONs come last.</summary>
    public void RefreshDependencyRows()
    {
        _modIdToSourceIndex = new Dictionary<string, ModSource>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var le in ModLibrary.List(_registry.ModsDir))
            {
                if (string.IsNullOrEmpty(le.ModId)) continue;
                if (le.Source.IsValid)
                    _modIdToSourceIndex[le.ModId] = le.Source;
            }
        }
        catch { /* best-effort; library scan failures fall back to registry/profile */ }
        foreach (var e in _registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            if (e.Source.IsValid) _modIdToSourceIndex[e.ModId] = e.Source;
        }
        if (_activeProfile != null)
        {
            foreach (var pm in _activeProfile.Mods)
            {
                if (string.IsNullOrEmpty(pm.ModId)) continue;
                if (pm.SourceRef.IsValid)
                    _modIdToSourceIndex[pm.ModId] = pm.SourceRef;
            }
        }
        foreach (var (id, src) in _learnedSources)
            _modIdToSourceIndex[id] = src;

        _depRows = _registry.Entries
            // Built-in mods ship with the manager: never a pack member
            // or a dependency to download.
            .Where(e => !string.IsNullOrEmpty(e.ModId) && !BuiltInMods.IsBuiltIn(e.ModId))
            // Distinct by mod_id — duplicates would let the user tick
            // the same id twice.
            .GroupBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PackagerDepRow(
                g.Key,
                // Prefer the merged index; the registry's own source
                // only when nothing else knows one.
                _modIdToSourceIndex.TryGetValue(g.Key, out var indexed) ? indexed : g.First().Source,
                g.First().DisplayName ?? "",
                true))
            // By display name: the order the user reads the list in.
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The source the packager knows for a mod_id, if any.</summary>
    public ModSource KnownSourceFor(string modId)
        => _modIdToSourceIndex.TryGetValue(modId, out var v) ? v : ModSource.None;

    private void LearnSource(string modId, ModSource source)
    {
        _learnedSources[modId] = source;
        _modIdToSourceIndex[modId] = source;
    }

    // ── drops ────────────────────────────────────────────────────────

    /// <summary>The first dropped path the packager can use as a whole:
    /// a folder, a .vmz, a .zip, or a .json pack. Null when nothing
    /// qualifies (the drag cursor shows "not allowed").</summary>
    public static string? PickDropPath(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (Directory.Exists(f)) return f;
            if (File.Exists(f)
                && (f.EndsWith(".vmz",  StringComparison.OrdinalIgnoreCase)
                 || f.EndsWith(".zip",  StringComparison.OrdinalIgnoreCase)
                 || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                return f;
        }
        return null;
    }

    /// <summary>Every dropped path that is a mod: a folder holding a
    /// mod.txt, or an existing .vmz / .zip.</summary>
    public static List<string> PickModDropPaths(IEnumerable<string> files)
    {
        var paths = new List<string>();
        foreach (var f in files)
        {
            if (Directory.Exists(f))
            {
                if (File.Exists(Path.Combine(f, "mod.txt"))) paths.Add(f);
                continue;
            }
            if (File.Exists(f)
                && (f.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)
                 || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                paths.Add(f);
        }
        return paths;
    }

    /// <summary>A drop on the packager as a whole: a .json loads as a
    /// pack list; anything else becomes the Main mod source (with the
    /// source kind matched to what was dropped), its manifest is read
    /// and an output path suggested.</summary>
    public void Drop(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            Log($"↓ dropped pack JSON: {path}");
            LoadPackJson(path);
            return;
        }
        State.SourceKind = Directory.Exists(path) ? PackagerSourceKind.Folder : PackagerSourceKind.Archive;
        State.SourcePath = path;
        Log($"↓ dropped source: {path}");
        ReloadManifest();
        AutosuggestOutputPath();
    }

    /// <summary>A source picked through Browse: reads its manifest so the
    /// fields fill in, and suggests an output path.</summary>
    public void OpenSource(string path)
    {
        State.SourcePath = path;
        ReloadManifest();
        AutosuggestOutputPath();
    }

    /// <summary>Mods dropped on the Required or Optional box: each one's
    /// mod.txt id is appended to that box as a dependency (one per
    /// line), skipping ids already listed. A source the dropped mod
    /// declares is remembered, so the exports carry it even when the
    /// dependency isn't installed here. Returns the ids added.</summary>
    public List<string> AddDroppedDependencies(IEnumerable<string> paths, bool optional)
    {
        var kindLabel = optional ? "optional" : "required";
        var text = optional ? State.CustomOptional : State.CustomRequired;
        var added = new List<string>();
        var skipped = new List<string>();
        var sourcesCaptured = 0;
        foreach (var p in paths)
        {
            var (id, source) = ExtractModInfoFromPath(p);
            if (string.IsNullOrEmpty(id))
            {
                skipped.Add(Path.GetFileName(p) + " (no mod.txt / no id)");
                continue;
            }
            if (TextContainsEntry(text, id))
            {
                skipped.Add($"{id} (already listed)");
                continue;
            }
            added.Add(id);
            if (source.IsValid) { LearnSource(id, source); sourcesCaptured++; }
        }
        if (added.Count > 0)
        {
            text = AppendLines(text, added);
            if (optional) State.CustomOptional = text; else State.CustomRequired = text;
            var sourceNote = sourcesCaptured > 0 ? $" ({sourcesCaptured} linked to VostokMods)" : "";
            Log($"↓ added {added.Count} {kindLabel} dep(s){sourceNote}: {string.Join(", ", added)}");
        }
        foreach (var s in skipped)
            Log($"  · skipped: {s}");
        return added;
    }

    /// <summary>A mod's `[mod] id` and `[updates] source` from its
    /// folder or archive, in one pass. Empty / None when unreadable.</summary>
    public static (string Id, ModSource Source) ExtractModInfoFromPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var p = Path.Combine(path, "mod.txt");
                if (!File.Exists(p)) return ("", ModSource.None);
                var parsed = ModArchive.ParseConfigFile(File.ReadAllText(p));
                return (Get(parsed, "mod", "id"), ModSource.Parse(Get(parsed, "updates", "source")));
            }
            if (File.Exists(path))
            {
                using var arch = new ModArchive();
                if (!arch.Open(path)) return ("", ModSource.None);
                return (arch.ModId ?? "", arch.Source);
            }
        }
        catch { /* malformed input → empty, caller logs */ }
        return ("", ModSource.None);
    }

    /// <summary>True when `text` already lists `id` as an entry (newline
    /// / comma separated, case-insensitive) — the same split rule the
    /// pack uses, so "exists" here equals "will be written".</summary>
    private static bool TextContainsEntry(string text, string id)
        => ParseCustomList(text).Any(part => string.Equals(part, id, StringComparison.OrdinalIgnoreCase));

    private static string AppendLines(string existing, IEnumerable<string> lines)
    {
        var nl = Environment.NewLine;
        // A leading newline only when the box has content that doesn't
        // already end in one.
        if (existing.Length > 0 && !existing.EndsWith("\n") && !existing.EndsWith(nl))
            existing += nl;
        return existing + string.Join(nl, lines);
    }

    // ── source + manifest ────────────────────────────────────────────

    /// <summary>Fills OutputPath next to the source when it is empty.</summary>
    public void AutosuggestOutputPath()
    {
        if (!string.IsNullOrEmpty(State.OutputPath)) return;
        var src = State.SourcePath;
        if (string.IsNullOrEmpty(src)) return;
        State.OutputPath = Path.Combine(SourceDirectory(), SuggestedOutputFileName());
    }

    /// <summary>The folder the source sits in: where outputs are
    /// suggested and save dialogs open.</summary>
    public string SourceDirectory()
    {
        var src = State.SourcePath.Trim();
        if (string.IsNullOrEmpty(src)) return "";
        return Directory.Exists(src)
            ? Path.GetDirectoryName(src) ?? src
            : Path.GetDirectoryName(src) ?? "";
    }

    /// <summary>"&lt;id&gt;-v&lt;version&gt;.vmz" (or "&lt;id&gt;.vmz").</summary>
    public string SuggestedOutputFileName()
    {
        var id  = State.ModId.Trim();
        var ver = State.Version.Trim();
        if (string.IsNullOrEmpty(id)) id = "mod";
        if (!string.IsNullOrEmpty(ver))
            return $"{ModProfile.SafeFileName(id)}-v{ver}.vmz";
        return $"{ModProfile.SafeFileName(id)}.vmz";
    }

    /// <summary>"&lt;id or name&gt;-pack.json".</summary>
    public string SuggestedExportFileName()
    {
        var id = State.ModId.Trim();
        if (string.IsNullOrEmpty(id))
        {
            var name = State.Name.Trim();
            id = !string.IsNullOrEmpty(name) ? name : "mod-pack";
        }
        return $"{ModProfile.SafeFileName(id)}-pack.json";
    }

    /// <summary>"&lt;pack name&gt;.zip".</summary>
    public string SuggestedModpackFileName()
        => ModProfile.SafeFileName(PackName()) + ".zip";

    private string PackName()
    {
        var mainName = State.Name.Trim();
        var mainModId = State.ModId.Trim();
        return !string.IsNullOrEmpty(mainName)
            ? mainName
            : (!string.IsNullOrEmpty(mainModId) ? mainModId : "Mod pack");
    }

    /// <summary>Reads the source's mod.txt into the manifest fields and
    /// pre-ticks the dependencies it declares. Returns false (with a log
    /// line) when there is nothing to read.</summary>
    public bool ReloadManifest()
    {
        var src = State.SourcePath.Trim();
        if (string.IsNullOrEmpty(src))
        {
            Log("(no source set yet)");
            return false;
        }
        try
        {
            string modTxt;
            if (Directory.Exists(src))
            {
                var p = Path.Combine(src, "mod.txt");
                if (!File.Exists(p))
                {
                    // Try one level deep — the common single-folder wrap.
                    var entries = Directory.GetFileSystemEntries(src);
                    if (entries.Length == 1 && Directory.Exists(entries[0]))
                        p = Path.Combine(entries[0], "mod.txt");
                }
                modTxt = File.Exists(p) ? File.ReadAllText(p) : "";
            }
            else if (File.Exists(src))
            {
                using var arch = new ModArchive();
                if (!arch.Open(src))
                {
                    Log("✗ Couldn't open source archive (not a valid zip?).");
                    return false;
                }
                modTxt = arch.ReadText("mod.txt");
            }
            else
            {
                Log($"✗ Source not found: {src}");
                return false;
            }

            if (string.IsNullOrEmpty(modTxt))
            {
                Log("(source has no mod.txt — fields left blank)");
                return false;
            }

            var parsed = ModArchive.ParseConfigFile(modTxt);
            State.ModId       = Get(parsed, "mod", "id");
            State.Name        = Get(parsed, "mod", "name");
            State.Version     = Get(parsed, "mod", "version");
            State.Description = Get(parsed, "mod", "description");
            State.Author      = Get(parsed, "mod", "author");
            State.Priority    = int.TryParse(Get(parsed, "mod", "priority"), out var pn) ? pn : 0;
            State.SourceText  = ModSource.Parse(Get(parsed, "updates", "source")).Id;

            // Pre-tick the deps the source already declares (Godot array or CSV).
            var reqExisting = ModEntry.ParseCsvOrArray(Get(parsed, "dependencies", "required"));
            var optExisting = ModEntry.ParseCsvOrArray(Get(parsed, "dependencies", "optional"));
            ApplyDepSelections(reqExisting, optExisting);

            Log($"✓ Loaded manifest ({parsed.Count} sections).");
            return true;
        }
        catch (Exception ex)
        {
            Log($"✗ Reload failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ticks the rows a manifest's dependencies name (by mod_id
    /// or by VostokMods slug, the form the packager falls back to for an
    /// unresolved dep); the rest go to the custom boxes.</summary>
    private void ApplyDepSelections(List<string> req, List<string> opt)
    {
        bool RowMatches(PackagerDepRow r, string depId)
            => string.Equals(r.ModId, depId, StringComparison.OrdinalIgnoreCase)
               || (r.Source.IsValid && string.Equals(r.Source.Id, depId, StringComparison.OrdinalIgnoreCase));

        var reqMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reqLeftover = new List<string>();
        var optLeftover = new List<string>();
        foreach (var dep in req)
        {
            var hit = _depRows.FirstOrDefault(r => RowMatches(r, dep));
            if (hit != null) reqMatched.Add(hit.ModId); else reqLeftover.Add(dep);
        }
        foreach (var dep in opt)
        {
            var hit = _depRows.FirstOrDefault(r => RowMatches(r, dep));
            if (hit != null) optMatched.Add(hit.ModId); else optLeftover.Add(dep);
        }

        // The form reset the ticks of the rows on screen (the filtered
        // grid); ticks on hidden rows survived. Do the same.
        foreach (var v in VisibleDependencyRows())
        {
            var id = v.Row.ModId;
            if (reqMatched.Contains(id)) State.CheckedRequired.Add(id); else State.CheckedRequired.Remove(id);
            if (optMatched.Contains(id)) State.CheckedOptional.Add(id); else State.CheckedOptional.Remove(id);
        }
        State.CustomRequired = string.Join(Environment.NewLine, reqLeftover);
        State.CustomOptional = string.Join(Environment.NewLine, optLeftover);
    }

    private static string Get(Dictionary<string, Dictionary<string, string>> m, string s, string k)
        => m.TryGetValue(s, out var sec) && sec.TryGetValue(k, out var v) ? v : "";

    // ── validation ───────────────────────────────────────────────────

    /// <summary>The problems that stop `what` from being written, as the
    /// form logged them. Empty when it can go ahead (the output path for
    /// the exports is picked by the caller afterwards).</summary>
    public IReadOnlyList<string> Validate(PackagerOutput what = PackagerOutput.Vmz)
    {
        var problems = new List<string>();
        if (what == PackagerOutput.Vmz)
        {
            if (string.IsNullOrEmpty(State.SourcePath.Trim()))
                problems.Add("✗ Pick a source first.");
            else if (string.IsNullOrEmpty(State.OutputPath.Trim()))
                problems.Add("✗ Pick an output .vmz path first.");
        }
        if (problems.Count == 0 && !TryReadMainSource(out _, out var sourceProblem))
            problems.Add(sourceProblem);
        if (problems.Count > 0 || what == PackagerOutput.Vmz) return problems;

        var set = CollectPackEntries(log: null);
        if (set == null)
        {
            problems.Add(NothingToExport);
            return problems;
        }
        if (what == PackagerOutput.Modpack && ToModpackEntries(set.Entries, out _).Count == 0)
            problems.Add(NoModpackSources);
        return problems;
    }

    private const string NothingToExport =
        "✗ Nothing to export. Fill in Main mod or tick at least one dependency.";
    private const string NoModpackSources =
        "✗ No mod in this set has a VostokMods source, so there is nothing a modpack could list. "
        + "Link each mod to its VostokMods page (or paste page URLs) and try again.";

    /// <summary>Reads the VostokMods box. Empty is fine (None); anything
    /// else must be a VostokMods mod page URL or slug.</summary>
    public bool TryReadMainSource(out ModSource source, out string problem)
    {
        source = ModSource.None;
        problem = "";
        var text = State.SourceText.Trim();
        if (text.Length == 0) return true;
        if (!text.All(char.IsDigit) && VostokModsUrl.TryParseSlug(text, out var slug))
        {
            source = ModSource.ForSlug(slug);
            return true;
        }
        problem = $"✗ VostokMods: '{text}' is not a VostokMods mod page URL or slug "
            + "(e.g. https://vostokmods.net/mod/my-mod or my-mod).";
        return false;
    }

    // ── dependencies ─────────────────────────────────────────────────

    /// <summary>Translates one typed dependency entry into the id
    /// written to mod.txt plus the source (when known) for the
    /// [dependency_sources] section.
    ///
    /// Input forms: a mod page URL ("https://vostokmods.net/mod/x"), a
    /// URL fragment ("vostokmods.net/mod/x"), a source key
    /// ("vostokmods:x"), or a manifest mod_id ("mcm").
    ///
    /// A URL / source key is matched against the installed mods to get
    /// the real manifest id; on a miss the slug is written as the id
    /// (the loader only matches when it equals the mod_id, but the
    /// manager's resolver reads the source directly), and IdIsManifest
    /// is false. Anything else is taken as a manifest id, with its
    /// source looked up from the index. A bare number or a URL that
    /// isn't a VostokMods mod page is neither, so it is skipped.</summary>
    public ResolvedDependency ResolveCustomDepEntry(string raw) => Resolve(raw, Log);

    private ResolvedDependency Resolve(string raw, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new("", ModSource.None, false);
        var trimmed = raw.Trim().Trim('"', '\'');

        ModSource source;
        if (VostokModsUrl.TryParseModPageSlug(trimmed, out var slug))
            source = ModSource.ForSlug(slug);
        else if (!ModSource.TryParse(trimmed, out source))
        {
            if (trimmed.All(char.IsDigit) || trimmed.Contains('/'))
            {
                log?.Invoke($"  ⚠ '{trimmed}': expected a VostokMods mod page URL or a mod_id — skipped.");
                return new("", ModSource.None, false);
            }
            // A manifest mod_id; the index often knows its source.
            return new(trimmed, KnownSourceFor(trimmed), true);
        }

        var match = _registry.Entries.FirstOrDefault(
            e => e.Source == source && !string.IsNullOrEmpty(e.ModId));
        if (match != null)
        {
            log?.Invoke($"  ↪ VostokMods {source.Id} → manifest id '{match.ModId}' (from installed '{match.DisplayName}')");
            return new(match.ModId, source, true);
        }
        log?.Invoke($"  ⚠ VostokMods {source.Id}: not in your installed mods — writing '{source.Id}' literally; "
            + "install the dep locally then re-pack to get the manifest mod_id.");
        return new(source.Id, source, false);
    }

    /// <summary>Splits a custom box on newlines and commas, trimming
    /// quotes.</summary>
    public static IEnumerable<string> ParseCustomList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in raw.Split(
            new[] { '\n', '\r', ',' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = part.Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
        }
    }

    /// <summary>The Required / Optional lists and the dep → source map
    /// Build writes. Ticks come first, then the custom boxes; deduped
    /// case-insensitively, and a mod required stays out of Optional.
    /// What lands in mod.txt is always the manifest mod_id when known.</summary>
    public PackDependencies CollectDependencies() => CollectDependencies(Log);

    private PackDependencies CollectDependencies(Action<string>? log)
    {
        var req = new List<string>();
        var opt = new List<string>();
        var reqSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in State.CheckedRequired)
            if (reqSeen.Add(id)) req.Add(id);
        foreach (var id in State.CheckedOptional)
            if (optSeen.Add(id) && !reqSeen.Contains(id)) opt.Add(id);

        // (dep id → source) pairs for [dependency_sources], so the
        // install-time resolver can download deps without asking.
        var depSources = new Dictionary<string, ModSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _depRows)
        {
            if (!r.Source.IsValid) continue;
            if (reqSeen.Contains(r.ModId) || optSeen.Contains(r.ModId))
                depSources[r.ModId] = r.Source;
        }

        foreach (var raw in ParseCustomList(State.CustomRequired))
        {
            var d = Resolve(raw, log);
            if (string.IsNullOrEmpty(d.Id)) continue;
            if (reqSeen.Add(d.Id)) req.Add(d.Id);
            if (d.Source.IsValid) depSources[d.Id] = d.Source;
        }
        foreach (var raw in ParseCustomList(State.CustomOptional))
        {
            var d = Resolve(raw, log);
            if (string.IsNullOrEmpty(d.Id)) continue;
            if (optSeen.Add(d.Id) && !reqSeen.Contains(d.Id)) opt.Add(d.Id);
            if (d.Source.IsValid) depSources[d.Id] = d.Source;
        }
        return new PackDependencies(req, opt, depSources);
    }

    // ── build ────────────────────────────────────────────────────────

    /// <summary>Packs the source into State.OutputPath (".vmz" appended
    /// when missing) with the manifest edits and dependencies applied.
    /// Returns null when validation stopped it (the reasons are logged);
    /// otherwise the packer's result, whose log and errors are logged too.</summary>
    public ModPacker.PackResult? Build()
    {
        var problems = Validate(PackagerOutput.Vmz);
        if (problems.Count > 0)
        {
            foreach (var p in problems) Log(p);
            return null;
        }
        var src = State.SourcePath.Trim();
        var dst = State.OutputPath.Trim();
        if (!dst.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase))
            dst += ".vmz";
        TryReadMainSource(out var mainSource, out _);

        var deps = CollectDependencies(Log);
        var opts = new ModPacker.PackOptions
        {
            ModId       = State.ModId.Trim(),
            Name        = State.Name.Trim(),
            Version     = State.Version.Trim(),
            Description = State.Description.Trim(),
            Priority    = State.Priority,
            Source      = mainSource,
            RequiredDependencies = deps.Required,
            OptionalDependencies = deps.Optional,
            DependencySources    = deps.Sources,
        };

        Log("");
        Log($"Packing → {dst}");
        var result = ModPacker.Pack(src, dst, opts);
        foreach (var line in result.Log)   Log("  " + line);
        foreach (var err in result.Errors) Log("  ✗ " + err);
        if (result.Success)
            Log($"✓ Done — {result.FilesPacked} file(s), {result.OutputBytes:N0} bytes.");
        return result;
    }

    // ── exports ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonExportOpts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder                = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The mod set the packager describes, as import entries:
    /// the Main mod (when a source is set and it is a real mod), every
    /// ticked dependency, then every custom-text dependency. Deduped by
    /// mod_id, then by source, first one wins (so required beats
    /// optional). Null (logged) when there is nothing to export or the
    /// Main mod's VostokMods box is invalid.</summary>
    public PackEntrySet? CollectPackEntries() => CollectPackEntries(Log);

    private PackEntrySet? CollectPackEntries(Action<string>? log)
    {
        var packDescription = State.Description.Trim();
        var mainModId = State.ModId.Trim();
        var mainName  = State.Name.Trim();
        var mainVer   = State.Version.Trim();
        if (!TryReadMainSource(out var mainSource, out var problem))
        {
            log?.Invoke(problem);
            return null;
        }

        var entries = new List<ImportEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<ModSource>();

        void AddEntry(ImportEntry e)
        {
            // Same mod_id OR same source collapses to one.
            if (!string.IsNullOrEmpty(e.ModId) && !seenIds.Add(e.ModId)) return;
            if (e.SourceRef.IsValid && !seenSources.Add(e.SourceRef))
            {
                if (!string.IsNullOrEmpty(e.ModId)) seenIds.Add(e.ModId);
                return;
            }
            entries.Add(e);
        }

        // Without a source path the Main mod fields only name the pack
        // ("My Pack"); they are not a mod the recipient should install.
        // With one, the Main mod is a real mod and always required.
        var hasSource = !string.IsNullOrEmpty(State.SourcePath.Trim());
        if (hasSource && (!string.IsNullOrEmpty(mainModId) || mainSource.IsValid))
        {
            AddEntry(new ImportEntry
            {
                ModId       = mainModId,
                DisplayName = !string.IsNullOrEmpty(mainName) ? mainName : mainModId,
                Source      = mainSource.Key,
                Version     = mainVer,
            });
        }

        var depByModId = _depRows.ToDictionary(r => r.ModId, StringComparer.OrdinalIgnoreCase);
        void AddFromTickedSet(IEnumerable<string> ticked, bool isOptional)
        {
            foreach (var id in ticked)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!depByModId.TryGetValue(id, out var r)) continue;
                AddEntry(new ImportEntry
                {
                    ModId       = r.ModId,
                    DisplayName = r.DisplayName,
                    Source      = r.Source.Key,
                    // Only emit the flag when truly optional; keeps the
                    // JSON clean and round-trips with pre-flag exports.
                    IsOptional  = isOptional ? true : (bool?)null,
                });
            }
        }
        // Required first so first-write-wins keeps them required.
        AddFromTickedSet(State.CheckedRequired, isOptional: false);
        AddFromTickedSet(State.CheckedOptional, isOptional: true);

        void AddFromCustomBox(string raw, bool isOptional)
        {
            foreach (var token in ParseCustomList(raw))
            {
                var d = Resolve(token, log);
                if (string.IsNullOrEmpty(d.Id) && !d.Source.IsValid) continue;
                // A slug standing in for an unresolved id stays out of
                // the JSON: the recipient resolves by source alone.
                AddEntry(new ImportEntry
                {
                    ModId      = d.IdIsManifest ? d.Id : "",
                    Source     = d.Source.Key,
                    IsOptional = isOptional ? true : (bool?)null,
                });
            }
        }
        AddFromCustomBox(State.CustomRequired, isOptional: false);
        AddFromCustomBox(State.CustomOptional, isOptional: true);

        if (entries.Count == 0)
        {
            log?.Invoke(NothingToExport);
            return null;
        }
        return new PackEntrySet(entries, PackName(), packDescription);
    }

    /// <summary>Writes the mod set as a mod-pack JSON (ModListImport
    /// shape) — the file a recipient drops on the manager to merge the
    /// set into their active profile. True when written.</summary>
    public bool ExportPackJson(string path)
    {
        var set = CollectPackEntries(Log);
        if (set == null) return false;
        var import = new ModListImport
        {
            Name = set.Name,
            Description = set.Description,
            Mods = set.Entries,
        };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(import, _jsonExportOpts));
            Log($"📤 Exported {set.Entries.Count} mod(s) → {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"✗ Export failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The modpack's rows: every entry with a VostokMods source,
    /// pinned to its typed version or the one installed here, in list
    /// order (the load order). Labels of entries with no source go to
    /// `skipped`.</summary>
    private List<ModpackFile.Entry> ToModpackEntries(List<ImportEntry> entries, out List<string> skipped)
    {
        var mods = new List<ModpackFile.Entry>();
        skipped = new List<string>();
        foreach (var e in entries)
        {
            var label = !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName
                : (!string.IsNullOrEmpty(e.ModId) ? e.ModId : e.SourceRef.Id);
            var src = e.SourceRef;
            if (!src.IsValid)
            {
                skipped.Add(label);
                continue;
            }
            var version = e.Version;
            if (string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(e.ModId))
                version = _registry.FindById(e.ModId)?.Version ?? "";
            mods.Add(new ModpackFile.Entry(src.Id, label, version, mods.Count + 1));
        }
        return mods;
    }

    /// <summary>Writes the mod set as a modpack zip in the mod loader's
    /// format: profile.json naming each mod by its VostokMods source and
    /// pinned version. Only mods with a source can be in it (the loader
    /// downloads each one); the rest are reported and left out. True
    /// when written.</summary>
    public bool BuildModpack(string path)
    {
        var set = CollectPackEntries(Log);
        if (set == null) return false;
        var mods = ToModpackEntries(set.Entries, out var skipped);
        if (mods.Count == 0)
        {
            Log(NoModpackSources);
            return false;
        }
        try
        {
            ModpackFile.Write(path, set.Name, set.Description, author: State.Author.Trim(), mods);
            Log($"📦 Built modpack with {mods.Count} mod(s) → {path}");
            foreach (var m in mods)
                Log($"    {m.LoadOrder,2}. {m.Name}  ({m.Slug}" + (m.Version.Length > 0 ? $" v{m.Version})" : ", newest)"));
            foreach (var s in skipped)
                Log($"  ! {s}: no VostokMods source — left out.");
            Log("  Put the zip in the game's mods folder for the loader's Modpacks tab, "
                + "or drop it on the manager to install everything in it.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"✗ Modpack build failed: {ex.Message}");
            return false;
        }
    }

    // ── load pack JSON ───────────────────────────────────────────────

    /// <summary>Opens a mod-pack JSON into the packager. Existing ticks
    /// or custom entries are replaced (after asking). Every entry lands
    /// in the Required or Optional box by its is_optional flag, as its
    /// mod_id (else its page URL), and its source is remembered so a
    /// re-export keeps it. The pack's name and description fill Name and
    /// Description; the other manifest fields are left alone. True when
    /// loaded.</summary>
    public bool LoadPackJson(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Log($"✗ Open failed: file not found — {path}");
            return false;
        }

        ModListImport import;
        try { import = ModListImport.LoadFromFile(path); }
        catch (Exception ex)
        {
            Log($"✗ Open failed: {ex.Message}");
            return false;
        }

        var hasExisting = State.CheckedRequired.Count > 0
                       || State.CheckedOptional.Count > 0
                       || !string.IsNullOrWhiteSpace(State.CustomRequired)
                       || !string.IsNullOrWhiteSpace(State.CustomOptional);
        if (hasExisting && !_prompt.Confirm("Replace dependencies?",
                "Opening a pack JSON will REPLACE your current dependency "
                + "selections (ticks + custom mod_ids). Manifest fields "
                + "(mod_id, name, version, etc.) stay as-is.\n\nContinue?",
                PromptKind.Warning))
            return false;

        State.CheckedRequired.Clear();
        State.CheckedOptional.Clear();
        State.CustomRequired = "";
        State.CustomOptional = "";

        // Only overwrite when the JSON has something, so a hand-written
        // file with no name doesn't wipe a label the user typed.
        if (!string.IsNullOrEmpty(import.Name))        State.Name = import.Name;
        if (!string.IsNullOrEmpty(import.Description)) State.Description = import.Description;

        var reqLines = new List<string>();
        var optLines = new List<string>();
        int reqCount = 0, optCount = 0, skippedAsMeta = 0;
        foreach (var fe in import.Flatten())
        {
            var entry = fe.Entry;
            // Pack metadata leaked into mods[] by pre-0.5.37 exports: an
            // entry named like the pack, with no source.
            if (!entry.SourceRef.IsValid
                && !string.IsNullOrEmpty(import.Name)
                && !string.IsNullOrEmpty(entry.DisplayName)
                && string.Equals(entry.DisplayName, import.Name, StringComparison.OrdinalIgnoreCase))
            {
                skippedAsMeta++;
                continue;
            }

            string line;
            if (!string.IsNullOrEmpty(entry.ModId)) line = entry.ModId;
            else if (entry.SourceRef.IsValid) line = entry.SourceRef.PageUrl;
            else continue;

            // A missing flag is required (pre-0.5.66 packs).
            if (entry.IsOptional == true) { optLines.Add(line); optCount++; }
            else { reqLines.Add(line); reqCount++; }

            if (!string.IsNullOrEmpty(entry.ModId) && entry.SourceRef.IsValid)
                LearnSource(entry.ModId, entry.SourceRef);
        }
        State.CustomRequired = string.Join(Environment.NewLine, reqLines);
        State.CustomOptional = string.Join(Environment.NewLine, optLines);

        var metaNote = skippedAsMeta > 0 ? $", {skippedAsMeta} pack-metadata row(s) skipped" : "";
        Log($"📂 Loaded pack '{import.Name}' from {Path.GetFileName(path)} "
            + $"— {reqCount} required + {optCount} optional{metaNote}.");
        return true;
    }
}
