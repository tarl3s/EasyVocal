using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal.Tests;

public class LocalizerTests
{
    [Fact]
    public void T_UsesTableAndFallsBackToEnglish()
    {
        try
        {
            L.Load("{ \"Voices\": \"Vozes\", \"Empty\": \"\", // comment\n }", "pt-BR");
            Assert.Equal("pt-BR", L.Language);
            Assert.Equal("Vozes", L.T("Voices"));
            Assert.Equal("Not in the table", L.T("Not in the table"));
            Assert.Equal("Empty", L.T("Empty")); // an empty translation is treated as missing

            L.Load("{ this is not json", "pt-BR");
            Assert.Equal("Voices", L.T("Voices")); // broken file → English, no exception
        }
        finally
        {
            L.Load(null);
        }
        Assert.Equal("en", L.Language);
        Assert.Equal("Voices", L.T("Voices"));
    }

    [Fact]
    public void F_FormatsTranslatedPattern()
    {
        try
        {
            L.Load("{ \"Already used by {0}\": \"Já usado por {0}\", \"{0} of {1}\": \"broken {2}\" }", "pt-BR");
            Assert.Equal("Já usado por Robot", L.F("Already used by {0}", "Robot"));
            Assert.Equal("Missing 3", L.F("Missing {0}", 3));
            // A translation that can't be formatted falls back to the English pattern.
            Assert.Equal("1 of 2", L.F("{0} of {1}", 1, 2));
        }
        finally
        {
            L.Load(null);
        }
    }

    [Fact]
    public void Resolve_AutoFollowsWindowsLanguage()
    {
        Assert.Equal("pt-BR", L.Resolve("auto", new CultureInfo("pt-BR")));
        Assert.Equal("pt-BR", L.Resolve("auto", new CultureInfo("pt-PT")));
        Assert.Equal("en", L.Resolve("auto", new CultureInfo("de-DE")));
        Assert.Equal("en", L.Resolve(null, new CultureInfo("en-US")));
        Assert.Equal("pt-BR", L.Resolve("pt-BR", new CultureInfo("en-US")));
        Assert.Equal("en", L.Resolve("en", new CultureInfo("pt-BR")));
    }

    [Fact]
    public void PtBr_CoversEveryUiString()
    {
        var table = PtBrTable();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in UiStrings())
            if (!table.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) missing.Add(key);
        Assert.True(missing.Count == 0,
            "pt-BR.json is missing " + missing.Count + " entries:\n" + string.Join("\n", missing.Select(m => "  \"" + m + "\"")));
    }

    [Fact]
    public void PtBr_KeepsFormatPlaceholders()
    {
        var bad = new List<string>();
        foreach (var (key, value) in PtBrTable())
        {
            if (key.StartsWith('_')) continue; // notes, not translations (L skips them too)
            foreach (Match m in Regex.Matches(key, @"\{\d+\}"))
                if (!value.Contains(m.Value, StringComparison.Ordinal)) bad.Add($"\"{key}\" → \"{value}\" lacks {m.Value}");
            foreach (Match m in Regex.Matches(value, @"\{\d+\}"))
                if (!key.Contains(m.Value, StringComparison.Ordinal)) bad.Add($"\"{key}\" → \"{value}\" adds {m.Value}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void PtBr_FileIsValidUtf8WithoutMojibake()
    {
        var bytes = File.ReadAllBytes(PtBrPath());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "pt-BR.json must not start with a BOM");
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        foreach (var bad in new[] { "Ã©", "Ã£", "Ã§", "Ã¡", "Ãª", "Ã³", "â€", "ðŸ", "ï¼", "�" })
            Assert.False(text.Contains(bad, StringComparison.Ordinal), "pt-BR.json contains mojibake: " + bad);
        using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }

    // ---------- helpers ----------

    private static readonly Regex CodeLiteral = new(@"\bL\.[TF]\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex XamlLiteral = new(@"\{loc:T\s+(?:'([^']*)'|([^}]*))\}", RegexOptions.Compiled);
    private static readonly Regex SoundLoadMessage = new(@"new\s+SoundLoadException\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex SoundLoadFactory = new(@"SoundLoadException\s+\w+\(\)\s*=>\s*new\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    /// <summary>Every English text the person can see: literals in the app's sources plus built-in names.</summary>
    private static IEnumerable<string> UiStrings()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "src", "EasyVocal");
        foreach (var file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var ext = Path.GetExtension(file);
            if (ext == ".cs")
            {
                foreach (Match m in CodeLiteral.Matches(File.ReadAllText(file))) yield return Unescape(m.Groups[1].Value);
            }
            else if (ext == ".xaml")
            {
                foreach (Match m in XamlLiteral.Matches(File.ReadAllText(file)))
                {
                    var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim();
                    yield return System.Net.WebUtility.HtmlDecode(raw);
                }
            }
        }
        // Core messages meant for L.T (sound loading errors are shown to the person).
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "EasyVocal.Core"), "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            foreach (Match m in SoundLoadMessage.Matches(code)) yield return Unescape(m.Groups[1].Value);
            foreach (Match m in SoundLoadFactory.Matches(code)) yield return Unescape(m.Groups[1].Value);
        }
        // Built-in names are translated where they are shown.
        foreach (var v in BuiltInVoices.All()) yield return v.Name;
        foreach (var s in StarterSounds.Items()) yield return s.Name;
        foreach (var (_, label) in HotkeyMap.General) yield return label;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case 'u' when i + 4 < s.Length:
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> PtBrTable()
    {
        var text = File.ReadAllText(PtBrPath(), Encoding.UTF8);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(text,
            new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
        Assert.NotNull(map);
        return map!;
    }

    private static string PtBrPath() => Path.Combine(RepoRoot(), "src", "EasyVocal", "Lang", "pt-BR.json");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EasyVocal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
