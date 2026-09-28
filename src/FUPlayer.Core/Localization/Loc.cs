using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace FUPlayer.Core.Localization;

/// <summary>
/// The player's translations. The English text is the key, so the code reads as it always did and a string with no
/// translation shows in English: <c>Loc.T("Take orders from the local network")</c>, and <c>Loc.F</c> for text with
/// values in it, whose translation carries the same numbered placeholders. The language is chosen once, before the
/// interface is built, and changing it takes a restart; the command line tool never chooses one and stays English.
/// </summary>
/// <remarks>
/// Each language is a JSON object of English text to translated text, embedded as <c>Localization/&lt;code&gt;.json</c>.
/// </remarks>
public static class Loc
{
    /// <summary>The languages there are translations for, besides English: code and name in that language.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("ko", "한국어")];

    private static FrozenDictionary<string, string> _table = FrozenDictionary<string, string>.Empty;

    /// <summary>The language in use: "en", or one of <see cref="Languages"/>.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>
    /// Chooses the language: a code from <see cref="Languages"/>, "en", or empty for the system's own when there is a
    /// translation for it. Returns the language chosen.
    /// </summary>
    public static string SetLanguage(string? code)
    {
        string wanted = Wanted(code);
        FrozenDictionary<string, string>? table = wanted == "en" ? null : Load(wanted);
        _table = table ?? FrozenDictionary<string, string>.Empty;
        Language = table is null ? "en" : wanted;
        return Language;
    }

    /// <summary>The language <see cref="SetLanguage"/> would choose for this setting, without choosing it.</summary>
    public static string Resolve(string? code)
    {
        string wanted = Wanted(code);
        return wanted != "en" && Languages.Any(l => l.Code == wanted) ? wanted : "en";
    }

    private static string Wanted(string? code) =>
        string.IsNullOrWhiteSpace(code) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : code.Trim().ToLowerInvariant();

    /// <summary>The text in the language in use.</summary>
    public static string T(string english)
    {
        if (_table.Count == 0 || string.IsNullOrEmpty(english))
        {
            return english;
        }

        if (_table.TryGetValue(english, out string? translated))
        {
            return translated;
        }

        // Text laid out with spaces around it ("  In use: ") is looked up without them and given them back.
        string trimmed = english.Trim();
        if (trimmed.Length != english.Length && _table.TryGetValue(trimmed, out translated))
        {
            int lead = english.Length - english.TrimStart().Length;
            return english[..lead] + translated + english[(lead + trimmed.Length)..];
        }

        return english;
    }

    /// <summary>A format string in the language in use, filled in with the current culture.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>Whether there is a translation for this text in the language in use.</summary>
    public static bool Has(string english) => _table.ContainsKey(english) || _table.ContainsKey(english.Trim());

    /// <summary>Every English text a language translates, for the test that checks nothing is missing.</summary>
    public static IReadOnlyDictionary<string, string> Table(string code) =>
        (IReadOnlyDictionary<string, string>?)Load(code) ?? FrozenDictionary<string, string>.Empty;

    private static FrozenDictionary<string, string>? Load(string code)
    {
        Assembly assembly = typeof(Loc).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream($"FUPlayer.Core.Localization.{code}.json");
        if (stream is null)
        {
            return null;
        }

        Dictionary<string, string>? entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return entries?.Where(e => !string.IsNullOrEmpty(e.Value)).ToFrozenDictionary(StringComparer.Ordinal);
    }
}
