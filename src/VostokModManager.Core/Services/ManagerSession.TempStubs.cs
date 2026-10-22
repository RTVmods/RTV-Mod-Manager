// TEMP: replaced by the profiles worker's implementation at merge

using VostokModManager.Domain;

namespace VostokModManager.Services;

public sealed partial class ManagerSession
{
    // TEMP: replaced by the profiles worker's implementation at merge
    public bool SwitchActiveProfile(ModProfile target)
    {
        Settings.ActiveProfileName = target.Name;
        ReloadProfiles();
        Refresh();
        return true;
    }
}
