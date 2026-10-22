using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class CheckpointTests
{
    private const string CfgOn = "[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=true\n";
    private const string CfgOff = "[settings]\n\nactive_profile=\"Default\"\n\n[profile.Default.enabled]\n\nalpha@1.0=false\n";

    [Fact]
    public void No_checkpoint_hides_the_row()
    {
        using var sb = new Sandbox();
        var session = LaunchKit.Open(sb, new ScriptedPrompt(), new RecordingLauncher());

        var status = session.GetCheckpointStatus();

        Assert.False(status.Any);
        Assert.Equal("", status.Text);
        Assert.False(session.RestoreCheckpoint());
    }

    [Fact]
    public void One_checkpoint_restores_directly_after_confirming()
    {
        using var sb = new Sandbox();
        sb.WriteCfg(CfgOn);
        var prompt = new ScriptedPrompt();
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());
        Assert.True(CrashCheckpoint.Capture(ModConfig.DefaultPath, "Default", session.ActiveProfile!.FolderPath));
        sb.WriteCfg(CfgOff);

        var status = session.GetCheckpointStatus();
        Assert.True(status.HasLastLaunch);
        Assert.False(status.NeedsPick);
        Assert.Contains("Last-launch checkpoint (0m ago)", status.Text);

        Assert.True(session.RestoreCheckpoint());

        Assert.DoesNotContain(prompt.Asked, a => a.StartsWith("choose:"));
        Assert.Contains("confirm:Restore last-launch checkpoint", prompt.Asked);
        Assert.True(sb.LoadCfg().IsEnabled("alpha", "1.0"));
        Assert.False(session.GetCheckpointStatus().Any);
    }

    [Fact]
    public void Both_checkpoints_ask_which_and_keep_the_other()
    {
        using var sb = new Sandbox();
        sb.WriteCfg(CfgOn);
        var prompt = new ScriptedPrompt { ChooseAnswer = 1 };
        var session = LaunchKit.Open(sb, prompt, new RecordingLauncher());
        CrashCheckpoint.Capture(ModConfig.DefaultPath, "Default", "");
        CrashCheckpoint.PromoteToKnownGood();         // known-good: alpha on
        sb.WriteCfg(CfgOff);
        CrashCheckpoint.Capture(ModConfig.DefaultPath, "Default", "");  // last-launch: alpha off

        Assert.True(session.GetCheckpointStatus().NeedsPick);
        Assert.True(session.RestoreCheckpoint());

        Assert.Contains("choose:Restore checkpoint — pick a slot", prompt.Asked);
        Assert.True(sb.LoadCfg().IsEnabled("alpha", "1.0"));
        Assert.True(CrashCheckpoint.Exists(CheckpointSlot.LastLaunch));
        Assert.False(CrashCheckpoint.Exists(CheckpointSlot.LastKnownGood));
    }

    [Fact]
    public void Declining_the_restore_changes_nothing()
    {
        using var sb = new Sandbox();
        sb.WriteCfg(CfgOn);
        var session = LaunchKit.Open(sb, new ScriptedPrompt { ConfirmAnswer = false }, new RecordingLauncher());
        CrashCheckpoint.Capture(ModConfig.DefaultPath, "Default", "");
        sb.WriteCfg(CfgOff);

        Assert.False(session.RestoreCheckpoint(CheckpointSlot.LastLaunch));

        Assert.False(sb.LoadCfg().IsEnabled("alpha", "1.0"));
        Assert.True(CrashCheckpoint.Exists(CheckpointSlot.LastLaunch));
    }
}
