// The game's font, when the Custom Fonts mod (Romero, id custom-fonts)
// picks one: the manager's text follows the in-game choice.
//
// The choice is the mod's MCM dropdown, user://MCM/CustomFonts/: MCM
// writes the active loader profile's <Profile>.ini, the mod itself reads
// config.ini, so the newer of the two is what was picked last. The
// mod's FONT_REGISTRY (Config/CustomFontsSettings.gd) maps the choice to
// font files inside the mod, which are Godot imports: the font is
// extracted from the .fontdata each .import points at, once per mod
// version, into the manager's folder. Nothing is bundled with the
// manager; the fonts stay the mod's.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VostokModManager.Api;

namespace VostokModManager.Services;

/// <summary>The font the game uses. `Files` are the extracted font files
/// (all in one folder); empty means the game's own Lora, which the
/// manager already uses.</summary>
public sealed record GameFont(string Id, string Title, IReadOnlyList<string> Files)
{
    public bool IsGameDefault => Files.Count == 0;
}

public sealed partial class ManagerSession
{
    public const string CustomFontsModId = "custom-fonts";

    /// <summary>The font the game shows with the Custom Fonts mod, or null
    /// when the setting is off, the mod isn't on in the active profile,
    /// or its font can't be read.</summary>
    public GameFont? CurrentGameFont()
    {
        if (!Settings.FollowGameFont) return null;
        var mod = Registry.FindById(CustomFontsModId);
        if (mod is not { IsEnabled: true } || !File.Exists(mod.Path)) return null;
        try
        {
            var choice = ReadCustomFontsChoice(Path.Combine(McmDir, "CustomFonts"), Config.ActiveProfile);
            return LoadCustomFont(mod.Path, mod.Version, choice,
                Path.Combine(AppPaths.ManagerDir, "GameFonts"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Everything CurrentGameFont depends on, as one string: the
    /// front end re-reads the font only when this changes.</summary>
    public string GameFontSignature()
    {
        var mod = Registry.FindById(CustomFontsModId);
        var mcm = Path.Combine(McmDir, "CustomFonts");
        static string Stamp(string f) => File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks.ToString() : "-";
        return string.Join("|", Settings.FollowGameFont, mod?.Path, mod?.IsEnabled, mod?.Version, Config.ActiveProfile,
            Stamp(Path.Combine(mcm, Config.ActiveProfile + ".ini")), Stamp(Path.Combine(mcm, "config.ini")));
    }

    /// <summary>The font id picked in the mod's MCM settings: the newer of
    /// the active profile's file and config.ini. Null when neither says.</summary>
    internal static string? ReadCustomFontsChoice(string mcmDir, string activeProfile)
    {
        var files = new[] { Path.Combine(mcmDir, activeProfile + ".ini"), Path.Combine(mcmDir, "config.ini") }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc);
        foreach (var f in files)
            if (DropdownValue(File.ReadAllText(f), "font_family") is { Length: > 0 } id) return id;
        return null;
    }

    /// <summary>The `value` of an MCM dropdown in a Godot ConfigFile
    /// (`key={ ... &"value": "x" ... }`), or null.</summary>
    internal static string? DropdownValue(string ini, string key)
    {
        var block = Block(ini, key + "={");
        if (block == null) return null;
        // Only the dropdown's own keys: skip its nested options dictionary.
        var own = Regex.Replace(block, @"\{[^{}]*\}", "");
        var m = Regex.Match(own, "&?\"value\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Extracts the picked font (or the mod's default) from the
    /// mod's archive into `cacheRoot`, once per mod version. Null when the
    /// archive has no usable registry or font.</summary>
    internal static GameFont? LoadCustomFont(string archive, string version, string? choice, string cacheRoot)
    {
        using var zip = ZipFile.OpenRead(archive);
        var settings = ReadEntry(zip, "mods/CustomFonts/Config/CustomFontsSettings.gd");
        if (settings == null) return null;
        var text = Encoding.UTF8.GetString(settings);

        var id = choice ?? "";
        var entry = id.Length > 0 ? Block(text, $"\"{id}\": {{") : null;
        if (entry == null)
        {
            var fallback = Regex.Match(text, "default_font_id\\s*:\\s*String\\s*=\\s*\"([^\"]+)\"");
            if (!fallback.Success) return null;
            id = fallback.Groups[1].Value;
            entry = Block(text, $"\"{id}\": {{");
            if (entry == null) return null;
        }
        var title = Field(entry, "title") ?? id;
        var paths = new[] { "regular", "medium", "bold" }
            .Select(k => Field(entry, k))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) return null;
        // The game's own font (Lora under res://Fonts/): nothing to extract.
        if (paths.All(p => p.StartsWith("res://Fonts/", StringComparison.OrdinalIgnoreCase)))
            return new GameFont(id, title, Array.Empty<string>());

        var dir = Path.Combine(cacheRoot, $"{CustomFontsModId}-{Safe(version)}", Safe(id));
        var files = new List<string>();
        foreach (var res in paths)
        {
            var target = Path.Combine(dir, Path.GetFileName(res));
            if (!File.Exists(target))
            {
                var font = ExtractImportedFont(zip, res);
                if (font == null) continue;
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(target, font);
            }
            files.Add(target);
        }
        return files.Count == 0 ? null : new GameFont(id, title, files);
    }

    /// <summary>The font file behind `res://…/Name.ttf`: its .import names
    /// the imported resource, which holds the original bytes.</summary>
    private static byte[]? ExtractImportedFont(ZipArchive zip, string resPath)
    {
        var import = ReadEntry(zip, resPath["res://".Length..] + ".import");
        if (import == null) return null;
        var m = Regex.Match(Encoding.UTF8.GetString(import), "^path=\"res://([^\"]+)\"", RegexOptions.Multiline);
        if (!m.Success) return null;
        var resource = ReadEntry(zip, m.Groups[1].Value);
        return resource == null ? null : GodotFontData.ExtractFont(resource);
    }

    private static byte[]? ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name)
            ?? zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (e == null) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The text between the brace that ends `opener` and the
    /// brace that closes it, or null.</summary>
    private static string? Block(string text, string opener)
    {
        var start = text.IndexOf(opener, StringComparison.Ordinal);
        if (start < 0) return null;
        var i = start + opener.Length;
        for (int depth = 1, j = i; j < text.Length; j++)
        {
            if (text[j] == '{') depth++;
            else if (text[j] == '}' && --depth == 0) return text[i..j];
        }
        return null;
    }

    private static string? Field(string block, string key)
    {
        var m = Regex.Match(block, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Safe(string s)
        => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
