using System.IO;
using System.Windows;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.ViewModels;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf.Services;

/// <summary>Bringing a mod list or modpack into a profile, start to end:
/// read it, ask which profile it goes to, show the plan, run it, then
/// check for updates and missing dependencies. Used by file drops, the
/// Browse page and the Profiles page.</summary>
public sealed class ImportFlow
{
    private readonly ManagerSession _session;
    private readonly Func<Window?> _owner;

    public ImportFlow(ManagerSession session, Func<Window?> owner)
    {
        _session = session;
        _owner = owner;
    }

    /// <summary>A dropped or picked file: a mod-list .json or a modpack .zip.</summary>
    public async Task RunFileAsync(string path)
    {
        var import = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? _session.LoadModListFile(path)
            : _session.LoadModpackFile(path);
        if (import != null) await RunAsync(import, Path.GetDirectoryName(path));
    }

    /// <summary>A VostokMods modpack by slug.</summary>
    public async Task RunModpackAsync(string slug)
    {
        var import = await _session.LoadModpackAsync(slug);
        if (import != null) await RunAsync(import, null);
    }

    public async Task RunAsync(ModListImport import, string? companionDir)
    {
        var options = _session.GetImportTargetOptions(import.Name);
        if (options == null) return;
        var choice = AskTarget(options);
        if (choice == null) return;
        if (!_session.SelectImportTarget(choice, import)) return;

        var plan = _session.BuildImportPlan(import, companionDir);
        if (plan == null) return;
        var vm = new ImportPlanViewModel(_session, plan);
        new ImportPlanWindow(vm) { Owner = _owner() }.ShowDialog();

        if (vm.Result is { Applied: true } result)
        {
            await _session.CheckUpdatesAsync();
            if (result.InstalledModIds.Count > 0)
                await _session.CheckAndPromptMissingDepsAsync(result.InstalledModIds, MissingDependenciesWindow.Resolver(_owner));
        }
    }

    private ImportTargetChoice? AskTarget(ImportTargetOptions o)
    {
        var options = new List<string>
        {
            $"Add to '{o.Active.Name}'",
            $"New profile '{o.SuggestedNewName}'",
        };
        if (o.Others.Count > 0) options.Add("Another profile…");
        var pick = _session.Prompt.Choose("Where should it go?",
            $"'{(string.IsNullOrEmpty(o.PackName) ? "This list" : o.PackName)}' can join the active profile, "
            + "start a new profile of its own, or go into another saved profile (which then becomes active).",
            options);
        switch (pick)
        {
            case 0: return new ImportTargetChoice(ImportTarget.ActiveProfile);
            case 1:
                var name = _session.Prompt.AskText("New profile", "Name for the new profile:", o.SuggestedNewName);
                return string.IsNullOrWhiteSpace(name) ? null : new ImportTargetChoice(ImportTarget.NewProfile, name.Trim());
            case 2:
                var others = o.Others.Select(p => p.Name).ToList();
                var which = _session.Prompt.Choose("Which profile?", "The import goes into this profile, which becomes active.", others);
                return which < 0 ? null : new ImportTargetChoice(ImportTarget.ExistingProfile, ExistingProfile: o.Others[which]);
            default:
                return null;
        }
    }
}
