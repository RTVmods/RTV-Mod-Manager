// Importing mod lists and modpacks into a profile. Ported from MainForm
// (RunModListImportAsync, ImportModpackAsync, ImportModpackFileAsync,
// SaveSampleModListTemplate), Ui/ImportTargetDialog, Ui/ImportModListDialog
// and the data half of Ui/ModpackBrowserDialog.
//
// The flow a front end drives:
//   1. get a ModListImport: LoadModListFile / LoadModpackFile /
//      LoadModpackAsync (or AskForModpackAsync);
//   2. GetImportTargetOptions → ask where it goes → SelectImportTarget
//      (creates and / or switches to that profile);
//   3. BuildImportPlan → the user ticks rows → ExecuteImportAsync;
//   4. the caller runs its update check (when Applied) and scans the
//      InstalledModIds for missing dependencies, as MainForm did.
//
// An import never wipes anything and never touches mods outside the
// list: it adds to the active profile.

using System.Security.Cryptography;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    /// <summary>The mod loader's own page on VostokMods. Modpacks list it
    /// as a member, but the manager doesn't install it as a mod: it is
    /// updated from the MML status row.</summary>
    private const string ImportsModLoaderSlug = "metro-mod-loader";

    /// <summary>Test seam: stands in for VostokModsClient.GetModpackManifestAsync.</summary>
    internal Func<string, CancellationToken, Task<VmModpackManifest>>? ProfilesManifestOverride { get; set; }

    /// <summary>Test seam: stands in for VostokModsClient.ListModpacksAsync (first page).</summary>
    internal Func<CancellationToken, Task<VmPage<VmModpackSummary>>>? ProfilesModpackListOverride { get; set; }

    private Task<VmModpackManifest> ImportsFetchManifestAsync(string slug, CancellationToken ct)
        => ProfilesManifestOverride != null ? ProfilesManifestOverride(slug, ct) : Vm.GetModpackManifestAsync(slug, ct);

    private Task<VmPage<VmModpackSummary>> ImportsListModpacksAsync(CancellationToken ct)
        => ProfilesModpackListOverride != null ? ProfilesModpackListOverride(ct) : Vm.ListModpacksAsync(ct: ct);

    // ── where the import goes ─────────────────────────────────────────

    /// <summary>Import adds mods to the active profile, so there has to
    /// be one. Tells the user and returns false when there isn't.</summary>
    public bool RequireActiveProfileForImport()
    {
        if (ActiveProfile != null) return true;
        Prompt.Inform("No active profile",
            "Import adds mods to the ACTIVE profile, but no profile is active yet.\n\n"
            + "Open Profiles to create or activate a profile first, then try the import again.");
        return false;
    }

    /// <summary>The choices for where an import named `packName` lands,
    /// or null (after telling the user) when no profile is active.</summary>
    public ImportTargetOptions? GetImportTargetOptions(string packName)
    {
        if (!RequireActiveProfileForImport()) return null;
        var active = ActiveProfile!;
        var others = AllProfiles
            .Where(p => !SameProfileName(p.Name, active.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var suggested = UniqueProfileName(
            string.IsNullOrWhiteSpace(packName) ? "Imported mods" : packName.Trim(),
            AllProfiles.Select(p => p.Name));
        return new ImportTargetOptions(packName, active, others, suggested);
    }

    /// <summary>Makes the chosen profile the active one: a new profile
    /// (described as the import) is created and switched to; another
    /// saved profile is switched to. Returns true when the import should
    /// go ahead into ActiveProfile; false when the name is taken or the
    /// switch was cancelled or failed (the switch says why), so the
    /// import never lands in the wrong profile.</summary>
    public bool SelectImportTarget(ImportTargetChoice choice, ModListImport import)
    {
        if (ActiveProfile == null) return false;
        ModProfile? target = null;
        if (choice.Target == ImportTarget.NewProfile)
        {
            var name = (choice.NewProfileName ?? "").Trim();
            if (name.Length == 0) return false;
            if (AllProfiles.Any(p => SameProfileName(p.Name, name)))
            {
                Prompt.Inform("Name taken",
                    $"A profile named '{name}' already exists. Pick another name, "
                    + "or choose it under \"Another saved profile\".", PromptKind.Warning);
                return false;
            }
            var fresh = new ModProfile
            {
                Name        = name,
                Description = import.Description,
                CreatedAt   = DateTime.UtcNow,
            };
            try { fresh.SaveMetadataOnly(); }
            catch (Exception ex)
            {
                Prompt.Error("Couldn't create the profile", ex);
                return false;
            }
            ReloadProfiles();
            target = AllProfiles.FirstOrDefault(p => SameProfileName(p.Name, fresh.Name));
        }
        else if (choice.Target == ImportTarget.ExistingProfile)
        {
            target = choice.ExistingProfile;
            if (target == null) return false;
        }

        if (target != null && !SwitchActiveProfile(target)) return false;
        return ActiveProfile != null
            && (target == null || SameProfileName(ActiveProfile.Name, target.Name));
    }

    /// <summary>`name`, or `name (2)`, `name (3)`… when taken.</summary>
    private static string UniqueProfileName(string name, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name)) return name;
        for (var i = 2; i < 100; i++)
            if (!set.Contains($"{name} ({i})")) return $"{name} ({i})";
        return name + " " + DateTime.Now.ToString("yyyyMMdd-HHmm");
    }

    // ── getting an import ─────────────────────────────────────────────

    /// <summary>Reads a mod-list .json. Null, after telling the user,
    /// when there is no active profile, it doesn't parse, or it lists no
    /// mods. Pass the file's folder as the plan's companion dir so .vmz
    /// files dropped beside it can supply missing sources.</summary>
    public ModListImport? LoadModListFile(string path)
    {
        if (!RequireActiveProfileForImport()) return null;
        ModListImport import;
        try { import = ModListImport.LoadFromFile(path); }
        catch (Exception ex)
        {
            Prompt.Inform("Import failed",
                $"Couldn't parse `{Path.GetFileName(path)}`:\n\n{ex.Message}\n\n"
                + "Expected shape:\n{\n  \"mods\": [\n    { \"mod_id\": \"x\", \"source\": \"vostokmods:x\" }\n  ]\n}",
                PromptKind.Warning);
            return null;
        }
        if (import.Mods.Count == 0)
        {
            Prompt.Inform("Empty mod list",
                "The JSON parsed cleanly but contained no entries under `mods`. Nothing to import.");
            return null;
        }
        return import;
    }

    /// <summary>Reads a modpack zip (the mod loader's format, which a
    /// downloaded VostokMods modpack also takes) into an import pinned to
    /// the pack's versions. Mods without a VostokMods source are left out
    /// and named. Null when it isn't a modpack, nothing is importable, or
    /// no profile is active.</summary>
    public ModListImport? LoadModpackFile(string path)
    {
        if (!ModpackFile.TryRead(path, out var import, out var skipped))
        {
            Prompt.Inform("Not a modpack",
                $"`{Path.GetFileName(path)}` isn't a modpack: it has no mod list (profile.json) at its root. "
                + "A mod is a .vmz file, not a .zip.", PromptKind.Warning);
            return null;
        }
        if (!RequireActiveProfileForImport()) return null;
        if (import.Mods.Count == 0)
        {
            Prompt.Inform("Nothing to import",
                $"`{import.Name}` lists no mod with a VostokMods source, so there is nothing to download."
                + (skipped.Count > 0
                    ? "\n\nListed without a source:\n" + string.Join("\n", skipped.Select(s => "   • " + s))
                    : ""));
            return null;
        }
        if (skipped.Count > 0)
            Prompt.Inform("Import modpack",
                $"`{import.Name}` has {import.Mods.Count} mod" + (import.Mods.Count == 1 ? "" : "s")
                + " to import. Left out (no VostokMods source):\n\n"
                + string.Join("\n", skipped.Select(s => "   • " + s)));
        return import;
    }

    /// <summary>Reads a VostokMods modpack's manifest into an import with
    /// each mod pinned to the version (and checksum) the pack lists, in
    /// load order. The mod loader itself and unavailable mods are left
    /// out and named. Null when it can't be read or nothing is importable.</summary>
    public async Task<ModListImport?> LoadModpackAsync(string slug, CancellationToken ct = default)
    {
        if (!RequireActiveProfileForImport()) return null;

        VmModpackManifest manifest;
        try { manifest = await ImportsFetchManifestAsync(slug, ct); }
        catch (VostokModsException ex)
        {
            Prompt.Inform("Import failed",
                ex.Kind == VostokModsError.NotFound
                    ? $"VostokMods has no modpack at `{slug}`. Check the URL and try again."
                    : $"Couldn't read the modpack `{slug}` from VostokMods:\n{ex.Message}",
                PromptKind.Warning);
            return null;
        }

        var import = new ModListImport
        {
            Name        = manifest.Name.Length > 0 ? manifest.Name : slug,
            Description = manifest.Summary,
            PinVersions = true,
        };
        var notes = new List<string>();
        foreach (var m in manifest.Mods.OrderBy(m => m.LoadOrder))
        {
            var label = m.Name.Length > 0 ? m.Name : m.Slug;
            if (string.Equals(m.Slug, ImportsModLoaderSlug, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"{label}: the mod loader itself — update it from the MML status row.");
                continue;
            }
            if (!m.Available)
            {
                notes.Add($"{label}: not available on VostokMods" + (m.Reason.Length > 0 ? $" ({m.Reason})." : "."));
                continue;
            }
            var source = ModSource.ForSlug(m.Slug);
            import.Mods.Add(new ImportEntry
            {
                DisplayName = label,
                Source      = source.Key,
                Version     = m.Version,
                IsEnabled   = true,
                Priority    = m.LoadOrder,
            });
            if (m.Sha256.Length > 0) import.Checksums[source.Key] = m.Sha256;
        }
        if (manifest.McmConfigJson.Length > 0)
            notes.Add("The pack also carries mod settings (MCM). The manager installs the mods only; "
                + "apply the pack from the in-game Modpacks tab to get its settings too.");

        if (import.Mods.Count == 0)
        {
            Prompt.Inform("Nothing to import",
                $"`{import.Name}` lists no mods that can be installed."
                + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : ""));
            return null;
        }
        if (notes.Count > 0)
            Prompt.Inform("Import modpack",
                $"`{import.Name}` has {import.Mods.Count} mod" + (import.Mods.Count == 1 ? "" : "s")
                + " to import. Left out:\n\n" + string.Join("\n", notes.Select(n => "   • " + n)));
        return import;
    }

    /// <summary>Asks for a modpack's page URL or slug (naming the packs
    /// VostokMods has now) and reads it with LoadModpackAsync.</summary>
    public async Task<ModListImport?> AskForModpackAsync(CancellationToken ct = default)
    {
        if (!RequireActiveProfileForImport()) return null;

        // Name the packs that exist, so the user doesn't have to go and
        // find a URL first.
        var hint = "";
        try
        {
            var packs = await ImportsListModpacksAsync(ct);
            if (packs.Items.Count > 0)
                hint = "\n\nOn VostokMods now:\n" + string.Join("\n",
                    packs.Items.Take(12).Select(p => $"   {p.Slug}  —  {p.Name}"));
        }
        catch { /* the prompt works without the list */ }

        var input = Prompt.AskText("Import modpack from VostokMods",
            "Paste the modpack's page URL (e.g. https://vostokmods.net/modpack/vostok-enhanced) or its slug." + hint);
        if (input == null) return null;
        input = input.Trim();
        if (!TryParseModpackInput(input, out var slug))
        {
            Prompt.Inform("Invalid input",
                $"That isn't a VostokMods modpack:\n  {input}\n\n"
                + "Expected a URL like https://vostokmods.net/modpack/vostok-enhanced or the slug on its own.",
                PromptKind.Warning);
            return null;
        }
        return await LoadModpackAsync(slug, ct);
    }

    /// <summary>A modpack page / API URL, or a bare slug (not a mod URL).</summary>
    public static bool TryParseModpackInput(string? input, out string slug)
    {
        input = (input ?? "").Trim();
        if (VostokModsUrl.TryParseModpackSlug(input, out slug)) return true;
        return VostokModsUrl.TryParseSlug(input, out slug) && !input.Contains('/');
    }

    /// <summary>The modpacks VostokMods lists (first page), for a browser.
    /// Never throws: a failure comes back as `Error`.</summary>
    public async Task<ModpackListing> ListModpacksAsync(CancellationToken ct = default)
    {
        try
        {
            var page = await ImportsListModpacksAsync(ct);
            var packs = page.Items.ToList();
            var header = packs.Count == 0
                ? "VostokMods lists no modpacks yet."
                : $"{packs.Count} modpack" + (packs.Count == 1 ? "" : "s")
                  + " on VostokMods. Installing adds every mod of the pack to "
                  + $"'{ActiveProfile?.Name ?? ""}', at the versions the pack lists.";
            return new ModpackListing(packs, header, null);
        }
        catch (Exception ex)
        {
            return new ModpackListing(Array.Empty<VmModpackSummary>(),
                "Couldn't read the modpack list from VostokMods — " + ex.Message, ex.Message);
        }
    }

    /// <summary>The packs whose name, author, summary or slug contains
    /// `query` (case-insensitive); all of them for an empty query.</summary>
    public static List<VmModpackSummary> FilterModpacks(IEnumerable<VmModpackSummary> packs, string? query)
    {
        var q = (query ?? "").Trim();
        return packs.Where(p => q.Length == 0
                || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.Slug.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ── the plan ──────────────────────────────────────────────────────

    /// <summary>What importing `import` into the active profile would do
    /// per entry (null without an active profile). Entries the list
    /// gives no source get one from, later overruling earlier: linked
    /// live mods, Library archives, then .vmz files in `companionDir`.
    /// An entry matches a live mod by id, then source, then (only when
    /// exactly one fits) name; a name match records the pack's source
    /// for that mod so the next import matches by source.</summary>
    public ImportPlan? BuildImportPlan(ModListImport import, string? companionDir = null)
    {
        if (ActiveProfile == null) return null;
        var profile = ActiveProfile;

        var liveById = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId)) liveById[e.ModId] = e;

        var profileIds = new HashSet<string>(profile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);

        // Library by mod_id → newest entry.
        var libByMod = new Dictionary<string, ModLibrary.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var le in ModLibrary.List(ModsDir))
        {
            if (string.IsNullOrEmpty(le.ModId)) continue;
            if (!libByMod.TryGetValue(le.ModId, out var cur)
                || ModRegistry.CompareVersions(le.Version, cur.Version) > 0)
                libByMod[le.ModId] = le;
        }

        var fallbackSources = new Dictionary<string, ModSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Registry.Entries)
            if (!string.IsNullOrEmpty(e.ModId) && e.Source.IsValid) fallbackSources[e.ModId] = e.Source;
        foreach (var le in libByMod.Values)
            if (le.Source.IsValid) fallbackSources[le.ModId] = le.Source;
        if (!string.IsNullOrEmpty(companionDir) && Directory.Exists(companionDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(companionDir, "*.vmz", SearchOption.TopDirectoryOnly))
                {
                    using var arch = new ModArchive();
                    if (!arch.Open(f)) continue;
                    if (!string.IsNullOrEmpty(arch.ModId) && arch.Source.IsValid)
                        fallbackSources[arch.ModId] = arch.Source;
                }
            }
            catch { /* best-effort; bad permissions / locked files shouldn't block import */ }
        }

        var flat = import.Flatten();
        // Patch missing sources in place; the rest reads the augmented values.
        foreach (var fe in flat)
        {
            var ent = fe.Entry;
            if (ent.SourceRef.IsValid || string.IsNullOrEmpty(ent.ModId)) continue;
            if (fallbackSources.TryGetValue(ent.ModId, out var src) && src.IsValid)
                ent.Source = src.Key;
        }
        // Top-level entries first, then those brought in as dependencies,
        // alphabetical within each.
        flat.Sort((a, b) =>
        {
            if (a.IsTopLevel != b.IsTopLevel) return a.IsTopLevel ? -1 : 1;
            return string.Compare(a.Entry.DisplayName ?? "", b.Entry.DisplayName ?? "",
                StringComparison.OrdinalIgnoreCase);
        });

        var rows = new List<ImportPlanRow>();
        foreach (var fe in flat)
        {
            var entry = fe.Entry;
            var source = entry.SourceRef;
            ModEntry? live = null;
            if (!string.IsNullOrEmpty(entry.ModId)) liveById.TryGetValue(entry.ModId, out live);
            if (live == null && source.IsValid)
                live = Registry.Entries.FirstOrDefault(e => ImportsSameSource(e.Source, source) && !string.IsNullOrEmpty(e.ModId));
            if (live == null)
            {
                live = ImportsMatchLiveByName(entry);
                if (live != null && source.IsValid && !live.Source.IsValid && live.IsArchive)
                    DownloadedSources.Record(live.Path, source);
            }
            // The live mod's own id counts from here on.
            var modId = live?.ModId ?? entry.ModId;

            var libPath = "";
            if (!string.IsNullOrEmpty(modId) && libByMod.TryGetValue(modId, out var lib)) libPath = lib.Path;

            var inProfile = !string.IsNullOrEmpty(modId) && profileIds.Contains(modId);
            if (!inProfile && source.IsValid)
                inProfile = profile.Mods.Any(m => ImportsSameSource(m.SourceRef, source) && !string.IsNullOrEmpty(m.ModId));

            // Optional entries start unticked; required (or unflagged) ticked.
            var optIn = entry.IsOptional != true;

            ImportPlanRow row;
            if (inProfile && live != null)
                row = new ImportPlanRow(ImportRowKind.AlreadyInProfile, entry, fe.ParentChain, live, libPath,
                    "✓", "Already in profile and live", false);
            else if (inProfile)
                // In the profile but not live: an Apply round fetches it.
                row = new ImportPlanRow(ImportRowKind.AlreadyInProfile, entry, fe.ParentChain, null, libPath,
                    "✓", "Already in profile (live missing — apply to populate)", false);
            else if (live != null)
                row = new ImportPlanRow(ImportRowKind.LivePresent, entry, fe.ParentChain, live, libPath,
                    "↪", "Live on disk — will add entry to profile", optIn);
            else if (!string.IsNullOrEmpty(libPath))
                row = new ImportPlanRow(ImportRowKind.FromLibrary, entry, fe.ParentChain, null, libPath,
                    "📚", "Will install from library (already downloaded)", optIn);
            else if (source.IsValid)
                row = new ImportPlanRow(ImportRowKind.Download, entry, fe.ParentChain, null, "",
                    "⬇", "Will download from VostokMods", optIn);
            else
                row = new ImportPlanRow(ImportRowKind.Unresolvable, entry, fe.ParentChain, null, "",
                    "✗", "No VostokMods source — cannot download", false);
            rows.Add(row);
        }
        return new ImportPlan(import, profile, companionDir, rows);
    }

    /// <summary>Runs an import plan: Library copies, then downloads (each
    /// archived in the Library), a rescan, then every ticked row added to
    /// (or refreshed in) the profile under the pack's name, and one
    /// profile.json save. Then everything is refreshed.</summary>
    public async Task<ImportResult> ExecuteImportAsync(
        ImportPlan plan, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var lines = new List<string>();
        void Log(string m) { lines.Add(m); log?.Report(m); }
        var installedIds = new List<string>();
        var profile = plan.Profile;
        var actions = plan.Rows.Select(r => (Row: r, OptIn: r.WillAct)).ToList();
        var profileDirty = false;

        // Library copies first (no network).
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn || row.Kind != ImportRowKind.FromLibrary) continue;
            try
            {
                var liveName = ImportsLiveFileName(row.Entry);
                var dst = Path.Combine(ModsDir, liveName);
                if (File.Exists(dst)) dst = Path.Combine(ModsDir, MakeUniqueLiveName(liveName));
                File.Copy(row.LibraryPath, dst, overwrite: false);
                Log($"  📚 {ImportsLabel(row.Entry)} → {Path.GetFileName(dst)}");
            }
            catch (Exception ex)
            {
                Log($"  ✗ {ImportsLabel(row.Entry)}: library copy failed — {ex.Message}");
            }
        }

        // VostokMods downloads.
        foreach (var (row, optIn) in actions)
        {
            if (ct.IsCancellationRequested) break;
            if (!optIn || row.Kind != ImportRowKind.Download) continue;
            try
            {
                var source = row.Entry.SourceRef;
                var liveName = ImportsLiveFileName(row.Entry);
                var dst = Path.Combine(ModsDir, liveName);
                if (File.Exists(dst)) dst = Path.Combine(ModsDir, MakeUniqueLiveName(liveName));
                Log($"⬇ {ImportsLabel(row.Entry)} ({source.Id}) …");
                await DownloadImportEntryAsync(plan.Import, row.Entry, source, dst, Log, ct);
                DownloadedSources.Record(dst, source);
                // Into the Library so a later switch needn't redownload.
                try { ModLibrary.Add(ModsDir, dst); } catch { /* best-effort */ }
                Log($"  ✓ → {Path.GetFileName(dst)}");
            }
            catch (OperationCanceledException) { Log("  ✗ Cancelled."); break; }
            catch (Exception ex) { Log($"  ✗ Download failed: {ex.Message}"); }
        }

        if (!ct.IsCancellationRequested)
        {
            // Rescan so the new mods show with their manifest ids. No cfg:
            // this is additive, and the refresh below rescans with it.
            Log("↻ Rescanning mods …");
            Registry.Scan(ModsDir, cfg: null);

            // Imported entries are grouped under the pack's name.
            var packLabel = !string.IsNullOrEmpty(plan.Import.Name) ? plan.Import.Name : "Imported mods";
            foreach (var (row, optIn) in actions)
            {
                if (ct.IsCancellationRequested) break;
                if (!optIn) continue;
                if (row.Kind is ImportRowKind.Unresolvable or ImportRowKind.AlreadyInProfile) continue;

                var entry = ImportsResolveRegistryEntry(row.Entry);
                if (entry == null)
                {
                    Log($"  — {ImportsLabel(row.Entry)}: not found after scan, skipping profile add.");
                    continue;
                }
                var existing = profile.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    profile.Mods.Add(new ProfileMod
                    {
                        ModId       = entry.ModId,
                        DisplayName = !string.IsNullOrEmpty(entry.DisplayName)
                            ? entry.DisplayName
                            : (string.IsNullOrEmpty(row.Entry.DisplayName) ? entry.ModId : row.Entry.DisplayName),
                        Version     = entry.Version,
                        IsEnabled   = row.Entry.IsEnabled ?? true,
                        Priority    = row.Entry.Priority ?? entry.DeclaredPriority,
                        Source      = entry.Source.IsValid ? entry.Source.Key : row.Entry.SourceRef.Key,
                        PackName    = packLabel,
                    });
                    Log($"  + profile: {ImportsLabel(row.Entry)} ({entry.ModId} v{entry.Version}) [{packLabel}]");
                }
                else
                {
                    // A mod already grouped under another pack keeps it.
                    existing.Version = entry.Version;
                    if (string.IsNullOrEmpty(existing.DisplayName)) existing.DisplayName = entry.DisplayName;
                    if (!existing.SourceRef.IsValid && entry.Source.IsValid) existing.Source = entry.Source.Key;
                    if (string.IsNullOrEmpty(existing.PackName)) existing.PackName = packLabel;
                    Log($"  ↻ profile updated: {ImportsLabel(row.Entry)} ({entry.ModId} v{entry.Version})");
                }
                profileDirty = true;
                // The registry's id, which declared dependencies match.
                if (!string.IsNullOrEmpty(entry.ModId)
                    && !installedIds.Contains(entry.ModId, StringComparer.OrdinalIgnoreCase))
                    installedIds.Add(entry.ModId);
            }

            if (profileDirty)
            {
                profile.UpdatedAt = DateTime.UtcNow;
                try { profile.SaveMetadataOnly(); Log("✓ profile.json saved."); }
                catch (Exception ex) { Log($"✗ profile save failed: {ex.Message}"); }
            }
            else
            {
                Log("(no profile changes — every row was already in profile)");
            }
        }

        var cancelled = ct.IsCancellationRequested;
        Log(cancelled ? "✗ Cancelled — partial changes may have been applied." : "✓ Import done.");

        // Refresh even when nothing was applied: a cancelled run may
        // still have put files on disk.
        Refresh();
        return new ImportResult(!cancelled && profileDirty, cancelled, installedIds, lines);
    }

    /// <summary>Downloads one entry to `dst`. A list that pins versions
    /// gets the entry's exact version, falling back to the newest when
    /// the host no longer has it. When the list carries a checksum for
    /// the mod, a file that doesn't match is deleted and the download
    /// fails.</summary>
    private async Task DownloadImportEntryAsync(
        ModListImport import, ImportEntry entry, ModSource source, string dst, Action<string> log, CancellationToken ct)
    {
        var pinned = import.PinVersions && !string.IsNullOrWhiteSpace(entry.Version) ? entry.Version.Trim() : null;
        VmFile file;
        try
        {
            file = await ProfilesDownloadAsync(source.Id, dst, pinned, ct);
        }
        catch (VostokModsException ex) when (pinned != null && ex.Kind == VostokModsError.VersionNotFound)
        {
            log($"  ! v{pinned} is no longer on VostokMods — downloading the newest version instead.");
            pinned = null;
            file = await ProfilesDownloadAsync(source.Id, dst, null, ct);
        }

        // A checksum describes the pinned file only.
        if (pinned == null || !import.Checksums.TryGetValue(source.Key, out var expected)) return;
        string actual;
        using (var fs = File.OpenRead(dst))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(dst); } catch { /* best-effort */ }
        throw new InvalidDataException(
            $"the downloaded file (v{file.Version}) does not match the modpack's checksum, so it was discarded.");
    }

    /// <summary>The just-installed registry entry for an import row: by
    /// mod_id, then source, then name.</summary>
    private ModEntry? ImportsResolveRegistryEntry(ImportEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ModId))
        {
            var byId = Registry.Entries.FirstOrDefault(e =>
                string.Equals(e.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        var source = entry.SourceRef;
        if (source.IsValid)
        {
            var bySource = Registry.Entries.FirstOrDefault(e =>
                ImportsSameSource(e.Source, source) && !string.IsNullOrEmpty(e.ModId));
            if (bySource != null) return bySource;
        }
        return ImportsMatchLiveByName(entry);
    }

    /// <summary>The one live mod whose name or id equals the entry's
    /// name, id or slug, ignoring case, spacing and punctuation; null
    /// when none or several do. A pack names a mod by its site page and
    /// a hand-installed mod carries no source, so they can only meet here.</summary>
    private ModEntry? ImportsMatchLiveByName(ImportEntry entry)
    {
        var keys = new[] { entry.DisplayName, entry.ModId, entry.SourceRef.Id }
            .Select(ModLinker.Normalize)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return null;
        var hits = Registry.Entries
            .Where(e => !string.IsNullOrEmpty(e.ModId)
                && (keys.Contains(ModLinker.Normalize(e.DisplayName))
                    || keys.Contains(ModLinker.Normalize(e.ModId))))
            .GroupBy(e => e.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return hits.Count == 1 ? hits[0].First() : null;
    }

    /// <summary>True when both sources are valid and name the same mod.
    /// Slugs compare case-insensitively: an import's slug arrives
    /// lowercased while a mod.txt keeps its own casing.</summary>
    private static bool ImportsSameSource(ModSource a, ModSource b) =>
        a.IsValid && b.IsValid
        && a.Provider == b.Provider
        && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private static string ImportsLabel(ImportEntry e) =>
        !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName
        : !string.IsNullOrEmpty(e.ModId) ? e.ModId
        : e.SourceRef.IsValid ? e.SourceRef.Id : "(unknown)";

    /// <summary>Live file name for a copied / downloaded entry: its mod_id,
    /// else its VostokMods slug (as Apply names downloads).</summary>
    private static string ImportsLiveFileName(ImportEntry e)
    {
        var key = !string.IsNullOrEmpty(e.ModId) ? e.ModId : (e.SourceRef.IsValid ? e.SourceRef.Id : "unknown");
        return $"{ModProfile.SafeFileName(key)}.vmz";
    }

    // ── the template ──────────────────────────────────────────────────

    /// <summary>The file name a save dialog suggests for the template.</summary>
    public const string ModListTemplateFileName = "my-mod-pack.json";

    /// <summary>Writes a hand-editable mod-pack JSON template to `path`:
    /// a working example of four mods with comments explaining each
    /// field and placeholder sources to replace with real VostokMods
    /// slugs. Tells the user where it went; false when it couldn't be
    /// written.</summary>
    public bool SaveModListTemplate(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ModListTemplate);
            Prompt.Inform("Template saved",
                $"Saved a starter template to:\n\n{path}\n\n"
                + "Edit the file in any text editor — replace the placeholder `source` values with each "
                + "mod's real VostokMods slug (the last part of its page URL: https://vostokmods.net/mod/<SLUG>).\n\n"
                + "Then either drop the .json on the manager or use Import list to bring those mods "
                + "into your active profile.");
            return true;
        }
        catch (Exception ex)
        {
            Prompt.Inform("Save failed", $"Couldn't write the template:\n\n{ex.Message}", PromptKind.Warning);
            return false;
        }
    }

    // Plain text: a serializer would strip the comment block, which
    // ModListImport's reader skips (ReadCommentHandling = Skip).
    internal const string ModListTemplate =
@"{
  /* Mod pack manifest — drop this file on the mod manager (or use
     Import list…) to merge every listed mod into your ACTIVE profile.

     • source (""vostokmods:<slug>"", where <slug> is the last part of
       the mod's page URL, vostokmods.net/mod/<slug>) drives the
       download. REQUIRED for any mod the recipient doesn't already
       have installed.
     • mod_id is the manifest mod_id (slug, from mod.txt). If
       you don't know it, leave it as a hint and we'll reconcile
       to the real id after the download.
     • display_name is just for the import-plan dialog header.
     • version, is_enabled, priority are optional.
     • The `dependencies` array nests the same shape; the
       importer flattens it so you can structure related mods
       hierarchically OR list them all top-level — either works. */
  ""name"": ""My mod pack"",
  ""description"": ""Description shown at the top of the import dialog."",
  ""mods"": [
    {
      ""mod_id"": ""my-first-mod"",
      ""display_name"": ""My First Mod"",
      ""source"": ""vostokmods:my-first-mod"",
      ""version"": ""1.0.0"",
      ""is_enabled"": true,
      ""priority"": 0
    },
    {
      ""mod_id"": ""my-second-mod"",
      ""display_name"": ""My Second Mod"",
      ""source"": ""vostokmods:my-second-mod""
    },
    {
      ""mod_id"": ""my-third-mod"",
      ""display_name"": ""My Third Mod"",
      ""source"": ""vostokmods:my-third-mod""
    },
    {
      ""mod_id"": ""my-fourth-mod"",
      ""display_name"": ""My Fourth Mod"",
      ""source"": ""vostokmods:my-fourth-mod""
    }
  ]
}
";
}
