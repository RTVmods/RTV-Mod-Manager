// A throwaway copy of everything the manager touches on disk: a fake
// roaming AppData (manager settings, profiles, the game's mod_config.cfg)
// and a fake game folder with a mods\ directory. Points AppPaths at it
// for the lifetime of the test, so nothing ever reaches the real files.

using System.IO.Compression;
using System.Text;
using VostokModManager.Domain;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VostokModManager.Tests;

public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public string AppData { get; }
    public string GameDir { get; }
    public string ModsDir { get; }

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "vmm-test-" + Guid.NewGuid().ToString("N")[..10]);
        AppData = Path.Combine(Root, "AppData", "Roaming");
        GameDir = Path.Combine(Root, "steamapps", "common", "Road to Vostok");
        ModsDir = Path.Combine(GameDir, "mods");
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(ModsDir);
        AppPaths.OverrideRoot = AppData;
        Directory.CreateDirectory(AppPaths.GameUserDir);
        Directory.CreateDirectory(AppPaths.ManagerDir);
    }

    public string CfgPath => ModConfig.DefaultPath;

    public ModConfig LoadCfg() => ModConfig.Load(CfgPath);

    public void WriteCfg(string text) => File.WriteAllText(CfgPath, text.Replace("\r\n", "\n"));

    /// <summary>Writes a minimal .vmz (a zip holding mod.txt plus any
    /// extra files) into mods\ (or mods\Disabled\) and returns its path.</summary>
    public string AddMod(
        string id, string version, string? name = null, int? priority = null,
        string? extraManifest = null, IDictionary<string, string>? files = null,
        bool inDisabledFolder = false, string? fileName = null, string? dir = null)
    {
        var target = dir ?? (inDisabledFolder ? Path.Combine(ModsDir, "Disabled") : ModsDir);
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, fileName ?? $"{id}-{version}.vmz");
        File.WriteAllBytes(path, BuildVmz(id, version, name, priority, extraManifest, files));
        return path;
    }

    public static byte[] BuildVmz(
        string id, string version, string? name = null, int? priority = null,
        string? extraManifest = null, IDictionary<string, string>? files = null)
    {
        var manifest = new StringBuilder()
            .Append("[mod]\n")
            .Append($"name=\"{name ?? id}\"\n")
            .Append($"id=\"{id}\"\n")
            .Append($"version=\"{version}\"\n");
        if (priority.HasValue) manifest.Append($"priority={priority.Value}\n");
        if (!string.IsNullOrEmpty(extraManifest)) manifest.Append('\n').Append(extraManifest.Replace("\r\n", "\n")).Append('\n');

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mod.txt", manifest.ToString());
            if (files != null)
                foreach (var (p, text) in files) Write(zip, p, text);
        }
        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string entryPath, string text)
    {
        var e = zip.CreateEntry(entryPath);
        using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
        s.Write(text);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        try { Directory.Delete(Root, recursive: true); } catch { /* temp; best effort */ }
    }
}
