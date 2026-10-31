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
        if (import != null)
            await RunAsync(import, Path.GetDirectoryName(path), isModpack: !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A VostokMods modpack by slug.</summary>
    public async Task RunModpackAsync(string slug)
    {
        var import = await _session.LoadModpackAsync(slug);
        if (import != null) await RunAsync(import, null, isModpack: true);
    }

    public async Task RunAsync(ModListImport import, string? companionDir, bool isModpack = true)
    {
        var options = _session.GetImportTargetOptions(import.Name);
        if (options == null) return;
        // What the active profile is missing, shown before asking where it goes.
        var preview = _session.BuildImportPlan(import, companionDir);
        var choice = AskTarget(new ImportTargetViewModel(options, preview, isModpack));
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

    private ImportTargetChoice? AskTarget(ImportTargetViewModel vm)
    {
        var o = vm.Options;
        var w = new ImportTargetWindow(vm);
        PromptOwner.Attach(w, _owner());
        using (PromptOwner.Use(_session, w)) w.ShowDialog();
        switch (w.Choice)
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
