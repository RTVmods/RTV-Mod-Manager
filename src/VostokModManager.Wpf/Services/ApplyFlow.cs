using System.Windows;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.ViewModels;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf.Services;

/// <summary>Applying a profile to the mods folder, start to end: plan it,
/// show the plan, run it if asked, then check for updates as a switch
/// would. Used by the Profiles page and the Loadout page's drift review.</summary>
public static class ApplyFlow
{
    /// <summary>False when the plan couldn't be made or was closed without
    /// running; true once it ran (whatever it managed).</summary>
    public static async Task<bool> RunAsync(ManagerSession session, ModProfile profile)
    {
        ApplyPlan plan;
        try { plan = session.BuildApplyPlan(profile); }
        catch (Exception ex)
        {
            session.Prompt.Error("Couldn't plan the apply", ex);
            return false;
        }
        var vm = new ApplyPlanViewModel(session, plan);
        new ApplyPlanWindow(vm) { Owner = Application.Current?.MainWindow }.ShowDialog();
        if (!vm.Finished) return false;  // closed without running
        if (vm.Result is { Applied: true }) await session.CheckUpdatesAsync();
        return true;
    }
}
