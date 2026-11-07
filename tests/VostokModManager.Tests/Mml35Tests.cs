using VostokModManager.Domain;

namespace VostokModManager.Tests;

/// <summary>What Metro Mod Loader 3.5.0 changed for the manager: the hook
/// lines in the log, the override.cfg merge and the deferred MCM switch.</summary>
public class Mml35Tests
{
    private const string Template = """
        [autoload_prepend]
        ModLoader="*res://modloader.gd"

        [autoload]
        """;

    [Fact]
    public void Override_merge_keeps_the_users_sections_and_drops_the_old_loader_autoload()
    {
        var user = "[display]\r\n\r\nwindow/size/mode=3\r\n\r\n[autoload]\r\n\r\nModLoader=\"*res://old/modloader.gd\"\r\nOther=\"*res://other.gd\"\r\n";
        var merged = OverrideCfg.Merge(user, Template);

        Assert.Contains("window/size/mode=3", merged);
        Assert.Contains("Other=\"*res://other.gd\"", merged);
        Assert.DoesNotContain("res://old/modloader.gd", merged);
        Assert.Contains("[autoload_prepend]\r\n\r\nModLoader=\"*res://modloader.gd\"", merged);
        Assert.True(OverrideCfg.LooksLikeLoaderTemplate(merged));
        Assert.DoesNotContain("\n\n\n", merged.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Override_merge_forces_the_template_value_in_place()
    {
        var user = "[autoload_prepend]\nModLoader=\"*res://stale.gd\"\nKeep=\"*res://keep.gd\"\n";
        var merged = OverrideCfg.Merge(user, Template);

        Assert.Equal("[autoload_prepend]\nModLoader=\"*res://modloader.gd\"\nKeep=\"*res://keep.gd\"\n\n[autoload]\n\n", merged);
    }

    [Fact]
    public void Hook_lines_from_3_5_are_read_and_a_wildcard_joins_named_methods()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vmm-mml35-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "godot.log");
            File.WriteAllText(log, """
                Godot Engine v4.6.3.stable.official - https://godotengine.org
                [ModLoader][Info] Metro Mod Loader v3.5.0
                [ModLoader][Debug]   [hooks] res://Scripts/Camera.gd :: _ready, Shake [Alpha]
                [ModLoader][Debug]   [hooks] res://Scripts/Camera.gd :: _ready [Beta]
                [ModLoader][Debug]   [hooks] res://Scripts/Camera.gd :: * [Gamma]
                [ModLoader][Info]   [hooks] 2 method(s) across 1 script(s) [Alpha]
                """.Replace("\r\n", "\n"));

            var a = GodotLogAnalyzer.Analyze(log);

            Assert.Equal("3.5.0", a.LoaderVersion);
            Assert.Equal(4, a.Hooks.Count);
            Assert.False(a.HooksNotLogged);
            var ready = Assert.Single(a.HookClashes, c => c.Method == "_ready");
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, ready.Mods);
            var shake = Assert.Single(a.HookClashes, c => c.Method == "Shake");
            Assert.Equal(new[] { "Alpha", "Gamma" }, shake.Mods);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("B", "\"A\"", "\"B\"")]   // a second switch moves the pending target
    [InlineData("A", null, null)]         // switching back to `from` cancels it
    public void A_switch_here_retargets_a_pending_MCM_switch(string switchTo, string? expectFrom, string? expectTo)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vmm-mml35-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "mod_config.cfg");
            File.WriteAllText(path,
                "[settings]\n\nactive_profile=\"Menu pick\"\nmcm_pending_from=\"A\"\nmcm_pending_to=\"Menu pick\"\n");
            var cfg = ModConfig.Load(path);
            cfg.ActiveProfile = switchTo;
            cfg.Save(path);

            var text = File.ReadAllText(path);
            Assert.Contains($"active_profile=\"{switchTo}\"", text);
            Assert.Equal(expectFrom, Value(text, "mcm_pending_from"));
            Assert.Equal(expectTo, Value(text, "mcm_pending_to"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_switch_without_a_pending_MCM_switch_adds_none()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vmm-mml35-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "mod_config.cfg");
            File.WriteAllText(path, "[settings]\n\nactive_profile=\"A\"\n");
            var cfg = ModConfig.Load(path);
            cfg.ActiveProfile = "B";
            cfg.Save(path);
            Assert.DoesNotContain("mcm_pending", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? Value(string cfg, string key)
        => cfg.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "="))?[(key.Length + 1)..];
}
