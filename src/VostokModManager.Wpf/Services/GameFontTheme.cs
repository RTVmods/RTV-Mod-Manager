using System.IO;
using System.Windows;
using System.Windows.Media;
using VostokModManager.Services;

namespace VostokModManager.Wpf.Services;

/// <summary>Points the theme's text font (the Font.Lora key) at the game's
/// font from the Custom Fonts mod, or back at Lora. Every view takes that
/// key as a DynamicResource, so the whole window changes at once.</summary>
internal static class GameFontTheme
{
    private const string Key = "Font.Lora";
    private const string LoraUri = "pack://application:,,,/Fonts/#Lora";
    private static FontFamily? _lora;

    /// <summary>The name of the font the manager shows now.</summary>
    public static string Current { get; private set; } = "Lora";

    public static void Apply(GameFont? font)
    {
        var dict = FindDictionary(Application.Current?.Resources);
        if (dict == null) return;
        _lora ??= (FontFamily)dict[Key];

        var family = _lora;
        var name = "Lora";
        if (font is { IsGameDefault: false } && Load(font.Files) is { } loaded)
        {
            family = loaded;
            name = font.Title;
        }
        if (!ReferenceEquals(dict[Key], family)) dict[Key] = family;
        Current = name;
    }

    /// <summary>The family in the extracted font files, falling back to Lora
    /// for any character it lacks. Null when the files can't be read.</summary>
    private static FontFamily? Load(IReadOnlyList<string> files)
    {
        try
        {
            var face = new GlyphTypeface(new Uri(files[0]));
            var familyName = face.FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var n)
                ? n : face.FamilyNames.Values.FirstOrDefault();
            if (string.IsNullOrEmpty(familyName)) return null;
            var folder = new Uri(Path.GetDirectoryName(files[0])!.TrimEnd('\\') + "\\");
            return new FontFamily(folder, $"./#{familyName}, {LoraUri}");
        }
        catch
        {
            return null;
        }
    }

    private static ResourceDictionary? FindDictionary(ResourceDictionary? d)
    {
        if (d == null) return null;
        foreach (var m in d.MergedDictionaries)
            if (FindDictionary(m) is { } found) return found;
        return d.Contains(Key) ? d : null;
    }
}
