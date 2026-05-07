// Reads a Road to Vostok .vmz archive (zip) and parses its mod.txt.
//
// mod.txt is in Godot's ConfigFile format — INI-ish but with quoted
// string keys (used for paths like "res://Scripts/Handling.gd") and
// Godot-typed values (we keep everything as strings; consumers
// int.TryParse / etc. as needed).
//
// Manifest shape returned:
//   {
//     "mod":           {id, name, version, priority?, description?, author?, url?},
//     "autoload":      {<Name>: <res:// path>, ...},
//     "hooks":         {<res:// target>: <method>, ...},
//     "script_extend": {<res:// target>: <res:// override>, ...},
//     "updates":       {modworkshop: <int>, ...},
//   }
// Sections that aren't present in the file are simply absent from the dict.

using System.IO.Compression;

namespace VostokModManager.Domain;

public class ModArchive : IDisposable
{
    public string Path { get; private set; } = "";
    private FileStream? _stream;
    private ZipArchive? _zip;
    private List<string>? _files;
    private Dictionary<string, Dictionary<string, string>>? _manifest;

    public bool Open(string path)
    {
        Path = path;
        try
        {
            _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _zip = new ZipArchive(_stream, ZipArchiveMode.Read);
            _files = _zip.Entries.Select(e => e.FullName).ToList();
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        _zip?.Dispose();
        _stream?.Dispose();
        _zip = null;
        _stream = null;
    }

    public bool IsOpen => _zip != null;

    public IReadOnlyList<string> FileList()
        => (IReadOnlyList<string>?)_files ?? Array.Empty<string>();

    public bool HasFile(string filePath)
        => _files != null && _files.Contains(filePath);

    public string ReadText(string filePath)
    {
        if (_zip == null) return "";
        var entry = _zip.GetEntry(filePath);
        if (entry == null) return "";
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    public byte[] ReadBytes(string filePath)
    {
        if (_zip == null) return Array.Empty<byte>();
        var entry = _zip.GetEntry(filePath);
        if (entry == null) return Array.Empty<byte>();
        try
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public Dictionary<string, Dictionary<string, string>> GetManifest()
    {
        if (_manifest != null) return _manifest;
        if (_zip == null) return new();
        if (!HasFile("mod.txt")) return new();
        var text = ReadText("mod.txt");
        _manifest = ParseConfigFile(text);
        return _manifest;
    }

    public string ModId
        => GetMod("id");
    public string ModName
        => GetMod("name");
    public string ModVersion
        => GetMod("version");
    public int ModPriority
        => int.TryParse(GetMod("priority"), out var p) ? p : 0;
    public string ModDescription
        => GetMod("description");
    public int ModWorkshopId
        => int.TryParse(GetUpdates("modworkshop"), out var i) ? i : 0;

    private string GetMod(string key)
    {
        var m = GetManifest();
        return m.TryGetValue("mod", out var sec) && sec.TryGetValue(key, out var v)
            ? v
            : "";
    }

    private string GetUpdates(string key)
    {
        var m = GetManifest();
        return m.TryGetValue("updates", out var sec) && sec.TryGetValue(key, out var v)
            ? v
            : "";
    }

    /// <summary>Hand-rolled parser for Godot ConfigFile syntax — INI
    /// with quoted string keys and values. We don't use the System.IO
    /// nor any third-party INI parser because Godot's quirks (quoted
    /// keys for paths, semicolon comments, blank lines) need
    /// specific handling.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseConfigFile(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith(";") || line.StartsWith("#")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var section = line.Substring(1, line.Length - 2);
                if (!result.ContainsKey(section))
                    result[section] = new Dictionary<string, string>();
                current = result[section];
                continue;
            }
            if (current == null) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();
            current[StripQuotes(key)] = StripQuotes(value);
        }
        return result;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            return s.Substring(1, s.Length - 2);
        return s;
    }
}
