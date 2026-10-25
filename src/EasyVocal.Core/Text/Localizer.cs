using System.Globalization;
using System.Text.Json;

namespace EasyVocal.Core.Text;

/// <summary>
/// UI translation. Every visible text is written in English and passed through <see cref="T"/> (or <see cref="F"/>
/// for a pattern with <c>{0}</c>); the loaded table maps the English text to the translation. A missing or empty
/// entry, or a broken table, shows the English text. The table is swapped atomically, so any thread may read.
/// </summary>
public static class L
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static Dictionary<string, string> table = new(StringComparer.Ordinal);
    private static string language = "en";
    private static CultureInfo culture = CultureInfo.InvariantCulture;

    /// <summary>"en" or the code of the loaded table (e.g. "pt-BR").</summary>
    public static string Language => Volatile.Read(ref language);

    /// <summary>
    /// How numbers are written in the UI's language rather than Windows' (an English UI shows "1.5" on a Brazilian
    /// PC): the loaded language's culture, the invariant one for English.
    /// </summary>
    public static CultureInfo Culture => Volatile.Read(ref culture);

    /// <summary>
    /// Loads a translation table (a JSON object "English": "translation"; keys starting with "_" are notes).
    /// Null loads English. <paramref name="language"/> is the code the table is for.
    /// </summary>
    public static void Load(string? json, string language = "en")
    {
        var t = Parse(json);
        var code = json == null ? "en" : language;
        Volatile.Write(ref table, t);
        Volatile.Write(ref L.language, code);
        Volatile.Write(ref culture, CultureFor(code));
    }

    private static CultureInfo CultureFor(string code)
    {
        if (code == "en") return CultureInfo.InvariantCulture;
        try { return CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>The language to use for a setting: "pt-BR" or "en" as chosen, "auto" follows Windows' display language.</summary>
    public static string Resolve(string? setting, CultureInfo? culture = null)
    {
        if (string.Equals(setting, "pt-BR", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase)) return "en";
        var name = (culture ?? CultureInfo.CurrentUICulture).Name;
        return name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en";
    }

    /// <summary>The translation of an English text, or the text itself.</summary>
    public static string T(string english)
    {
        if (english == null) return "";
        return Volatile.Read(ref table).TryGetValue(english, out var t) ? t : english;
    }

    /// <summary>
    /// <see cref="T"/> of the pattern, formatted with <see cref="Culture"/>; a translation that can't be formatted
    /// falls back to English.
    /// </summary>
    public static string F(string englishFormat, params object[] args)
    {
        if (englishFormat == null) return "";
        var pattern = T(englishFormat);
        try
        {
            return string.Format(Culture, pattern, args);
        }
        catch (FormatException)
        {
            try { return string.Format(Culture, englishFormat, args); }
            catch (FormatException) { return englishFormat; }
        }
    }

    private static Dictionary<string, string> Parse(string? json)
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return t;
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return t;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_') || p.Value.ValueKind != JsonValueKind.String) continue;
                var value = p.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value)) t[p.Name] = value;
            }
        }
        catch (JsonException)
        {
            t.Clear(); // a broken file shows English rather than half a translation
        }
        return t;
    }
}
