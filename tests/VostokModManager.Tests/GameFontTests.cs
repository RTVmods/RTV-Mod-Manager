using System.IO.Compression;
using System.Text;
using VostokModManager.Api;
using VostokModManager.Services;

namespace VostokModManager.Tests;

public class GameFontTests
{
    /// <summary>Enough of an OpenType file for the extractor: the tag and a
    /// table count.</summary>
    private static byte[] FakeFont(byte fill)
    {
        var f = Enumerable.Repeat(fill, 3000).ToArray();
        Encoding.ASCII.GetBytes("OTTO").CopyTo(f, 0);
        f[4] = 0; f[5] = 10;
        return f;
    }

    /// <summary>A FontFile resource the way Godot writes one: the font as a
    /// length-prefixed byte array among other data, zstd-compressed in
    /// 4 KB blocks behind an RSCC header.</summary>
    private static byte[] ImportedFont(byte[] font)
    {
        var raw = new MemoryStream();
        raw.Write(Encoding.ASCII.GetBytes("RSRC" + new string('x', 60)));
        raw.Write(BitConverter.GetBytes((uint)font.Length));
        raw.Write(font);
        raw.Write(new byte[100]);
        var data = raw.ToArray();

        const int block = 4096;
        var blocks = data.Length / block + 1;
        using var z = new ZstdSharp.Compressor();
        var packed = Enumerable.Range(0, blocks)
            .Select(i => z.Wrap(data.AsSpan(i * block, Math.Min(block, data.Length - i * block))).ToArray())
            .ToList();
        var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("RSCC"));
        foreach (var v in new[] { 2u, (uint)block, (uint)data.Length }) file.Write(BitConverter.GetBytes(v));
        foreach (var p in packed) file.Write(BitConverter.GetBytes((uint)p.Length));
        foreach (var p in packed) file.Write(p);
        return file.ToArray();
    }

    private const string Registry = """
        @export var default_font_id: String = "inter"
        @export var FONT_REGISTRY : Dictionary = {
            "bender": {
                "title": "Bender",
                "regular": "res://mods/CustomFonts/Fonts/Bender/Bender-Bold.otf",
                "bold": "res://mods/CustomFonts/Fonts/Bender/Bender-Bold.otf",
                "default_font_size": 15
            },
            "lora": {
                "title": "Lora",
                "regular": "res://Fonts/Lora-Regular.ttf",
                "bold": "res://Fonts/Lora-SemiBold.ttf",
                "default_font_size": 16
            },
            "inter": {
                "title": "Inter",
                "regular": "res://mods/CustomFonts/Fonts/Inter/Inter-Regular.ttf",
                "bold": "res://mods/CustomFonts/Fonts/Inter/Inter-Bold.ttf",
                "default_font_size": 15
            }
        }
        """;

    private static string FontsMod(Sandbox sb)
    {
        var path = Path.Combine(sb.Root, "custom-fonts.vmz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
        Put("mods/CustomFonts/Config/CustomFontsSettings.gd", Encoding.UTF8.GetBytes(Registry));
        foreach (var (res, fill) in new[] { ("Bender/Bender-Bold.otf", (byte)1), ("Inter/Inter-Regular.ttf", (byte)2), ("Inter/Inter-Bold.ttf", (byte)3) })
        {
            var imported = $".godot/imported/{Path.GetFileName(res)}-0123.fontdata";
            Put($"mods/CustomFonts/Fonts/{res}.import",
                Encoding.UTF8.GetBytes($"[remap]\n\nimporter=\"font_data_dynamic\"\npath=\"res://{imported}\"\n"));
            Put(imported, ImportedFont(FakeFont(fill)));
        }
        return path;
    }

    [Fact]
    public void The_font_comes_back_out_of_a_compressed_godot_import()
    {
        var font = FakeFont(7);
        Assert.Equal(font, GodotFontData.ExtractFont(ImportedFont(font)));
        Assert.Null(GodotFontData.ExtractFont(Encoding.ASCII.GetBytes("RSRC no font in here at all")));
    }

    [Fact]
    public void The_picked_font_is_extracted_once_and_lora_is_the_games_own()
    {
        using var sb = new Sandbox();
        var mod = FontsMod(sb);
        var cache = Path.Combine(sb.Root, "fonts");

        var bender = ManagerSession.LoadCustomFont(mod, "1.2.0", "bender", cache)!;
        Assert.Equal("Bender", bender.Title);
        Assert.Equal(FakeFont(1), File.ReadAllBytes(Assert.Single(bender.Files)));

        var lora = ManagerSession.LoadCustomFont(mod, "1.2.0", "lora", cache)!;
        Assert.True(lora.IsGameDefault);

        // An id the mod doesn't know falls back to its default, as in game.
        var fallback = ManagerSession.LoadCustomFont(mod, "1.2.0", "gone", cache)!;
        Assert.Equal("inter", fallback.Id);
        Assert.Equal(2, fallback.Files.Count);
        Assert.All(fallback.Files, f => Assert.StartsWith(Path.Combine(cache, "custom-fonts-1.2.0", "inter"), f));
    }

    [Fact]
    public void The_choice_is_read_from_the_newer_of_the_profile_and_config_files()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "MCM", "CustomFonts");
        Directory.CreateDirectory(dir);
        static string Ini(string value) => "[Dropdown]\n\nfont_family={\n\"category\": \"General\",\n&\"default\": \"jetbrains_mono\",\n"
            + "&\"options\": {\n\"bender\": \"Bender\",\n\"value\": \"not this one\"\n},\n&\"value\": \"" + value + "\"\n}\n";
        File.WriteAllText(Path.Combine(dir, "config.ini"), Ini("bender"));
        File.SetLastWriteTimeUtc(Path.Combine(dir, "config.ini"), DateTime.UtcNow.AddHours(-1));
        Assert.Equal("bender", ManagerSession.ReadCustomFontsChoice(dir, "Testing Profile"));

        File.WriteAllText(Path.Combine(dir, "Testing Profile.ini"), Ini("inter"));
        Assert.Equal("inter", ManagerSession.ReadCustomFontsChoice(dir, "Testing Profile"));
        Assert.Null(ManagerSession.ReadCustomFontsChoice(Path.Combine(sb.Root, "none"), "Default"));
    }
}
