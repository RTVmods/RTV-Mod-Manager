// Applying a change made on the Settings screen: the built-in save slots
// switch, the mods folder (validated, reverted when missing), and in the
// AI edition the Claude CLI path and the Decomp/ folder.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>The settings the Settings screen edits.</summary>
public sealed record SettingsValues(
    string ModsDir,
    string ClaudePath,
    string GameSourcePath,
    bool SkipLoaderUi,
    bool SaveSlotsBuiltIn)
{
    public static SettingsValues From(Settings s)
        => new(s.ModsDir, s.ClaudePath, s.GameSourcePath, s.SkipLoaderUi, s.SaveSlotsBuiltIn);

    internal void WriteTo(Settings s)
    {
        s.ModsDir = ModsDir;
        s.ClaudePath = ClaudePath;
        s.GameSourcePath = GameSourcePath;
        s.SkipLoaderUi = SkipLoaderUi;
        s.SaveSlotsBuiltIn = SaveSlotsBuiltIn;
    }
}

/// <param name="Saved">settings.json was written.</param>
/// <param name="RearmFolderWatcher">The mods folder moved: the UI must
/// point its file-system watcher at ModsDir again, or it keeps watching
/// the old location and misses every change in the new one.</param>
/// <param name="ModsDirReverted">The new mods folder didn't exist and the
/// previous value was kept (the user was told).</param>
public sealed record SettingsApplyResult(bool Saved, bool RearmFolderWatcher, bool ModsDirReverted);

public sealed partial class ManagerSession
{
    /// <summary>Applies the Settings screen's Save. `after` is written
    /// into Settings (a no-op when the screen already edited Settings in
    /// place); `before` is what they were when the screen opened, so only
    /// what changed is acted on.</summary>
    public SettingsApplyResult ApplySettingsChange(SettingsValues before, SettingsValues after)
    {
        after.WriteTo(Settings);

        if (Settings.SaveSlotsBuiltIn != before.SaveSlotsBuiltIn)
        {
            try { Settings.Save(); } catch { /* saved again below */ }
            if (!Settings.SaveSlotsBuiltIn) RemoveBuiltInSaveSlotsFromCfg();
            Rescan();
            if (Settings.SaveSlotsBuiltIn) SetUpBuiltInSaveSlots();
            Requery();
        }

        // Validate the mods folder before persisting; when it's missing,
        // revert that one field so nothing scans a missing folder. The
        // other paths can be empty (auto-detect) or optional (Decomp).
        var reverted = false;
        if (!string.IsNullOrEmpty(Settings.ModsDir) && !Directory.Exists(Settings.ModsDir))
        {
            Prompt.Inform("Mods folder not found",
                $"`{Settings.ModsDir}` doesn't exist. Reverting Mods "
                + "folder to the previous value. Pick the `mods/` "
                + "folder inside your Road to Vostok install.",
                PromptKind.Warning);
            Settings.ModsDir = before.ModsDir;
            reverted = true;
        }

        try { Settings.Save(); }
        catch (Exception ex)
        {
            Prompt.Error("Couldn't save settings", ex);
            return new SettingsApplyResult(false, false, reverted);
        }

#if AI_RESOLVER
        if (Settings.ClaudePath != before.ClaudePath)
        {
            Claude.OverridePath = Settings.ClaudePath;
            DetectClaude();
            Say(StatusArea.Claude, ClaudeStatusText());
            RaiseChanged();  // the Resolve buttons follow Claude's availability
        }
        if (Settings.GameSourcePath != before.GameSourcePath)
            Resolver.GameSourcePath = Settings.GameSourcePath;
#endif
        var moved = Settings.ModsDir != before.ModsDir;
        if (moved)
        {
            // The loader version is read from the game folder beside the
            // mods folder; profiles written from now on carry the new one.
            MetroProfile.ModloaderVersion = MmlInstall.DetectInstalledVersion(ModsDir);
            Refresh();
        }
        else RaiseChanged();  // the setup notes may have changed
        return new SettingsApplyResult(true, moved, reverted);
    }
}
